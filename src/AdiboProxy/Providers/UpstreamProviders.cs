using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdiboProxy.Endpoints;
using AdiboProxy.Json;
using AdiboProxy.Logging;
using AdiboProxy.Rcouyi;

namespace AdiboProxy.Providers;

/// <summary>一个上游供应商的配置。</summary>
public sealed class ProviderConfig
{
    public string Name { get; set; } = "";

    /// <summary>rcouyi / openai-chat / openai-responses / anthropic</summary>
    public string Type { get; set; } = "rcouyi";

    public string BaseUrl { get; set; } = "";

    public string? ApiKey { get; set; }

    /// <summary>该供应商承接哪些模型，支持 <c>*</c> 通配；留空表示作为兜底供应商。</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>该供应商是否原生支持工具调用（为 true 时不再注入提示词协议）。</summary>
    public bool NativeTools { get; set; }

    /// <summary>发往该上游时使用的模型名；留空则沿用客户端请求里的模型名。</summary>
    public string? TargetModel { get; set; }
}

/// <summary>
/// 上游供应商抽象：把归一化的 <see cref="TurnRequest"/> 发出去，把结果归一化成
/// <see cref="ChatEvent"/> 流。实现方可以是 rcouyi 面板，也可以是任何 OpenAI / Anthropic 兼容服务。
/// </summary>
public interface IUpstreamProvider
{
    string Name { get; }

    /// <summary>原生支持工具调用则为 true，代理不再注入提示词协议。</summary>
    bool NativeTools { get; }

    IAsyncEnumerable<ChatEvent> StreamAsync(TurnRequest request, UpstreamTrace trace, CancellationToken ct);
}

/// <summary>按模型名挑选上游供应商。</summary>
public sealed class ProviderRegistry
{
    private readonly List<(ProviderConfig Config, IUpstreamProvider Provider)> _items = [];

    public IUpstreamProvider? Fallback { get; private set; }

    public IReadOnlyList<IUpstreamProvider> All => _items.Select(x => x.Provider).ToList();

    public void Add(ProviderConfig config, IUpstreamProvider provider)
    {
        _items.Add((config, provider));
        Fallback ??= provider;

        if (config.Models.Count == 0)
        {
            Fallback = provider;
        }
    }

    public IUpstreamProvider Resolve(string model)
    {
        foreach (var (config, provider) in _items)
        {
            if (config.Models.Any(pattern => Match(pattern, model)))
            {
                return provider;
            }
        }

        return Fallback ?? throw new InvalidOperationException("没有配置任何上游供应商");
    }

    private static bool Match(string pattern, string model)
    {
        if (pattern is "*" or "")
        {
            return true;
        }

        // 只支持 * 通配，够用且不会有正则注入问题。
        var parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        foreach (var part in parts)
        {
            var found = model.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            index = found + part.Length;
        }

        return true;
    }
}

/// <summary>三个通用上游（OpenAI Chat / OpenAI Responses / Anthropic）共用的底座。</summary>
public abstract class HttpUpstreamProvider : IUpstreamProvider
{
    private readonly HttpClient _http;
    protected readonly ProviderConfig Config;

    protected HttpUpstreamProvider(ProviderConfig config, IHttpClientFactory factory)
    {
        Config = config;
        _http = factory.CreateClient("upstream:" + config.Name);
    }

    public string Name => Config.Name;

    public bool NativeTools => Config.NativeTools;

    public abstract IAsyncEnumerable<ChatEvent> StreamAsync(TurnRequest request, UpstreamTrace trace, CancellationToken ct);

    protected JsonObject BuildBase(TurnRequest request)
        => J.Obj(
            ("model", string.IsNullOrWhiteSpace(Config.TargetModel) ? request.Model : Config.TargetModel),
            ("stream", true));

    protected async Task<HttpResponseMessage> PostAsync(
        string path, JsonNode payload, UpstreamTrace trace, CancellationToken ct)
    {
        var url = Config.BaseUrl.TrimEnd('/') + path;
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(J.Options), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(Config.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
        }

        var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;
        response.Dispose();
        trace.Error = $"上游 HTTP {status}";
        throw new UpstreamException(status, body, $"上游 {Name} 返回 HTTP {status}: {Trim(body)}");
    }

    /// <summary>把 SSE 拆成 (事件名, data 字符串) 序列。</summary>
    protected static async IAsyncEnumerable<(string Event, string Data)> ReadSseAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var eventName = "";
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                yield break;
            }

            if (line.Length == 0)
            {
                eventName = "";
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                yield return (eventName, line[5..].Trim());
            }
        }
    }

    protected static string? Str(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Trim(string text, int max = 300)
        => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>通用 OpenAI Chat Completions 上游（<c>POST {base}/chat/completions</c>）。</summary>
public sealed class OpenAiChatUpstream : HttpUpstreamProvider
{
    public OpenAiChatUpstream(ProviderConfig config, IHttpClientFactory factory) : base(config, factory)
    {
    }

    public override async IAsyncEnumerable<ChatEvent> StreamAsync(
        TurnRequest request,
        UpstreamTrace trace,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = BuildBase(request);
        payload["messages"] = ProviderPayload.Messages(request);

        if (request.Tools.Count > 0 && NativeTools)
        {
            payload["tools"] = ProviderPayload.Tools(request.Tools);
            payload["tool_choice"] = request.ForcedToolName is null ? request.ToolChoice : "required";
        }

        if (request.JsonMode)
        {
            payload["response_format"] = J.Obj(("type", "json_object"));
        }

        using var response = await PostAsync("/chat/completions", payload, trace, ct);
        trace.Chunks = 0;

        // tool_calls 是按下标分片下发的，需要拼起来。
        var pending = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        var sawToolCall = false;

        await foreach (var (_, data) in ReadSseAsync(response, ct))
        {
            if (data == "[DONE]" || data.Length == 0)
            {
                continue;
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(data);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            trace.Chunks++;
            trace.AppendRaw(data);

            if (root.TryGetProperty("error", out var error))
            {
                throw new UpstreamException(0, error.GetRawText(), Str(error, "message") ?? "上游返回错误");
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta))
                {
                    continue;
                }

                var reasoning = Str(delta, "reasoning_content") ?? Str(delta, "reasoning");
                if (!string.IsNullOrEmpty(reasoning))
                {
                    trace.ReasoningChars += reasoning.Length;
                    yield return new ChatReasoningEvent(reasoning);
                }

                var content = Str(delta, "content");
                if (!string.IsNullOrEmpty(content))
                {
                    trace.ContentChars += content.Length;
                    yield return new ChatTextEvent(content);
                }

                if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var call in calls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : 0;
                    if (!pending.TryGetValue(index, out var entry))
                    {
                        entry = (Str(call, "id") ?? $"call_{Guid.NewGuid():N}"[..29], "", new StringBuilder());
                    }

                    if (call.TryGetProperty("function", out var function))
                    {
                        var name = Str(function, "name");
                        if (!string.IsNullOrEmpty(name))
                        {
                            entry = (entry.Id, name, entry.Args);
                        }

                        var args = Str(function, "arguments");
                        if (!string.IsNullOrEmpty(args))
                        {
                            entry.Args.Append(args);
                        }
                    }

                    pending[index] = entry;
                }
            }
        }

        if (pending.Count > 0)
        {
            sawToolCall = true;
            var parsed = pending.OrderBy(x => x.Key)
                .Where(x => x.Value.Name.Length > 0)
                .Select(x => OpenAiEmulation.CreateToolCall(x.Value.Name, x.Value.Args.ToString(), x.Value.Id))
                .ToList();

            if (parsed.Count > 0)
            {
                trace.ToolCallDetected = true;
                trace.FinishReason = "tool_calls";
                yield return new ChatToolCallsEvent(parsed);
            }
        }

        trace.FinishReason ??= sawToolCall ? "tool_calls" : "stop";
    }
}

/// <summary>通用 OpenAI Responses 上游（<c>POST {base}/responses</c>）。</summary>
public sealed class OpenAiResponsesUpstream : HttpUpstreamProvider
{
    public OpenAiResponsesUpstream(ProviderConfig config, IHttpClientFactory factory) : base(config, factory)
    {
    }

    public override async IAsyncEnumerable<ChatEvent> StreamAsync(
        TurnRequest request,
        UpstreamTrace trace,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = BuildBase(request);
        payload["input"] = ProviderPayload.ResponsesInput(request);
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            payload["instructions"] = request.System;
        }

        if (request.Tools.Count > 0 && NativeTools)
        {
            payload["tools"] = ProviderPayload.ResponsesTools(request.Tools);
        }

        if (request.JsonMode)
        {
            payload["text"] = J.Obj(("format", J.Obj(("type", "json_object"))));
        }

        using var response = await PostAsync("/responses", payload, trace, ct);

        var calls = new Dictionary<string, (string Name, StringBuilder Args)>();

        await foreach (var (_, data) in ReadSseAsync(response, ct))
        {
            if (data.Length == 0)
            {
                continue;
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(data);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            var type = Str(root, "type");
            trace.Chunks++;
            trace.AppendRaw(data);

            switch (type)
            {
                case "response.output_text.delta":
                    var text = Str(root, "delta") ?? string.Empty;
                    trace.ContentChars += text.Length;
                    yield return new ChatTextEvent(text);
                    break;

                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    var think = Str(root, "delta") ?? string.Empty;
                    trace.ReasoningChars += think.Length;
                    yield return new ChatReasoningEvent(think);
                    break;

                case "response.output_item.added":
                    if (root.TryGetProperty("item", out var item) && Str(item, "type") == "function_call")
                    {
                        var callId = Str(item, "call_id") ?? Str(item, "id") ?? Guid.NewGuid().ToString("N");
                        calls[callId] = (Str(item, "name") ?? string.Empty, new StringBuilder());
                    }

                    break;

                case "response.function_call_arguments.delta":
                    var id = Str(root, "item_id");
                    if (id is not null && calls.TryGetValue(id, out var entry))
                    {
                        entry.Args.Append(Str(root, "delta") ?? string.Empty);
                        calls[id] = entry;
                    }

                    break;

                case "response.completed":
                    if (calls.Count == 0)
                    {
                        break;
                    }

                    var parsed = calls
                        .Where(x => x.Value.Name.Length > 0)
                        .Select(x => OpenAiEmulation.CreateToolCall(x.Value.Name, x.Value.Args.ToString(), x.Key))
                        .ToList();

                    if (parsed.Count > 0)
                    {
                        trace.ToolCallDetected = true;
                        trace.FinishReason = "tool_calls";
                        yield return new ChatToolCallsEvent(parsed);
                    }

                    calls.Clear();
                    break;
            }
        }

        trace.FinishReason ??= "stop";
    }
}

/// <summary>通用 Anthropic Messages 上游（<c>POST {base}/messages</c>）。</summary>
public sealed class AnthropicUpstream : HttpUpstreamProvider
{
    public AnthropicUpstream(ProviderConfig config, IHttpClientFactory factory) : base(config, factory)
    {
    }

    public override async IAsyncEnumerable<ChatEvent> StreamAsync(
        TurnRequest request,
        UpstreamTrace trace,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = BuildBase(request);
        payload["max_tokens"] = request.MaxTokens ?? 4096;
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            payload["system"] = request.System;
        }

        payload["messages"] = ProviderPayload.AnthropicMessages(request);

        if (request.Tools.Count > 0 && NativeTools)
        {
            payload["tools"] = ProviderPayload.AnthropicTools(request.Tools);
        }

        using var response = await PostAsync("/messages", payload, trace, ct);

        var calls = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

        await foreach (var (name, data) in ReadSseAsync(response, ct))
        {
            if (data.Length == 0)
            {
                continue;
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(data);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            var type = Str(root, "type") ?? name;
            trace.Chunks++;
            trace.AppendRaw(data);

            if (type == "error")
            {
                throw new UpstreamException(0, data, "上游返回错误事件");
            }

            if (type == "content_block_start"
                && root.TryGetProperty("content_block", out var block)
                && Str(block, "type") == "tool_use")
            {
                var index = root.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : 0;
                calls[index] = (
                    Str(block, "id") ?? Guid.NewGuid().ToString("N"),
                    Str(block, "name") ?? string.Empty,
                    new StringBuilder());
                continue;
            }

            if (type != "content_block_delta" || !root.TryGetProperty("delta", out var delta))
            {
                continue;
            }

            var deltaType = Str(delta, "type");
            var blockIndex = root.TryGetProperty("index", out var bi) && bi.TryGetInt32(out var b) ? b : 0;

            switch (deltaType)
            {
                case "text_delta":
                    var text = Str(delta, "text") ?? string.Empty;
                    trace.ContentChars += text.Length;
                    yield return new ChatTextEvent(text);
                    break;

                case "thinking_delta":
                    var think = Str(delta, "thinking") ?? string.Empty;
                    trace.ReasoningChars += think.Length;
                    yield return new ChatReasoningEvent(think);
                    break;

                case "input_json_delta":
                    if (calls.TryGetValue(blockIndex, out var entry))
                    {
                        entry.Args.Append(Str(delta, "partial_json") ?? string.Empty);
                        calls[blockIndex] = entry;
                    }

                    break;
            }
        }

        if (calls.Count > 0)
        {
            var parsed = calls.OrderBy(x => x.Key)
                .Where(x => x.Value.Name.Length > 0)
                .Select(x => OpenAiEmulation.CreateToolCall(x.Value.Name, x.Value.Args.ToString(), x.Value.Id))
                .ToList();

            if (parsed.Count > 0)
            {
                trace.ToolCallDetected = true;
                trace.FinishReason = "tool_calls";
                yield return new ChatToolCallsEvent(parsed);
            }
        }

        trace.FinishReason ??= "stop";
    }
}
