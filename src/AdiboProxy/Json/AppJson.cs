// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AdiboProxy.Endpoints;
using AdiboProxy.Rcouyi;

namespace AdiboProxy.Json;

/// <summary>
/// Native AOT 下 System.Text.Json 不能靠反射推断类型，所有需要反序列化的类型
/// 都必须在这里显式登记，由源生成器产出 JsonTypeInfo。
/// </summary>
[JsonSerializable(typeof(RcouyiLoginRequest))]
[JsonSerializable(typeof(RcouyiStreamRequest))]
[JsonSerializable(typeof(RcouyiTopicChatRequest))]
[JsonSerializable(typeof(RcouyiCreateTopicRequest))]
[JsonSerializable(typeof(RcouyiSetTokenRequest))]
[JsonSerializable(typeof(OpenAiEndpoints.ChatCompletionRequest))]
[JsonSerializable(typeof(TokenRecord))]
[JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 响应体一律用 <see cref="JsonObject"/> 现搭，避免任何反射序列化。
/// </summary>
internal static class J
{
    /// <summary>给 JsonNode 用的选项：不缩进、中文不转义。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static JsonObject Obj(params (string Key, object? Value)[] pairs)
    {
        var node = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            node[key] = Node(value);
        }

        return node;
    }

    public static JsonArray Arr(IEnumerable<object?> items)
        => new(items.Select(Node).ToArray());

    /// <summary>用源生成的 JsonTypeInfo 把 DTO 转成节点，不碰反射。</summary>
    public static JsonNode? From<T>(T value, JsonTypeInfo<T> info)
        => JsonSerializer.SerializeToNode(value, info);

    public static JsonObject Message(RcouyiMessage message)
        => Obj(("role", message.Role), ("content", message.Content));

    public static JsonArray Messages(IEnumerable<RcouyiMessage> messages)
        => new(messages.Select(m => (JsonNode?)Message(m)).ToArray());

    public static JsonNode? Node(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        DateTimeOffset moment => JsonValue.Create(moment.ToString("yyyy-MM-dd'T'HH:mm:sszzz")),
        System.Collections.IEnumerable list => new JsonArray(list.Cast<object?>().Select(Node).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>把 JsonNode 直接写出去，不经过任何序列化解析器。</summary>
    public static IResult Send(JsonNode node, int statusCode = StatusCodes.Status200OK)
        => Results.Text(node.ToJsonString(Options), "application/json; charset=utf-8", statusCode: statusCode);

    /// <summary>上游透传的 JSON 原文直接回吐，省掉一次解析。</summary>
    public static IResult Raw(string json, int statusCode = StatusCodes.Status200OK)
        => Results.Text(json, "application/json; charset=utf-8", statusCode: statusCode);
}
