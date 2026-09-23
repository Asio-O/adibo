using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using AdiboProxy.Json;
using AdiboProxy.Logging;
using AdiboProxy.Providers;
using AdiboProxy.Rcouyi;

namespace AdiboProxy.Endpoints;

/// <summary>
/// OpenAI Responses 协议（<c>POST /v1/responses</c>）。
///
/// 和 Chat Completions 的差别主要在报文形状：输入叫 <c>input</c>、工具是扁平结构、
/// 输出是 <c>output</c> 数组（message / function_call 两类 item），流式事件名也完全不同。
/// 内部仍然走同一套 <see cref="ChatCore"/>。
/// </summary>
public static class ResponsesEndpoints
{
    public static void MapResponsesEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var prefix in new[] { "/v1", "" })
        {
            app.MapPost(prefix + "/responses", Handle);
        }
    }

    private static async Task<IResult> Handle(
        JsonElement body,
        ProviderRegistry registry,
        IOptions<AdiboOptions> options,
        HttpContext http,
        UpstreamTrace trace,
        CancellationToken ct)
    {
        var request = Parse(body);
        if (request is null)
        {
            return J.Send(Error("invalid_request_error", "input 里没有可用的用户输入"), StatusCodes.Status400BadRequest);
        }

        var responseId = "resp_" + Guid.NewGuid().ToString("N")[..24];
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var streaming = body.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;

        try
        {
            if (!streaming)
            {
                var reply = new StringBuilder();
                var reasoning = new StringBuilder();
                IReadOnlyList<OpenAiEmulation.ParsedToolCall> calls = [];

                await foreach (var e in ChatCore.RunAsync(request, registry, options, trace, ct))
                {
                    switch (e)
                    {
                        case ChatTextEvent t: reply.Append(t.Text); break;
                        case ChatReasoningEvent r: reasoning.Append(r.Text); break;
                        case ChatToolCallsEvent c: calls = c.Calls; break;
                    }
                }

                var output = new JsonArray();
                if (reasoning.Length > 0)
                {
                    output.Add((JsonNode)J.Obj(
                        ("type", "reasoning"),
                        ("id", "rs_" + Guid.NewGuid().ToString("N")[..20]),
                        ("summary", new JsonArray())));
                }

                if (reply.Length > 0 || calls.Count == 0)
                {
                    output.Add((JsonNode)MessageItem("msg_" + Guid.NewGuid().ToString("N")[..20], reply.ToString(), "completed"));
                }

                foreach (var call in calls)
                {
                    output.Add((JsonNode)FunctionCallItem(call, "completed"));
                }

                var outputText = reply.ToString();
                return J.Send(J.Obj(
                    ("id", responseId),
                    ("object", "response"),
                    ("created_at", createdAt),
                    ("status", "completed"),
                    ("model", request.Model),
                    ("output", output),
                    ("output_text", outputText),
                    ("parallel_tool_calls", true),
                    ("tool_choice", request.ToolChoice),
                    ("tools", new JsonArray()),
                    ("usage", Usage(request, outputText.Length + calls.Sum(c => c.ArgumentsJson.Length), calls.Count > 0))));
            }

            http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var sequence = 0;
            long Seq() => sequence++;

            await SseAsync(http, J.Obj(
                ("type", "response.created"),
                ("sequence_number", Seq()),
                ("response", ResponseShell(responseId, createdAt, request.Model, "in_progress", new JsonArray()))), ct);
            await SseAsync(http, J.Obj(
                ("type", "response.in_progress"),
                ("sequence_number", Seq()),
                ("response", ResponseShell(responseId, createdAt, request.Model, "in_progress", new JsonArray()))), ct);

            var messageId = "msg_" + Guid.NewGuid().ToString("N")[..20];
            var messageStarted = false;
            var outputIndex = 0;
            var text = new StringBuilder();
            var sawToolCall = false;

            async Task StartMessageAsync()
            {
                if (messageStarted)
                {
                    return;
                }

                messageStarted = true;
                await SseAsync(http, J.Obj(
                    ("type", "response.output_item.added"),
                    ("sequence_number", Seq()),
                    ("output_index", outputIndex),
                    ("item", J.Obj(
                        ("id", messageId),
                        ("type", "message"),
                        ("status", "in_progress"),
                        ("role", "assistant"),
                        ("content", new JsonArray())))), ct);
                await SseAsync(http, J.Obj(
                    ("type", "response.content_part.added"),
                    ("sequence_number", Seq()),
                    ("item_id", messageId),
                    ("output_index", outputIndex),
                    ("content_index", 0),
                    ("part", J.Obj(("type", "output_text"), ("text", ""), ("annotations", new JsonArray())))), ct);
            }

            await foreach (var e in ChatCore.RunAsync(request, registry, options, trace, ct))
            {
                switch (e)
                {
                    case ChatTextEvent t when t.Text.Length > 0:
                        await StartMessageAsync();
                        text.Append(t.Text);
                        await SseAsync(http, J.Obj(
                            ("type", "response.output_text.delta"),
                            ("sequence_number", Seq()),
                            ("item_id", messageId),
                            ("output_index", outputIndex),
                            ("content_index", 0),
                            ("delta", t.Text)), ct);
                        break;

                    case ChatToolCallsEvent calls:
                        sawToolCall = true;

                        if (messageStarted)
                        {
                            await CloseMessageAsync(Seq, http, messageId, outputIndex, text.ToString(), ct);
                            outputIndex++;
                            messageStarted = false;
                        }

                        foreach (var call in calls.Calls)
                        {
                            await SseAsync(http, J.Obj(
                                ("type", "response.output_item.added"),
                                ("sequence_number", Seq()),
                                ("output_index", outputIndex),
                                ("item", FunctionCallItem(call, "in_progress"))), ct);
                            await SseAsync(http, J.Obj(
                                ("type", "response.function_call_arguments.delta"),
                                ("sequence_number", Seq()),
                                ("item_id", call.Id),
                                ("output_index", outputIndex),
                                ("delta", call.ArgumentsJson)), ct);
                            await SseAsync(http, J.Obj(
                                ("type", "response.function_call_arguments.done"),
                                ("sequence_number", Seq()),
                                ("item_id", call.Id),
                                ("output_index", outputIndex),
                                ("arguments", call.ArgumentsJson)), ct);
                            await SseAsync(http, J.Obj(
                                ("type", "response.output_item.done"),
                                ("sequence_number", Seq()),
                                ("output_index", outputIndex),
                                ("item", FunctionCallItem(call, "completed"))), ct);
                            outputIndex++;
                        }

                        break;
                }
            }

            if (messageStarted)
            {
                await CloseMessageAsync(Seq, http, messageId, outputIndex, text.ToString(), ct);
            }
            else if (!sawToolCall)
            {
                await StartMessageAsync();
                await CloseMessageAsync(Seq, http, messageId, outputIndex, string.Empty, ct);
            }

            await SseAsync(http, J.Obj(
                ("type", "response.completed"),
                ("sequence_number", Seq()),
                ("response", ResponseShell(responseId, createdAt, request.Model, "completed", new JsonArray(),
                    Usage(request, text.Length, sawToolCall)))), ct);
            return Results.Empty;
        }
        catch (UpstreamException ex)
        {
            return J.Send(Error("upstream_error", ex.Message), StatusCodes.Status502BadGateway);
        }
    }

    private static async Task CloseMessageAsync(
        Func<long> seq,
        HttpContext http,
        string messageId,
        int outputIndex,
        string text,
        CancellationToken ct)
    {
        await SseAsync(http, J.Obj(
            ("type", "response.output_text.done"),
            ("sequence_number", seq()),
            ("item_id", messageId),
            ("output_index", outputIndex),
            ("content_index", 0),
            ("text", text)), ct);
        await SseAsync(http, J.Obj(
            ("type", "response.content_part.done"),
            ("sequence_number", seq()),
            ("item_id", messageId),
            ("output_index", outputIndex),
            ("content_index", 0),
            ("part", J.Obj(("type", "output_text"), ("text", text), ("annotations", new JsonArray())))), ct);
        await SseAsync(http, J.Obj(
            ("type", "response.output_item.done"),
            ("sequence_number", seq()),
            ("output_index", outputIndex),
            ("item", MessageItem(messageId, text, "completed"))), ct);
    }

    private static JsonObject ResponseShell(
        string id, long createdAt, string model, string status, JsonArray output, JsonObject? usage = null)
        => J.Obj(
            ("id", id),
            ("object", "response"),
            ("created_at", createdAt),
            ("status", status),
            ("model", model),
            ("output", output),
            ("parallel_tool_calls", true),
            ("usage", usage));

    private static JsonObject MessageItem(string id, string text, string status)
        => J.Obj(
            ("id", id),
            ("type", "message"),
            ("status", status),
            ("role", "assistant"),
            ("content", new JsonArray(
                J.Obj(("type", "output_text"), ("text", text), ("annotations", new JsonArray())))));

    private static JsonObject FunctionCallItem(OpenAiEmulation.ParsedToolCall call, string status)
        => J.Obj(
            ("type", "function_call"),
            ("id", "fc_" + Guid.NewGuid().ToString("N")[..20]),
            ("call_id", call.Id),
            ("name", call.Name),
            ("arguments", call.ArgumentsJson),
            ("status", status));

    private static JsonObject Usage(TurnRequest request, int outputChars, bool toolCall)
    {
        var prompt = Math.Max(1, (request.Messages.Sum(m => m.Text.Length) + (request.System?.Length ?? 0)) / 2);
        var completion = Math.Max(1, outputChars / 2);
        return J.Obj(
            ("input_tokens", prompt),
            ("output_tokens", completion),
            ("total_tokens", prompt + completion),
            ("output_tokens_details", J.Obj(("reasoning_tokens", 0))),
            ("input_tokens_details", J.Obj(("cached_tokens", 0))));
    }

    private static JsonObject Error(string type, string message)
        => J.Obj(("error", J.Obj(("type", type), ("message", message), ("code", type))));

    private static async Task SseAsync(HttpContext http, JsonNode payload, CancellationToken ct)
    {
        await http.Response.WriteAsync("data: " + payload.ToJsonString(J.Options) + "\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    /// <summary>把 Responses 请求翻译成归一化请求。</summary>
    private static TurnRequest? Parse(JsonElement body)
    {
        var request = new TurnRequest
        {
            Model = body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()!
                : "ouyi-chat",
        };

        if (body.TryGetProperty("instructions", out var instructions))
        {
            request.System = instructions.ValueKind == JsonValueKind.String ? instructions.GetString() : null;
        }

        ReadInput(body, request);
        ReadTools(body, request);
        ReadFormat(body, request);

        if (body.TryGetProperty("metadata", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("topic_id", out var topic)
            && topic.TryGetInt64(out var topicId))
        {
            request.TopicId = topicId;
        }

        var hasInput = request.Messages.Any(x => x.Role == "user" && x.Text.Length > 0)
            || request.Messages.Any(x => x.Role == "tool");
        return hasInput ? request : null;
    }

    private static void ReadInput(JsonElement body, TurnRequest request)
    {
        if (!body.TryGetProperty("input", out var input))
        {
            return;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            request.Messages.Add(new TurnMessage { Role = "user", Text = input.GetString() ?? string.Empty });
            return;
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                request.Messages.Add(new TurnMessage { Role = "user", Text = item.GetString() ?? string.Empty });
                continue;
            }

            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "function_call_output")
            {
                var output = item.TryGetProperty("output", out var o) ? o.GetRawText() : string.Empty;
                if (output.StartsWith('"') && output.EndsWith('"'))
                {
                    using var doc = JsonDocument.Parse(output);
                    output = doc.RootElement.GetString() ?? string.Empty;
                }

                request.Messages.Add(new TurnMessage
                {
                    Role = "tool",
                    Text = output,
                    ToolCallId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() : null,
                });
                continue;
            }

            if (type is "reasoning")
            {
                continue;
            }

            var role = item.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
            var text = item.TryGetProperty("content", out var content) ? ReadContent(content) : string.Empty;

            var calls = new List<OpenAiEmulation.ParsedToolCall>();
            if (type == "function_call")
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    calls.Add(OpenAiEmulation.CreateToolCall(
                        name!,
                        item.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}",
                        item.TryGetProperty("call_id", out var cid) ? cid.GetString() : null));
                }
            }

            if (text.Length > 0 || calls.Count > 0)
            {
                request.Messages.Add(new TurnMessage { Role = role, Text = text, ToolCalls = calls });
            }
        }
    }

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                text.Append(part.GetString());
            }
            else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var t))
            {
                text.Append(t.GetString());
            }
        }

        return text.ToString();
    }

    private static void ReadTools(JsonElement body, TurnRequest request)
    {
        if (!body.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            // Responses 的工具是扁平结构：{type:"function", name, description, parameters}
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            request.Tools.Add(new ToolSpec(
                name!,
                tool.TryGetProperty("description", out var d) ? d.GetString() : null,
                tool.TryGetProperty("parameters", out var p) ? p : default));
        }

        if (body.TryGetProperty("tool_choice", out var choice))
        {
            if (choice.ValueKind == JsonValueKind.String)
            {
                request.ToolChoice = choice.GetString() switch
                {
                    "required" => "required",
                    "none" => "none",
                    _ => "auto",
                };
            }
            else if (choice.ValueKind == JsonValueKind.Object
                && choice.TryGetProperty("name", out var forced))
            {
                request.ToolChoice = "forced";
                request.ForcedToolName = forced.GetString();
            }
        }
    }

    private static void ReadFormat(JsonElement body, TurnRequest request)
    {
        if (!body.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.Object
            || !text.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = format.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "json_object":
                request.JsonMode = true;
                request.JsonModePrompt = OpenAiEmulation.JsonObjectPrompt;
                break;

            case "json_schema":
                request.JsonMode = true;
                request.JsonModePrompt = format.TryGetProperty("schema", out var schema)
                    ? OpenAiEmulation.BuildJsonSchemaPrompt(schema.GetRawText())
                    : OpenAiEmulation.JsonSchemaFallbackPrompt;
                break;
        }
    }
}
