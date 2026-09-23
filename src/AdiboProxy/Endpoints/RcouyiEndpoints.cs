using System.Text.Json;
using Microsoft.Extensions.Options;
using AdiboProxy.Json;
using AdiboProxy.Rcouyi;

namespace AdiboProxy.Endpoints;

/// <summary>上游原生接口的对等透传，统一挂在 <c>/rcouyi</c> 下。</summary>
public static class RcouyiEndpoints
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void MapRcouyiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/rcouyi");

        // ---------------------------------------------------------------- 账号

        group.MapGet("/status", (TokenStore tokens) =>
        {
            var info = tokens.Info;
            return J.Send(J.Obj(
                ("hasToken", tokens.HasToken),
                ("tokenFile", tokens.FilePath),
                ("account", info?.Account),
                ("nickName", info?.NickName),
                ("memberId", info?.MemberId),
                ("expiresAt", info?.ExpiresAt),
                ("expired", info?.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow)));
        });

        group.MapPost("/token", async (RcouyiSetTokenRequest request, TokenStore tokens, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return J.Send(J.Obj(("error", "token 不能为空")), StatusCodes.Status400BadRequest);
            }

            var record = await tokens.SetAsync(request.Token!, ct);
            return J.Send(J.Obj(
                ("ok", true),
                ("account", record.Account),
                ("nickName", record.NickName),
                ("memberId", record.MemberId),
                ("expiresAt", record.ExpiresAt)));
        });

        group.MapDelete("/token", async (TokenStore tokens, CancellationToken ct) =>
        {
            await tokens.ClearAsync(ct);
            return J.Send(J.Obj(("ok", true)));
        });

        group.MapGet("/captcha", (RcouyiClient client, CancellationToken ct) =>
            ProxyAsync(() => client.GetCaptchaAsync(ct)));

        group.MapPost("/login", (RcouyiLoginRequest request, RcouyiClient client, TokenStore tokens, CancellationToken ct) =>
            GuardAsync(async () =>
            {
                var root = await client.LoginAsync(request, ct);
                var accessToken = ExtractAccessToken(root);
                var record = await tokens.SetAsync(accessToken, ct);

                return J.Send(J.Obj(
                    ("ok", true),
                    ("account", record.Account),
                    ("nickName", record.NickName),
                    ("memberId", record.MemberId),
                    ("expiresAt", record.ExpiresAt),
                    ("tokenFile", tokens.FilePath)));
            }));

        group.MapGet("/member", (RcouyiClient client, CancellationToken ct) =>
            ProxyAsync(() => client.GetMemberInfoAsync(ct)));

        group.MapGet("/wallet", (RcouyiClient client, CancellationToken ct) =>
            ProxyAsync(() => client.GetWalletAsync(ct)));

        group.MapGet("/models", (RcouyiClient client, CancellationToken ct) =>
            ProxyAsync(() => client.GetSiteModelsAsync(ct)));

        group.MapGet("/model-enums", (RcouyiClient client, CancellationToken ct) =>
            ProxyAsync(() => client.GetModelEnumsAsync(ct)));

        // 站点原生「工具」：会话插件清单。
        group.MapGet("/plugins", (string? tag, int? page, int? pageSize, RcouyiClient client, CancellationToken ct) =>
            ProxyAsync(() => client.GetPluginsAsync(tag ?? string.Empty, page ?? 1, pageSize ?? 99, ct)));

        group.MapGet("/plugin-tags", (RcouyiClient client, CancellationToken ct) =>
            ProxyAsync(() => client.GetPluginTagsAsync(ct)));

        // ---------------------------------------------------------------- 会话

        group.MapGet("/topics", (int? page, int? pageSize, RcouyiClient client, CancellationToken ct) =>
            ProxyAsync(() => client.GetTopicsAsync(page ?? 1, pageSize ?? 30, ct)));

        group.MapPost("/topics", (RcouyiCreateTopicRequest request, RcouyiClient client, CancellationToken ct) =>
            ProxyAsync(() => client.CreateTopicAsync(request, ct)));

        group.MapPost("/topics/{id:long}/messages", (long id, int? page, int? pageSize, RcouyiClient client, CancellationToken ct) =>
            ProxyAsync(() => client.GetTopicMessagesAsync(id, page ?? 1, pageSize ?? 20, ct)));

        // ---------------------------------------------------------------- 对话

        // 无状态：直接透传上游的纯文本流。
        group.MapPost("/chat", (RcouyiStreamRequest request, RcouyiClient client, HttpContext http, CancellationToken ct) =>
            GuardAsync(async () =>
            {
                await using var enumerator = client.CommonMessageStreamAsync(request, ct).GetAsyncEnumerator(ct);
                return await PipeTextAsync(http, enumerator, ct);
            }));

        // 持久化：先写消息拿 id，再按 id 拉流。
        group.MapPost("/chat/topic", (RcouyiTopicChatRequest request, RcouyiClient client,
            IOptions<AdiboOptions> options, HttpContext http, CancellationToken ct) =>
            GuardAsync(async () =>
            {
                var topicId = request.TopicId;
                if (topicId <= 0)
                {
                    var created = await client.CreateTopicAsync(new RcouyiCreateTopicRequest
                    {
                        Title = options.Value.TopicTitle,
                        SystemMessage = request.SystemMessage,
                    }, ct);

                    topicId = ExtractTopicId(created);
                }

                var ids = await client.SendMessageAsync(topicId, request.Messages, request.Content, ct);

                http.Response.Headers["X-Rcouyi-Topic-Id"] = topicId.ToString();
                http.Response.Headers["X-Rcouyi-User-Message-Id"] = ids[0].ToString();
                http.Response.Headers["X-Rcouyi-Assistant-Message-Id"] = ids[^1].ToString();

                await using var enumerator = client.StreamMessageAsync(ids[^1], ct).GetAsyncEnumerator(ct);
                return await PipeTextAsync(http, enumerator, ct);
            }));
    }

    /// <summary>把上游的纯文本流原样写回，逐块 flush。</summary>
    private static async Task<IResult> PipeTextAsync(
        HttpContext http,
        IAsyncEnumerator<string> enumerator,
        CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/plain; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        while (await enumerator.MoveNextAsync())
        {
            await http.Response.WriteAsync(enumerator.Current, ct);
            await http.Response.Body.FlushAsync(ct);
        }

        return Results.Empty;
    }

    private static Task<IResult> ProxyAsync(Func<Task<JsonElement>> action)
        => GuardAsync(async () => J.Raw((await action()).GetRawText()));

    /// <summary>把上游异常翻译成合适的 HTTP 状态码与 JSON 错误体。</summary>
    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (UpstreamException ex)
        {
            var status = ex.StatusCode is >= 400 and < 600
                ? ex.StatusCode
                : StatusCodes.Status502BadGateway;

            return J.Send(
                J.Obj(("error", ex.Message), ("upstreamCode", ex.StatusCode)),
                status);
        }
    }

    internal static long ExtractTopicId(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("id", out var id)
            && id.TryGetInt64(out var parsed))
        {
            return parsed;
        }

        throw new UpstreamException(200, root.ToString(), "新建会话失败：响应里没有 id");
    }

    internal static string ExtractAccessToken(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("accessToken", out var token)
            && token.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(token.GetString()))
        {
            return token.GetString()!;
        }

        throw new UpstreamException(200, root.ToString(), "登录响应里没有 accessToken");
    }

}
