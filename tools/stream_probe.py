#!/usr/bin/env python3
"""验证代理是否真流式：记录每个 chunk 的到达时间，看内容是随生成过程逐步到达，还是一次性到达。

    python tools/stream_probe.py
    python tools/stream_probe.py --base http://localhost:5080 --model deepseek-v4-flash
"""

from __future__ import annotations

import argparse
import codecs
import json
import time
import urllib.request

PROMPT = "请用大约 300 字介绍杭州这座城市，分段写，不要用列表。"


WEATHER_TOOL = {
    "type": "function",
    "function": {
        "name": "get_weather",
        "description": "查询指定城市的当前天气",
        "parameters": {
            "type": "object",
            "properties": {"city": {"type": "string"}},
            "required": ["city"],
        },
    },
}


def stream_openai(base: str, model: str, prompt: str, tools: bool = False):
    """走 /v1/chat/completions 的 SSE，返回 (内容分片, 推理分片, 正文)。"""
    body = {"model": model, "stream": True, "messages": [{"role": "user", "content": prompt}]}
    if tools:
        body["tools"] = [WEATHER_TOOL]
        body["tool_choice"] = "auto"
    payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        f"{base}/v1/chat/completions",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    content_marks: list[tuple[float, str]] = []
    reasoning_marks: list[tuple[float, str]] = []
    text: list[str] = []
    start = time.monotonic()

    with urllib.request.urlopen(request, timeout=300) as response:
        for raw_line in response:
            line = raw_line.decode("utf-8", errors="replace").strip()
            if not line.startswith("data:"):
                continue
            body = line[5:].strip()
            if body == "[DONE]":
                break
            try:
                chunk = json.loads(body)
            except json.JSONDecodeError:
                continue
            choices = chunk.get("choices") or []
            if not choices:
                continue
            delta = choices[0].get("delta") or {}
            piece = delta.get("content")
            if piece:
                content_marks.append((time.monotonic() - start, piece))
                text.append(piece)
            think = delta.get("reasoning_content")
            if think:
                reasoning_marks.append((time.monotonic() - start, think))

    return content_marks, reasoning_marks, "".join(text)


def stream_raw(base: str, prompt: str):
    """走 /rcouyi/chat 的纯文本流，按读取块记录时间。"""
    payload = json.dumps({"type": 1, "topicId": 0, "content": prompt, "messages": []}, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        f"{base}/rcouyi/chat",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    marks: list[tuple[float, str]] = []
    start = time.monotonic()
    # 逐字节读取时必须用增量解码器，否则多字节 UTF-8 会被切碎成乱码。
    decoder = codecs.getincrementaldecoder("utf-8")("replace")
    with urllib.request.urlopen(request, timeout=300) as response:
        while True:
            block = response.read(1)
            if not block:
                break
            piece = decoder.decode(block)
            if piece:
                marks.append((time.monotonic() - start, piece))

    return marks, "".join(piece for _, piece in marks)


def non_stream(base: str, model: str, prompt: str):
    payload = json.dumps(
        {"model": model, "stream": False, "messages": [{"role": "user", "content": prompt}]},
        ensure_ascii=False,
    ).encode("utf-8")
    request = urllib.request.Request(
        f"{base}/v1/chat/completions",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    start = time.monotonic()
    with urllib.request.urlopen(request, timeout=300) as response:
        body = response.read().decode("utf-8", errors="replace")
    elapsed = time.monotonic() - start
    content = json.loads(body)["choices"][0]["message"].get("content") or ""
    return elapsed, len(content)


def report(title: str, marks: list[tuple[float, str]], text: str) -> None:
    if not marks:
        print(f"{title}: 没有收到任何数据")
        return

    first = marks[0][0]
    last = marks[-1][0]
    span = last - first
    gaps = [b[0] - a[0] for a, b in zip(marks, marks[1:])]

    print(f"{title}")
    print(f"  分片数量      : {len(marks)}")
    print(f"  首片到达      : {first * 1000:.0f} ms")
    print(f"  末片到达      : {last * 1000:.0f} ms")
    print(f"  首末跨度      : {span * 1000:.0f} ms")
    print(f"  正文长度      : {len(text)} 字符")
    if gaps:
        print(f"  片间间隔      : 最小 {min(gaps) * 1000:.0f} ms / 中位 {sorted(gaps)[len(gaps) // 2] * 1000:.0f} ms / 最大 {max(gaps) * 1000:.0f} ms")
    verdict = "真流式（内容随生成逐步到达）" if span > 0.5 else "疑似被缓冲（分片几乎同时到达）"
    print(f"  判定          : {verdict}")
    print(f"  正文开头      : {text[:60]}…")
    print()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://localhost:5080")
    parser.add_argument("--model", default="deepseek-v4-flash")
    parser.add_argument("--tools", action="store_true", help="带上 tools 再测一次，验证是否被缓冲")
    args = parser.parse_args()

    print(f"==> 目标 {args.base}  模型 {args.model}\n")

    marks, reasoning, text = stream_openai(args.base, args.model, PROMPT)
    report("【OpenAI 兼容】POST /v1/chat/completions  stream=true", marks, text)
    if reasoning:
        print(f"  （另有 {len(reasoning)} 个 reasoning_content 分片，已分流到独立字段）\n")

    if args.tools:
        marks, reasoning, text = stream_openai(args.base, args.model, PROMPT, tools=True)
        report("【带 tools】stream=true + tools=[get_weather]", marks, text)

    marks, text = stream_raw(args.base, PROMPT)
    report("【原生透传】POST /rcouyi/chat", marks, text)

    elapsed, length = non_stream(args.base, args.model, PROMPT)
    print("【对照】stream=false")
    print(f"  一次性返回，耗时 {elapsed * 1000:.0f} ms，正文 {length} 字符")
    print("  （总耗时和流式接近，区别是内容要等全部生成完才拿到）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
