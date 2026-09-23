// SPDX-License-Identifier: MIT
using System.Text;

namespace AdiboProxy.Logging;

/// <summary>
/// 一次请求的诊断轨迹：记录上游原始输出、推理/正文长度、think 标签是否闭合、
/// 是否识别出工具调用等。出问题时靠它定位。
/// </summary>
public sealed class UpstreamTrace(int rawLimit)
{
    private readonly StringBuilder _raw = new();

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    public string? Model { get; set; }

    public string? Mode { get; set; }

    public string? Streaming { get; set; }

    public bool ToolsEnabled { get; set; }

    /// <summary>本轮是否走了模型原生工具调用（false 表示用提示词注入）。</summary>
    public bool NativeTools { get; set; }

    public bool JsonMode { get; set; }

    public string? ReasoningMode { get; set; }

    public int Chunks { get; set; }

    public int ContentChars { get; set; }

    public int ReasoningChars { get; set; }

    public bool ThinkOpened { get; set; }

    public bool ThinkClosed { get; set; }

    public bool ToolCallDetected { get; set; }

    public string? FinishReason { get; set; }

    public string? Error { get; set; }

    /// <summary>上游输出是否被截断保存（超过上限时）。</summary>
    public bool RawTruncated { get; private set; }

    public void AppendRaw(string text)
    {
        var room = rawLimit - _raw.Length;
        if (room <= 0)
        {
            RawTruncated = true;
            return;
        }

        if (text.Length > room)
        {
            _raw.Append(text.AsSpan(0, room));
            RawTruncated = true;
            return;
        }

        _raw.Append(text);
    }

    public string RawText => _raw.ToString();

    /// <summary>一行摘要，方便 grep。</summary>
    public string Summary()
        => $"req={Id} model={Model} mode={Mode} streaming={Streaming} tools={ToolsEnabled} json={JsonMode} "
         + $"nativeTools={NativeTools} "
         + $"chunks={Chunks} upstream={_raw.Length}{(RawTruncated ? "+" : string.Empty)} "
         + $"content={ContentChars} reasoning={ReasoningChars} "
         + $"thinkOpen={ThinkOpened} thinkClose={ThinkClosed} toolCall={ToolCallDetected} finish={FinishReason}"
         + (Error is null ? string.Empty : $" error={Error}");
}
