using STranslate.Plugin;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace STranslate.Plugin.Translate.LanguageModel;

/// <summary>
/// 构造语言模型翻译请求。内置请求体提供稳定的 OpenAI 兼容结构，
/// 用户请求体只覆盖自己填写的字段，未填写字段继续保留内置值。
/// </summary>
internal static class RequestBuilder
{
    private const string DefaultAccept = "text/event-stream";
    private static readonly HashSet<string> ManagedMessageKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "messages",
        "contents",
        "input"
    };

    /// <summary>
    /// 根据当前提示词和设置生成请求正文及请求头。
    /// </summary>
    public static BuiltRequest Build(Settings settings, IReadOnlyList<PromptItem> promptItems)
    {
        JsonObject? customBody = null;
        if (!string.IsNullOrWhiteSpace(settings.RequestBodyJson))
            customBody = ParseObject(settings.RequestBodyJson, "自定义请求体");

        // 用户明确提供 contents 或 input 时，先创建同协议的内置消息骨架。
        // 这样仍然保留 model、stream 等未覆盖字段，同时避免不同供应商的消息节点并存。
        var messageShape = DetectMessageShape(customBody);
        var request = CreateDefaultRequest(settings.ModelId, promptItems, messageShape);
        var ignoredPaths = new List<string>();

        if (customBody is not null)
            MergeCustomFields(request, customBody, ignoredPaths);

        InjectPromptItems(request, promptItems, messageShape);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {settings.ApiKey}",
            ["Accept"] = DefaultAccept
        };

        if (!string.IsNullOrWhiteSpace(settings.RequestHeadersJson))
        {
            var customHeaders = ParseObject(settings.RequestHeadersJson, "自定义请求头");
            foreach (var property in customHeaders)
                headers[property.Key] = ConvertJsonValueToHeader(property.Value);
        }

        return new BuiltRequest(
            request,
            headers,
            request.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
            ignoredPaths);
    }

    private static JsonObject CreateDefaultRequest(string modelId, IReadOnlyList<PromptItem> promptItems, MessageShape messageShape)
    {
        var request = new JsonObject
        {
            ["model"] = modelId,
            ["stream"] = true
        };

        request[GetMessagePropertyName(messageShape)] = CreatePromptArray(promptItems, messageShape);
        return request;
    }

    /// <summary>
    /// 将当前 PromptItem 写入用户请求体中的常见文本消息节点，
    /// 同时保留用户请求体中没有被提示词覆盖的其他字段。
    /// </summary>
    private static void InjectPromptItems(JsonObject root, IReadOnlyList<PromptItem> promptItems, MessageShape messageShape)
    {
        var propertyName = GetMessagePropertyName(messageShape);
        if (root[propertyName] is not JsonArray messages)
        {
            root[propertyName] = CreatePromptArray(promptItems, messageShape);
            return;
        }

        var normalizedItems = EnsureUserItem(promptItems);
        for (var index = 0; index < normalizedItems.Count; index++)
        {
            var item = normalizedItems[index];
            JsonObject message;
            if (index < messages.Count && messages[index] is JsonObject existing)
            {
                message = existing;
            }
            else
            {
                message = new JsonObject();
                messages.Add(message);
            }

            WritePromptItem(message, item, messageShape);
        }

        // 自定义数组中多出的旧消息会被移除，确保实际发送的提示词只来自当前启用的官方 Prompt。
        while (messages.Count > normalizedItems.Count)
            messages.RemoveAt(messages.Count - 1);
    }

    /// <summary>
    /// 根据用户请求体明确提供的根级消息节点选择协议。用户没有指定时使用 OpenAI 兼容 messages。
    /// </summary>
    private static MessageShape DetectMessageShape(JsonObject? customBody)
    {
        if (customBody is null) return MessageShape.Messages;
        if (customBody.Any(property => string.Equals(property.Key, "contents", StringComparison.OrdinalIgnoreCase))) return MessageShape.Contents;
        if (customBody.Any(property => string.Equals(property.Key, "input", StringComparison.OrdinalIgnoreCase))) return MessageShape.Input;
        return MessageShape.Messages;
    }

    private static string GetMessagePropertyName(MessageShape messageShape) => messageShape switch
    {
        MessageShape.Input => "input",
        MessageShape.Contents => "contents",
        _ => "messages"
    };

    private static JsonArray CreatePromptArray(IReadOnlyList<PromptItem> promptItems, MessageShape messageShape)
    {
        var messages = new JsonArray();
        foreach (var item in EnsureUserItem(promptItems))
        {
            var message = new JsonObject();
            WritePromptItem(message, item, messageShape);
            messages.Add(message);
        }
        return messages;
    }

    private static IReadOnlyList<PromptItem> EnsureUserItem(IReadOnlyList<PromptItem> promptItems)
    {
        if (promptItems.Any(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)))
            return promptItems;

        return promptItems.Concat([new PromptItem("user", string.Empty)]).ToList();
    }

    private static void WritePromptItem(JsonObject message, PromptItem item, MessageShape messageShape)
    {
        message["role"] = messageShape == MessageShape.Contents && string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? "model"
            : item.Role;

        if (messageShape == MessageShape.Contents)
        {
            message["parts"] = new JsonArray { new JsonObject { ["text"] = item.Content } };
            message.Remove("content");
            return;
        }

        if (messageShape == MessageShape.Input)
        {
            message["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = item.Content } };
            message.Remove("parts");
            return;
        }

        message["content"] = item.Content;
        message.Remove("parts");
    }

    /// <summary>
    /// 递归合并非消息字段。根级消息节点属于插件生成内容，用户重复填写时被忽略，
    /// 其他字段按大小写不敏感方式合并，数组由用户值整体覆盖。
    /// </summary>
    private static void MergeCustomFields(JsonObject target, JsonObject source, ICollection<string> ignoredPaths, string path = "")
    {
        foreach (var property in source)
        {
            var propertyPath = string.IsNullOrEmpty(path) ? property.Key : $"{path}.{property.Key}";
            if (ManagedMessageKeys.Contains(property.Key))
            {
                ignoredPaths.Add(propertyPath);
                continue;
            }

            var existingName = target.Select(item => item.Key)
                .FirstOrDefault(name => string.Equals(name, property.Key, StringComparison.OrdinalIgnoreCase));

            if (existingName is not null && target[existingName] is JsonObject existingObject && property.Value is JsonObject sourceObject)
            {
                MergeCustomFields(existingObject, sourceObject, ignoredPaths, propertyPath);
                continue;
            }

            if (existingName is not null)
                target[existingName] = property.Value?.DeepClone();
            else
                target[property.Key] = property.Value?.DeepClone();
        }
    }

    private static JsonObject ParseObject(string json, string fieldName)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new FormatException($"{fieldName}的根节点必须是 JSON 对象。");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{fieldName}不是有效的 JSON，位置：{ex.BytePositionInLine}。", ex);
        }
    }

    private static string ConvertJsonValueToHeader(JsonNode? value)
    {
        if (value is null) return string.Empty;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue)) return stringValue;
        return value.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}

internal sealed record BuiltRequest(JsonObject Body, Dictionary<string, string> Headers, string RawBody, IReadOnlyList<string> IgnoredCustomPaths);

/// <summary>语言模型请求中承载提示词的根级 JSON 结构。</summary>
internal enum MessageShape
{
    Messages,
    Input,
    Contents
}
