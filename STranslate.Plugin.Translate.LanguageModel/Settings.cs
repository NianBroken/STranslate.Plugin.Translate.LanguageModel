using STranslate.Plugin;

namespace STranslate.Plugin.Translate.LanguageModel;

/// <summary>
/// 保存语言模型翻译插件的全部用户配置。字符串和数值字段默认为空，
/// 由请求入口统一执行必填校验，避免在未配置时静默使用隐含供应商参数。
/// </summary>
public sealed class Settings
{
    /// <summary>语言模型 API 地址。</summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>语言模型 API 密钥。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>语言模型 ID。</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>自定义 JSON 请求体，空值表示使用插件内置的兼容请求体。</summary>
    public string RequestBodyJson { get; set; } = string.Empty;

    /// <summary>自定义 JSON 请求头，空值表示使用插件内置的认证和流式请求头。</summary>
    public string RequestHeadersJson { get; set; } = string.Empty;

    /// <summary>使用 STranslate 官方 Prompt 模型维护 system、user 和其他角色项。</summary>
    public List<Prompt> Prompts { get; set; } = null!;

    /// <summary>一次翻译允许使用的最大请求次数。</summary>
    public int? MaxRequestCount { get; set; }

    /// <summary>单次请求的超时时间，单位为秒。</summary>
    public int? TimeoutSeconds { get; set; }
}
