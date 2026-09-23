#!/usr/bin/env python3
"""用官方 OpenAI SDK 跑代理的冒烟测试：JSON Output 与 Tool Calls。

上游本身既不认 response_format 也不认 tools，这两项由代理侧模拟，
所以这里用真实 SDK 验证「客户端视角」是否成立。

    pip install openai
    python tools/smoke_test.py                     # 默认 http://localhost:5080/v1
    python tools/smoke_test.py --base-url http://localhost:5080/v1
"""

from __future__ import annotations

import argparse
import json
import sys

from openai import OpenAI

WEATHER_TOOL = {
    "type": "function",
    "function": {
        "name": "get_weather",
        "description": "查询指定城市的当前天气（实时数据）",
        "parameters": {
            "type": "object",
            "properties": {"city": {"type": "string", "description": "城市名"}},
            "required": ["city"],
        },
    },
}

# 假的工具后端：真实场景里换成你自己的实现即可。
FAKE_WEATHER = {"city": "北京", "temp_c": 3, "condition": "晴", "wind": "西北风3级", "humidity": 28}

results: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    results.append((name, ok, detail))
    print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f" — {detail}" if detail else ""))


def test_json_object(client: OpenAI, model: str) -> None:
    response = client.chat.completions.create(
        model=model,
        response_format={"type": "json_object"},
        messages=[{"role": "user", "content": "生成一个人的信息，字段 name、age、city"}],
    )
    content = response.choices[0].message.content or ""
    try:
        parsed = json.loads(content)
    except json.JSONDecodeError as exc:
        check("JSON Output / json_object", False, f"内容不是合法 JSON: {exc}; 原文={content[:120]}")
        return

    missing = {"name", "age", "city"} - parsed.keys()
    check("JSON Output / json_object", not missing, f"字段缺失={sorted(missing)}" if missing else f"keys={sorted(parsed)}")


def test_json_schema(client: OpenAI, model: str) -> None:
    response = client.chat.completions.create(
        model=model,
        response_format={
            "type": "json_schema",
            "json_schema": {
                "name": "person",
                "schema": {
                    "type": "object",
                    "properties": {
                        "name": {"type": "string"},
                        "age": {"type": "integer"},
                        "hobbies": {"type": "array", "items": {"type": "string"}},
                    },
                    "required": ["name", "age", "hobbies"],
                },
            },
        },
        messages=[{"role": "user", "content": "随便编一个人，名字叫李雷"}],
    )
    content = response.choices[0].message.content or ""
    try:
        parsed = json.loads(content)
    except json.JSONDecodeError as exc:
        check("JSON Output / json_schema", False, f"内容不是合法 JSON: {exc}; 原文={content[:120]}")
        return

    ok = (
        isinstance(parsed.get("name"), str)
        and isinstance(parsed.get("age"), int)
        and isinstance(parsed.get("hobbies"), list)
    )
    check("JSON Output / json_schema", ok, f"类型校验 {'通过' if ok else '失败'}: {parsed}")


def test_tool_call(client: OpenAI, model: str) -> None:
    response = client.chat.completions.create(
        model=model,
        tools=[WEATHER_TOOL],
        tool_choice="auto",
        messages=[{"role": "user", "content": "北京现在天气怎么样？"}],
    )
    choice = response.choices[0]
    calls = choice.message.tool_calls or []

    if not calls:
        check("Tool Calls / 触发调用", False, f"finish_reason={choice.finish_reason}，未返回 tool_calls")
        return

    call = calls[0]
    try:
        arguments = json.loads(call.function.arguments)
    except json.JSONDecodeError as exc:
        check("Tool Calls / 触发调用", False, f"arguments 不是合法 JSON: {exc}")
        return

    ok = call.function.name == "get_weather" and arguments.get("city") == "北京"
    check("Tool Calls / 触发调用", ok, f"name={call.function.name} args={arguments} finish_reason={choice.finish_reason}")


def test_tool_loop(client: OpenAI, model: str) -> None:
    """完整回合：模型要工具 → 客户端执行 → 结果回传 → 模型作答。"""
    messages = [{"role": "user", "content": "北京现在天气怎么样？"}]

    first = client.chat.completions.create(model=model, tools=[WEATHER_TOOL], messages=messages)
    choice = first.choices[0]
    if not choice.message.tool_calls:
        check("Tool Calls / 完整回合", False, "第一轮没有触发工具调用")
        return

    messages.append(choice.message)
    for call in choice.message.tool_calls:
        messages.append(
            {
                "role": "tool",
                "tool_call_id": call.id,
                "name": call.function.name,
                "content": json.dumps(FAKE_WEATHER, ensure_ascii=False),
            }
        )

    second = client.chat.completions.create(model=model, tools=[WEATHER_TOOL], messages=messages)
    answer = second.choices[0].message.content or ""

    used = all(token in answer for token in ("3", "晴"))
    check("Tool Calls / 完整回合", used, f"最终回答={'用了工具数据' if used else '没体现工具数据'}: {answer[:80]}")


def test_tool_call_stream(client: OpenAI, model: str) -> None:
    stream = client.chat.completions.create(
        model=model,
        tools=[WEATHER_TOOL],
        tool_choice="required",
        stream=True,
        messages=[{"role": "user", "content": "帮我看看上海天气"}],
    )

    names: list[str] = []
    finish: str | None = None
    for chunk in stream:
        if not chunk.choices:
            continue
        delta = chunk.choices[0].delta
        for call in delta.tool_calls or []:
            if call.function and call.function.name:
                names.append(call.function.name)
        if chunk.choices[0].finish_reason:
            finish = chunk.choices[0].finish_reason

    check(
        "Tool Calls / 流式",
        bool(names) and finish == "tool_calls",
        f"names={names} finish_reason={finish}",
    )


def test_tool_call_after_preamble(client: OpenAI, model: str) -> None:
    """模型先写一句说明、再给工具调用 JSON —— 这种写法也必须被识别成工具调用。

    这是真实客户端最容易踩的坑：JSON 不是输出的第一个字符，
    如果只靠"首个字符是不是 {"判定，整段 JSON 会当成正文漏出去。
    """
    response = client.chat.completions.create(
        model=model,
        tools=[WEATHER_TOOL],
        tool_choice="auto",
        messages=[
            {
                "role": "user",
                "content": "先用一句话说明你接下来要做什么，然后调用 get_weather 查北京天气。",
            }
        ],
    )
    message = response.choices[0].message
    calls = message.tool_calls or []
    content = message.content or ""

    if not calls:
        check("Tool Calls / 前置说明后调用", False, f"未识别为工具调用；content={content[:90]}")
        return

    leaked = "tool_calls" in content and "{" in content
    check(
        "Tool Calls / 前置说明后调用",
        not leaked,
        f"name={calls[0].function.name} args={calls[0].function.arguments}；正文={'未泄漏JSON' if not leaked else '泄漏了JSON'}",
    )


def test_plain_json_despite_tools(client: OpenAI, model: str) -> None:
    """声明了 tools，但用户要的就是一段普通 JSON —— 不能被误判成工具调用。

    这也是"不能只看 {"的正面例子：判定依据必须是内容里真的解析出 tool_calls。
    """
    response = client.chat.completions.create(
        model=model,
        tools=[WEATHER_TOOL],
        tool_choice="auto",
        response_format={"type": "json_object"},
        messages=[
            {
                "role": "user",
                "content": "不要调用任何工具。直接输出一个 JSON 对象，字段：city、population、area。",
            }
        ],
    )
    message = response.choices[0].message
    calls = message.tool_calls or []
    content = (message.content or "").strip()

    if calls:
        check("Tool Calls / 普通JSON不误判", False, f"被误判成工具调用：{calls[0].function.name}")
        return

    # 关键断言是「没有被误判成工具调用」+「内容完整透传」。
    # 模型偶尔会输出非法 JSON（例如数值带中文单位且不加引号），那是模型的问题，
    # 与代理无关，所以只作为提示信息，不参与判定。
    intact = all(key in content for key in ("city", "population", "area"))
    try:
        json.loads(content)
        note = "且为合法 JSON"
    except json.JSONDecodeError:
        note = "（模型输出的是非法 JSON，但代理未误判、内容完整透传）"

    check("Tool Calls / 普通JSON不误判", intact, f"content={content[:80]}；{note}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default="http://localhost:5080/v1")
    parser.add_argument("--model", default="ouyi-chat")
    args = parser.parse_args()

    client = OpenAI(base_url=args.base_url, api_key="not-used", timeout=180)

    print(f"==> 目标 {args.base_url}，模型 {args.model}\n")

    test_json_object(client, args.model)
    test_json_schema(client, args.model)
    test_tool_call(client, args.model)
    test_tool_loop(client, args.model)
    test_tool_call_stream(client, args.model)
    test_tool_call_after_preamble(client, args.model)
    test_plain_json_despite_tools(client, args.model)

    failed = [name for name, ok, _ in results if not ok]
    print(f"\n==> {len(results) - len(failed)}/{len(results)} 通过")
    if failed:
        print("失败项: " + ", ".join(failed))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
