// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdiboProxy.Endpoints;

/// <summary>
/// 上游既不认 <c>response_format</c>，也不认 <c>tools</c>（实测连 tool_choice=required 都会被忽略），
/// 所以这两个 OpenAI 能力只能在这里用「提示词注入 + 输出解析」模拟出来。
/// </summary>
public static class OpenAiEmulation
{
    /// <summary>当请求以工具结果结尾时，用它当作本轮输入，让模型基于结果作答。</summary>
    public const string ToolFollowUpInstruction = "请根据上面工具返回的结果，回答用户最初的问题。";

    public const string ToolResultPrefix = "[工具返回]";
    public const string ToolCallPrefix = "[请求调用工具]";

    /// <summary>把 OpenAI 的 tool 定义渲染成模型能读懂的说明书。</summary>
    public static string BuildToolPrompt(IReadOnlyList<ToolSpec> tools, string? toolChoiceName, bool requireTool)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你可以调用下列外部工具来完成任务：");
        builder.AppendLine();

        for (var i = 0; i < tools.Count; i++)
        {
            var function = tools[i];
            builder.AppendLine($"【工具 {i + 1}】");
            builder.AppendLine($"名称: {function.Name}");
            if (!string.IsNullOrWhiteSpace(function.Description))
            {
                builder.AppendLine($"说明: {function.Description}");
            }

            if (function.Parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                builder.AppendLine($"参数(JSON Schema): {function.Parameters.GetRawText()}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("调用规则：");
        builder.AppendLine("""1. 需要调用工具时，在回复里输出这样一段 JSON：""");
        builder.AppendLine("""   {"tool_calls":[{"name":"工具名","arguments":{"参数名":"参数值"}}]}""");
        builder.AppendLine("2. 可以先写一段普通文字说明你打算做什么，这段会直接展示给用户，不必刻意省略。");
        builder.AppendLine("3. 那段 JSON 必须是纯 JSON，不要包 markdown 代码块。");
        builder.AppendLine("4. arguments 必须是与该工具参数 Schema 匹配的 JSON 对象。");
        builder.AppendLine("5. 一次可以调用多个工具，放进同一个 tool_calls 数组。");
        builder.AppendLine("6. 不需要调用工具时，直接正常回答，不要输出这段 JSON。");
        builder.AppendLine("7. 需要实时数据（天气、新闻、股价、网页内容等）时，主动调用工具，");
        builder.AppendLine("   不要声称自己无法获取实时信息。");
        builder.AppendLine("8. 如果打算使用工具，不要只在正文里描述意图就结束，必须把那段 JSON 输出出来，");
        builder.AppendLine("   否则这次调用不会真正发生。");
        builder.AppendLine();
        builder.AppendLine("格式示例（工具名与参数换成实际的）：");
        builder.AppendLine("""用户：帮我看看 xxx 文件里有什么""");
        builder.AppendLine("""助手：我去读一下这个文件。{"tool_calls":[{"name":"read_file","arguments":{"path":"xxx"}}]}""");

        if (requireTool)
        {
            builder.AppendLine();
            builder.AppendLine("注意：本轮**必须**调用工具，不允许直接回答。");
            if (!string.IsNullOrWhiteSpace(toolChoiceName))
            {
                builder.AppendLine($"必须调用名为 {toolChoiceName} 的工具。");
            }
        }

        return builder.ToString();
    }

    /// <summary>把 response_format 翻译成一段「只输出 JSON」的系统指令；识别不了就返回 null。</summary>
    public static string? BuildJsonModePrompt(JsonElement responseFormat)
    {
        if (responseFormat.ValueKind != JsonValueKind.Object
            || !responseFormat.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        switch (type.GetString())
        {
            case "json_object":
                return JsonObjectPrompt;

            case "json_schema":
                var schema = ExtractSchema(responseFormat);
                return schema is null ? JsonSchemaFallbackPrompt : BuildJsonSchemaPrompt(schema);

            default:
                return null;
        }
    }

    public const string JsonObjectPrompt = """
        你必须只输出一个合法的 JSON 对象。
        规则：
        1. 不要输出 markdown 代码块（不要 ```json），不要任何解释、前言或后记。
        2. 输出的第一个字符必须是 { ，最后一个字符必须是 } 。
        3. 所有字符串必须使用双引号并正确转义。
        """;

    public const string JsonSchemaFallbackPrompt =
        "你必须只输出符合要求的合法 JSON 对象，不要输出 markdown 代码块或任何解释文字。";

    /// <summary>按给定的 JSON Schema 生成「只输出 JSON」的指令。</summary>
    public static string BuildJsonSchemaPrompt(string schemaJson)
        => $"""
            你必须只输出一个 JSON 对象，且严格满足下面的 JSON Schema：

            {schemaJson}

            规则：
            1. 不要输出 markdown 代码块（不要 ```json），不要任何解释、前言或后记。
            2. 缺少信息的字段按 Schema 要求填默认值或 null，不要省略必填字段。
            """;

    private static string? ExtractSchema(JsonElement responseFormat)
    {
        if (!responseFormat.TryGetProperty("json_schema", out var wrapper) || wrapper.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (wrapper.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object)
        {
            return schema.GetRawText();
        }

        // 有些客户端直接平铺 schema。
        return wrapper.ToString();
    }

    /// <summary>去掉模型偶尔还是会加的 markdown 代码块包裹。</summary>
    public static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }

        var body = trimmed[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? body[..closing].Trim() : body.Trim();
    }

    /// <summary>把推理块从正文里拆出来。</summary>
    public static (string Reasoning, string Content) SplitReasoning(string text)
    {
        var filter = new ReasoningStreamFilter();
        var reasoning = new StringBuilder();
        var content = new StringBuilder();

        void Consume(IReadOnlyList<ReasoningStreamFilter.Segment> segments)
        {
            foreach (var segment in segments)
            {
                (segment.Kind == ReasoningStreamFilter.Kind.Reasoning ? reasoning : content).Append(segment.Text);
            }
        }

        Consume(filter.Push(text));
        Consume(filter.Flush());

        return (reasoning.ToString().Trim(), content.ToString());
    }

    /// <summary>只做诊断：判断文本里有没有推理块的开/闭标签，用来发现"只开不闭"的截断输出。</summary>
    public static (bool Opened, bool Closed) ReasoningTagFlags(string text)
    {
        var opened = text.Contains("<think", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<thinking", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<reasoning", StringComparison.OrdinalIgnoreCase);
        var closed = text.Contains("</think", StringComparison.OrdinalIgnoreCase)
            || text.Contains("</thinking", StringComparison.OrdinalIgnoreCase)
            || text.Contains("</reasoning", StringComparison.OrdinalIgnoreCase);
        return (opened, closed);
    }

    /// <summary>从模型输出里解析出工具调用；不是工具调用就返回 false。</summary>
    public static bool TryParseToolCalls(string content, out List<ParsedToolCall> calls)
    {
        calls = [];
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        // 1) 主协议：<tool_call>…</tool_call> 块，可以出现多个。
        var blocks = ExtractBlocks(content, "tool_call");
        if (blocks.Count > 0)
        {
            var collected = new List<ParsedToolCall>();
            foreach (var block in blocks)
            {
                if (TryParseNode(StripCodeFence(block), out var parsed))
                {
                    collected.AddRange(parsed);
                }
            }

            if (collected.Count > 0)
            {
                calls = collected;
                return true;
            }

            // 有块但解析不出来：不当作工具调用，避免误吞正文。
            return false;
        }

        // 2) 兼容旧协议：裸 JSON。
        var text = StripCodeFence(content);
        if (text.Length == 0)
        {
            return false;
        }

        if (TryParseNode(text, out calls))
        {
            return true;
        }

        // 模型可能裹了一层说明文字，退一步找最外层的 {...}。
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start && TryParseNode(text[start..(end + 1)], out calls);
    }

    /// <summary>抽出形如 &lt;tag&gt;…&lt;/tag&gt; 的块内容（不含标签本身）。</summary>
    public static List<string> ExtractBlocks(string text, string tag)
    {
        var blocks = new List<string>();
        var open = "<" + tag + ">";
        var close = "</" + tag + ">";
        var index = 0;

        while (true)
        {
            var start = text.IndexOf(open, index, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            var contentStart = start + open.Length;
            var end = text.IndexOf(close, contentStart, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                // 没闭合：把剩下的都当作块内容，交给解析器判断。
                blocks.Add(text[contentStart..]);
                break;
            }

            blocks.Add(text[contentStart..end]);
            index = end + close.Length;
        }

        return blocks;
    }

    private static bool TryParseNode(string json, out List<ParsedToolCall> calls)
    {
        calls = [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // 形式一：{"name":…,"arguments":{…}} —— 单次调用。
            // 必须同时有 name 和 arguments，否则像 {"name":"张三","age":28} 这种普通 JSON
            // 会被误判成工具调用。
            if (root.TryGetProperty("name", out _) && root.TryGetProperty("arguments", out _))
            {
                if (ReadCall(root, out var single))
                {
                    calls.Add(single);
                }

                return calls.Count > 0;
            }

            // 形式二：{"tool_calls":[{…},{…}]} —— 旧协议
            if (!root.TryGetProperty("tool_calls", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && ReadCall(item, out var parsed))
                {
                    calls.Add(parsed);
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return calls.Count > 0;
    }

    private static bool ReadCall(JsonElement item, out ParsedToolCall call)
    {
        call = null!;

        var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var arguments = item.TryGetProperty("arguments", out var a) ? a : default;
        call = new ParsedToolCall
        {
            Name = name!,
            ArgumentsJson = arguments.ValueKind switch
            {
                JsonValueKind.Undefined => "{}",
                JsonValueKind.String => arguments.GetString() ?? "{}",
                _ => arguments.GetRawText(),
            },
        };
        return true;
    }

    public sealed class ParsedToolCall
    {
        public string Name { get; init; } = "";

        /// <summary>已经是 JSON 字符串，直接作为 OpenAI 的 function.arguments 返回。</summary>
        public string ArgumentsJson { get; init; } = "{}";

        public string Id { get; init; } = "call_" + Guid.NewGuid().ToString("N")[..24];
    }

    /// <summary>构造一个工具调用（供把外部协议里的 tool_use / function_call 转成统一表示）。</summary>
    public static ParsedToolCall CreateToolCall(string name, string argumentsJson, string? id = null)
        => new()
        {
            Name = name,
            ArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
            Id = string.IsNullOrWhiteSpace(id) ? "call_" + Guid.NewGuid().ToString("N")[..24] : id!,
        };

    public sealed class ToolDefinition
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("function")] public FunctionDefinition? Function { get; set; }
    }

    public sealed class FunctionDefinition
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
    }
}
