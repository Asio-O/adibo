// SPDX-License-Identifier: MIT
using System.Text;

namespace AdiboProxy.Endpoints;

/// <summary>
/// 流式下从模型输出里认出工具调用。
///
/// 协议是**不分块**的：模型可以正常写开场白，需要调工具时把那段 JSON 输出出来即可。
/// 难点在于「普通正文里也可能出现 <c>{</c>」（JSON 回答、代码块），所以判定分两步：
///
/// <list type="bullet">
/// <item>遇到 <c>{</c> 只是开始留意，**不立即缓冲**；</item>
/// <item>看紧跟其后的 64 个字符里有没有 <c>"tool_calls"</c> 这个键——工具协议的 JSON 一定
/// 在开头就带这个键，普通 JSON 几乎不可能有。没有就立刻放行，所以正常 JSON 回答照样逐字流式；</item>
/// <item>有就继续缓冲到大括号配平（跳过字符串与转义），解析出来是工具调用就转成标准
/// <c>tool_calls</c>，解析失败则原样当正文，绝不吞内容。</item>
/// </list>
/// </summary>
internal sealed class ToolCallScanner
{
    /// <summary>探测窗口：这么多字符内还没出现 tool_calls 键，就认定不是工具调用。</summary>
    private const int ProbeLength = 64;

    /// <summary>工具协议固定包含的键名。</summary>
    private const string ToolCallsKey = "\"tool_calls\"";

    /// <summary>候选 JSON 的长度上限，超了就放弃识别、原样当正文。</summary>
    private const int MaxCandidateLength = 262144;

    private readonly StringBuilder _candidate = new();
    private readonly List<OpenAiEmulation.ParsedToolCall> _calls = [];

    private bool _scanning;
    private bool _inString;
    private bool _escape;
    private int _depth;

    public IReadOnlyList<OpenAiEmulation.ParsedToolCall> Calls => _calls;

    public bool HasToolCall => _calls.Count > 0;

    /// <summary>看着像工具调用但解析失败、被原样放行的片段，用于日志诊断。</summary>
    public List<string> RejectedBlocks { get; } = [];

    /// <summary>投入一段正文，返回可以立刻作为 content 下发的文本（可能为空）。</summary>
    public string Push(string chunk)
    {
        var output = new StringBuilder();

        foreach (var ch in chunk)
        {
            if (!_scanning)
            {
                if (ch == '{')
                {
                    _scanning = true;
                    _candidate.Clear();
                    _candidate.Append(ch);
                    _depth = 1;
                    _inString = false;
                    _escape = false;
                }
                else
                {
                    output.Append(ch);
                }

                continue;
            }

            _candidate.Append(ch);

            if (_escape)
            {
                _escape = false;
            }
            else if (_inString)
            {
                if (ch == '\\')
                {
                    _escape = true;
                }
                else if (ch == '"')
                {
                    _inString = false;
                }
            }
            else if (ch == '"')
            {
                _inString = true;
            }
            else if (ch == '{')
            {
                _depth++;
            }
            else if (ch == '}')
            {
                _depth--;
                if (_depth == 0)
                {
                    ResolveCandidate(output);
                    continue;
                }
            }

            if (!_scanning)
            {
                continue;
            }

            // 探测窗一到就做一次判定：不是工具协议立刻放行，恢复流式。
            if (_candidate.Length == ProbeLength
                && !_candidate.ToString().Contains(ToolCallsKey, StringComparison.OrdinalIgnoreCase))
            {
                output.Append(_candidate);
                _candidate.Clear();
                _scanning = false;
                continue;
            }

            if (_candidate.Length > MaxCandidateLength)
            {
                output.Append(_candidate);
                _candidate.Clear();
                _scanning = false;
            }
        }

        return output.ToString();
    }

    /// <summary>流结束：把没消化完的候选按普通文本放行。</summary>
    public string Flush()
    {
        if (_candidate.Length == 0)
        {
            return string.Empty;
        }

        var text = _candidate.ToString();
        _candidate.Clear();
        _scanning = false;

        if (OpenAiEmulation.TryParseToolCalls(text, out var calls))
        {
            _calls.AddRange(calls);
            return string.Empty;
        }

        RejectedBlocks.Add(text);
        return text;
    }

    private void ResolveCandidate(StringBuilder output)
    {
        var json = _candidate.ToString();
        _candidate.Clear();
        _scanning = false;

        if (OpenAiEmulation.TryParseToolCalls(json, out var calls))
        {
            _calls.AddRange(calls);
            return;
        }

        // 只是普通的 JSON/代码，原样作为正文输出。
        RejectedBlocks.Add(json);
        output.Append(json);
    }
}
