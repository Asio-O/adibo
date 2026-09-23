namespace AdiboProxy.Rcouyi;

/// <summary>
/// 上游站点（ai.rcouyi.com / api-8.rcouyi.com）的连接参数与默认会话参数。
/// </summary>
public sealed class AdiboOptions
{
    public const string SectionName = "Adibo";

    /// <summary>后端 API 根地址，前端 <c>/config/index.js</c> 里写死的 BASE_URL。</summary>
    public string BaseUrl { get; set; } = "https://api-8.rcouyi.com";

    /// <summary>站点前端地址，用来取静态模型清单 <c>/config/system.json</c>。</summary>
    public string SiteBaseUrl { get; set; } = "https://ai.rcouyi.com";

    /// <summary>与网页端一致的 Accept-Language。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>token 落盘位置（相对 ContentRoot）。</summary>
    public string TokenFile { get; set; } = "data/token.json";

    /// <summary>可选：直接写死 token，省掉登录流程。</summary>
    public string? Token { get; set; }

    /// <summary>可选：给本代理自身加一层 Bearer 校验。</summary>
    public string? ApiKey { get; set; }

    /// <summary>建会话时使用的默认模型名。</summary>
    public string DefaultModel { get; set; } = "ouyi-chat";

    /// <summary>
    /// 代理新建会话时用的标题。上游强制要求 Title 非空，但不需要把用户提问写进去——
    /// 用一个固定名字，避免侧边栏里泄露对话内容。
    /// </summary>
    public string TopicTitle { get; set; } = "新会话";

    /// <summary>
    /// 已知**原生支持工具调用**（function calling）的模型名。命中这些模型时，代理把工具定义
    /// 原样透传给上游，交给模型自己的能力；其余模型一律退化为「提示词注入 + 输出解析」。
    ///
    /// 注意：当前上游（rcouyi 面板）对 <c>tools</c> 字段完全无视——实测传工具与传非法模型名
    /// 一样都只是普通回答，前端产物里也搜不到任何 <c>tool_calls</c>/<c>function_call</c> 代码，
    /// 所以这份列表默认留空，即全部走提示词注入。换成支持原生工具调用的上游时，把模型名填进来即可。
    /// 用 <c>tools/probe_native_tools.py</c> 可以实测某个模型到底支不支持。
    /// </summary>
    public List<string> NativeToolModels { get; set; } = [];

    /// <summary>会话参数：带多少轮历史。</summary>
    public int RequestMsgCount { get; set; } = 20;

    /// <summary>会话参数：max_tokens。</summary>
    public int MaxTokens { get; set; } = 32768;

    /// <summary>会话参数：temperature。</summary>
    public double Temperature { get; set; } = 0.8;

    /// <summary>是否附带 <c>xx-cf-source</c> 头（复刻网页端行为）。</summary>
    public bool SendSignatureHeader { get; set; } = true;

    /// <summary>
    /// 推理块（如 DeepSeek V4 的 <c>&lt;think&gt;…&lt;/think&gt;</c>）怎么处理：
    /// <c>separate</c> 拆到 <c>reasoning_content</c> 字段（默认，与 DeepSeek 官方 API 一致）；
    /// <c>strip</c> 直接丢弃；<c>inline</c> 原样留在 content 里（等同不过滤）。
    /// </summary>
    public string ReasoningMode { get; set; } = "separate";

    /// <summary>日志目录（相对 ContentRoot）。留空则关闭文件日志。</summary>
    public string LogDirectory { get; set; } = "logs";

    /// <summary>写入文件的日志级别。</summary>
    public string LogFileLevel { get; set; } = "Information";

    /// <summary>日志保留天数。</summary>
    public int LogRetainDays { get; set; } = 7;

    /// <summary>每轮请求最多缓存多少字符的上游原文，用于排查。</summary>
    public int TraceRawChars { get; set; } = 16000;

    /// <summary>
    /// 附加到每个上游请求的请求头。给 Cloudflare 保护的站点用：
    /// 从浏览器里复制 Cookie（含 cf_clearance）和 User-Agent 填进来即可。
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>User-Agent；留空则不发这个头。</summary>
    public string UserAgent { get; set; } = "";

    /// <summary>
    /// 上游供应商列表。留空则退化为「只有一个 rcouyi 面板供应商」的旧行为。
    /// 每项可指定 Type（rcouyi / openai-chat / openai-responses / anthropic）以及承接哪些模型。
    /// </summary>
    public List<ProviderConfigEntry> Providers { get; set; } = [];
}

/// <summary>配置文件里的供应商条目。</summary>
public sealed class ProviderConfigEntry
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "rcouyi";
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public List<string> Models { get; set; } = [];
    public bool NativeTools { get; set; }
    public string? TargetModel { get; set; }
}
