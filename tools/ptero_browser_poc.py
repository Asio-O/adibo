#!/usr/bin/env python3
"""验证「用真实浏览器当传输层」能否打通 ptero.pro。

背景：ptero.pro 的 API 在 Cloudflare 后面且校验 TLS 指纹，普通 HTTP 客户端（含 curl、
Playwright 的 APIRequestContext）都会被 403。只有真实浏览器能过。
本脚本用本机 Edge 打开站点，等 CF 通过后，直接在页面里 fetch 它的接口——
同源请求天然带着 CF 通行证和访客令牌，不需要我们自己拼。

用法：
    python tools/ptero_browser_poc.py

首次运行若停在「请稍候…」，请在弹出的 Edge 窗口里手动完成一次人机验证（通常是个复选框），
脚本会继续等待。
"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
PROFILE = ROOT / ".edge-profile"
API = "/wp-json/mlp/v1"


def is_challenge(title: str) -> bool:
    return (not title) or "请稍候" in title or "Just a moment" in title or title.startswith("Loading")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="https://ptero.pro/")
    parser.add_argument("--wait", type=float, default=240.0, help="等待人工通过 CF 的秒数")
    parser.add_argument("--message", default="只回复两个字：收到")
    args = parser.parse_args()

    PROFILE.mkdir(exist_ok=True)

    with sync_playwright() as pw:
        ctx = pw.chromium.launch_persistent_context(
            user_data_dir=str(PROFILE),
            channel="msedge",
            headless=False,
            viewport=None,
            args=["--start-maximized"],
        )
        page = ctx.pages[0] if ctx.pages else ctx.new_page()

        try:
            page.goto(args.url, wait_until="commit", timeout=60_000)
        except Exception as exc:
            print(f"[poc] goto 被 CF 跳转打断（正常）: {type(exc).__name__}")

        deadline = time.time() + args.wait
        passed = False
        while time.time() < deadline:
            time.sleep(3)
            try:
                title = page.title()
            except Exception:
                continue
            if not is_challenge(title):
                print(f"[poc] CF 已通过: {title!r}")
                passed = True
                break
            print(f"[poc] 等待中…（若窗口里有人机验证，请手动点一下）title={title!r}")

        if not passed:
            print("[poc] 超时，CF 未通过")
            ctx.close()
            return 1

        time.sleep(5)

        print("\n=== localStorage ===")
        ls = page.evaluate("() => Object.fromEntries(Object.entries(localStorage).map(([k,v]) => [k, String(v).slice(0,120)]))")
        print(json.dumps(ls, ensure_ascii=False, indent=1))

        print("\n=== GET /status ===")
        print(page.evaluate(f"() => fetch('{API}/status').then(r => r.text())"))

        print("\n=== GET /api-models（前 3 个）===")
        models = page.evaluate(f"() => fetch('{API}/api-models').then(r => r.json())")
        for m in models.get("models", [])[:3]:
            print(f"  {m['id']}  {m['name']}")
        default_model = "mercury-2.5:free"

        print(f"\n=== POST /chat-stream（流式，模型 {default_model}）===")
        script = """
        async ([api, message, model]) => {
          const res = await fetch(api + '/chat-stream', {
            method: 'POST',
            headers: {'Content-Type': 'application/json'},
            body: JSON.stringify({
              message, model,
              conversation_id: crypto.randomUUID(),
              attachments: [], history: [], lang: 'zh',
              github_repo: '', mode: 'quick'
            })
          });
          const reader = res.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '', text = '', events = [];
          while (true) {
            const {value, done} = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, {stream: true});
            const lines = buffer.split('\\n');
            buffer = lines.pop();
            for (const line of lines) {
              if (!line.startsWith('data:')) continue;
              const payload = line.slice(5).trim();
              if (!payload) continue;
              try {
                const ev = JSON.parse(payload);
                events.push(ev.phase || (ev.token ? 'token' : Object.keys(ev).join(',')));
                if (ev.token) text += ev.token;
                if (ev.done) events.push('done:' + ev.usage_tokens + 'tok');
              } catch (e) {}
            }
          }
          return {status: res.status, text, events};
        }
        """
        result = page.evaluate(script, [API, args.message, default_model])
        print(f"  HTTP {result['status']}")
        print(f"  事件序列: {result['events']}")
        print(f"  回复内容: {result['text']!r}")

        print("\n[poc] 浏览器保持打开，你可以继续用；不用了就关掉窗口。")
        ctx.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
