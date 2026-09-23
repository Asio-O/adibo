#!/usr/bin/env python3
"""浏览器登录一次，把 ai.rcouyi.com 的 accessToken 写进代理的 token 文件。

站点的登录挂的是腾讯 TCaptcha（拖拽验证），纯 HTTP 过不去，所以这里用真实浏览器：
你手动登录，脚本轮询 localStorage 里的 `_token_`，拿到后落盘并退出。

    python tools/grab_token.py                    # 打开浏览器，登录后自动保存
    python tools/grab_token.py --headless         # 已登录过时可无人值守刷新
    python tools/grab_token.py --out path.json    # 自定义输出位置

输出字段与代理的 TokenStore 对齐：token / account / memberId / nickName / savedAt / expiresAt。
"""

from __future__ import annotations

import argparse
import base64
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_OUT = ROOT / "src" / "AdiboProxy" / "data" / "token.json"
DEFAULT_PROFILE = ROOT / ".browser-profile"
LOGIN_URL = "https://ai.rcouyi.com/login"

READ_TOKEN_JS = "() => localStorage.getItem('_token_')"


def decode_jwt(token: str) -> dict:
    """从 JWT payload 取账号信息；解不出来就返回空字典。"""
    try:
        payload = token.split(".")[1]
        payload += "=" * (-len(payload) % 4)
        data = json.loads(base64.urlsafe_b64decode(payload))
    except Exception:  # noqa: BLE001 - 尽力而为，失败不影响主流程
        return {}

    record = {
        "account": data.get("Account"),
        "nickName": data.get("NickName"),
        "memberId": data.get("MemberId"),
    }
    if isinstance(data.get("exp"), (int, float)):
        record["expiresAt"] = (
            datetime.fromtimestamp(data["exp"], tz=timezone.utc).astimezone().isoformat()
        )
    return {key: value for key, value in record.items() if value is not None}


def extract_token(raw: str | None) -> str | None:
    """localStorage 里存的是 {"data":"<jwt>"} 包装，剥一层再返回。"""
    if not raw:
        return None

    text = raw.strip()
    if text.startswith("{"):
        try:
            text = json.loads(text).get("data") or ""
        except json.JSONDecodeError:
            return None

    text = text.strip()
    return text if text.count(".") >= 2 else None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=str(DEFAULT_OUT))
    parser.add_argument("--profile", default=str(DEFAULT_PROFILE))
    parser.add_argument("--url", default=LOGIN_URL)
    parser.add_argument("--timeout", type=float, default=600.0, help="等待登录的秒数")
    parser.add_argument("--headless", action="store_true")
    args = parser.parse_args()

    out_path = Path(args.out)
    deadline = time.time() + args.timeout
    token: str | None = None

    with sync_playwright() as playwright:
        context = playwright.chromium.launch_persistent_context(
            user_data_dir=args.profile,
            headless=args.headless,
            viewport=None,
            args=["--start-maximized"],
        )
        page = context.pages[0] if context.pages else context.new_page()
        page.goto(args.url, wait_until="domcontentloaded", timeout=60_000)

        print(f"[grab_token] 请在打开的窗口里登录（最多等 {args.timeout:.0f} 秒）…", flush=True)

        try:
            while time.time() < deadline:
                try:
                    raw = page.evaluate(READ_TOKEN_JS)
                except Exception:  # noqa: BLE001 - 页面导航时上下文会短暂失效
                    raw = None

                token = extract_token(raw)
                if token:
                    break

                page.wait_for_timeout(1000)
        except KeyboardInterrupt:
            print("[grab_token] 已中断", file=sys.stderr)

        context.close()

    if not token:
        print("[grab_token] 超时，没有拿到 token", file=sys.stderr)
        return 1

    record = {"token": token, "savedAt": datetime.now(timezone.utc).astimezone().isoformat()}
    record.update(decode_jwt(token))

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")

    expires = record.get("expiresAt")
    note = f"，有效期到 {datetime.fromisoformat(expires):%Y-%m-%d}" if expires else ""
    print(f"[grab_token] 已保存到 {out_path}（账号 {record.get('account', '?')}{note}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
