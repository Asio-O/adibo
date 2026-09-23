// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AdiboProxy.Logging;
using AdiboProxy.Rcouyi;

namespace AdiboProxy.Endpoints;

/// <summary>一轮对话里的单条消息（已归一化）。</summary>
public sealed class TurnMessage
{
    /// <summary>user / assistant / system / tool</summary>
    public string Role { get; init; } = "user";

    public string Text { get; init; } = "";

    /// <summary>assistant 消息里携带的工具调用。</summary>
    public IReadOnlyList<OpenAiEmulation.ParsedToolCall> ToolCalls { get; init; } = [];

    /// <summary>tool 结果对应的调用 id。</summary>
    public string? ToolCallId { get; init; }

    public string? Name { get; init; }
}

/// <summary>归一化后的对话请求，三个协议适配器都翻译成它。</summary>
public sealed class TurnRequest
{
    public string Model { get; set; } = "ouyi-chat";

    public string? System { get; set; }

    public List<TurnMessage> Messages { get; set; } = [];

    public List<ToolSpec> Tools { get; set; } = [];

    /// <summary>auto / required / none</summary>
    public string ToolChoice { get; set; } = "auto";

    public string? ForcedToolName { get; set; }

    public bool JsonMode { get; set; }

    public string? JsonModePrompt { get; set; }

    public long? TopicId { get; set; }

    public List<string>? Plugins { get; set; }

    public int? ChatType { get; set; }

    public int? MaxTokens { get; set; }

    public double? Temperature { get; set; }

    /// <summary>代理是否已注入工具提示词协议（由 ChatCore 决定，供应商据此决定要不要解析输出）。</summary>
    public bool ToolPromptInjected { get; set; }
}

/// <summary>上游输出被归一化成的事件流。</summary>
public abstract record ChatEvent;

public sealed record ChatTextEvent(string Text) : ChatEvent;

public sealed record ChatReasoningEvent(string Text) : ChatEvent;

public sealed record ChatToolCallsEvent(IReadOnlyList<OpenAiEmulation.ParsedToolCall> Calls) : ChatEvent;

/// <summary>
/// 三个协议共用的一条流水线：拆历史 → 注入工具/JSON 提示词 → 路由到上游 →
/// 分流推理块 → 识别工具调用 → 以 <see cref="ChatEvent"/> 吐出。
/// </summary>
public static class ChatCore
{
    public static async IAsyncEnumerable<ChatEvent> RunAsync(
        TurnRequest request,
        AdiboProxy.Providers.ProviderRegistry registry,
        IOptions<AdiboOptions> options,
        UpstreamTrace trace,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var settings = options.Value;
        var model = string.IsNullOrWhiteSpace(request.Model) ? settings.DefaultModel : request.Model;
        request.Model = model;

        var (userContent, _) = Split(request);
        if (userContent.Length == 0 && request.Messages.All(m => m.Role != "tool"))
        {
            throw new UpstreamException(0, null, "请求里没有可用的用户输入");
        }

        var toolsEnabled = request.Tools.Count > 0 && request.ToolChoice != "none";
        var provider = registry.Resolve(model);

        trace.Model = model;
        trace.Mode = provider.Name;
        trace.ToolsEnabled = toolsEnabled;
        trace.ReasoningMode = settings.ReasoningMode;

        // 供应商原生支持工具调用就用它自己的能力；否则退化为提示词注入 + 输出解析。
        var native = toolsEnabled && provider.NativeTools;
        trace.NativeTools = native;

        if (toolsEnabled && !native)
        {
            var prompt = OpenAiEmulation.BuildToolPrompt(
                request.Tools,
                request.ForcedToolName,
                request.ToolChoice is "required" or "forced");
            request.System = string.IsNullOrWhiteSpace(request.System)
                ? prompt
                : request.System + "\n\n" + prompt;
            request.ToolPromptInjected = true;
        }
        else if (request.JsonMode && string.IsNullOrWhiteSpace(request.System))
        {
            request.System = request.JsonModePrompt;
        }

        trace.JsonMode = request.JsonMode && !toolsEnabled;

        await foreach (var e in provider.StreamAsync(request, trace, ct).WithCancellation(ct))
        {
            yield return e;
        }
    }

    /// <summary>把归一化消息拆成「本轮输入」+「历史上下文」。</summary>
    public static (string UserContent, List<RcouyiMessage> History) Split(TurnRequest request)
    {
        var items = request.Messages.Select(Convert).ToList();
        var history = new List<RcouyiMessage>();

        if (items.Count == 0)
        {
            return (string.Empty, history);
        }

        // 以 tool 结果结尾：本轮由模型基于结果作答。
        if (items[^1].IsToolResult)
        {
            history.AddRange(items.Select(i => i.Message));
            return (OpenAiEmulation.ToolFollowUpInstruction, history);
        }

        var lastUser = -1;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Message.Role == "user")
            {
                lastUser = i;
                break;
            }
        }

        if (lastUser < 0)
        {
            history.AddRange(items.Select(i => i.Message));
            return (string.Empty, history);
        }

        var userContent = items[lastUser].Message.Content;
        for (var i = 0; i < items.Count; i++)
        {
            if (i != lastUser)
            {
                history.Add(items[i].Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.System) && history.All(m => m.Role != "system"))
        {
            history.Insert(0, new RcouyiMessage { Role = "system", Content = request.System! });
        }

        return (userContent, history);
    }

    private static MessageItem Convert(TurnMessage message)
    {
        if (message.Role == "tool")
        {
            var label = message.Name ?? message.ToolCallId ?? "tool";
            return new MessageItem(
                new RcouyiMessage { Role = "system", Content = $"{OpenAiEmulation.ToolResultPrefix} {label}\n{message.Text}" },
                true);
        }

        if (message.Role == "assistant" && message.ToolCalls.Count > 0)
        {
            var parts = message.ToolCalls.Select(c =>
                $"{{\"name\":{Quote(c.Name)},\"arguments\":{NormalizeArguments(c.ArgumentsJson)}}}");
            var json = $"{{\"tool_calls\":[{string.Join(",", parts)}]}}";
            var text = message.Text.Length > 0 ? message.Text + "\n" + json : json;
            return new MessageItem(new RcouyiMessage { Role = "assistant", Content = text }, false);
        }

        return new MessageItem(new RcouyiMessage { Role = message.Role, Content = message.Text }, false);
    }

    private static string NormalizeArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return "{}";
        }

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return Quote(arguments);
        }
    }

    private static string Quote(string text)
        => "\"" + System.Text.Json.JsonEncodedText.Encode(text) + "\"";

    /// <summary>把归一化工具定义拼成 OpenAI 形状的 JSON（手写字符串，AOT 安全）。</summary>
    private static List<JsonElement> BuildNativeTools(IReadOnlyList<ToolSpec> tools)
    {
        var result = new List<JsonElement>();
        foreach (var tool in tools)
        {
            var parameters = tool.Parameters.ValueKind == JsonValueKind.Undefined
                ? "{\"type\":\"object\",\"properties\":{}}"
                : tool.Parameters.GetRawText();
            var description = string.IsNullOrWhiteSpace(tool.Description) ? "null" : Quote(tool.Description!);
            var json =
                $"{{\"type\":\"function\",\"function\":{{\"name\":{Quote(tool.Name)},\"description\":{description},\"parameters\":{parameters}}}}}";

            using var doc = JsonDocument.Parse(json);
            result.Add(doc.RootElement.Clone());
        }

        return result;
    }

    /// <summary>按「指定会话 → 插件 → 指定模型 → 无状态」的顺序选择上游调用方式。</summary>
    private static async IAsyncEnumerable<string> RouteAsync(
        TurnRequest request,
        string model,
        AdiboOptions settings,
        bool nativeTools,
        string userContent,
        List<RcouyiMessage> history,
        RcouyiClient client,
        TopicRouter router,
        HttpContext http,
        UpstreamTrace trace,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 响应一旦开始（比如 Anthropic/Responses 先发了起始事件），就不能再写响应头了。
        void SetHeader(string name, string value)
        {
            if (!http.Response.HasStarted)
            {
                http.Response.Headers[name] = value;
            }
        }

        if (request.TopicId is > 0)
        {
            trace.Mode = "topic";
            var ids = await SendWithRecoveryAsync(client, null, router, model, request.TopicId.Value, history, userContent, ct);
            await foreach (var chunk in client.StreamMessageAsync(ids[^1], ct).WithCancellation(ct))
            {
                trace.Chunks++;
                trace.AppendRaw(chunk);
                yield return chunk;
            }

            yield break;
        }

        if (request.Plugins is { Count: > 0 })
        {
            trace.Mode = "plugin";
            var created = await client.CreateTopicAsync(new RcouyiCreateTopicRequest
            {
                Title = settings.TopicTitle,
                Model = string.Equals(model, settings.DefaultModel, StringComparison.OrdinalIgnoreCase) ? null : model,
                ChatPluginIds = request.Plugins,
            }, ct);

            var topicId = RcouyiEndpoints.ExtractTopicId(created);
            SetHeader("X-Rcouyi-Topic-Id", topicId.ToString());

            var ids = await SendWithRecoveryAsync(client, null, router, model, topicId, history, userContent, ct);
            await foreach (var chunk in client.StreamMessageAsync(ids[^1], ct).WithCancellation(ct))
            {
                trace.Chunks++;
                trace.AppendRaw(chunk);
                yield return chunk;
            }

            yield break;
        }

        if (!string.Equals(model, settings.DefaultModel, StringComparison.OrdinalIgnoreCase))
        {
            trace.Mode = "model-topic";
            var topicId = await router.ResolveAsync(client, model, ct);

            var ids = await SendWithRecoveryAsync(client, model, router, model, topicId, history, userContent, ct);
            SetHeader("X-Rcouyi-Topic-Id", ids[0].ToString());

            await foreach (var chunk in client.StreamMessageAsync(ids[^1], ct).WithCancellation(ct))
            {
                trace.Chunks++;
                trace.AppendRaw(chunk);
                yield return chunk;
            }

            yield break;
        }

        trace.Mode = "stateless";
        await foreach (var chunk in client.CommonMessageStreamAsync(new RcouyiStreamRequest
        {
            Type = request.ChatType ?? 1,
            TopicId = 0,
            Content = userContent,
            Messages = history,
            Tools = nativeTools ? BuildNativeTools(request.Tools) : null,
        }, ct).WithCancellation(ct))
        {
            trace.Chunks++;
            trace.AppendRaw(chunk);
            yield return chunk;
        }
    }

    /// <summary>发消息；遇到 [D1002]（会话被删）时丢掉缓存、重建会话再试一次。</summary>
    private static async Task<long[]> SendWithRecoveryAsync(
        RcouyiClient client,
        string? model,
        TopicRouter router,
        string resolvedModel,
        long topicId,
        List<RcouyiMessage> history,
        string userContent,
        CancellationToken ct)
    {
        try
        {
            var ids = await client.SendMessageAsync(topicId, history, userContent, ct);
            return [topicId, ids[^1]];
        }
        catch (UpstreamException ex) when (model is not null && IsMissingTopic(ex))
        {
            await router.InvalidateAsync(model, ct);
            var fresh = await router.ResolveAsync(client, model, ct);
            var ids = await client.SendMessageAsync(fresh, history, userContent, ct);
            return [fresh, ids[^1]];
        }
    }

    private static bool IsMissingTopic(UpstreamException ex)
        => ex.Message.Contains("D1002", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("记录不存在", StringComparison.Ordinal)
        || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private readonly record struct MessageItem(RcouyiMessage Message, bool IsToolResult);
}

/// <summary>
/// 剥掉模型偶尔加的 markdown 代码块围栏。只影响头部一小段，不牺牲流式。
/// </summary>
internal sealed class FenceStripper
{
    private readonly StringBuilder _head = new();
    private bool _decided;
    private bool _skipFirstLine;

    public string Push(string text)
    {
        if (_decided)
        {
            return text;
        }

        _head.Append(text);
        var buffered = _head.ToString();
        var trimmed = buffered.TrimStart();

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed[0] != '`')
        {
            _decided = true;
            _head.Clear();
            return buffered;
        }

        var newline = buffered.IndexOf('\n');
        if (newline < 0)
        {
            return string.Empty;   // 围栏那行还没读完
        }

        _decided = true;
        _skipFirstLine = true;
        _head.Clear();
        return buffered[(newline + 1)..].TrimStart();
    }

    public string Flush()
    {
        if (_head.Length == 0)
        {
            return string.Empty;
        }

        var text = _head.ToString();
        _head.Clear();
        return _decided && _skipFirstLine ? text.TrimStart() : text;
    }
}
