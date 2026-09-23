# ai.rcouyi.com 对话接口地图

逆向来源：`https://ai.rcouyi.com/` 的 Vite 打包产物（`/assets/index-*.js` 及各懒加载 chunk），
加一轮真实浏览器操作抓包。API 基址来自站点公开的 `/config/index.js`：

```js
globalThis.g = Object.freeze({ BASE_URL: 'https://api-8.rcouyi.com' })
```

后端是 Furion（响应头 `Furion: 4.9.5.26`），统一信封：

```json
{ "code": 200, "message": "", "result": {}, "extras": null, "type": "success" }
```

`code != 200` 即业务失败，`message` 是中文提示；HTTP 状态码基本恒为 200。

## 通用请求头

| 头 | 值 | 说明 |
|---|---|---|
| `Authorization` | `Bearer <accessToken>` | 除登录 / 验证码 / 配置外必带 |
| `Accept-Language` | `zh-CN` | 前端固定值 |
| `xx-cf-source` | 见下 | 前端每个请求都带；实测可省略 |
| `Content-Type` | `application/json` | POST 必带，即使 body 为空 |

`xx-cf-source` 算法（`CryptoJS` 去混淆后）：

```js
key  = "b1d8c88f9dfc9316f4b37f462d77ae5d";     // 32 字节 → AES-256
host = BASE_URL.replace(/^https?:\/\//i, "");  // "api-8.rcouyi.com"
value = Base64(AES_ECB_PKCS7(host, key));
// => "Ho0V2S+0tBAGsMNClOSIimFx7Eeq6/Elz4bXwij2JK0="
```

代码里那个 `iv: "123456789ddwwqqs"` 在 ECB 模式下不参与运算，属于摆设。

## 鉴权

`accessToken` 是 JWT，payload 形如：

```json
{
  "MemberId": 14664000000000,
  "AccountType": 1,
  "NickName": "<昵称>",
  "Account": "<账号>",
  "LoginMode": 1,
  "iat": 1790000000,
  "nbf": 1790000000,
  "exp": 1840000000
}
```

**有效期约 608 天**，没有刷新接口——所以「登录一次、长期使用」可行。
前端把它存在 `localStorage['_token_']`，外壳是 `{"data":"<jwt>"}`。

### 登录

```
POST /chatapi/auth/login

账号模式：{"account":"...","password":"...","codeId":"...","code":"...","verCode":""}
手机模式：{"account":"...","password":"","codeId":"","code":"","verCode":"<短信码>"}
```

成功返回 `result.accessToken`。

### 验证码

| 接口 | 说明 |
|---|---|
| `GET /chatapi/auth/captcha` | 返回 `{id, img}`，`img` 是 base64 JPEG，`id` 作为登录的 `codeId` |
| `POST /chatapi/auth/vercode/{1\|2}` | 发短信 / 邮件码，body `{account, codeId, code, isMobile}` |

`GET /chatapi/config` 里 `verifyCode: { verifyCodeType: 2, tposConfig: { appId: "198255736" } }`。
`verifyCodeType` 命中 `[1,2,3,4]` 时网页改用第三方行为验证
（这里是 `turing.captcha.gtimg.com`，即腾讯 TCaptcha），它给出的 `code` 是票据、
`codeId` 形如 `@RJy`，仍按同样字段提交给登录接口。

## 模型清单（三处，别搞混）

1. **网页模型选择器真正用的那份**——站点静态配置：

```
GET https://ai.rcouyi.com/config/system.json
=> { "defaultGPTModel": "ouyi-chat", "defaultDocModel": "ouyi-chat",
     "model": [ { "label": "Ouyi Chat", "value": "ouyi-chat",
                  "description": "...", "enable": true,
                  "maxTokens": 1114112, "maxContextToken": 1048576,
                  "maxResponseToken": 65536, "uploadFile": true }, ... ] }
```

   实测 66 条，`value` 就是建会话时 `params.model` 要填的名字
   （`ouyi-chat`、`deepseek-v4-flash`、`glm-4.6` …），`enable: false` 的是灰掉的。

2. **旧版枚举**（前后端按数字 id 通信时用的）：

```
GET /api/sysEnum/enumDataList?EnumName=InterfaceAIModelEnum
=> result: [ { "name": "ChatGPTTurbo", "describe": "gpt-3.5-turbo", "value": 10 }, ... ]
```

3. **账号实际可用的枚举值**——`GET /chatapi/auth/memberInfo` 里
   `result.groupInfo.privileges[].aiModels`，空格分隔的数字，对应上面第 2 条的 `value`。

三者之间没有直接映射表：`config.system.json` 是模型名，枚举是数字 id，只有 `describe`
字段偶尔能对上。做代理直接用第 1 条。

## 对话

### 方式一：无状态单次流（推荐做代理）

```
POST /chatapi/chat/commonmessagestream
{
  "type": 1,
  "topicId": 0,
  "content": "本轮用户输入",
  "messages": [{"role": "system", "content": "..."}]
}
```

- `content` 是本轮输入，`messages` 是历史 / 系统消息；
- `type`：`1` 普通对话、`4` 写作、`5` 思维导图；
- 响应是 **`text/plain` 分块流**（不是 SSE），前端把每个 chunk 直接拼到气泡里；
- 不落库，不污染会话列表。

**注意：这个接口忽略请求里的 `model` 字段**——实测传 `__no_such_model_xyz__` 仍返回 200
且答案正常，所以想指定模型必须走下面那条路。

### 方式二：持久化两步流（网页版主对话走这条）

```
1) POST /chatapi/chat/message
   { "topicId": <会话id>, "messages": [...历史...], "content": "本轮输入", "contentFiles": [] }
   => { "result": [<用户消息id>, <助手消息id>] }

2) POST /chatapi/chat/message/{助手消息id}
   Content-Type: application/json（无 body）
   => text/plain 分块流
```

第 2 步才真正触发模型生成。用的是该会话 `params.model` 指定的模型。

> 注意：用 `curl` 调第 2 步会收到 Tengine 返回的空 400；用 .NET `HttpClient`
> 或浏览器 `fetch` 都正常。疑似边缘节点针对 curl 指纹的策略，与接口本身无关。

### 会话管理

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/chatapi/chat/topics?page=&pageSize=` | 会话列表 |
| POST | `/chatapi/chat/save` | 新建会话，字段 **PascalCase**：`{Title, Params, SystemMessage, RoleId}` |
| POST | `/chatapi/chat/topic/messages` | 历史消息，字段是 **`id`（不是 topicId）**、`page`、`pageSize` |
| POST | `/chatapi/chat/delete` | 删会话 |
| POST | `/chatapi/chat/delete/message/{topicId}/{messageId}` | 删消息 |
| GET | `/chatapi/chat/{topicId}` | 会话详情 |

`Params` 是**字符串化的 JSON**：

```json
{"chatPluginIds":[],"frequency_penalty":null,"max_tokens":32768,"model":"ouyi-chat",
 "presence_penalty":null,"requestMsgCount":20,"speechVoice":"Alloy","temperature":0.8}
```

## 其它常用只读接口

| 路径 | 说明 |
|---|---|
| `GET /chatapi/config` | 站点配置（站点名、注册 / 登录开关、验证码配置） |
| `GET /chatapi/auth/memberInfo` | 当前账号、分组、权益（含可用模型枚举值） |
| `GET /chatapi/member/wallet` | 余额 / token 用量 |
| `GET /chatapi/notice/unreadcount` | 未读通知数 |
| `GET /chatapi/notice/stickyinfo` | 置顶公告 |
| `GET /chatapi/marketing/signinpage?StartTime=YYYY-MM` | 签到页数据 |
| `GET /chatapi/member/info` 等 | 会员 / 邀请 / 订单相关 |

## JSON Output 与 Tool Calls（实测）

这两项 OpenAI 能力**上游都不支持**，结论来自直接打上游的对照实验：

| 能力 | 请求 | 上游响应 |
|---|---|---|
| JSON 模式 | `response_format: {"type":"json_object"}` | 被忽略，返回 markdown 散文（`好的，以下是一个…` ＋列表） |
| Tool Calls（auto） | `tools:[…]`, `tool_choice:"auto"` | `finish_reason: stop`，只有 `content`，无 `tool_calls` |
| Tool Calls（强制） | `tool_choice:"required"` | 同上，仍是纯文本 |

前端打包产物里搜不到 `tool_calls` / `function_call` / `tool_call_id` 任何一个字符串，
模型枚举接口也传不了工具——**站点从未实现过 OpenAI 的 function calling**。

### 站点自己的「工具」：会话插件

真正能触发工具调用的是会话参数里的 `chatPluginIds`（identifier 字符串）：

```
POST /chatapi/chatplugin/page   {"page":1,"pageSize":99,"tag":""}
=> 4 个插件：
   GetCurrentWeather        天气助手
   search-engine            搜索引擎（联网搜索）
   search_hero              王者百科
   JuHeApiCommon_BWecs3z    聚合数据API通用模板
```

把 identifier 填进会话 `params.chatPluginIds` 再发消息，模型会自己去调插件，流里出现
形如 `> 正在调用天气助手🌤️` 的提示行——这就是该站点的「工具调用」。

**但实测四个插件的后端全部故障**：天气与搜索引擎返回
`抱歉，网络出现异常，请你重试或联系客服！🚨InternalServerError`，王者百科返回空。
这是站点侧问题，代理绕不过去。

### 代理侧的模拟

因为上游做不到，代理用「提示词注入 + 输出解析」把这两项模拟出来
（见 `src/AdiboProxy/Endpoints/OpenAiEmulation.cs`）：

- **JSON 模式**：把 `response_format` 翻译成「只输出 JSON」的系统指令，返回前再剥掉模型
  偶尔加的 ```json 代码块；
- **Tool Calls**：把 `tools` 渲染成工具说明书注入系统提示词，要求模型按固定格式输出
  `{"tool_calls":[{"name":…,"arguments":…}]}`，再解析成标准 `tool_calls` 响应
  （`finish_reason: "tool_calls"`）；下一轮客户端回传 `role: "tool"` 结果时，代理把它
  还原成 prompt 里的 `[工具返回]` 片段，让模型基于结果作答。

流式处理：开了 `tools` 时按「扫描 `{` + 大括号配平」来识别工具调用——JSON 之前的文字立刻
流出，遇到 `{` 才开始缓冲候选，配平后能解析出 `tool_calls` 就转标准工具调用，解析不出来就
原样当正文。所以带 tools 的普通回答仍是真流式，且兼容「先写说明再给 JSON」的写法。
实测 100+ 个分片、跨度数秒。
只有 JSON 模式仍然全量缓冲，因为要剥代码块围栏。响应头 `X-Rcouyi-Streaming` 会标出实际模式。

代价：提示词模拟在工具集很复杂时不如原生 function calling 稳。

验证方式：`python tools/smoke_test.py`（官方 OpenAI SDK，5 项断言）。

### 推理块（DeepSeek V4）

`deepseek-v4-flash` 会在正文前输出 `<think>…</think>` 推理过程。这会让严格 JSON 解析失败
（JSON 本身是对的，只是前面挂了段推理），所以代理默认把它拆到 `reasoning_content` 字段，
与 DeepSeek 官方 API 的行为一致；流式下推理与正文在同一个 SSE 流里逐块下发。

### 模型路由

因为无状态接口忽略 `model`，代理对**非默认模型**会自动建一个带 `params.model` 的上游会话并
缓存（`data/model-topics.json`），响应头回 `X-Rcouyi-Topic-Id`。默认模型仍走无状态接口，
不产生任何会话记录。换模型会真实写进账号的会话列表——上游限制，绕不开。

两个配套处理：

- **会话被删了会自愈**。上游对不存在的会话返回 `[D1002] 记录不存在`；代理捕获后会丢掉这条
  缓存、重建一个会话再重试一次，所以手动清理站点会话不会把代理搞坏。
- **会话标题固定**。上游强制要求 `Title` 非空，但代理不再把用户提问写进标题（配置项
  `Rcouyi:TopicTitle`，默认「新会话」），避免侧边栏泄露对话内容。

## 尚未验证

- 附件上传链路（`contentFiles` 的真实结构）；
- 绘画（MJ / DALL·E）与画廊相关接口；
- `commonmessagestream` 的默认模型到底固定在哪个（`defaultGPTModel` 是 `ouyi-chat`，
  但没有能直接验证的接口）；
- `config.system.json` 里的模型名与后端实际路由的对应关系（未逐个试）；
- 四个插件的后端能否恢复（当前全部 500 / 空响应）。
