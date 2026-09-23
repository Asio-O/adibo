using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using AdiboProxy.Json;

namespace AdiboProxy.Rcouyi;

/// <summary>上游返回非 2xx，或业务 code != 200 时抛出。</summary>
public sealed class UpstreamException : Exception
{
    public UpstreamException(int statusCode, string? body, string message) : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }

    public int StatusCode { get; }

    public string? Body { get; }
}

/// <summary>
/// 上游 API 的薄封装：拼请求头、发请求、解析 Furion 风格的 <c>{code,message,result}</c> 信封。
/// </summary>
public sealed class RcouyiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly HttpClient _site;
    private readonly TokenStore _tokens;
    private readonly AdiboOptions _options;
    private readonly ILogger<RcouyiClient> _log;
    private readonly string? _signature;

    public RcouyiClient(
        HttpClient http,
        IHttpClientFactory factory,
        TokenStore tokens,
        IOptions<AdiboOptions> options,
        ILogger<RcouyiClient> log)
    {
        _http = http;
        _site = factory.CreateClient(SiteClientName);
        _tokens = tokens;
        _options = options.Value;
        _log = log;
        _signature = _options.SendSignatureHeader ? RcouyiSignature.Compute(_options.BaseUrl) : null;
    }

    public const string SiteClientName = "rcouyi-site";

    // ------------------------------------------------------------------ 账号

    public Task<JsonElement> LoginAsync(RcouyiLoginRequest request, CancellationToken ct)
        => PostEnvelopeAsync("/chatapi/auth/login", J.From(request, AppJsonContext.Default.RcouyiLoginRequest), ct);

    public Task<JsonElement> GetCaptchaAsync(CancellationToken ct)
        => GetEnvelopeAsync("/chatapi/auth/captcha", ct);

    public Task<JsonElement> GetMemberInfoAsync(CancellationToken ct)
        => GetEnvelopeAsync("/chatapi/auth/memberInfo", ct);

    public Task<JsonElement> GetWalletAsync(CancellationToken ct)
        => GetEnvelopeAsync("/chatapi/member/wallet", ct);

    /// <summary>旧版模型枚举（{name, describe, value}）。</summary>
    public Task<JsonElement> GetModelEnumsAsync(CancellationToken ct)
        => GetEnvelopeAsync("/api/sysEnum/enumDataList?EnumName=InterfaceAIModelEnum", ct);

    /// <summary>站点原生「工具」清单：会话插件。</summary>
    public Task<JsonElement> GetPluginsAsync(string tag, int page, int pageSize, CancellationToken ct)
        => PostEnvelopeAsync("/chatapi/chatplugin/page", J.Obj(("page", page), ("pageSize", pageSize), ("tag", tag)), ct);

    public Task<JsonElement> GetPluginTagsAsync(CancellationToken ct)
        => GetEnvelopeAsync("/chatapi/chatplugin/tags", ct);

    /// <summary>
    /// 站点前端静态配置里的模型清单——网页的模型选择器用的就是这条
    /// （<c>https://ai.rcouyi.com/config/system.json</c> 的 <c>model</c> 数组）。
    /// </summary>
    public async Task<JsonElement> GetSiteModelsAsync(CancellationToken ct)
    {
        using var response = await _site.GetAsync("/config/system.json", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();

        if (!root.TryGetProperty("model", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new UpstreamException(200, null, "站点配置里没有 model 数组");
        }

        return models;
    }

    // ------------------------------------------------------------------ 会话

    public Task<JsonElement> GetTopicsAsync(int page, int pageSize, CancellationToken ct)
        => GetEnvelopeAsync($"/chatapi/chat/topics?page={page}&pageSize={pageSize}", ct);

    /// <summary>上游这个接口的字段是 <c>id</c>（不是 topicId）。</summary>
    public Task<JsonElement> GetTopicMessagesAsync(long topicId, int page, int pageSize, CancellationToken ct)
        => PostEnvelopeAsync("/chatapi/chat/topic/messages", J.Obj(("id", topicId), ("page", page), ("pageSize", pageSize)), ct);

    /// <summary>新建会话。上游该接口要 PascalCase 字段，<c>Params</c> 还是字符串化的 JSON。</summary>
    public async Task<JsonElement> CreateTopicAsync(RcouyiCreateTopicRequest request, CancellationToken ct)
    {
        var parameters = J.Obj(
            ("chatPluginIds", J.Arr((request.ChatPluginIds ?? []).Cast<object?>())),
            ("frequency_penalty", null),
            ("max_tokens", request.MaxTokens ?? _options.MaxTokens),
            ("model", string.IsNullOrWhiteSpace(request.Model) ? _options.DefaultModel : request.Model),
            ("presence_penalty", null),
            ("requestMsgCount", request.RequestMsgCount ?? _options.RequestMsgCount),
            ("speechVoice", "Alloy"),
            ("temperature", request.Temperature ?? _options.Temperature));

        // 只有显式指定时才下发，否则沿用站点默认，避免改变已有行为。
        if (request.WebSearch is { } webSearch)
        {
            parameters["is_webSearch"] = webSearch;
        }

        var payload = J.Obj(
            ("Title", string.IsNullOrWhiteSpace(request.Title) ? "新会话" : request.Title),
            ("Params", parameters.ToJsonString(J.Options)),
            ("SystemMessage", request.SystemMessage ?? string.Empty),
            ("RoleId", 0));

        return await PostEnvelopeAsync("/chatapi/chat/save", payload, ct).ConfigureAwait(false);
    }

    /// <summary>写入用户消息并预置助手消息，返回 <c>[用户消息id, 助手消息id]</c>。</summary>
    public async Task<long[]> SendMessageAsync(
        long topicId,
        IReadOnlyList<RcouyiMessage> messages,
        string content,
        CancellationToken ct)
    {
        var payload = J.Obj(
            ("topicId", topicId),
            ("messages", J.Messages(messages)),
            ("content", content),
            ("contentFiles", new JsonArray()));

        var root = await PostEnvelopeAsync("/chatapi/chat/message", payload, ct).ConfigureAwait(false);
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            throw new UpstreamException(200, root.ToString(), "上游未返回消息 id");
        }

        var ids = result
            .EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Number)
            .Select(e => e.GetInt64())
            .ToArray();

        if (ids.Length < 2)
        {
            throw new UpstreamException(200, root.ToString(), "上游返回的消息 id 数量不足");
        }

        return ids;
    }

    /// <summary>按助手消息 id 拉流（这一步才真正触发生成）。</summary>
    public IAsyncEnumerable<string> StreamMessageAsync(long assistantMessageId, CancellationToken ct)
        => StreamTextAsync($"/chatapi/chat/message/{assistantMessageId}", body: null, ct);

    /// <summary>无状态对话流。注意：上游忽略请求里的 model 字段。</summary>
    public IAsyncEnumerable<string> CommonMessageStreamAsync(RcouyiStreamRequest request, CancellationToken ct)
        => StreamTextAsync(
            "/chatapi/chat/commonmessagestream",
            J.From(request, AppJsonContext.Default.RcouyiStreamRequest),
            ct);

    // ------------------------------------------------------------------ 底座

    private async Task<JsonElement> GetEnvelopeAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        return await ReadEnvelopeAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> PostEnvelopeAsync(string path, JsonNode? body, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, ct).ConfigureAwait(false);
        return await ReadEnvelopeAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadEnvelopeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new UpstreamException(
                (int)response.StatusCode,
                text,
                $"上游 HTTP {(int)response.StatusCode}: {Truncate(text)}");
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new UpstreamException((int)response.StatusCode, text, $"上游返回的不是 JSON: {Truncate(text)}");
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.GetInt32() != 200)
        {
            var message = root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                ? msg.GetString()
                : null;

            throw new UpstreamException(
                code.GetInt32(),
                text,
                string.IsNullOrWhiteSpace(message) ? "上游业务错误" : message);
        }

        return root;
    }

    /// <summary>
    /// 上游流式接口返回的是**纯文本分块**（不是 SSE），这里逐块透传并处理 UTF-8 跨块边界。
    /// </summary>
    public async IAsyncEnumerable<string> StreamTextAsync(
        string path,
        JsonNode? body,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, ct, responseHeadersRead: true)
            .ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[8192];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var count = decoder.GetChars(buffer, 0, read, chars, 0);
            if (count > 0)
            {
                yield return new string(chars, 0, count);
            }
        }

        var flushed = decoder.GetChars(buffer, 0, 0, chars, 0, flush: true);
        if (flushed > 0)
        {
            yield return new string(chars, 0, flushed);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body,
        CancellationToken ct,
        bool responseHeadersRead = false)
    {
        using var request = BuildRequest(method, path);

        if (method == HttpMethod.Post)
        {
            // 上游部分接口要求 Content-Type 存在，即使 body 为空。
            request.Content = new StringContent(
                body?.ToJsonString(J.Options) ?? string.Empty,
                Encoding.UTF8,
                "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                request,
                responseHeadersRead ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "请求上游失败 {Method} {Path}", method, path);
            throw new UpstreamException(0, null, $"无法连接上游: {ex.Message}");
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        response.Dispose();

        if (status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            throw new UpstreamException(status, text, "token 无效或已过期，请重新登录");
        }

        throw new UpstreamException(status, text, $"上游 HTTP {status}: {Truncate(text)}");
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);

        var token = _tokens.Token;
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        request.Headers.TryAddWithoutValidation("Accept-Language", _options.Language);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

        if (!string.IsNullOrWhiteSpace(_options.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        }

        // Cloudflare 保护的上游需要 cf_clearance 之类的 Cookie，由配置注入。
        foreach (var (name, value) in _options.Headers)
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (_signature is not null)
        {
            request.Headers.TryAddWithoutValidation("xx-cf-source", _signature);
        }

        return request;
    }

    private static string Truncate(string? text, int max = 400)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= max ? text : text[..max] + "…";
    }
}
