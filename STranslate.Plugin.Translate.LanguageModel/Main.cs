using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using STranslate.Plugin.Translate.LanguageModel.View;
using STranslate.Plugin.Translate.LanguageModel.ViewModel;
using STranslate.Plugin;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.LanguageModel;

/// <summary>
/// 语言模型翻译插件主入口。插件只实现文本翻译接口，
/// 网络请求、提示词存储、主题和日志全部通过 STranslate 官方上下文完成。
/// </summary>
public sealed class Main : LlmTranslatePluginBase
{
    private const string StreamingIdleTimeoutMode = "流式相邻响应空闲超时";
    private const string NonStreamingTotalTimeoutMode = "非流式总超时";
    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private IPluginContext _context = null!;
    private Settings _settings = null!;

    public override void SelectPrompt(Prompt? prompt)
    {
        base.SelectPrompt(prompt);
        if (prompt is null || _settings is null || _context is null) return;
        _settings.Prompts = Prompts.Select(item => item.Clone()).ToList();
        _context.SaveSettingStorage<Settings>();
    }

    public override Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(_context, _settings, this);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public override string? GetSourceLanguage(LangEnum langEnum) => GetLanguageName(langEnum);
    public override string? GetTargetLanguage(LangEnum langEnum) => GetLanguageName(langEnum);

    public override void Init(IPluginContext context)
    {
        _context = context;
        _settings = context.LoadSettingStorage<Settings>();
        Prompts.Clear();
        if (_settings.Prompts is null || _settings.Prompts.Count == 0)
        {
            _settings.Prompts =
            [
                new Prompt("语言模型翻译",
                [
                    new PromptItem("system", string.Empty),
                    new PromptItem("user", string.Empty)
                ], true)
            ];
            _context.SaveSettingStorage<Settings>();
        }

        foreach (var prompt in _settings.Prompts)
            Prompts.Add(prompt);

        if (Prompts.All(prompt => !prompt.IsEnabled) && Prompts.Count > 0)
        {
            Prompts[0].IsEnabled = true;
            _settings.Prompts = Prompts.Select(item => item.Clone()).ToList();
            _context.SaveSettingStorage<Settings>();
        }
    }

    public override void Dispose() => _viewModel?.Dispose();

    public override async Task TranslateAsync(TranslateRequest request, TranslateResult result, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        result.IsProcessing = true;
        try
        {
            var prompt = GetSelectedPrompt();
            ValidateSettings(prompt, requireSystemPrompt: true);
            var source = GetSourceLanguage(request.SourceLang) ?? throw new InvalidOperationException("源语言不受支持。");
            var target = GetTargetLanguage(request.TargetLang) ?? throw new InvalidOperationException("目标语言不受支持。");
            var promptItems = ExpandPrompt(prompt, source, target, request.Text);
            // 将每一段已经过滤掉思考内容的增量文本立即写入宿主结果对象，
            // STranslate 会观察 TranslateResult 的属性变化并持续刷新翻译结果区域。
            // 请求完成后仍会再次写入完整文本，确保重试或分段响应不会留下截断结果。
            var translated = await ExecuteWithRetriesAsync(promptItems, text =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                    result.Success(text);
            }, cancellationToken);
            result.Success(translated);
            _context.Logger.LogInformation("语言模型翻译成功。TextLength={TextLength}, DurationMilliseconds={DurationMilliseconds}", translated.Length, (DateTimeOffset.Now - started).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = BuildFailureMessage("语言模型翻译请求失败", ex, 0, null);
            _context.Logger.LogError(ex, "语言模型翻译执行失败。{Message}", message);
            // 宿主在失败状态下可能不发布结果文本，因此将完整诊断文本作为结果返回，保证用户能够看到实际问题。
            result.Success(message);
        }
        finally
        {
            result.Duration = DateTimeOffset.Now - started;
            result.IsProcessing = false;
        }
    }

    /// <summary>
    /// 设置页校验使用专用 system 和 user 内容，不读取当前翻译提示词，
    /// 但 API、模型、请求体、请求头、重试次数和超时仍使用用户当前设置。
    /// </summary>
    internal async Task ValidateAsync(Action<string> onTextUpdated, CancellationToken cancellationToken = default)
    {
        ValidateSettings(null, requireSystemPrompt: false);
        var validationPrompt = new List<PromptItem>
        {
            new("system", string.Empty),
            new("user", "你好")
        };
        await ExecuteWithRetriesAsync(validationPrompt, onTextUpdated, cancellationToken);
    }

    private async Task<string> ExecuteWithRetriesAsync(IReadOnlyList<PromptItem> promptItems, Action<string>? onTextUpdated, CancellationToken cancellationToken)
    {
        var maxAttempts = _settings.MaxRequestCount!.Value;
        AttemptDiagnostics? lastDiagnostics = null;
        Exception? lastException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var diagnostics = await ExecuteAttemptAsync(promptItems, attempt, onTextUpdated, cancellationToken);
                lastDiagnostics = diagnostics;
                if (!string.IsNullOrWhiteSpace(diagnostics.Text))
                    return diagnostics.Text.Trim();
                lastException = new InvalidOperationException("模型响应中没有非空文本。");
                _context.Logger.LogWarning("语言模型返回空内容，将继续重试。Attempt={Attempt}", attempt);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (ex is AttemptFailedException failed)
                    lastDiagnostics = failed.Diagnostics;
                _context.Logger.LogError(ex, "语言模型请求失败，将继续重试。Attempt={Attempt}", attempt);
            }
        }

        throw new RetryExhaustedException(
            BuildFailureMessage($"语言模型请求失败，已用完全部 {maxAttempts} 次请求次数", lastException ?? new InvalidOperationException("模型没有返回非空文本。"), maxAttempts, lastDiagnostics),
            lastException,
            lastDiagnostics);
    }

    private async Task<AttemptDiagnostics> ExecuteAttemptAsync(IReadOnlyList<PromptItem> promptItems, int attempt, Action<string>? onTextUpdated, CancellationToken cancellationToken)
    {
        var requestStarted = DateTimeOffset.Now;
        var built = RequestBuilder.Build(_settings, promptItems);
        var url = BuildRequestUrl(_settings.ApiUrl);
        var streamEnabled = ReadStreamEnabled(built.Body["stream"]);
        var timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds!.Value);
        var options = new Options
        {
            Headers = built.Headers,
            Timeout = streamEnabled ? Timeout.InfiniteTimeSpan : timeout,
            ContentType = "application/json"
        };
        var headersForLog = string.Join(", ", built.Headers.Select(pair => $"{pair.Key}={RedactHeader(pair.Key, pair.Value)}"));
        var ignoredCustomPaths = built.IgnoredCustomPaths.Count == 0 ? "无" : string.Join(", ", built.IgnoredCustomPaths);
        var ignoredCustomHeaders = built.IgnoredCustomHeaderKeys.Count == 0 ? "无" : string.Join(", ", built.IgnoredCustomHeaderKeys);
        var customBodyFields = built.CustomBodyFields.Count == 0 ? "无" : string.Join(", ", built.CustomBodyFields);
        var customHeaderFields = built.CustomHeaderFields.Count == 0 ? "无" : string.Join(", ", built.CustomHeaderFields);
        _context.Logger.LogInformation("语言模型请求开始。Attempt={Attempt}, RequestTime={RequestTime}, Url={Url}, Model={Model}, Headers={Headers}, TimeoutSeconds={TimeoutSeconds}, TimeoutMode={TimeoutMode}, RequestBodyRaw={RequestBodyRaw}", attempt, requestStarted.ToString("O"), url, _settings.ModelId, headersForLog, _settings.TimeoutSeconds.Value, streamEnabled ? StreamingIdleTimeoutMode : NonStreamingTotalTimeoutMode, built.RawBody);
        _context.Logger.LogInformation("语言模型自定义字段处理。IgnoredManagedPaths={IgnoredManagedPaths}, IgnoredCoreHeaders={IgnoredCoreHeaders}, CustomFieldsRemain=非消息字段均已保留", ignoredCustomPaths, ignoredCustomHeaders);
        _context.Logger.LogInformation("语言模型自定义字段类型。BodyFields={BodyFields}, HeaderFields={HeaderFields}, ThinkingLocation={ThinkingLocation}", customBodyFields, customHeaderFields, customBodyFields.Contains("thinking:", StringComparison.OrdinalIgnoreCase) ? "请求体" : (customHeaderFields.Contains("thinking:", StringComparison.OrdinalIgnoreCase) ? "请求头" : "未填写"));

        var parser = new StreamResponseParser();
        var rawResponse = new StringBuilder();
        var responseLineCount = 0;
        DateTimeOffset? firstResponseAt = null;
        DateTimeOffset? lastResponseAt = null;
        var maxInterResponseWaitMilliseconds = 0d;
        try
        {
            if (streamEnabled)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var streamToken = timeoutCts.Token;
                await using var enumerator = _context.HttpService.StreamPostAsyncEnumerable(url, built.Body, options, streamToken).GetAsyncEnumerator(streamToken);
                while (true)
                {
                    timeoutCts.CancelAfter(timeout);
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                    {
                        throw new TimeoutException($"流式响应空闲超时。超时时间：{timeout.TotalSeconds:0} 秒，最近一次响应时间：{lastResponseAt?.ToString("O") ?? "无"}，已接收响应行数：{responseLineCount}，当前累计文本长度：{parser.CurrentText.Length}。请求已经收到部分响应，但连续等待超过配置的相邻响应间隔。");
                    }
                    finally
                    {
                        timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
                    }

                    if (!hasNext) break;
                    var line = enumerator.Current;
                    if (string.IsNullOrEmpty(line)) continue;
                    var responseAt = DateTimeOffset.Now;
                    var wait = lastResponseAt.HasValue ? (responseAt - lastResponseAt.Value).TotalMilliseconds : (responseAt - requestStarted).TotalMilliseconds;
                    responseLineCount++;
                    firstResponseAt ??= responseAt;
                    lastResponseAt = responseAt;
                    maxInterResponseWaitMilliseconds = Math.Max(maxInterResponseWaitMilliseconds, wait);
                    rawResponse.AppendLine(line);
                    var previous = parser.CurrentText;
                    var current = parser.AppendStreamLine(line);
                    _context.Logger.LogInformation("语言模型流式响应 RAW。Attempt={Attempt}, ResponseTime={ResponseTime}, ResponseLineCount={ResponseLineCount}, InterResponseWaitMilliseconds={InterResponseWaitMilliseconds}, CumulativeTextLength={CumulativeTextLength}, RawLine={RawLine}", attempt, responseAt.ToString("O"), responseLineCount, wait, current.Length, line);
                    if (!string.Equals(previous, current, StringComparison.Ordinal)) onTextUpdated?.Invoke(current);
                }
            }
            else
            {
                var response = await _context.HttpService.PostAsync(url, built.Body, options, cancellationToken);
                rawResponse.Append(response);
                parser.AppendNonStreamingResponse(response);
                onTextUpdated?.Invoke(parser.CurrentText);
            }

            var completed = DateTimeOffset.Now;
            _context.Logger.LogInformation("语言模型响应完成。Attempt={Attempt}, ResponseTime={ResponseTime}, ResponseBodyRaw={ResponseBodyRaw}, ResponseLineCount={ResponseLineCount}, FirstResponseTime={FirstResponseTime}, LastResponseTime={LastResponseTime}, MaxInterResponseWaitMilliseconds={MaxInterResponseWaitMilliseconds}, TimeoutMode={TimeoutMode}, TextLength={TextLength}", attempt, completed.ToString("O"), rawResponse.ToString(), responseLineCount, firstResponseAt?.ToString("O") ?? "无", lastResponseAt?.ToString("O") ?? "无", maxInterResponseWaitMilliseconds, streamEnabled ? StreamingIdleTimeoutMode : NonStreamingTotalTimeoutMode, parser.CurrentText.Length);
            return new AttemptDiagnostics(parser.CurrentText, rawResponse.ToString(), requestStarted, completed, url, built.RawBody, headersForLog, responseLineCount, firstResponseAt, lastResponseAt, maxInterResponseWaitMilliseconds, streamEnabled ? StreamingIdleTimeoutMode : NonStreamingTotalTimeoutMode);
        }
        catch (Exception ex)
        {
            var completed = DateTimeOffset.Now;
            var diagnostics = new AttemptDiagnostics(parser.CurrentText, rawResponse.ToString(), requestStarted, completed, url, built.RawBody, headersForLog, responseLineCount, firstResponseAt, lastResponseAt, maxInterResponseWaitMilliseconds, streamEnabled ? StreamingIdleTimeoutMode : NonStreamingTotalTimeoutMode);
            _context.Logger.LogError(ex, "语言模型响应异常。Attempt={Attempt}, ResponseTime={ResponseTime}, ResponseBodyRaw={ResponseBodyRaw}, ResponseLineCount={ResponseLineCount}", attempt, completed.ToString("O"), rawResponse.ToString(), responseLineCount);
            throw new AttemptFailedException($"第 {attempt} 次请求失败，响应 RAW：{rawResponse}", diagnostics, ex);
        }
    }

    private static IReadOnlyList<PromptItem> ExpandPrompt(Prompt prompt, string source, string target, string content) =>
        prompt.Clone().Items.Select(item => new PromptItem(item.Role, item.Content.Replace("$source", source).Replace("$target", target).Replace("$content", content))).ToList();

    private Prompt GetSelectedPrompt() => Prompts.FirstOrDefault(item => item.IsEnabled) ?? throw new InvalidOperationException("提示词配置中没有启用的提示词，请在设置页选择一个提示词。");

    private void ValidateSettings(Prompt? prompt, bool requireSystemPrompt)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiUrl)) throw new InvalidOperationException("API 地址不能为空。");
        if (!Uri.TryCreate(_settings.ApiUrl.TrimEnd('#').Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("API 地址必须是有效的 HTTP 或 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(_settings.ApiKey)) throw new InvalidOperationException("API 密钥不能为空。");
        if (string.IsNullOrWhiteSpace(_settings.ModelId)) throw new InvalidOperationException("模型 ID 不能为空。");
        if (requireSystemPrompt && string.IsNullOrWhiteSpace(prompt?.Items.FirstOrDefault(item => string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase))?.Content)) throw new InvalidOperationException("当前启用提示词的 system 内容不能为空，请在提示词配置中填写系统提示词。");
        if (_settings.MaxRequestCount is null || _settings.MaxRequestCount < 1) throw new InvalidOperationException("最大请求次数必须大于或等于 1。");
        if (_settings.TimeoutSeconds is null || _settings.TimeoutSeconds < 3) throw new InvalidOperationException("超时时间必须大于或等于 3 秒。");
    }

    private static string BuildRequestUrl(string configuredUrl)
    {
        var normalized = configuredUrl.Trim();
        var resolved = UrlHelper.BuildFinalUrl(normalized, "/v1/chat/completions");
        if (normalized.EndsWith('#')) return resolved;
        if (Uri.TryCreate(resolved, UriKind.Absolute, out var uri) && uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/chat/completions" };
            return builder.Uri.ToString();
        }
        return resolved;
    }

    private static bool ReadStreamEnabled(JsonNode? value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var booleanValue)) return booleanValue;
            if (jsonValue.TryGetValue<string>(out var stringValue) && bool.TryParse(stringValue, out var parsedValue)) return parsedValue;
        }
        return true;
    }

    private static string? GetLanguageName(LangEnum language) => language switch
    {
        LangEnum.Auto => "Auto-detect",
        LangEnum.ChineseSimplified => "Simplified Chinese",
        LangEnum.ChineseTraditional => "Traditional Chinese",
        LangEnum.Cantonese => "Cantonese",
        LangEnum.English => "English",
        LangEnum.Japanese => "Japanese",
        LangEnum.Korean => "Korean",
        LangEnum.French => "French",
        LangEnum.Spanish => "Spanish",
        LangEnum.Russian => "Russian",
        LangEnum.German => "German",
        LangEnum.Italian => "Italian",
        LangEnum.Turkish => "Turkish",
        LangEnum.PortuguesePortugal or LangEnum.PortugueseBrazil => "Portuguese",
        LangEnum.Vietnamese => "Vietnamese",
        LangEnum.Indonesian => "Indonesian",
        LangEnum.Thai => "Thai",
        LangEnum.Malay => "Malay",
        LangEnum.Arabic => "Arabic",
        LangEnum.Hindi => "Hindi",
        LangEnum.MongolianCyrillic or LangEnum.MongolianTraditional => "Mongolian",
        LangEnum.Khmer => "Central Khmer",
        LangEnum.NorwegianBokmal => "Norwegian Bokmål",
        LangEnum.NorwegianNynorsk => "Norwegian Nynorsk",
        LangEnum.Persian => "Persian",
        LangEnum.Swedish => "Swedish",
        LangEnum.Polish => "Polish",
        LangEnum.Dutch => "Dutch",
        LangEnum.Ukrainian => "Ukrainian",
        LangEnum.Uzbek => "Uzbek",
        _ => null
    };

    private static string BuildFailureMessage(string title, Exception exception, int attempts, AttemptDiagnostics? diagnostics) => string.Join(Environment.NewLine, title, $"异常类型：{exception.GetType().FullName}", $"异常信息：{exception.Message}", $"已执行请求次数：{attempts}", diagnostics is null ? "最近一次请求诊断：无" : $"最近一次请求时间：{diagnostics.RequestStarted:O}{Environment.NewLine}请求完成时间：{diagnostics.ResponseCompleted:O}{Environment.NewLine}请求地址：{diagnostics.Url}{Environment.NewLine}请求头：{diagnostics.Headers}{Environment.NewLine}请求体 RAW：{diagnostics.RawRequest}{Environment.NewLine}最近一次响应 RAW：{diagnostics.RawResponse}", diagnostics is null ? "响应行数：无" : $"响应行数：{diagnostics.ResponseLineCount}{Environment.NewLine}首次响应时间：{diagnostics.FirstResponseAt?.ToString("O") ?? "无"}{Environment.NewLine}最近一次响应时间：{diagnostics.LastResponseAt?.ToString("O") ?? "无"}{Environment.NewLine}最大相邻响应间隔毫秒数：{diagnostics.MaxInterResponseWaitMilliseconds}{Environment.NewLine}超时模式：{diagnostics.TimeoutMode}", $"异常堆栈：{exception}");

    private static string RedactHeader(string name, string value) => name.Contains("authorization", StringComparison.OrdinalIgnoreCase) || name.Contains("api-key", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) ? "***" : value;

    private sealed record AttemptDiagnostics(string Text, string RawResponse, DateTimeOffset RequestStarted, DateTimeOffset ResponseCompleted, string Url, string RawRequest, string Headers, int ResponseLineCount, DateTimeOffset? FirstResponseAt, DateTimeOffset? LastResponseAt, double MaxInterResponseWaitMilliseconds, string TimeoutMode);
    private sealed class AttemptFailedException(string message, AttemptDiagnostics diagnostics, Exception innerException) : Exception(message, innerException) { public AttemptDiagnostics Diagnostics { get; } = diagnostics; }
    private sealed class RetryExhaustedException(string message, Exception? innerException, AttemptDiagnostics? diagnostics) : Exception(message, innerException) { public AttemptDiagnostics? Diagnostics { get; } = diagnostics; }
}
