# rcouyi 对话 API 反向代理（C#）

把 `https://ai.rcouyi.com/` 网页版对话的私有接口封装成可直接调用的 HTTP 服务，
同时提供 **OpenAI 兼容接口**（`/v1/*`）和 **原生接口**（`/rcouyi/*`）。

接口逆向的完整结论（含模型清单从哪来）见 [docs/api-map.md](docs/api-map.md)。

## 它做了什么

网页版所有请求都打到 `https://api-8.rcouyi.com`，鉴权只有一个 `Authorization: Bearer <JWT>`。
这个 JWT **有效期约 608 天**，所以"登录一次、长期使用"是可行的：

1. 登录（需要过一次验证码）拿到 `accessToken`；
2. token 存到 `data/token.json`；
3. 之后代理只做转发与协议转换。

代理层负责三件事：

- **鉴权注入**：自动补 `Authorization` / `Accept-Language` / `xx-cf-source`；
- **协议转换**：上游返回的是**纯文本分块流**（不是 SSE），代理转成 OpenAI 的 SSE；
- **上下文拼装**：把 OpenAI 的 `messages` 拆成上游要的 `messages`（历史）+ `content`（本轮输入）。

## 快速开始

```powershell
cd chatapi-proxy\src\AdiboProxy
dotnet run
```

默认监听 `http://localhost:5080`。

### 1. 拿 token

方式 A（推荐）——用浏览器登录一次，脚本自动把 token 落盘：

```powershell
python chatapi-proxy\tools\grab_token.py
# 在弹出的窗口里完成登录，脚本检测到 token 后自动退出
```

方式 B——调用代理的登录接口（需要你先拿到验证码）：

```bash
curl http://localhost:5080/rcouyi/captcha     # 返回 {id, img}，img 是 base64 JPEG
curl -X POST http://localhost:5080/rcouyi/login \
     -H "Content-Type: application/json" \
     -d '{"account":"...","password":"...","codeId":"<captcha 的 id>","code":"<识别出的字符>"}'
```

方式 C——手工写入：`POST /rcouyi/token`，body `{"token":"<jwt>"}`。

> 账号模式登录必带验证码。站点 `verifyCodeType: 2` 时网页走腾讯 TCaptcha 拖拽验证，
> 它给出的 `code` 是票据、`codeId` 形如 `@RJy`，同样字段直接提交给 `/rcouyi/login` 即可。

### 2. 调对话

OpenAI 兼容（流式）：

```bash
curl -N http://localhost:5080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"ouyi-chat","stream":true,
       "messages":[{"role":"system","content":"你是简洁助手"},
                   {"role":"user","content":"用一句话解释什么是反向代理"}]}'
```

原生接口（直接拿上游纯文本流）：

```bash
curl -N http://localhost:5080/rcouyi/chat \
  -H "Content-Type: application/json" \
  -d '{"type":1,"content":"你好","messages":[]}'
```

Python `openai` SDK 直接指过来也行：

```python
from openai import OpenAI
client = OpenAI(base_url="http://localhost:5080/v1", api_key="not-used")
print(client.chat.completions.create(
    model="ouyi-chat",
    messages=[{"role": "user", "content": "你好"}],
).choices[0].message.content)
```

## 接口一览

OpenAI 兼容：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/v1/models` | 模型列表（取自站点 `config/system.json`，失败时回落到枚举接口） |
| POST | `/v1/chat/completions` | 对话，支持 `stream: true/false` |

原生透传：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/rcouyi/status` | token 状态、账号、过期时间 |
| POST | `/rcouyi/token` | 手工写入 token |
| DELETE | `/rcouyi/token` | 清空 token |
| GET | `/rcouyi/captcha` | 取图形验证码 |
| POST | `/rcouyi/login` | 账号密码 + 验证码登录，成功后自动落盘 |
| GET | `/rcouyi/member` | 当前账号 / 权益 |
| GET | `/rcouyi/wallet` | 余额与 token 用量 |
| GET | `/rcouyi/models` | 模型清单（站点静态配置，含 enable / 上下文长度） |
| GET | `/rcouyi/model-enums` | 旧版模型枚举 `{name, describe, value}` |
| GET | `/rcouyi/plugins` | 站点会话插件清单（原生「工具」） |
| GET | `/rcouyi/plugin-tags` | 插件标签 |
| GET | `/rcouyi/topics` | 会话列表，`?page=&pageSize=` |
| POST | `/rcouyi/topics` | 新建会话 `{"title":"...","model":"deepseek-v4-flash"}` |
| POST | `/rcouyi/topics/{id}/messages` | 会话历史消息 |
| POST | `/rcouyi/chat` | 无状态对话（纯文本流） |
| POST | `/rcouyi/chat/topic` | 持久化对话（先存后流，纯文本流） |
| GET | `/health` | 存活探针 |

`/rcouyi/chat` 请求体就是上游原生格式：

```json
{ "type": 1, "topicId": 0, "content": "本轮输入", "messages": [{"role":"system","content":"..."}] }
```

`type`：`1` 普通对话、`4` 写作、`5` 思维导图。

## 模型选择怎么走

上游的无状态接口**根本不吃 `model`**（传非法模型名也照常返回），换模型只能建一个带
`params.model` 的会话再走「先存后流」。代理把这件事包掉了：

- OpenAI 层直接写 `"model": "deepseek-v4-flash"` 就行——代理会为该模型建一个会话并缓存，
  响应头 `X-Rcouyi-Topic-Id` 告诉你用的是哪个；
- 缓存落在 `data/model-topics.json`，重启不丢；
- 配置里的 `DefaultModel`（默认 `ouyi-chat`）走无状态接口，不进会话列表、不产生任何痕迹；
- 想完全手动控制，就带扩展字段 `"topic_id": <会话id>`，代理会沿用该会话的模型与插件。

代价：非默认模型下对话会真实写进你的账号（每个用到的模型一个会话，会出现在网页侧边栏），
默认模型不会。想完全不留痕就用 `ouyi-chat`。

## JSON Output 与 Tool Calls

上游**两项都不支持**（实测 `response_format` 被忽略、`tool_choice: "required"` 也照样返回纯文本），
所以代理用「提示词注入 + 输出解析」把它们模拟了出来——对客户端来说就是正常可用的 OpenAI 接口：

```python
from openai import OpenAI
client = OpenAI(base_url="http://localhost:5080/v1", api_key="not-used")

# JSON 模式
client.chat.completions.create(
    model="ouyi-chat",
    response_format={"type": "json_object"},          # 也支持 json_schema
    messages=[{"role": "user", "content": "生成一个人的信息，字段 name、age、city"}],
)

# Tool Calls
client.chat.completions.create(
    model="ouyi-chat",
    tools=[{"type": "function", "function": {
        "name": "get_weather",
        "description": "查询指定城市的当前天气",
        "parameters": {"type": "object",
                       "properties": {"city": {"type": "string"}},
                       "required": ["city"]}}}],
    messages=[{"role": "user", "content": "北京现在天气怎么样？"}],
)   # -> choices[0].message.tool_calls，finish_reason == "tool_calls"
```

完整的工具回合也支持：客户端回传 `role: "tool"` 结果后，模型会基于结果给出最终回答。
跑 `python tools/smoke_test.py` 可以一次性验证全部 5 项。

### 流式与工具调用共存

注入给模型的协议是**不分块**的：模型可以正常写开场白，需要调工具时把下面这段 JSON 输出出来。

```
{"tool_calls":[{"name":"工具名","arguments":{"参数名":"参数值"}}]}
```

代理在流式下按「扫 `{` + 64 字符探测窗」判定：

- 开场白照常逐字流出，**不要求模型省略开场白**；
- 遇到 `{` 只是开始留意，看紧跟的 64 个字符里有没有 `"tool_calls"` 这个键——工具协议的 JSON
  一定在开头带这个键，普通 JSON 几乎不可能有；没有就立刻放行，所以 JSON 回答和代码块都能正常流式；
- 有就缓冲到大括号配平再解析，解析成功转成标准 `tool_calls`，失败则原样当正文，**绝不吞内容**。

> 早期试过用 `<tool_call>` 标签分块（thinking / text / tool-call 三通道），但实测模型遵循度不稳定，
> 经常只在正文里"承诺"要用工具却不输出块，因此回退到这套不加块约束的方案。

```
finish_reason = tool_calls
content       = '我这就去查一下。'            ← 开场白照常流出
tool_calls    = [{"name":"get_weather","arguments":"{\"city\":\"北京\"}"}]
```

解析器对分片边界、未闭合 JSON、格式非法等情况都有兜底，不会误判也不会丢内容。

实测带 tools 的普通回答仍有上百个分片、跨度数秒。

解析逻辑有离线回归测试（不消耗任何额度）：`dotnet run --project chatapi-proxy\tests\ParserTests`。

仍会缓冲的只剩 **JSON 模式**（`response_format`），因为要剥掉可能的代码块围栏。

响应头可以一眼看出走了哪条路：

| 响应头 | 取值 |
|---|---|
| `X-Rcouyi-Mode` | `stateless` / `topic` / `plugin` / `model-topic` |
| `X-Rcouyi-Streaming` | `incremental` / `incremental-tools-gated` / `buffered-json` / `none` |

另外，工具调用本质是提示词模拟，工具集很复杂时不如原生 function calling 稳。

另外，站点自己有一套「工具」——会话插件（天气助手、搜索引擎、王者百科、聚合数据 API），
用 `plugins: ["GetCurrentWeather"]` 触发。**实测四个插件的后端目前全部故障**
（插件确实被调起来了，但插件服务返回 InternalServerError），那是站点侧的问题。

## 配置

`appsettings.json` 的 `Rcouyi` 段，或同名环境变量（双下划线写法，如 `Adibo__Token`）：

| 键 | 默认值 | 说明 |
|---|---|---|
| `BaseUrl` | `https://api-8.rcouyi.com` | 上游 API 根地址 |
| `SiteBaseUrl` | `https://ai.rcouyi.com` | 站点前端地址（取静态模型清单用） |
| `Language` | `zh-CN` | `Accept-Language` |
| `TokenFile` | `data/token.json` | token 落盘位置（相对项目目录） |
| `Token` | 空 | 直接写死 token，优先级高于文件 |
| `ApiKey` | 空 | 非空时 `/v1/*` 要求 `Authorization: Bearer <这个值>` |
| `DefaultModel` | `ouyi-chat` | 建会话时的默认模型 |
| `TopicTitle` | `新会话` | 代理新建会话时用的固定标题（不写用户提问） |
| `SendSignatureHeader` | `true` | 是否发送 `xx-cf-source` |
| `ReasoningMode` | `separate` | 推理块处理：`separate` / `strip` / `inline` |

### 推理块（DeepSeek V4 等）

DeepSeek V4 会在正文前输出 `<think>…</think>` 推理过程。直接混在 `content` 里会让 JSON
解析失败、OpenAI 客户端也意外，所以代理按 DeepSeek 官方 API 的做法把它拆到独立字段：

    { "message": { "role": "assistant",
                   "content": "3的阶乘是 6。",
                   "reasoning_content": "1. 理解用户的请求… 2. 定义阶乘…" } }

流式下同理，推理片段走 `delta.reasoning_content`、正文走 `delta.content`，
**两者在同一个 SSE 流里逐块下发**，不是缓冲后一次性吐出来。
`ReasoningMode` 可改成 `strip`（丢弃）或 `inline`（原样保留，不过滤）。

## 已知边界

- **登录验证码**：站点启用了腾讯 TCaptcha，纯 HTTP 过不了，所以推荐用浏览器登录一次；
  JWT 长期有效，日常不需要重复登录。
- **token 失效**：站点没有刷新接口，失效后重新登录即可，`/rcouyi/status` 能看到有效期。
- **模型切换**：见上一节，无状态接口不吃 `model`。
- 本项目按站点自身前端行为做对等封装，请自行确认符合目标站点的使用条款。
