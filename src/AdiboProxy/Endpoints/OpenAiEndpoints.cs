// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using AdiboProxy.Json;
using AdiboProxy.Logging;
using AdiboProxy.Rcouyi;

namespace AdiboProxy.Endpoints;

/// <summary>
/// OpenAI 兼容层：把 <c>/v1/chat/completions</c> 翻译成上游调用。
///
/// 上游本身没有 JSON 模式与 function calling，这两块由 <see cref="OpenAiEmulation"/> 模拟。
/// 所有响应体都用 <see cref="JsonObject"/> 现搭，保证 Native AOT 下不触发反射序列化。
/// </summary>
public static class OpenAiEndpoints
{
    public static void MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        // 同时挂 /v1/... 和 /... 两套路径：客户端把 base_url 配成带不带 /v1 都能用。
        foreach (var prefix in new[] { "/v1", "" })
        {
            app.MapGet(prefix + "/models", MapModels);
            app.MapPost(prefix + "/chat/completions", MapChatCompletions);
        }
    }

    private static async Task<IResult> MapModels(RcouyiClient client, CancellationToken ct)
    {
        var models = new JsonArray();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            // 站点前端静态配置里的清单，网页模型选择器用的就是它。
            var list = await client.GetSiteModelsAsync(ct);
            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var enabled = !item.TryGetProperty("enable", out var flag) || flag.ValueKind != JsonValueKind.False;
                models.Add((JsonNode)J.Obj(
                    ("id", value.GetString()),
                    ("object", "model"),
                    ("created", now),
                    ("owned_by", "rcouyi"),
                    ("enabled", enabled),
                    ("description", item.TryGetProperty("description", out var d) ? d.GetString() : null),
                    ("label", item.TryGetProperty("label", out var l) ? l.GetString() : null)));
            }
        }
        catch (Exception ex) when (ex is UpstreamException or HttpRequestException or JsonException)
        {
            // 静态配置拉不到时回落到旧版枚举接口。
            try
            {
                var root = await client.GetModelEnumsAsync(ct);
                if (root.TryGetProperty("result", out var enums) && enums.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in enums.EnumerateArray())
                    {
                        var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        models.Add((JsonNode)J.Obj(
                            ("id", name),
                            ("object", "model"),
                            ("created", now),
                            ("owned_by", "rcouyi"),
                            ("description", item.TryGetProperty("describe", out var d) ? d.GetString() : null)));
                    }
                }
            }
            catch (Exception)
            {
                // 两条路都失败时给个兜底。
            }
        }

        if (models.Count == 0)
        {
            foreach (var id in new[] { "ouyi-chat", "deepseek-v4-flash" })
            {
                models.Add((JsonNode)J.Obj(("id", id), ("object", "model"), ("created", now), ("owned_by", "rcouyi")));
            }
        }

        return J.Send(J.Obj(("object", "list"), ("data", models)));
    }

    private static async Task<IResult> MapChatCompletions(
        ChatCompletionRequest request,
        RcouyiClient client,
        TopicRouter router,
        IOptions<AdiboOptions> options,
        HttpContext http,
        UpstreamTrace trace,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            return await HandleChatCompletions(request, client, router, options, http, trace, ct);
        }
        catch (Exception ex)
        {
            trace.Error = ex.GetType().Name + ": " + ex.Message;
            throw;
        }
        finally
        {
            LogTrace(loggerFactory, trace);
        }
    }

    /// <summary>请求结束后落一条摘要；正文为空时把上游原文一起 dump 出来。</summary>
    private static void LogTrace(ILoggerFactory loggerFactory, UpstreamTrace trace)
    {
        var logger = loggerFactory.CreateLogger("Chat");
        logger.LogInformation("{Summary}", trace.Summary());

        // 工具调用本来就只有 tool_calls、没有正文，不该算异常。
        if (trace.ContentChars > 0 || trace.ToolCallDetected)
        {
            // 声明了 tools 却没调用：这往往意味着模型只在正文里"承诺"要用工具，
            // 需要看原始输出才能判断是提示词不够强还是模型没遵守。
            if (trace.ToolsEnabled && !trace.ToolCallDetected)
            {
                logger.LogInformation(
                    "req={Id} 声明了 tools 但未触发调用（正文 {Content} 字符）。上游原文=\n{Raw}",
                    trace.Id,
                    trace.ContentChars,
                    trace.RawText);
            }

            return;
        }

        // 正文为空是最容易让人以为「卡住了」的情况，把上游输出留全。
        logger.LogWarning(
            "req={Id} 正文为空：model={Model} mode={Mode} streaming={Streaming} tools={Tools} "
            + "thinkOpen={Open} thinkClose={Close} reasoningChars={Reasoning} finish={Finish}。上游原文=\n{Raw}",
            trace.Id,
            trace.Model,
            trace.Mode,
            trace.Streaming,
            trace.ToolsEnabled,
            trace.ThinkOpened,
            trace.ThinkClosed,
            trace.ReasoningChars,
            trace.FinishReason,
            trace.RawText);
    }

    private static async Task<IResult> HandleChatCompletions(
        ChatCompletionRequest request,
        RcouyiClient client,
        TopicRouter router,
        IOptions<AdiboOptions> options,
        HttpContext http,
        UpstreamTrace trace,
        CancellationToken ct)
    {
        var plan = BuildPlan(request);
        if (string.IsNullOrWhiteSpace(plan.UserContent))
        {
            return J.Send(Error("messages 里没有可用的 user 内容", "invalid_request_error"), StatusCodes.Status400BadRequest);
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? "ouyi-chat" : request.Model;
        var defaultModel = options.Value.DefaultModel;
        var completionId = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // JSON 模式要剥掉可能存在的代码块围栏，仍然内部缓冲；
        // 工具调用改成「只缓冲开头一小段」的判定，普通回答保持逐块流式。
        var bufferJson = plan.JsonMode;

        // 诊断用：一眼看出这轮走了哪条路、流式有没有被降级。
        var routeMode = request.TopicId is > 0 ? "topic"
            : request.Plugins is { Count: > 0 } ? "plugin"
            : string.Equals(model, defaultModel, StringComparison.OrdinalIgnoreCase) ? "stateless"
            : "model-topic";
        var streamingMode = !request.Stream ? "none"
            : plan.JsonMode ? "buffered-json"
            : plan.ToolsEnabled ? "incremental-tools-gated"
            : "incremental";

        http.Response.Headers["X-Rcouyi-Mode"] = routeMode;
        http.Response.Headers["X-Rcouyi-Streaming"] = streamingMode;

        trace.Model = model;
        trace.Mode = routeMode;
        trace.Streaming = streamingMode;
        trace.ToolsEnabled = plan.ToolsEnabled;
        trace.JsonMode = plan.JsonMode;

        // 推理块（DeepSeek V4 的 <think>）处理方式。
        var reasoningMode = (options.Value.ReasoningMode ?? "separate").Trim().ToLowerInvariant();
        trace.ReasoningMode = reasoningMode;

        ReasoningStreamFilter? filter = null;
        ToolCallScanner? scanner = null;

        (string Reasoning, string Content) Split(string text)
            => reasoningMode == "inline" ? (string.Empty, text) : OpenAiEmulation.SplitReasoning(text);

        async IAsyncEnumerable<string> Upstream(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            // 指定了会话：沿用该会话的模型、插件与提示词。
            if (request.TopicId is > 0)
            {
                var thread = await client.SendMessageAsync(request.TopicId.Value, plan.History, plan.UserContent, token);
                await foreach (var chunk in client.StreamMessageAsync(thread[^1], token).WithCancellation(token))
                {
                    trace.AppendRaw(chunk);
                    trace.Chunks++;
                    yield return chunk;
                }

                yield break;
            }

            // 指定了站点插件：先建一个挂载插件的会话，再走「先存后流」。
            if (request.Plugins is { Count: > 0 })
            {
                var createdTopic = await client.CreateTopicAsync(new RcouyiCreateTopicRequest
                {
                    Title = options.Value.TopicTitle,
                    Model = string.Equals(model, defaultModel, StringComparison.OrdinalIgnoreCase) ? null : model,
                    ChatPluginIds = request.Plugins,
                    SystemMessage = plan.SystemPrompt,
                }, token);

                var topicId = RcouyiEndpoints.ExtractTopicId(createdTopic);
                http.Response.Headers["X-Rcouyi-Topic-Id"] = topicId.ToString();

                var thread = await client.SendMessageAsync(topicId, plan.History, plan.UserContent, token);
                await foreach (var chunk in client.StreamMessageAsync(thread[^1], token).WithCancellation(token))
                {
                    trace.AppendRaw(chunk);
                    trace.Chunks++;
                    yield return chunk;
                }

                yield break;
            }

            // 指定了非默认模型：上游无状态接口会忽略 model，必须走会话路由才对。
            if (!string.Equals(model, defaultModel, StringComparison.OrdinalIgnoreCase))
            {
                var topicId = await router.ResolveAsync(client, model, token);
                http.Response.Headers["X-Rcouyi-Topic-Id"] = topicId.ToString();

                long[] thread;
                try
                {
                    thread = await client.SendMessageAsync(topicId, plan.History, plan.UserContent, token);
                }
                catch (UpstreamException ex) when (IsMissingTopic(ex))
                {
                    // 缓存的会话在上游已经被删掉了：丢掉缓存、重建一个再来一次。
                    await router.InvalidateAsync(model, token);
                    topicId = await router.ResolveAsync(client, model, token);
                    http.Response.Headers["X-Rcouyi-Topic-Id"] = topicId.ToString();
                    thread = await client.SendMessageAsync(topicId, plan.History, plan.UserContent, token);
                }

                await foreach (var chunk in client.StreamMessageAsync(thread[^1], token).WithCancellation(token))
                {
                    trace.AppendRaw(chunk);
                    trace.Chunks++;
                    yield return chunk;
                }

                yield break;
            }

            await foreach (var chunk in client.CommonMessageStreamAsync(new RcouyiStreamRequest
            {
                Type = request.ChatType ?? 1,
                TopicId = 0,
                Content = plan.UserContent,
                Messages = plan.History,
            }, token).WithCancellation(token))
            {
                trace.AppendRaw(chunk);
                trace.Chunks++;
                yield return chunk;
            }
        }

        // ---------------------------------------------------------------- 非流式

        if (!request.Stream)
        {
            var raw = await CollectAsync(Upstream, ct);
            if (raw.Error is { } error)
            {
                return UpstreamError(error);
            }

            var (reasoning, body) = Split(raw.Text);

            trace.ContentChars = body.Length;
            trace.ReasoningChars = reasoning.Length;
            (trace.ThinkOpened, trace.ThinkClosed) = OpenAiEmulation.ReasoningTagFlags(raw.Text);

            if (plan.ToolsEnabled && OpenAiEmulation.TryParseToolCalls(body, out var calls))
            {
                trace.ToolCallDetected = true;
                trace.FinishReason = "tool_calls";
                var toolCalls = new JsonArray();
                foreach (var call in calls)
                {
                    toolCalls.Add(ToolCallNode(call, index: null));
                }

                var message = J.Obj(("role", "assistant"), ("tool_calls", toolCalls));
                return J.Send(Completion(completionId, created, model,
                    Choice(0, message, "tool_calls"),
                    Usage(plan.UserContent, body)));
            }

            var content = plan.JsonMode ? OpenAiEmulation.StripCodeFence(body) : body;
            trace.FinishReason = "stop";
            var reply = J.Obj(
                ("role", "assistant"),
                ("content", content),
                ("reasoning_content", reasoningMode == "separate" && reasoning.Length > 0 ? reasoning : null));

            return J.Send(Completion(completionId, created, model,
                Choice(0, reply, "stop"),
                Usage(plan.UserContent, content)));
        }

        // ---------------------------------------------------------------- 流式

        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        if (bufferJson)
        {
            var raw = await CollectAsync(Upstream, ct);
            if (raw.Error is { } error)
            {
                await WriteSseErrorAsync(http, completionId, created, model, error, ct);
                return Results.Empty;
            }

            var (reasoning, body) = Split(raw.Text);

            trace.ContentChars = body.Length;
            trace.ReasoningChars = reasoning.Length;
            (trace.ThinkOpened, trace.ThinkClosed) = OpenAiEmulation.ReasoningTagFlags(raw.Text);

            if (plan.ToolsEnabled && OpenAiEmulation.TryParseToolCalls(body, out var calls))
            {
                trace.ToolCallDetected = true;
                trace.FinishReason = "tool_calls";
                for (var i = 0; i < calls.Count; i++)
                {
                    await WriteSseAsync(http, Chunk(completionId, created, model,
                        J.Obj(("tool_calls", new JsonArray(ToolCallNode(calls[i], i)))), null), ct);
                }

                await WriteSseAsync(http, Chunk(completionId, created, model, J.Obj(), "tool_calls"), ct);
                await http.Response.WriteAsync("data: [DONE]\n\n", ct);
                return Results.Empty;
            }

            var text = plan.JsonMode ? OpenAiEmulation.StripCodeFence(body) : body;

            if (reasoningMode == "separate" && reasoning.Length > 0)
            {
                await WriteSseAsync(http, Chunk(completionId, created, model,
                    J.Obj(("reasoning_content", reasoning)), null), ct);
            }

            if (text.Length > 0)
            {
                await WriteSseAsync(http, Chunk(completionId, created, model,
                    J.Obj(("content", text)), null), ct);
            }

            await WriteSseAsync(http, Chunk(completionId, created, model, J.Obj(), "stop"), ct);
            trace.FinishReason = "stop";
            await http.Response.WriteAsync("data: [DONE]\n\n", ct);
            return Results.Empty;
        }

        // 真·逐块透传。reasoning 模式下用过滤器把推理块分流；
        // 开了 tools 时再用 gate 判定开头是不是工具调用，判定完立刻放行。
        filter = reasoningMode == "inline" ? null : new ReasoningStreamFilter();
        scanner = plan.ToolsEnabled ? new ToolCallScanner() : null;

        async Task EmitContentAsync(string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            trace.ContentChars += text.Length;
            await WriteSseAsync(http, Chunk(completionId, created, model, J.Obj(("content", text)), null), ct);
        }

        async Task EmitAsync(ReasoningStreamFilter.Segment segment)
        {
            if (segment.Text.Length == 0)
            {
                return;
            }

            if (segment.Kind == ReasoningStreamFilter.Kind.Reasoning)
            {
                if (reasoningMode != "separate")
                {
                    return;
                }

                trace.ReasoningChars += segment.Text.Length;
                await WriteSseAsync(http, Chunk(completionId, created, model,
                    J.Obj(("reasoning_content", segment.Text)), null), ct);
                return;
            }

            if (scanner is null)
            {
                await EmitContentAsync(segment.Text);
                return;
            }

            // JSON 之前的文字照常流式；从 '{' 开始的内容先攒着做工具调用判定。
            var released = scanner.Push(segment.Text);
            if (released.Length > 0)
            {
                await EmitContentAsync(released);
            }
        }

        try
        {
            await foreach (var chunk in Upstream(ct))
            {
                if (chunk.Length == 0)
                {
                    continue;
                }

                if (filter is null)
                {
                    await EmitAsync(new ReasoningStreamFilter.Segment(ReasoningStreamFilter.Kind.Content, chunk));
                    continue;
                }

                foreach (var segment in filter.Push(chunk))
                {
                    await EmitAsync(segment);
                }
            }

            if (filter is not null)
            {
                foreach (var segment in filter.Flush())
                {
                    await EmitAsync(segment);
                }
            }
        }
        catch (UpstreamException ex)
        {
            trace.Error = ex.Message;
            // 已经在流中间了，只能把错误塞进 SSE 再收尾。
            await WriteSseErrorAsync(http, completionId, created, model, ex, ct);
            return Results.Empty;
        }

        // 只开不闭的 <think> 是「思维链结束但正文为空」的头号嫌疑，单独记下来。
        trace.ThinkOpened = filter?.SawOpenTag ?? false;
        trace.ThinkClosed = filter?.SawCloseTag ?? false;

        if (scanner is not null)
        {
            // 先 Flush：未闭合的块可能正好是个工具调用，得在判定前处理掉。
            var tail = scanner.Flush();

            if (scanner.HasToolCall)
            {
                trace.ToolCallDetected = true;
                trace.FinishReason = "tool_calls";
                for (var i = 0; i < scanner.Calls.Count; i++)
                {
                    await WriteSseAsync(http, Chunk(completionId, created, model,
                        J.Obj(("tool_calls", new JsonArray(ToolCallNode(scanner.Calls[i], i)))), null), ct);
                }

                await WriteSseAsync(http, Chunk(completionId, created, model, J.Obj(), "tool_calls"), ct);
                await http.Response.WriteAsync("data: [DONE]\n\n", ct);
                return Results.Empty;
            }

            if (tail.Length > 0)
            {
                await EmitContentAsync(tail);
            }
        }

        await WriteSseAsync(http, Chunk(completionId, created, model, J.Obj(), "stop"), ct);
        trace.FinishReason = "stop";
        await http.Response.WriteAsync("data: [DONE]\n\n", ct);
        return Results.Empty;
    }

    // ---------------------------------------------------------------- 响应构造

    private static JsonObject Error(string message, string type, int? code = null)
        => J.Obj(("error", J.Obj(("message", message), ("type", type), ("code", code))));

    private static JsonObject Completion(string id, long created, string model, JsonObject choice, JsonObject usage)
        => J.Obj(
            ("id", id),
            ("object", "chat.completion"),
            ("created", created),
            ("model", model),
            ("choices", new JsonArray(choice)),
            ("usage", usage));

    private static JsonObject Choice(int index, JsonObject message, string finishReason)
        => J.Obj(("index", index), ("message", message), ("finish_reason", finishReason));

    private static JsonObject Chunk(string id, long created, string model, JsonObject delta, string? finishReason)
        => J.Obj(
            ("id", id),
            ("object", "chat.completion.chunk"),
            ("created", created),
            ("model", model),
            ("choices", new JsonArray(
                J.Obj(("index", 0), ("delta", delta), ("finish_reason", finishReason)))));

    private static JsonNode ToolCallNode(OpenAiEmulation.ParsedToolCall call, int? index)
    {
        var function = J.Obj(("name", call.Name), ("arguments", call.ArgumentsJson));
        return index is { } i
            ? J.Obj(("index", i), ("id", call.Id), ("type", "function"), ("function", function))
            : J.Obj(("id", call.Id), ("type", "function"), ("function", function));
    }

    private static JsonObject Usage(string prompt, string completion)
    {
        var promptTokens = EstimateTokens(prompt);
        var completionTokens = EstimateTokens(completion);
        return J.Obj(
            ("prompt_tokens", promptTokens),
            ("completion_tokens", completionTokens),
            ("total_tokens", promptTokens + completionTokens));
    }

    private static async Task WriteSseErrorAsync(
        HttpContext http,
        string completionId,
        long created,
        string model,
        UpstreamException error,
        CancellationToken ct)
    {
        var payload = Chunk(completionId, created, model, J.Obj(), null);
        payload["choices"] = new JsonArray();
        payload["error"] = J.Obj(("message", error.Message), ("type", "upstream_error"), ("code", error.StatusCode));
        await WriteSseAsync(http, payload, ct);
        await http.Response.WriteAsync("data: [DONE]\n\n", ct);
    }

    private static IResult UpstreamError(UpstreamException ex)
        => J.Send(
            Error(ex.Message, "upstream_error", ex.StatusCode),
            ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status502BadGateway);

    // ---------------------------------------------------------------- 其它

    /// <summary>把上游流转成完整文本；异常也一并捕获，交给调用方决定怎么报。</summary>
    private static async Task<(string Text, UpstreamException? Error)> CollectAsync(
        Func<CancellationToken, IAsyncEnumerable<string>> stream,
        CancellationToken ct)
    {
        var builder = new StringBuilder();
        try
        {
            await foreach (var chunk in stream(ct))
            {
                builder.Append(chunk);
            }
        }
        catch (UpstreamException ex)
        {
            return (builder.ToString(), ex);
        }

        return (builder.ToString(), null);
    }

    /// <summary>把 OpenAI 请求翻译成「上游要怎么调」。</summary>
    private static RequestPlan BuildPlan(ChatCompletionRequest request)
    {
        var plan = new RequestPlan();
        var tools = (request.Tools ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Function?.Name))
            .Select(t => new ToolSpec(t.Function!.Name!, t.Function.Description, t.Function.Parameters))
            .ToList();

        var choiceKind = "auto";
        string? forcedName = null;
        if (request.ToolChoice.ValueKind == JsonValueKind.String)
        {
            choiceKind = request.ToolChoice.GetString() ?? "auto";
        }
        else if (request.ToolChoice.ValueKind == JsonValueKind.Object
            && request.ToolChoice.TryGetProperty("function", out var function)
            && function.TryGetProperty("name", out var name))
        {
            choiceKind = "forced";
            forcedName = name.GetString();
        }

        plan.ToolsEnabled = tools.Count > 0 && choiceKind != "none";

        // 工具与 JSON 模式不同时生效，工具优先。
        if (plan.ToolsEnabled)
        {
            plan.SystemPrompt = OpenAiEmulation.BuildToolPrompt(tools, forcedName, choiceKind is "required" or "forced");
        }
        else if (request.ResponseFormat.ValueKind == JsonValueKind.Object)
        {
            plan.SystemPrompt = OpenAiEmulation.BuildJsonModePrompt(request.ResponseFormat);
            plan.JsonMode = plan.SystemPrompt is not null;
        }

        var split = SplitMessages(request);
        plan.UserContent = split.UserContent;
        plan.History = split.History;

        if (plan.SystemPrompt is not null)
        {
            // 追加在历史末尾、紧挨着本轮输入，不覆盖用户自己的 system 提示词。
            plan.History.Add(new RcouyiMessage { Role = "system", Content = plan.SystemPrompt });
        }

        return plan;
    }

    /// <summary>把 OpenAI 的 messages 拆成「本轮输入」+「历史上下文」。</summary>
    private static (string UserContent, List<RcouyiMessage> History) SplitMessages(ChatCompletionRequest request)
    {
        var items = (request.Messages ?? []).Select(Convert).ToList();
        if (items.Count == 0)
        {
            return (string.Empty, []);
        }

        // 标准工具回合：请求以 tool 结果结尾，本轮该由模型基于结果作答。
        if (items[^1].IsToolResult)
        {
            return (OpenAiEmulation.ToolFollowUpInstruction, items.Select(i => i.Message).ToList());
        }

        var lastUserIndex = items.FindLastIndex(i => string.Equals(i.Message.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (lastUserIndex < 0)
        {
            return (string.Empty, items.Select(i => i.Message).ToList());
        }

        var userContent = items[lastUserIndex].Message.Content;
        var history = items.Where((_, index) => index != lastUserIndex).Select(i => i.Message).ToList();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt)
            && !history.Any(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)))
        {
            history.Insert(0, new RcouyiMessage { Role = "system", Content = request.SystemPrompt });
        }

        return (userContent, history);
    }

    private static MessageItem Convert(ChatMessage message)
    {
        var role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role!;
        var text = ExtractText(message.Content);

        if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            var label = message.Name ?? message.ToolCallId ?? "tool";
            return new MessageItem(
                new RcouyiMessage { Role = "system", Content = $"{OpenAiEmulation.ToolResultPrefix} {label}\n{text}" },
                IsToolResult: true);
        }

        // 助手请求调用工具的回合：还原成 prompt 里的 JSON，让模型知道上一次调了什么。
        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) && message.ToolCalls is { Count: > 0 })
        {
            return new MessageItem(
                new RcouyiMessage
                {
                    Role = "assistant",
                    Content = OpenAiEmulation.ToolCallPrefix + " " + RenderToolCalls(message.ToolCalls),
                },
                IsToolResult: false);
        }

        return new MessageItem(new RcouyiMessage { Role = role, Content = text }, IsToolResult: false);
    }

    private static string RenderToolCalls(List<ToolCallDto> calls)
    {
        var parts = calls
            .Where(c => !string.IsNullOrWhiteSpace(c.Function?.Name))
            .Select(c =>
            {
                var arguments = c.Function?.Arguments;
                var rendered = "{}";
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(arguments);
                        rendered = doc.RootElement.GetRawText();
                    }
                    catch (JsonException)
                    {
                        rendered = Quote(arguments);
                    }
                }

                return $"{{\"name\":{Quote(c.Function!.Name!)},\"arguments\":{rendered}}}";
            });

        return $"{{\"tool_calls\":[{string.Join(",", parts)}]}}";
    }

    /// <summary>把一个字符串转成带引号的 JSON 字面量（AOT 安全，不用反射）。</summary>
    private static string Quote(string? text)
        => "\"" + System.Text.Json.JsonEncodedText.Encode(text ?? string.Empty) + "\"";

    /// <summary>content 可能是字符串，也可能是多模态数组，这里统一抽成纯文本。</summary>
    private static string ExtractText(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? string.Empty;

            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(part.GetString() ?? string.Empty);
                    }
                    else if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(text.GetString() ?? string.Empty);
                    }
                }

                return string.Concat(parts);

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return string.Empty;

            default:
                return content.ToString();
        }
    }

    private static async Task WriteSseAsync(HttpContext http, JsonNode payload, CancellationToken ct)
    {
        await http.Response.WriteAsync("data: " + payload.ToJsonString(J.Options) + "\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    /// <summary>上游不返回 usage，这里给个粗略估算，只为让客户端不报错。</summary>
    private static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 2);

    /// <summary>上游用 [D1002] 记录不存在表示会话/消息已被删除。</summary>
    private static bool IsMissingTopic(UpstreamException ex)
        => ex.Message.Contains("D1002", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("记录不存在", StringComparison.Ordinal)
        || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private sealed class RequestPlan
    {
        public string UserContent { get; set; } = "";
        public List<RcouyiMessage> History { get; set; } = [];
        public string? SystemPrompt { get; set; }
        public bool ToolsEnabled { get; set; }
        public bool JsonMode { get; set; }
    }

    private readonly record struct MessageItem(RcouyiMessage Message, bool IsToolResult);

    public sealed class ToolCallDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("function")] public ToolCallFunctionDto? Function { get; set; }
    }

    public sealed class ToolCallFunctionDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }

    public sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public JsonElement Content { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
        [JsonPropertyName("tool_calls")] public List<ToolCallDto>? ToolCalls { get; set; }
    }

    public sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("messages")] public List<ChatMessage>? Messages { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("temperature")] public double? Temperature { get; set; }
        [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
        [JsonPropertyName("system")] public string? SystemPrompt { get; set; }

        /// <summary>OpenAI 标准的工具声明，由代理侧模拟成提示词。</summary>
        [JsonPropertyName("tools")] public List<OpenAiEmulation.ToolDefinition>? Tools { get; set; }

        [JsonPropertyName("tool_choice")] public JsonElement ToolChoice { get; set; }

        /// <summary><c>{"type":"json_object"}</c> 或 <c>{"type":"json_schema",...}</c>，由代理侧模拟。</summary>
        [JsonPropertyName("response_format")] public JsonElement ResponseFormat { get; set; }

        /// <summary>扩展字段：指定上游会话 id，走「先存后流」并沿用该会话配置的模型。</summary>
        [JsonPropertyName("topic_id")] public long? TopicId { get; set; }

        /// <summary>扩展字段：上游功能类型，1 普通对话 / 4 写作 / 5 思维导图。</summary>
        [JsonPropertyName("chat_type")] public int? ChatType { get; set; }

        /// <summary>
        /// 扩展字段：站点原生「工具」——会话插件 identifier（如 <c>GetCurrentWeather</c>、
        /// <c>search-engine</c>）。非空时代理会先建一个挂载这些插件的会话再对话。
        /// </summary>
        [JsonPropertyName("plugins")] public List<string>? Plugins { get; set; }
    }
}
