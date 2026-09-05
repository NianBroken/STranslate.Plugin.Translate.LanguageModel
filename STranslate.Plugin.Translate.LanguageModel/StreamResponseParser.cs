using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STranslate.Plugin.Translate.LanguageModel;

/// <summary>
/// 解析常见语言模型的 SSE 和普通 JSON 响应。解析器只保留模型最终文本，
/// 自动过滤思考标签、推理字段和工具调用内容，避免中间过程进入翻译结果。
/// </summary>
internal sealed class StreamResponseParser
{
    private readonly StringBuilder _text = new();
    private bool _insideThinkTag;

    public string CurrentText => RemoveHiddenText(_text.ToString());

    public string AppendStreamLine(string line)
    {
        if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("retry:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith(":", StringComparison.Ordinal))
            return CurrentText;

        var payload = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? line[5..].Trim()
            : line.Trim();
        if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            return CurrentText;

        JsonNode json;
        try
        {
            json = JsonNode.Parse(payload) ?? throw new FormatException("流式响应 JSON 根节点为空。");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new FormatException($"流式响应不是有效 JSON，原始数据：{line}，解析错误：{ex.Message}", ex);
        }

        var error = ExtractError(json);
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"模型返回错误：{error}");

        var text = ExtractStreamText(json);
        if (!string.IsNullOrEmpty(text))
            AppendVisibleText(text);
        return CurrentText;
    }

    public string AppendNonStreamingResponse(string rawResponse)
    {
        JsonNode json;
        try
        {
            json = JsonNode.Parse(rawResponse) ?? throw new FormatException("普通响应 JSON 根节点为空。");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new FormatException($"普通响应不是有效 JSON，原始响应：{rawResponse}，解析错误：{ex.Message}", ex);
        }

        var error = ExtractError(json);
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"模型返回错误：{error}");

        var text = ExtractNonStreamingText(json);
        if (!string.IsNullOrEmpty(text))
            AppendVisibleText(text);
        return CurrentText;
    }

    private void AppendVisibleText(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var index = 0;
        while (index < normalized.Length)
        {
            if (!_insideThinkTag && normalized[index..].StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
            {
                _insideThinkTag = true;
                index += "<think>".Length;
                continue;
            }

            if (_insideThinkTag)
            {
                var end = normalized.IndexOf("</think>", index, StringComparison.OrdinalIgnoreCase);
                if (end < 0) return;
                _insideThinkTag = false;
                index = end + "</think>".Length;
                continue;
            }

            var nextTag = normalized.IndexOf("<think>", index, StringComparison.OrdinalIgnoreCase);
            var endIndex = nextTag >= 0 ? nextTag : normalized.Length;
            _text.Append(normalized[index..endIndex]);
            index = endIndex;
        }
    }

    private static string? ExtractStreamText(JsonNode root)
    {
        var choices = root["choices"] as JsonArray;
        if (choices is { Count: > 0 })
        {
            var choice = choices[0];
            var delta = choice?["delta"];
            return ReadText(delta?["content"])
                ?? ReadText(delta?["text"])
                ?? ReadText(choice?["message"]?["content"]);
        }

        return ReadText(root["output_text"])
            ?? ReadText(root["delta"]?["text"])
            ?? ReadText(root["delta"])
            ?? ReadText(root["content_block_delta"]?["delta"]?["text"])
            ?? ReadText(root["candidates"]?[0]?["content"]?["parts"]?[0]?["text"])
            ?? ReadText(root["content"]?[0]?["text"]);
    }

    private static string? ExtractNonStreamingText(JsonNode root)
    {
        var choices = root["choices"] as JsonArray;
        if (choices is { Count: > 0 })
        {
            var choice = choices[0];
            return ReadText(choice?["message"]?["content"])
                ?? ReadText(choice?["text"])
                ?? ReadText(choice?["delta"]?["content"]);
        }

        return ReadText(root["output_text"])
            ?? ReadText(root["output"]?[0]?["content"])
            ?? ReadText(root["candidates"]?[0]?["content"]?["parts"]?[0]?["text"])
            ?? ReadText(root["content"])
            ?? ReadText(root["text"]);
    }

    private static string? ReadText(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (node is JsonArray array)
        {
            var builder = new StringBuilder();
            foreach (var item in array)
                builder.Append(ReadText(item?["text"] ?? item?["content"] ?? item));
            return builder.Length == 0 ? null : builder.ToString();
        }
        return null;
    }

    private static string? ExtractError(JsonNode root) =>
        ReadText(root["error"]?["message"])
        ?? ReadText(root["response"]?["error"]?["message"])
        ?? ReadText(string.Equals(root["type"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase) ? root["message"] : null);

    private static string RemoveHiddenText(string text)
    {
        var result = Regex.Replace(text, "<think>.*?</think>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        result = Regex.Replace(result, "<think>.*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return result.Trim();
    }
}
