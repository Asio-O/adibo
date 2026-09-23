// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using AdiboProxy.Endpoints;
using AdiboProxy.Json;
using AdiboProxy.Logging;
using AdiboProxy.Rcouyi;

namespace AdiboProxy.Providers;

/// <summary>各种上游协议共用的报文拼装。</summary>
public static class ProviderPayload
{
    /// <summary>OpenAI 风格的 messages 数组。</summary>
    public static JsonArray Messages(TurnRequest request)
    {
        var array = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            array.Add((JsonNode)J.Obj(("role", "system"), ("content", request.System)));
        }

        foreach (var message in request.Messages)
        {
            if (message.Role == "assistant" && message.ToolCalls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                {
                    calls.Add((JsonNode)J.Obj(
                        ("id", call.Id),
                        ("type", "function"),
                        ("function", J.Obj(("name", call.Name), ("arguments", call.ArgumentsJson)))));
                }

                array.Add((JsonNode)J.Obj(
                    ("role", "assistant"),
                    ("content", message.Text.Length > 0 ? message.Text : null),
                    ("tool_calls", calls)));
                continue;
            }

            if (message.Role == "tool")
            {
                array.Add((JsonNode)J.Obj(
                    ("role", "tool"),
                    ("tool_call_id", message.ToolCallId ?? ""),
                    ("content", message.Text)));
                continue;
            }

            array.Add((JsonNode)J.Obj(("role", message.Role), ("content", message.Text)));
        }

        return array;
    }

    public static JsonArray Tools(IReadOnlyList<ToolSpec> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add((JsonNode)J.Obj(
                ("type", "function"),
                ("function", J.Obj(
                    ("name", tool.Name),
                    ("description", tool.Description),
                    ("parameters", Parameters(tool))))));
        }

        return array;
    }

    /// <summary>Responses 协议的输入数组。</summary>
    public static JsonArray ResponsesInput(TurnRequest request)
    {
        var array = new JsonArray();
        foreach (var message in request.Messages)
        {
            if (message.Role == "tool")
            {
                array.Add((JsonNode)J.Obj(
                    ("type", "function_call_output"),
                    ("call_id", message.ToolCallId ?? ""),
                    ("output", message.Text)));
                continue;
            }

            if (message.Role == "assistant" && message.ToolCalls.Count > 0)
            {
                if (message.Text.Length > 0)
                {
                    array.Add((JsonNode)J.Obj(
                        ("role", "assistant"),
                        ("content", message.Text)));
                }

                foreach (var call in message.ToolCalls)
                {
                    array.Add((JsonNode)J.Obj(
                        ("type", "function_call"),
                        ("call_id", call.Id),
                        ("name", call.Name),
                        ("arguments", call.ArgumentsJson)));
                }

                continue;
            }

            array.Add((JsonNode)J.Obj(("role", message.Role), ("content", message.Text)));
        }

        return array;
    }

    /// <summary>Responses 的工具是扁平结构。</summary>
    public static JsonArray ResponsesTools(IReadOnlyList<ToolSpec> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add((JsonNode)J.Obj(
                ("type", "function"),
                ("name", tool.Name),
                ("description", tool.Description),
                ("parameters", Parameters(tool))));
        }

        return array;
    }

    /// <summary>Anthropic 的 messages（content 用 block 数组）。</summary>
    public static JsonArray AnthropicMessages(TurnRequest request)
    {
        var array = new JsonArray();
        foreach (var message in request.Messages)
        {
            if (message.Role == "tool")
            {
                array.Add((JsonNode)J.Obj(
                    ("role", "user"),
                    ("content", new JsonArray(
                        J.Obj(
                            ("type", "tool_result"),
                            ("tool_use_id", message.ToolCallId ?? ""),
                            ("content", message.Text))))));
                continue;
            }

            if (message.Role == "assistant" && message.ToolCalls.Count > 0)
            {
                var blocks = new JsonArray();
                if (message.Text.Length > 0)
                {
                    blocks.Add((JsonNode)J.Obj(("type", "text"), ("text", message.Text)));
                }

                foreach (var call in message.ToolCalls)
                {
                    blocks.Add((JsonNode)J.Obj(
                        ("type", "tool_use"),
                        ("id", call.Id),
                        ("name", call.Name),
                        ("input", Parse(call.ArgumentsJson))));
                }

                array.Add((JsonNode)J.Obj(("role", "assistant"), ("content", blocks)));
                continue;
            }

            array.Add((JsonNode)J.Obj(
                ("role", message.Role == "assistant" ? "assistant" : "user"),
                ("content", message.Text)));
        }

        return array;
    }

    public static JsonArray AnthropicTools(IReadOnlyList<ToolSpec> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add((JsonNode)J.Obj(
                ("name", tool.Name),
                ("description", tool.Description),
                ("input_schema", Parameters(tool))));
        }

        return array;
    }

    private static JsonNode Parameters(ToolSpec tool)
        => tool.Parameters.ValueKind == JsonValueKind.Undefined
            ? J.Obj(("type", "object"), ("properties", new JsonObject()))
            : JsonNode.Parse(tool.Parameters.GetRawText()) ?? new JsonObject();

    private static JsonNode Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}

/// <summary>
/// rcouyi 面板上游。它是私有协议（<c>/chatapi/*</c>）且**不支持原生工具调用**，
/// 所以工具能力由代理注入提示词并解析输出实现。
/// </summary>
public sealed class RcouyiProvider : IUpstreamProvider
{
    private readonly RcouyiClient _client;
    private readonly TopicRouter _router;
    private readonly AdiboOptions _settings;
    private readonly IHttpContextAccessor _accessor;
    private readonly ILogger<RcouyiProvider> _log;

    public RcouyiProvider(
        RcouyiClient client,
        TopicRouter router,
        IOptions<AdiboOptions> options,
        IHttpContextAccessor accessor,
        ILogger<RcouyiProvider> log)
    {
        _client = client;
        _router = router;
        _settings = options.Value;
        _accessor = accessor;
        _log = log;
    }

    public string Name => "rcouyi";

    /// <summary>上游没有原生工具调用能力，永远由代理用提示词模拟。</summary>
    public bool NativeTools => false;

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        TurnRequest request,
        UpstreamTrace trace,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _settings.DefaultModel : request.Model;
        var (userContent, history) = ChatCore.Split(request);
        if (userContent.Length == 0)
        {
            throw new UpstreamException(0, null, "请求里没有可用的用户输入");
        }

        var reasoningMode = (_settings.ReasoningMode ?? "separate").Trim().ToLowerInvariant();
        var filter = reasoningMode == "inline" ? null : new ReasoningStreamFilter();
        var scanner = request.Tools.Count > 0 && request.ToolChoice != "none" && request.ToolPromptInjected
            ? new ToolCallScanner()
            : null;

        await foreach (var chunk in RouteAsync(request, model, userContent, history, ct))
        {
            if (chunk.Length == 0)
            {
                continue;
            }

            if (filter is null)
            {
                foreach (var e in Flush(chunk))
                {
                    yield return e;
                }

                continue;
            }

            foreach (var segment in filter.Push(chunk))
            {
                if (segment.Kind == ReasoningStreamFilter.Kind.Reasoning)
                {
                    if (reasoningMode != "separate")
                    {
                        continue;
                    }

                    trace.ReasoningChars += segment.Text.Length;
                    yield return new ChatReasoningEvent(segment.Text);
                    continue;
                }

                foreach (var e in Flush(segment.Text))
                {
                    yield return e;
                }
            }
        }

        if (filter is not null)
        {
            foreach (var segment in filter.Flush())
            {
                if (segment.Kind == ReasoningStreamFilter.Kind.Reasoning)
                {
                    if (reasoningMode == "separate")
                    {
                        trace.ReasoningChars += segment.Text.Length;
                        yield return new ChatReasoningEvent(segment.Text);
                    }

                    continue;
                }

                foreach (var e in Flush(segment.Text))
                {
                    yield return e;
                }
            }
        }

        trace.ThinkOpened = filter?.SawOpenTag ?? false;
        trace.ThinkClosed = filter?.SawCloseTag ?? false;

        if (scanner is not null)
        {
            var tail = scanner.Flush();
            if (scanner.HasToolCall)
            {
                trace.ToolCallDetected = true;
                trace.FinishReason = "tool_calls";
                yield return new ChatToolCallsEvent(scanner.Calls);
            }

            if (tail.Length > 0 && !scanner.HasToolCall)
            {
                trace.ContentChars += tail.Length;
                yield return new ChatTextEvent(tail);
            }
        }

        trace.FinishReason ??= "stop";

        IEnumerable<ChatEvent> Flush(string text)
        {
            if (text.Length == 0)
            {
                yield break;
            }

            if (scanner is null)
            {
                trace.ContentChars += text.Length;
                yield return new ChatTextEvent(text);
                yield break;
            }

            var released = scanner.Push(text);
            if (released.Length > 0)
            {
                trace.ContentChars += released.Length;
                yield return new ChatTextEvent(released);
            }
        }
    }

    private async IAsyncEnumerable<string> RouteAsync(
        TurnRequest request,
        string model,
        string userContent,
        List<RcouyiMessage> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var http = _accessor.HttpContext;

        void SetHeader(string name, string value)
        {
            if (http is not null && !http.Response.HasStarted)
            {
                http.Response.Headers[name] = value;
            }
        }

        if (request.TopicId is > 0)
        {
            _log.LogDebug("使用指定会话 {TopicId}", request.TopicId);
            var ids = await _client.SendMessageAsync(request.TopicId.Value, history, userContent, ct);
            await foreach (var chunk in _client.StreamMessageAsync(ids[^1], ct).WithCancellation(ct))
            {
                yield return chunk;
            }

            yield break;
        }

        if (request.Plugins is { Count: > 0 })
        {
            var created = await _client.CreateTopicAsync(new RcouyiCreateTopicRequest
            {
                Title = _settings.TopicTitle,
                Model = string.Equals(model, _settings.DefaultModel, StringComparison.OrdinalIgnoreCase) ? null : model,
                ChatPluginIds = request.Plugins,
            }, ct);

            var topicId = RcouyiEndpoints.ExtractTopicId(created);
            SetHeader("X-Rcouyi-Topic-Id", topicId.ToString());

            var ids = await _client.SendMessageAsync(topicId, history, userContent, ct);
            await foreach (var chunk in _client.StreamMessageAsync(ids[^1], ct).WithCancellation(ct))
            {
                yield return chunk;
            }

            yield break;
        }

        if (!string.Equals(model, _settings.DefaultModel, StringComparison.OrdinalIgnoreCase))
        {
            var topicId = await _router.ResolveAsync(_client, model, ct);
            long[] ids;
            try
            {
                ids = await _client.SendMessageAsync(topicId, history, userContent, ct);
            }
            catch (UpstreamException ex) when (IsMissingTopic(ex))
            {
                await _router.InvalidateAsync(model, ct);
                topicId = await _router.ResolveAsync(_client, model, ct);
                ids = await _client.SendMessageAsync(topicId, history, userContent, ct);
            }

            SetHeader("X-Rcouyi-Topic-Id", topicId.ToString());

            await foreach (var chunk in _client.StreamMessageAsync(ids[^1], ct).WithCancellation(ct))
            {
                yield return chunk;
            }

            yield break;
        }

        await foreach (var chunk in _client.CommonMessageStreamAsync(new RcouyiStreamRequest
        {
            Type = request.ChatType ?? 1,
            TopicId = 0,
            Content = userContent,
            Messages = history,
        }, ct).WithCancellation(ct))
        {
            yield return chunk;
        }
    }

    private static bool IsMissingTopic(UpstreamException ex)
        => ex.Message.Contains("D1002", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("记录不存在", StringComparison.Ordinal)
        || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
}
