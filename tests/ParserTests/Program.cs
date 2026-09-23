using AdiboProxy.Endpoints;

var passed = 0;
var failed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok)
    {
        passed++;
        Console.WriteLine($"[PASS] {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"[FAIL] {name}" + (detail.Length > 0 ? $" — {detail}" : ""));
    }
}

// 按固定大小切片喂给扫描器，模拟流式分片。
string Feed(ToolCallScanner scanner, string text, int chunkSize = 7)
{
    var output = "";
    for (var i = 0; i < text.Length; i += chunkSize)
    {
        output += scanner.Push(text[i..Math.Min(text.Length, i + chunkSize)]);
    }

    output += scanner.Flush();
    return output;
}

const string CallJson = """{"tool_calls":[{"name":"read_file","arguments":{"path":"src/main.py"}}]}""";

Console.WriteLine("=== 1. 非流式解析 ===");
{
    var ok = OpenAiEmulation.TryParseToolCalls(CallJson, out var calls);
    Check("裸 JSON 可解析", ok && calls.Count == 1 && calls[0].Name == "read_file",
        ok ? $"calls={calls.Count}" : "未解析出");
    Check("arguments 正确", ok && calls.Count > 0 && calls[0].ArgumentsJson.Contains("src/main.py"));

    var wrapped = "我需要先看一下文件。\n" + CallJson;
    var ok2 = OpenAiEmulation.TryParseToolCalls(wrapped, out var calls2);
    Check("带开场白也能解析", ok2 && calls2.Count == 1);

    var two = """{"tool_calls":[{"name":"a","arguments":{}},{"name":"b","arguments":{"x":1}}]}""";
    var ok3 = OpenAiEmulation.TryParseToolCalls(two, out var calls3);
    Check("多个工具调用", ok3 && calls3.Count == 2, ok3 ? string.Join(",", calls3.Select(c => c.Name)) : "未解析出");
}

Console.WriteLine("\n=== 2. 流式：开场白 + 工具调用（核心场景）===");
{
    var scanner = new ToolCallScanner();
    var text = "我这就去查一下。\n" + CallJson;
    var content = Feed(scanner, text);

    Check("工具调用被识别", scanner.HasToolCall && scanner.Calls.Count == 1,
        $"HasToolCall={scanner.HasToolCall}");
    Check("块内 JSON 没泄漏进正文", !content.Contains("tool_calls") && !content.Contains("read_file"),
        content.Replace("\n", "\\n"));
    Check("开场白照常流出", content.Contains("我这就去查一下"), content.Replace("\n", "\\n"));
}

Console.WriteLine("\n=== 3. 流式：普通 JSON 回答不能被误判、也不该被卡住 ===");
{
    var scanner = new ToolCallScanner();
    var text = """{"name":"张三","age":28,"city":"北京","hobbies":["阅读","编程","跑步"]}""";
    var content = Feed(scanner, text, 5);
    Check("未被误判成工具调用", !scanner.HasToolCall);
    Check("内容原样流出", content == text, content);

    // 探测窗是 64 字符，所以超过窗长的普通 JSON 会在窗口处就开始放行
    var longJson = "{\"data\":\"" + new string('x', 200) + "\"}";
    var scanner2 = new ToolCallScanner();
    var emittedEarly = "";
    for (var i = 0; i < 70 && i < longJson.Length; i++)
    {
        emittedEarly += scanner2.Push(longJson[i].ToString());
    }
    Check("长 JSON 在前 70 字符内就已开始输出（未整段缓冲）", emittedEarly.Length > 0,
        $"已输出 {emittedEarly.Length} 字符");
}

Console.WriteLine("\n=== 4. 流式：分片边界 ===");
{
    var scanner = new ToolCallScanner();
    var text = "先说明。" + CallJson + "结束";
    var output = "";
    foreach (var ch in text)
    {
        output += scanner.Push(ch.ToString());
    }
    output += scanner.Flush();

    Check("逐字符也能识别", scanner.HasToolCall, $"HasToolCall={scanner.HasToolCall}");
    Check("首尾正文完整", output == "先说明。结束", output);
}

Console.WriteLine("\n=== 5. 边界情况 ===");
{
    var scanner = new ToolCallScanner();
    var content = Feed(scanner, """{"tool_calls":[{"name":"f","arguments":{"a":1}}]""");   // 故意不闭合
    // 不完整的 JSON 无法解析，此时必须原样放行而不是吞掉
    Check("未闭合的 JSON 原样放行、不吞内容", !scanner.HasToolCall && content.Contains("tool_calls"), content);

    var scanner2 = new ToolCallScanner();
    var content2 = Feed(scanner2, """{"tool_calls":"这不是数组"}后面还有字""");
    Check("解析失败时不吞正文", !scanner2.HasToolCall && content2.Contains("后面还有字"), content2);

    var scanner3 = new ToolCallScanner();
    var content3 = Feed(scanner3, """{"note":"普通 JSON","tool_calls_note":"干扰"}""");
    Check("不含工具调用键的 JSON 原样放行", !scanner3.HasToolCall && content3.Contains("普通 JSON"), content3);
}

Console.WriteLine($"\n==> {passed} 通过 / {failed} 失败");
return failed == 0 ? 0 : 1;
