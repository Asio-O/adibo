using System.Text;

namespace AdiboProxy.Endpoints;

/// <summary>
/// 流式剥离推理块。
///
/// 部分上游模型（如 DeepSeek V4）会在正文前输出 <c>&lt;think&gt;…&lt;/think&gt;</c> 推理过程，
/// 直接混在 content 里会破坏 JSON 解析、也让 OpenAI 客户端意外。
/// 这里按段切分并保留跨 chunk 的半个标签，做到既流式又不丢字符。
/// </summary>
public sealed class ReasoningStreamFilter
{
    public enum Kind
    {
        Content,
        Reasoning,
    }

    public readonly record struct Segment(Kind Kind, string Text);

    private static readonly string[] OpenTags = ["<think>", "<thinking>", "<reasoning>"];
    private static readonly string[] CloseTags = ["</think>", "</thinking>", "</reasoning>"];

    private readonly StringBuilder _pending = new();
    private bool _inside;

    /// <summary>是否见过 &lt;think&gt; 之类的开标签。</summary>
    public bool SawOpenTag { get; private set; }

    /// <summary>是否见过对应的闭标签。只开不闭说明模型输出被截断了。</summary>
    public bool SawCloseTag { get; private set; }

    /// <summary>流结束时是否仍停在推理块内部。</summary>
    public bool EndsInsideReasoning => _inside;

    public IReadOnlyList<Segment> Push(string chunk)
    {
        if (chunk.Length > 0)
        {
            _pending.Append(chunk);
        }

        return Drain();
    }

    /// <summary>流结束时把残留吐出来（未闭合的推理块按推理处理）。</summary>
    public IReadOnlyList<Segment> Flush()
    {
        var segments = Drain();
        if (_pending.Length > 0)
        {
            segments.Add(new Segment(_inside ? Kind.Reasoning : Kind.Content, _pending.ToString()));
            _pending.Clear();
        }

        return segments;
    }

    private List<Segment> Drain()
    {
        var output = new List<Segment>();

        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            var tags = _inside ? CloseTags : OpenTags;
            var index = IndexOfAny(text, tags);

            if (index >= 0)
            {
                var tag = MatchedTag(text, index, tags);
                if (index > 0)
                {
                    output.Add(new Segment(_inside ? Kind.Reasoning : Kind.Content, text[..index]));
                }

                _pending.Remove(0, index + tag.Length);
                _inside = !_inside;
                if (_inside)
                {
                    SawOpenTag = true;
                }
                else
                {
                    SawCloseTag = true;
                }

                continue;
            }

            // 没有完整标签：留一段可能是半个标签的尾巴，其余立刻吐出去。
            var keep = PartialSuffixLength(text, tags);
            var emit = text.Length - keep;
            if (emit <= 0)
            {
                break;
            }

            output.Add(new Segment(_inside ? Kind.Reasoning : Kind.Content, text[..emit]));
            _pending.Remove(0, emit);
            break;
        }

        return output;
    }

    private static int IndexOfAny(string text, string[] tags)
    {
        var best = -1;
        foreach (var tag in tags)
        {
            var index = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }

        return best;
    }

    private static string MatchedTag(string text, int index, string[] tags)
    {
        foreach (var tag in tags)
        {
            if (index + tag.Length <= text.Length
                && text.AsSpan(index, tag.Length).Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return string.Empty;
    }

    /// <summary>返回 text 末尾有多少字符可能是某个标签的开头，需要留在缓冲区。</summary>
    private static int PartialSuffixLength(string text, string[] tags)
    {
        var max = Math.Min(text.Length, tags.Max(t => t.Length) - 1);
        for (var length = max; length > 0; length--)
        {
            var suffix = text[^length..];
            if (tags.Any(tag => tag.StartsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return length;
            }
        }

        return 0;
    }
}
