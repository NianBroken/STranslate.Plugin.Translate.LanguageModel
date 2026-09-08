using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Windows;

namespace STranslate.Plugin.Translate.LanguageModel.ViewModel;

/// <summary>
/// 语言模型翻译设置页视图模型。属性变化立即保存到 STranslate 官方设置存储，
/// 校验命令复用插件真实请求流程并把流式文本发布到结果框。
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPluginContext _context;
    private readonly Settings _settings;
    private readonly Main _main;
    private CancellationTokenSource? _validationCancellation;

    public SettingsViewModel(IPluginContext context, Settings settings, Main main)
    {
        _context = context;
        _settings = settings;
        _main = main;
        ApiUrl = settings.ApiUrl;
        ApiKey = settings.ApiKey;
        ModelId = settings.ModelId;
        RequestBodyJson = settings.RequestBodyJson;
        RequestHeadersJson = settings.RequestHeadersJson;
        MaxRequestCount = settings.MaxRequestCount;
        TimeoutSeconds = settings.TimeoutSeconds;
        PropertyChanged += OnPropertyChanged;
    }

    public Main Main => _main;

    [ObservableProperty] public partial string ApiUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string ApiKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string ModelId { get; set; } = string.Empty;
    [ObservableProperty] public partial string RequestBodyJson { get; set; } = string.Empty;
    [ObservableProperty] public partial string RequestHeadersJson { get; set; } = string.Empty;
    [ObservableProperty] public partial double? MaxRequestCount { get; set; }
    [ObservableProperty] public partial double? TimeoutSeconds { get; set; }
    [ObservableProperty] public partial string ValidationResult { get; private set; } = "校验尚未开始。";
    [ObservableProperty] public partial bool IsValidating { get; private set; }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ApiUrl): _settings.ApiUrl = ApiUrl; break;
            case nameof(ApiKey): _settings.ApiKey = ApiKey; break;
            case nameof(ModelId): _settings.ModelId = ModelId; break;
            case nameof(RequestBodyJson): _settings.RequestBodyJson = RequestBodyJson; break;
            case nameof(RequestHeadersJson): _settings.RequestHeadersJson = RequestHeadersJson; break;
            case nameof(MaxRequestCount): _settings.MaxRequestCount = ConvertNumber(MaxRequestCount); break;
            case nameof(TimeoutSeconds): _settings.TimeoutSeconds = ConvertNumber(TimeoutSeconds); break;
            default: return;
        }
        _context.SaveSettingStorage<Settings>();
    }

    [RelayCommand]
    private async Task ValidateAsync()
    {
        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        IsValidating = true;
        ValidationResult = "正在发送校验请求……";
        try
        {
            await _main.ValidateAsync(text =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess()) ValidationResult = text;
                else dispatcher.Invoke(() => ValidationResult = text);
            }, _validationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            ValidationResult = "校验已取消，未获得模型文本。";
        }
        catch (Exception ex)
        {
            ValidationResult = FormatValidationError(ex);
            _context.Logger.LogError(ex, "语言模型翻译校验失败。{Message}", ValidationResult);
        }
        finally
        {
            IsValidating = false;
        }
    }

    [RelayCommand]
    private void EditPrompt()
    {
        // 直接调用 STranslate 官方提示词编辑窗口，保存后的 PromptItem 继续由宿主模型负责角色、顺序和启用状态。
        var dialog = _context.GetPromptEditWindow(_main.Prompts);
        if (dialog.ShowDialog() != true) return;
        _settings.Prompts = _main.Prompts.Select(prompt => prompt.Clone()).ToList();
        _context.SaveSettingStorage<Settings>();
        _main.SelectedPrompt = _main.Prompts.FirstOrDefault(prompt => prompt.IsEnabled);
    }

    private static int? ConvertNumber(double? value) => value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value) ? null : (int)Math.Truncate(value.Value);

    private static string FormatValidationError(Exception exception)
    {
        var requestDiagnostics = exception.Message.Contains("已执行请求次数：", StringComparison.Ordinal)
            ? exception.Message
            : string.Join(Environment.NewLine,
                $"异常信息：{exception.Message}",
                "已执行请求次数：0",
                "最近一次请求诊断：无");
        return string.Join(Environment.NewLine,
            "语言模型翻译校验失败",
            $"异常类型：{exception.GetType().FullName}",
            requestDiagnostics,
            $"异常堆栈：{exception}");
    }

    public void Dispose()
    {
        PropertyChanged -= OnPropertyChanged;
        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = null;
    }
}
