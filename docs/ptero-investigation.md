# ptero.pro 接口侦察记录

目标是让代理也能对接 `https://ptero.pro/`。**结论先行：它和 rcouyi 不是同一套面板，
接口层无法复用，需要单独写适配器。**

## 已确认

| 项 | rcouyi（已实现） | ptero.pro |
|---|---|---|
| 站点 | `ai.rcouyi.com` | `ptero.pro` |
| API 域 | `api-8.rcouyi.com`（独立域） | 同源，`ptero.pro` 下 |
| 后端 | .NET Furion 面板，`/chatapi/*` | **WordPress + `mlp` 插件**，`/wp-json/mlp/v1/*` |
| 鉴权 | `Authorization: Bearer <JWT>` | 待查（WordPress 通常是 nonce / cookie，或 JWT 插件） |
| 前置防护 | 无 | **Cloudflare 托管挑战**（curl 与 Playwright 自带 Chromium 均被拦） |
| 验证码 | 腾讯 TCaptcha（登录用） | 待查 |

## 证据

浏览器控制台里可见的真实请求（用户在已通过 CF 的 Edge 中捕获）：

```
GET https://ptero.pro/wp-json/mlp/v1/status  403 (Forbidden)
GET https://ptero.pro/wp-json/mlp/v1/news    403 (Forbidden)
```

调用栈里出现 `checkAiStatus`、`checkNewsUnread`、`apiFetch` 等函数名，
说明前端封装了一个 `apiFetch`，统一走 `/wp-json/mlp/v1/` 命名空间。

补充探测：

- `api.ptero.pro` / `api-8.ptero.pro` / `api1.ptero.pro` / `api2.ptero.pro` 均 DNS 不存在；
- `https://ptero.pro/chatapi/config` 返回 CF 挑战页，说明没有沿用 rcouyi 的接口路径；
- `403` 出现在**已通过 CF 的浏览器**里，因此更像是 WordPress 的权限校验（`rest_forbidden`），
  而不是 CF 拦截——即这些接口需要登录态或 nonce。

## Cloudflare 带来的约束

这是 ptero.pro 比 rcouyi 麻烦的根本原因：

1. 前台和 API 同源，且都在 CF 后面；
2. 普通 `HttpClient` 请求会拿到 403 挑战页，**不是改几行代码能绕过的**；
3. 手动注入 `cf_clearance`（代理已支持 `Rcouyi:Headers` / `Rcouyi:UserAgent` 配置）也不稳：
   cookie 绑 IP + User-Agent、有效期通常几十分钟，且 CF 还会校验 TLS 指纹（JA3）；
4. 稳定方案是**用真实浏览器当传输层**（Playwright 驱动本机 Edge，或通过 CDP 接管已登录的
   Edge 会话），代价是整个上游调用链路要改造成"经浏览器转发"。

## 待办

- [ ] 拿到 `/wp-json/mlp/v1` 的路由清单（WordPress REST 会返回完整 endpoints 列表）
- [ ] 确认鉴权方式（nonce 头名？登录接口？token 存哪）
- [ ] 确认对话接口与流式格式（WordPress 常见 SSE 或 `text/event-stream`）
- [ ] 决定传输层：直接 HTTP + clearance，还是浏览器转发
- [ ] 需要 ptero.pro 的账号密码做真实联调

## 实现方式（待接口确认后）

代理当前是 rcouyi 专用实现。对接 ptero.pro 的正确做法不是改配置，而是
**抽象出 provider 接口**：

```
IUpstreamProvider
  ├─ RcouyiProvider   （现有 /chatapi/* 逻辑）
  └─ PteroProvider    （新增 /wp-json/mlp/v1/* 逻辑）
```

路由按前缀区分（`/rcouyi/*` 与 `/ptero/*`），OpenAI 兼容层复用同一套转换逻辑。
