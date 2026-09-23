using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdiboProxy.Rcouyi;

/// <summary>一条对话消息。</summary>
public sealed class RcouyiMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

/// <summary>登录请求。账号模式填 password + codeId/code；手机模式填 verCode。</summary>
public sealed class RcouyiLoginRequest
{
    [JsonPropertyName("account")] public string? Account { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("codeId")] public string? CodeId { get; set; }
    [JsonPropertyName("verCode")] public string? VerCode { get; set; }
    [JsonPropertyName("oAuthVerCode")] public string? OAuthVerCode { get; set; }
}

/// <summary>无状态流式对话，字段与上游 <c>/chatapi/chat/commonmessagestream</c> 一致。</summary>
public sealed class RcouyiStreamRequest
{
    [JsonPropertyName("type")] public int Type { get; set; } = 1;
    [JsonPropertyName("topicId")] public long TopicId { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("messages")] public List<RcouyiMessage> Messages { get; set; } = [];

    /// <summary>
    /// 模型原生工具调用时透传的工具定义。当前上游会忽略这个字段（实测），
    /// 只在 <c>Rcouyi:NativeToolModels</c> 里声明了模型时才会带上。
    /// </summary>
    [JsonPropertyName("tools")] public List<JsonElement>? Tools { get; set; }
}

/// <summary>持久化对话，等价于网页端「先存消息、再按 id 拉流」。</summary>
public sealed class RcouyiTopicChatRequest
{
    [JsonPropertyName("topicId")] public long TopicId { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("messages")] public List<RcouyiMessage> Messages { get; set; } = [];
    [JsonPropertyName("systemMessage")] public string? SystemMessage { get; set; }
}

/// <summary>新建会话。</summary>
public sealed class RcouyiCreateTopicRequest
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("systemMessage")] public string? SystemMessage { get; set; }
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    [JsonPropertyName("maxTokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("requestMsgCount")] public int? RequestMsgCount { get; set; }

    /// <summary>站点原生「工具」：会话插件 identifier，取自 <c>/rcouyi/plugins</c>。</summary>
    [JsonPropertyName("chatPluginIds")] public List<string>? ChatPluginIds { get; set; }

    /// <summary>是否开启联网搜索。不填则保持站点默认（不下发该字段）。</summary>
    [JsonPropertyName("webSearch")] public bool? WebSearch { get; set; }
}

public sealed class RcouyiSetTokenRequest
{
    [JsonPropertyName("token")] public string? Token { get; set; }
}
