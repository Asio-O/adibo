#!/usr/bin/env python3
"""rcouyi 全模型测评：分项能力 + 上下文长度 + 最大输出。

通过本地代理跑（代理负责鉴权、模型路由、推理块剥离），结果写成 Markdown 报告。

    python tools/model_bench.py --phase capability     # 只跑分项能力
    python tools/model_bench.py --phase limits         # 只跑上下文 / 输出上限
    python tools/model_bench.py --models ouyi-chat,gpt-4o

产物：docs/model-capability.md 与 docs/model-capability.json
"""

from __future__ import annotations

import argparse
import json
import re
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT_MD = ROOT / "docs" / "model-capability.md"
OUT_JSON = ROOT / "docs" / "model-capability.json"

DEFAULT_BASE = "http://localhost:5080"

# 每个模型家族只保留最新一代，跳过带日期/旧版本的变体。
LATEST_MODELS = [
    "ouyi-chat",                          # 站点自定义默认
    "gpt-5-nano",                         # OpenAI
    "claude-3-7-sonnet-20250219-vip",     # Anthropic
    "gemini-3.1-flash-lite-preview",      # Google
    "deepseek-v4-flash",                  # DeepSeek
    "grok-3",                             # xAI
    "glm-4.6",                            # 智谱
    "qwen3-8b",                           # 阿里通义
    "doubao-seed-2-0-lite-260428",        # 字节豆包
    "yi-lightning",                       # 零一万物
    "stepfun-ai/step-3.7-flash",          # 阶跃星辰
    "generalv3.5",                        # 讯飞星火
]


def _first_number(text: str):
    numbers = re.findall(r"\d+(?:\.\d+)?", text)
    if not numbers:
        return None
    try:
        return int(float(numbers[0]))
    except ValueError:
        return None


def grade_number(expected: int):
    def grader(text: str):
        got = _first_number(text)
        return got == expected, f"答 {got}（应 {expected}）"

    return grader


def grade_bigger(text: str):
    # 正确答案是 9.9；答案里出现 9.11 就算错。
    if "9.11" in text:
        return False, "答 9.11（应 9.9）"
    return ("9.9" in text), ("答 9.9" if "9.9" in text else "未明确")


def grade_reverse(text: str):
    ok = "fedcba" in text.lower()
    return ok, ("fedcba" if ok else "不是 fedcba")


def grade_chicken_rabbit(text: str):
    ok = re.search(r"鸡\D{0,6}23", text) is not None and re.search(r"兔\D{0,6}12", text) is not None
    return ok, ("鸡23 / 兔12" if ok else "未同时给出 23 与 12")


def grade_json_array(text: str):
    cleaned = re.sub(r"```(?:json)?|```", "", text).strip()
    match = re.search(r"\[[^\[\]]*\]", cleaned)
    if not match:
        return False, "没有 JSON 数组"
    try:
        data = json.loads(match.group(0))
    except json.JSONDecodeError:
        return False, "数组不是合法 JSON"
    ok = (
        isinstance(data, list)
        and len(data) == 3
        and all(isinstance(x, int) and 1 <= x <= 5 for x in data)
        and sum(data) == 10
    )
    return ok, f"{data}（需 3 个 1-5 整数、和=10）"


CAPABILITY_PROBES = [
    {
        "key": "字母计数",
        "prompt": "strawberry 里有几个字母 r？只回答一个数字。",
        "grader": grade_number(3),
        "tip": "3",
    },
    {
        "key": "汉字计数",
        "prompt": "请数一下这句话有几个汉字：「今天天气真好我们一起去公园散步吧」，只回答数字。",
        "grader": grade_number(16),
        "tip": "16",
    },
    {
        "key": "多步算术",
        "prompt": "小明有12个苹果，给了小红一半，又买了8个，然后吃掉3个。请问他现在有几个苹果？只回答数字。",
        "grader": grade_number(11),
        "tip": "12/2+8-3=11",
    },
    {
        "key": "精确乘法",
        "prompt": "123 × 456 等于多少？只回答数字。",
        "grader": grade_number(56088),
        "tip": "56088",
    },
    {
        "key": "小数比较",
        "prompt": "9.9 和 9.11 哪个更大？只回答哪个更大。",
        "grader": grade_bigger,
        "tip": "9.9",
    },
    {
        "key": "字符反转",
        "prompt": "把字符串 abcdef 倒过来写，只输出结果。",
        "grader": grade_reverse,
        "tip": "fedcba",
    },
    {
        "key": "逻辑推理",
        "prompt": "鸡兔同笼，共 35 个头、94 只脚，请问鸡和兔各几只？只用一句话给出结果。",
        "grader": grade_chicken_rabbit,
        "tip": "鸡23 兔12",
    },
    {
        "key": "指令遵循",
        "prompt": "只输出一个 JSON 数组，恰好 3 个整数元素，每个在 1 到 5 之间，总和为 10。不要任何其他文字。",
        "grader": grade_json_array,
        "tip": "3 个 1-5 整数且和为 10",
    },
]


CONTEXT_SIZES = [8000, 32000, 128000, 256000]
HEAD_NEEDLE = "ZQ7K"
TAIL_NEEDLE = "M4XP"
FILLER = (
    "在软件开发过程中，保持代码的清晰与可维护性通常比追求短期的开发速度更重要。"
    "团队应当建立一致的命名约定、完善的测试覆盖以及可追溯的变更记录。"
)


def call(base: str, model: str, prompt: str, timeout: float):
    """返回 (文本, 耗时秒, 错误)。错误非空即本次失败。"""
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

    started = time.monotonic()
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[:200]
        return "", time.monotonic() - started, f"HTTP {exc.code}: {detail}"
    except Exception as exc:
        return "", time.monotonic() - started, f"{type(exc).__name__}: {exc}"

    elapsed = time.monotonic() - started
    try:
        parsed = json.loads(body)
    except json.JSONDecodeError:
        return "", elapsed, f"非 JSON 响应: {body[:160]}"

    if "error" in parsed:
        return "", elapsed, str(parsed["error"])[:200]

    choices = parsed.get("choices") or []
    if not choices:
        return "", elapsed, f"无 choices: {body[:160]}"

    return choices[0].get("message", {}).get("content") or "", elapsed, None


def call_with_retry(base: str, model: str, prompt: str, timeout: float, attempts: int = 3):
    last = ("", 0.0, "unknown")
    for attempt in range(attempts):
        text, elapsed, error = call(base, model, prompt, timeout)
        if error is None:
            return text, elapsed, None
        last = (text, elapsed, error)
        if not re.search(r"Lock|TooManyRequests|500|502|timed out|超时", error, re.I):
            break
        time.sleep(1.5 * (attempt + 1))
    return last


def build_context_prompt(size: int) -> str:
    head = f"下面是一段很长的资料，开头和结尾各有一个四字符暗号。\n\n【开头暗号】{HEAD_NEEDLE}\n\n"
    tail = (
        f"\n\n【结尾暗号】{TAIL_NEEDLE}\n\n"
        "问题：请回答资料里的两个暗号，格式必须是「开头=XXXX 结尾=YYYY」。"
    )
    body_len = max(0, size - len(head) - len(tail))
    body = (FILLER * (body_len // len(FILLER) + 1))[:body_len]
    return head + body + tail


def probe_context(base: str, model: str, timeout: float):
    """阶梯式逼近可接受的最大输入长度（字符）。

    首尾各埋一个暗号：结尾能看到说明这份输入确实被接受了；
    此时开头是否还在，则反映站点是否把内容按窗口截断。
    返回 (最大可用输入, 该尺寸下开头是否可见, 首次失败尺寸)。
    """
    best = 0
    head_visible_at_best = None
    first_failure = None

    for size in CONTEXT_SIZES:
        text, _, error = call_with_retry(base, model, build_context_prompt(size), timeout, attempts=2)
        if error is not None or TAIL_NEEDLE not in text:
            first_failure = size
            break
        best = size
        head_visible_at_best = HEAD_NEEDLE in text

    return best, head_visible_at_best, first_failure


OUTPUT_TARGET = 5000


def probe_output(base: str, model: str, timeout: float):
    """让模型输出一长串数字，测实际产出长度（下界）。

    选计数任务而不是「重复某句话」：后者模型经常敷衍两句就收尾，测不出上限。
    返回 (字符数, 最后数到的数字, 错误)。
    """
    text, _, error = call_with_retry(
        base,
        model,
        f"请从 1 开始依次输出每个整数，每个数字单独一行，一直输出到 {OUTPUT_TARGET} 为止。"
        "不要省略任何数字，不要加任何解释或总结。",
        timeout,
        attempts=2,
    )
    if error is not None:
        return 0, None, error

    numbers = re.findall(r"(?m)^\s*(\d+)\s*$", text)
    last = max((int(n) for n in numbers), default=None)
    return len(text), last, None


def measure_limits(base: str, model: str, timeout: float):
    context, head_visible, first_failure = probe_context(base, model, timeout)
    output, last_number, error = probe_output(base, model, timeout)
    if error:
        return context, 0, f"输出测试失败: {error}"

    if first_failure:
        notes = f"输入 ≥{first_failure} 字符即失败"
    else:
        notes = "输入阶梯最高档通过"
    if head_visible is True:
        notes += "；该长度下开头仍可见"
    elif head_visible is False:
        notes += "；该长度下开头已被截断（只保留尾部窗口）"
    if last_number is not None:
        notes += f"；输出数到 {last_number}"
    return context, output, notes


def load_models(base: str):
    with urllib.request.urlopen(f"{base}/rcouyi/models", timeout=60) as response:
        items = json.loads(response.read().decode("utf-8"))
    return [
        {
            "id": item["value"],
            "label": item.get("label") or item["value"],
            "enabled": item.get("enable", True),
            "declared_context": item.get("maxContextToken"),
            "declared_output": item.get("maxResponseToken"),
            "declared_max": item.get("maxTokens"),
        }
        for item in items
    ]


def bench_capability(base: str, model_id: str, timeout: float) -> dict:
    checks = {}
    latencies = []
    first_error = None

    for probe in CAPABILITY_PROBES:
        text, elapsed, error = call_with_retry(base, model_id, probe["prompt"], timeout)
        if error is not None:
            checks[probe["key"]] = {"pass": False, "note": error, "answer": "", "ms": round(elapsed * 1000)}
            first_error = first_error or error
            continue
        passed, note = probe["grader"](text)
        checks[probe["key"]] = {
            "pass": passed,
            "note": note,
            "answer": text.strip()[:200],
            "ms": round(elapsed * 1000),
        }
        latencies.append(elapsed)

    return {
        "model": model_id,
        "score": sum(1 for c in checks.values() if c["pass"]),
        "total": len(CAPABILITY_PROBES),
        "checks": checks,
        "avg_ms": round(sum(latencies) / len(latencies) * 1000) if latencies else None,
        "first_error": first_error,
    }


def write_report(results: list[dict], args) -> None:
    OUT_MD.parent.mkdir(parents=True, exist_ok=True)
    OUT_JSON.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")

    scored = [r for r in results if r.get("score") is not None]
    scored.sort(key=lambda r: (-(r.get("score") or 0), r.get("avg_ms") or 10**9))
    probe_names = [p["key"] for p in CAPABILITY_PROBES]

    lines = [
        "# rcouyi 模型能力测评",
        "",
        f"- 生成时间：{datetime.now():%Y-%m-%d %H:%M:%S}",
        f"- 入口：`{args.base}/v1/chat/completions`（经本地代理）",
        f"- 参测模型：{len(results)} 个",
        "- 评分口径：8 道客观题，答对得 1 分，答错或调用失败计 0 分",
        "",
    ]

    if scored:
        top = scored[0]
        lines += [
            "## 结论：最强的模型",
            "",
            f"**`{top['model']}`**（{top.get('label', '')}）以 **{top['score']}/{top['total']}** 位列第一，"
            f"平均响应 {top.get('avg_ms')} ms。",
            "",
        ]
        perfect = [r for r in scored if r["score"] == r["total"]]
        if perfect:
            lines.append("满分模型：" + "、".join(f"`{r['model']}`" for r in perfect))
            lines.append("")

    lines += [
        "## 分项能力总表",
        "",
        "| # | 模型 | 总分 | " + " | ".join(probe_names) + " | 平均耗时(ms) |",
        "|---|---|---|" + "---|" * (len(probe_names) + 1),
    ]
    for index, row in enumerate(scored, 1):
        marks = []
        for key in probe_names:
            check = (row.get("checks") or {}).get(key)
            marks.append("✅" if check and check.get("pass") else "❌")
        avg = row.get("avg_ms")
        lines.append(
            f"| {index} | `{row['model']}` | {row['score']}/{row['total']} | "
            + " | ".join(marks)
            + f" | {avg if avg is not None else '-'} |"
        )

    lines += [
        "",
        "## 上下文 / 输出上限",
        "",
        "> `实测最大输入`：在超长铺垫的首尾各埋一个暗号，阶梯加大长度，以「结尾暗号能否被正确回忆」",
        "> 判定这份输入是否真的被接受；同时记录该长度下「开头暗号」是否还在，用来区分"
        "「输入被拒绝」和「只保留尾部窗口」。",
        "> `实测最长输出`：让模型从 1 连续数到 5000（每行一个），取实际返回字符数；",
        "> 模型可能中途收尾，所以这是下界。`-` 表示该项未能测出。",
        "",
        "| 模型 | 实测最大输入(字符) | 实测最长输出(字符) | 标称上下文 | 标称输出 | 说明 |",
        "|---|---|---|---|---|---|",
    ]
    for row in scored:
        if row.get("measured_context") is None and row.get("measured_output") is None:
            continue
        lines.append(
            f"| `{row['model']}` | {row.get('measured_context') or '-'} | {row.get('measured_output') or '-'} | "
            f"{row.get('declared_context') or '-'} | {row.get('declared_output') or '-'} | {row.get('limit_notes', '')} |"
        )

    routed = [r for r in scored if r.get("model") != "ouyi-chat" and (r.get("measured_context") or 0) > 0]
    stateless = next((r for r in scored if r.get("model") == "ouyi-chat"), None)
    if routed:
        ceiling = max(r["measured_context"] for r in routed)
        lines += [
            "",
            "**观察**：走「会话路由」的模型（除默认模型外的全部）输入上限都被卡在同一档 "
            f"≈{ceiling} 字符，与各自标称的上下文长度无关；",
            (
                f"而走「无状态接口」的 `ouyi-chat` 能到 {stateless['measured_context']} 字符。"
                "说明这个天花板来自站点的会话侧截断，不是各家模型自身的能力差异。"
            )
            if stateless and stateless.get("measured_context")
            else "",
            "",
        ]

    unavailable = [r for r in scored if not any((r.get("checks") or {}).values())]
    if unavailable:
        lines += ["", "## 完全不可用的模型", ""]
        for row in unavailable:
            lines.append(f"- `{row['model']}`：{row.get('first_error') or '全部题目失败'}")

    lines += ["", "## 明细：每个模型的原始回答", ""]
    for row in scored:
        lines += [
            f"### {row['model']} — {row['score']}/{row['total']}",
            "",
            "| 题目 | 结果 | 说明 | 原始回答 |",
            "|---|---|---|---|",
        ]
        for probe in CAPABILITY_PROBES:
            check = (row.get("checks") or {}).get(probe["key"], {})
            answer = (check.get("answer") or "").replace("|", "\\|").replace("\n", " ")[:80]
            note = (check.get("note") or "").replace("|", "\\|")[:60]
            lines.append(f"| {probe['key']} | {'✅' if check.get('pass') else '❌'} | {note} | {answer} |")
        lines.append("")

    OUT_MD.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"[bench] 报告写入 {OUT_MD}", flush=True)
    print(f"[bench] 原始数据 {OUT_JSON}", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default=DEFAULT_BASE)
    parser.add_argument("--phase", choices=["capability", "limits", "all"], default="all")
    parser.add_argument("--models", default="", help="逗号分隔的模型 id，默认全部启用的")
    parser.add_argument("--latest", action="store_true", help="只测每个家族的最新版")
    parser.add_argument("--timeout", type=float, default=120.0)
    parser.add_argument("--workers", type=int, default=3)
    parser.add_argument("--merge", action="store_true", help="与已有 JSON 结果合并后再出报告")
    parser.add_argument("--report-only", action="store_true", help="不跑测试，只用已有 JSON 重新生成报告")
    args = parser.parse_args()

    catalogue = load_models(args.base)
    enabled = [m for m in catalogue if m["enabled"]]
    if args.latest:
        known = {m["id"] for m in catalogue}
        missing = [m for m in LATEST_MODELS if m not in known]
        if missing:
            print(f"[bench] 警告：目录里没有这些模型 {missing}", flush=True)
        targets = [m for m in catalogue if m["id"] in set(LATEST_MODELS)]
    elif args.models:
        wanted = {m.strip() for m in args.models.split(",") if m.strip()}
        targets = [m for m in catalogue if m["id"] in wanted]
    else:
        targets = enabled

    print(f"[bench] 目录 {len(catalogue)}，启用 {len(enabled)}，本次测 {len(targets)}", flush=True)
    print(f"[bench] phase={args.phase} workers={args.workers}", flush=True)

    results: list[dict] = []
    by_id: dict[str, dict] = {}

    if args.report_only:
        rows = json.loads(OUT_JSON.read_text(encoding="utf-8"))
        write_report(rows, args)
        return 0

    if args.merge and OUT_JSON.exists():
        try:
            for row in json.loads(OUT_JSON.read_text(encoding="utf-8")):
                results.append(row)
                by_id[row["model"]] = row
            print(f"[bench] 已载入 {len(results)} 条历史结果用于合并", flush=True)
        except (json.JSONDecodeError, KeyError) as exc:
            print(f"[bench] 历史结果读取失败，忽略: {exc}", flush=True)

    def upsert(model_id: str, item: dict, updater) -> dict:
        """合并时不要把「只跑了限速」的空能力结果覆盖掉已有分数。"""
        row = by_id.get(model_id)
        if row is None:
            row = {"model": model_id, "score": None, "total": len(CAPABILITY_PROBES), "checks": {}}
            row.update({k: item.get(k) for k in ("label", "declared_context", "declared_output", "declared_max")})
            results.append(row)
            by_id[model_id] = row
        updater(row)
        return row

    if args.phase in ("capability", "all"):
        print("[bench] === 分项能力 ===", flush=True)
        with ThreadPoolExecutor(max_workers=args.workers) as pool:
            futures = {pool.submit(bench_capability, args.base, m["id"], args.timeout): m for m in targets}
            for done, future in enumerate(as_completed(futures), 1):
                item = futures[future]
                try:
                    row = future.result()
                except Exception as exc:
                    row = {"model": item["id"], "score": 0, "total": len(CAPABILITY_PROBES),
                           "checks": {}, "avg_ms": None, "first_error": f"{type(exc).__name__}: {exc}"}
                row.update({k: item[k] for k in ("label", "declared_context", "declared_output", "declared_max")})
                row = upsert(item["id"], item, lambda target, new=row: target.update(new))
                note = f"  ({row['first_error'][:60]})" if row.get("first_error") else ""
                print(f"[bench] ({done}/{len(targets)}) {item['id']:<34} {row['score']}/{row['total']}{note}", flush=True)

    if args.phase in ("limits", "all"):
        print(f"[bench] === 上下文 / 输出上限（{len(targets)} 个）===", flush=True)
        with ThreadPoolExecutor(max_workers=args.workers) as pool:
            futures = {pool.submit(measure_limits, args.base, m["id"], args.timeout): m for m in targets}
            for done, future in enumerate(as_completed(futures), 1):
                item = futures[future]
                try:
                    context, output, notes = future.result()
                except Exception as exc:
                    context, output, notes = 0, 0, f"{type(exc).__name__}: {exc}"
                row = by_id.get(item["id"])
                if row is None:
                    row = {"model": item["id"], "score": None, "total": len(CAPABILITY_PROBES), "checks": {}}
                    row.update({k: item[k] for k in ("label", "declared_context", "declared_output", "declared_max")})
                    results.append(row)
                    by_id[item["id"]] = row
                row["measured_context"] = context
                row["measured_output"] = output
                row["limit_notes"] = notes
                print(f"[bench] ({done}/{len(targets)}) {item['id']:<34} 输入≈{context} 输出≈{output}", flush=True)

    write_report(results, args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
