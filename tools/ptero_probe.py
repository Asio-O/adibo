#!/usr/bin/env python3
"""接管本机 Edge 会话，侦察 ptero.pro 的接口结构。

为什么不用普通 HTTP：ptero.pro 前后台都在 Cloudflare 后面，curl / Playwright 自带 Chromium
都会被挑战页拦住。能过 CF 的是用户本机那个有历史记录的 Edge 配置，所以这里直接接管它。

用法：
    1. 完全退出 Edge（任务管理器确认没有 msedge.exe）
    2. python tools/ptero_probe.py            # 自带启动 Edge + 调试端口
       或先自己启动： msedge.exe --remote-debugging-port=9222
          python tools/ptero_probe.py --attach-only

产物：控制台输出 + recording/ptero-probe/*.json
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import time
from pathlib import Path

from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
OUT_DIR = ROOT / "recording" / "ptero-probe"
CDP_URL = "http://127.0.0.1:9222"

EDGE_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]


def find_edge() -> str | None:
    for path in EDGE_CANDIDATES:
        if Path(path).exists():
            return path
    return None


def launch_edge_with_debug(port: int, profile: str | None) -> bool:
    edge = find_edge()
    if not edge:
        print("[probe] 找不到 msedge.exe")
        return False

    args = [edge, f"--remote-debugging-port={port}", "--start-maximized"]
    if profile:
        args.append(f"--user-data-dir={profile}")

    print(f"[probe] 启动 Edge: {edge}")
    subprocess.Popen(args)
    return True


def wait_for_cdp(port: int, timeout: float = 40.0) -> bool:
    import urllib.request

    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/version", timeout=3) as r:
                info = json.loads(r.read().decode("utf-8"))
            print(f"[probe] CDP 就绪: {info.get('Browser')}")
            return True
        except Exception:
            time.sleep(1.5)
    return False


def is_challenge(title: str) -> bool:
    return (not title) or "请稍候" in title or "Just a moment" in title or title.startswith("Loading")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="https://ptero.pro/")
    parser.add_argument("--port", type=int, default=9222)
    parser.add_argument("--profile", default="", help="Edge 用户数据目录；留空则用 Edge 默认配置")
    parser.add_argument("--attach-only", action="store_true", help="只连接已开着的调试端口")
    parser.add_argument("--timeout", type=float, default=120.0)
    args = parser.parse_args()

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    calls: list[tuple[str, int, str]] = []

    with sync_playwright() as pw:
        try:
            browser = pw.chromium.connect_over_cdp(f"http://127.0.0.1:{args.port}")
        except Exception as exc:
            if args.attach_only:
                print(f"[probe] 连不上 CDP({args.port}): {exc}")
                return 1
            if not launch_edge_with_debug(args.port, args.profile or None):
                return 1
            if not wait_for_cdp(args.port):
                print("[probe] 等不到 CDP 端口，Edge 可能已在运行（需先完全退出）")
                return 1
            browser = pw.chromium.connect_over_cdp(f"{CDP_URL}")

        context = browser.contexts[0] if browser.contexts else browser.new_context()
        page = context.pages[0] if context.pages else context.new_page()

        page.on(
            "response",
            lambda r: calls.append((r.request.method, r.status, r.url)),
        )

        try:
            page.goto(args.url, wait_until="commit", timeout=60_000)
        except Exception as exc:
            print(f"[probe] goto 异常（CF 跳转时正常）: {type(exc).__name__}")

        deadline = time.time() + args.timeout
        while time.time() < deadline:
            time.sleep(3)
            try:
                title = page.title()
            except Exception:
                continue
            if not is_challenge(title):
                print(f"[probe] CF 已通过: {title!r} {page.url}")
                break
            print(f"[probe] 等待 CF… {title!r}")
        else:
            print("[probe] 超时仍未通过 CF；请在窗口里手动点一下验证，或确认已登录")

        time.sleep(5)

        info: dict = {}
        for name, script in {
            "page": "() => ({url: location.href, title: document.title, ls: Object.keys(localStorage)})",
            "routes": "() => fetch('/wp-json/mlp/v1').then(r => r.text().then(t => ({status: r.status, body: t.slice(0, 20000)})))",
            "namespaces": "() => fetch('/wp-json').then(r => r.text().then(t => ({status: r.status, body: t.slice(0, 8000)})))",
        }.items():
            try:
                info[name] = page.evaluate(script)
            except Exception as exc:
                info[name] = {"error": f"{type(exc).__name__}: {exc}"}

        (OUT_DIR / "page.json").write_text(json.dumps(info, ensure_ascii=False, indent=2), encoding="utf-8")

        print("\n=== 页面 ===")
        print(json.dumps(info.get("page"), ensure_ascii=False, indent=1))

        print("\n=== 非静态网络请求（去重）===")
        skip_suffix = (".js", ".css", ".png", ".jpg", ".jpeg", ".svg", ".ico", ".woff", ".woff2", ".webp")
        for method, status, url in dict.fromkeys(calls):
            clean = url.split("?")[0]
            if clean.endswith(skip_suffix) or "challenges.cloudflare.com" in url or "cdn-cgi" in url:
                continue
            print(f"  {method:5} {status} {url[:160]}")

        for key in ("routes", "namespaces"):
            data = info.get(key) or {}
            print(f"\n=== {key} (HTTP {data.get('status')}) ===")
            body = data.get("body") or data.get("error") or ""
            print(body[:2500])

        try:
            page.screenshot(path=str(OUT_DIR / "page.png"), full_page=False)
            print(f"\n截图: {OUT_DIR / 'page.png'}")
        except Exception:
            pass

        # 不关浏览器：那是用户的会话
        browser.close()

    print(f"\n产物目录: {OUT_DIR}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
