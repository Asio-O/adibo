// SPDX-License-Identifier: MIT
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
/// Anthropic Messages 协议（<c>POST /v1/messages</c>）：把请求翻译成 <see cref="TurnRequest"/>，
/// 再把 <see cref="ChatEvent"/> 翻译回 Anthropic 的 content block / SSE 事件。
/// </summary>
public static class AnthropicEndpoints
{
    public static void MapAnthropicEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var prefix in new[] { "/v1", "" })
        {
            app.MapPost(prefix + "/messages", Handle);
            app.MapPost(prefix + "/messages/count_tokens", (JsonElement body) =>
            {
                // 粗略估算，够客户端做上下文管理用。
                var chars = body.GetRawText().Length;
                return J.Send(J.Obj(("input_tokens", Math.Max(1, chars / 2))));
            });
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
            return J.Send(Error("invalid_request_error", "messages 里没有可用的用户输入"), StatusCodes.Status400BadRequest);
        }

        var messageId = "msg_" + Guid.NewGuid().ToString("N")[..24];
        var sawToolCall = false;

        try
        {
            if (!body.TryGetProperty("stream", out var streamFlag) || streamFlag.ValueKind != JsonValueKind.True)
            {
                var text = new StringBuilder();
                var reasoning = new StringBuilder();
                IReadOnlyList<OpenAiEmulation.ParsedToolCall> calls = [];

                await foreach (var e in ChatCore.RunAsync(request, registry, options, trace, ct))
                {
                    switch (e)
                    {
                        case ChatTextEvent t: text.Append(t.Text); break;
                        case ChatReasoningEvent r: reasoning.Append(r.Text); break;
                        case ChatToolCallsEvent c: calls = c.Calls; break;
                    }
                }

                var content = new JsonArray();
                if (reasoning.Length > 0)
                {
                    content.Add((JsonNode)J.Obj(("type", "thinking"), ("thinking", reasoning.ToString())));
                }

                if (text.Length > 0)
                {
                    content.Add((JsonNode)J.Obj(("type", "text"), ("text", text.ToString())));
                }

                foreach (var call in calls)
                {
                    content.Add((JsonNode)ToolUseBlock(call));
                }

                return J.Send(J.Obj(
                    ("id", messageId),
                    ("type", "message"),
                    ("role", "assistant"),
                    ("model", request.Model),
                    ("content", content),
                    ("stop_reason", calls.Count > 0 ? "tool_use" : "end_turn"),
                    ("stop_sequence", null),
                    ("usage", Usage(request, text.Length + reasoning.Length))));
            }

            http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            await SseAsync(http, "message_start", J.Obj(
                ("type", "message_start"),
                ("message", J.Obj(
                    ("id", messageId),
                    ("type", "message"),
                    ("role", "assistant"),
                    ("model", request.Model),
                    ("content", new JsonArray()),
                    ("stop_reason", null),
                    ("stop_sequence", null),
                    ("usage", J.Obj(("input_tokens", Estimate(request)), ("output_tokens", 0)))))), ct);

            var index = 0;
            var contentChars = 0;
            var reasoningOpen = false;
            var textOpen = false;

            await foreach (var e in ChatCore.RunAsync(request, registry, options, trace, ct))
            {
                if (e is ChatReasoningEvent r && r.Text.Length > 0)
                {
                    if (!reasoningOpen)
                    {
                        reasoningOpen = true;
                        await SseAsync(http, "content_block_start", J.Obj(
                            ("type", "content_block_start"),
                            ("index", index),
                            ("content_block", J.Obj(("type", "thinking"), ("thinking", "")))), ct);
                    }

                    await SseAsync(http, "content_block_delta", J.Obj(
                        ("type", "content_block_delta"),
                        ("index", index),
                        ("delta", J.Obj(("type", "thinking_delta"), ("thinking", r.Text)))), ct);
                    continue;
                }

                if (e is ChatTextEvent t2 && t2.Text.Length > 0)
                {
                    if (reasoningOpen)
                    {
                        await SseAsync(http, "content_block_stop", J.Obj(("type", "content_block_stop"), ("index", index)), ct);
                        index++;
                        reasoningOpen = false;
                    }

                    if (!textOpen)
                    {
                        textOpen = true;
                        await SseAsync(http, "content_block_start", J.Obj(
                            ("type", "content_block_start"),
                            ("index", index),
                            ("content_block", J.Obj(("type", "text"), ("text", "")))), ct);
                    }

                    contentChars += t2.Text.Length;
                    await SseAsync(http, "content_block_delta", J.Obj(
                        ("type", "content_block_delta"),
                        ("index", index),
                        ("delta", J.Obj(("type", "text_delta"), ("text", t2.Text)))), ct);
                    continue;
                }

                if (e is ChatToolCallsEvent calls)
                {
                    if (reasoningOpen)
                    {
                        await SseAsync(http, "content_block_stop", J.Obj(("type", "content_block_stop"), ("index", index)), ct);
                        index++;
                        reasoningOpen = false;
                    }

                    if (textOpen)
                    {
                        await SseAsync(http, "content_block_stop", J.Obj(("type", "content_block_stop"), ("index", index)), ct);
                        index++;
                        textOpen = false;
                    }

                    foreach (var call in calls.Calls)
                    {
                        await SseAsync(http, "content_block_start", J.Obj(
                            ("type", "content_block_start"),
                            ("index", index),
                            ("content_block", ToolUseBlock(call))), ct);
                        await SseAsync(http, "content_block_delta", J.Obj(
                            ("type", "content_block_delta"),
                            ("index", index),
                            ("delta", J.Obj(("type", "input_json_delta"), ("partial_json", call.ArgumentsJson)))), ct);
                        await SseAsync(http, "content_block_stop", J.Obj(("type", "content_block_stop"), ("index", index)), ct);
                        index++;
                    }

                    sawToolCall = true;
                }
            }

            if (reasoningOpen || textOpen)
            {
                await SseAsync(http, "content_block_stop", J.Obj(("type", "content_block_stop"), ("index", index)), ct);
            }

            await SseAsync(http, "message_delta", J.Obj(
                ("type", "message_delta"),
                ("delta", J.Obj(("stop_reason", sawToolCall ? "tool_use" : "end_turn"), ("stop_sequence", null))),
                ("usage", J.Obj(("output_tokens", Math.Max(1, contentChars / 2))))), ct);
            await SseAsync(http, "message_stop", J.Obj(("type", "message_stop")), ct);
            return Results.Empty;
        }
        catch (UpstreamException ex)
        {
            return J.Send(Error("upstream_error", ex.Message), StatusCodes.Status502BadGateway);
        }
    }

    private static JsonObject ToolUseBlock(OpenAiEmulation.ParsedToolCall call)
        => J.Obj(
            ("type", "tool_use"),
            ("id", "toolu_" + call.Id.Replace("call_", string.Empty)),
            ("name", call.Name),
            ("input", ParseObject(call.ArgumentsJson)));

    private static JsonNode ParseObject(string json)
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

    private static JsonObject Usage(TurnRequest request, int outputChars)
        => J.Obj(
            ("input_tokens", Estimate(request)),
            ("output_tokens", Math.Max(1, outputChars / 2)));

    private static int Estimate(TurnRequest request)
    {
        var chars = request.Messages.Sum(m => m.Text.Length) + (request.System?.Length ?? 0);
        return Math.Max(1, chars / 2);
    }

    private static JsonObject Error(string type, string message)
        => J.Obj(("type", "error"), ("error", J.Obj(("type", type), ("message", message))));

    private static async Task SseAsync(HttpContext http, string eventName, JsonNode payload, CancellationToken ct)
    {
        await http.Response.WriteAsync("event: " + eventName + "\n", ct);
        await http.Response.WriteAsync("data: " + payload.ToJsonString(J.Options) + "\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    /// <summary>把 Anthropic 请求翻译成归一化请求。</summary>
    private static TurnRequest? Parse(JsonElement body)
    {
        var request = new TurnRequest
        {
            Model = body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()!
                : "ouyi-chat",
        };

        if (body.TryGetProperty("system", out var system))
        {
            request.System = ReadText(system);
        }

        if (body.TryGetProperty("metadata", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("topic_id", out var topic)
            && topic.TryGetInt64(out var topicId))
        {
            request.TopicId = topicId;
        }

        if (body.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in tools.EnumerateArray())
            {
                var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var parameters = tool.TryGetProperty("input_schema", out var schema) ? schema : default;
                request.Tools.Add(new ToolSpec(
                    name!,
                    tool.TryGetProperty("description", out var d) ? d.GetString() : null,
                    parameters));
            }
        }

        if (body.TryGetProperty("tool_choice", out var choice) && choice.ValueKind == JsonValueKind.Object)
        {
            var type = choice.TryGetProperty("type", out var t) ? t.GetString() : null;
            request.ToolChoice = type switch
            {
                "any" => "required",
                "none" => "none",
                "tool" => "forced",
                _ => "auto",
            };

            if (type == "tool" && choice.TryGetProperty("name", out var forced))
            {
                request.ForcedToolName = forced.GetString();
            }
        }

        if (body.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                ReadMessage(message, request);
            }
        }

        var hasInput = request.Messages.Any(x => x.Role == "user" && x.Text.Length > 0)
            || request.Messages.Any(x => x.Role == "tool");
        return hasInput ? request : null;
    }

    private static void ReadMessage(JsonElement message, TurnRequest request)
    {
        var role = message.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
        if (!message.TryGetProperty("content", out var content))
        {
            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            request.Messages.Add(new TurnMessage { Role = role, Text = content.GetString() ?? string.Empty });
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var text = new StringBuilder();
        var toolCalls = new List<OpenAiEmulation.ParsedToolCall>();

        foreach (var block in content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "text":
                    text.Append(block.TryGetProperty("text", out var tx) ? tx.GetString() : string.Empty);
                    break;

                case "tool_use":
                    var name = block.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        break;
                    }

                    var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    var id = block.TryGetProperty("id", out var bid) ? bid.GetString() : null;
                    toolCalls.Add(OpenAiEmulation.CreateToolCall(name!, input, id));
                    break;

                case "tool_result":
                    var callId = block.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() : null;
                    request.Messages.Add(new TurnMessage
                    {
                        Role = "tool",
                        Text = block.TryGetProperty("content", out var rc) ? ReadText(rc) : string.Empty,
                        ToolCallId = callId,
                    });
                    break;
            }
        }

        if (text.Length > 0 || toolCalls.Count > 0)
        {
            request.Messages.Add(new TurnMessage { Role = role, Text = text.ToString(), ToolCalls = toolCalls });
        }
    }

    private static string ReadText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var block in element.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                text.Append(block.GetString());
            }
            else if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var t))
            {
                text.Append(t.GetString());
            }
        }

        return text.ToString();
    }
}
