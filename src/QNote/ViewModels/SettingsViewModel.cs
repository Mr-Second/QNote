using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using QNote.Models;
using QNote.Services;

namespace QNote.ViewModels;

/// <summary>
/// Settings panel view-model. Loads the typed <see cref="AppSettings"/> snapshot
/// once (<see cref="InitializeAsync"/>), exposes index/bool properties for the
/// controls, and saves the whole snapshot on every change — settings apply
/// immediately, there is no save button (Qt parity: SettingsDialog).
/// Indices mirror the enum values (0/1/2 in declaration order).
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ILogger<SettingsViewModel> _log;

    private bool _loading = true;
    private AppSettings _snapshot = new();

    public SettingsViewModel(ISettingsService settings, ILogger<SettingsViewModel> log)
    {
        _settings = settings;
        _log = log;
    }

    // ---------- 显示 ----------

    /// <summary>列表密度: 0 紧凑 / 1 标准 / 2 宽松.</summary>
    [ObservableProperty]
    public partial int DensityIndex { get; set; } = 1;

    /// <summary>时间格式: 0 相对 / 1 分级 / 2 完整.</summary>
    [ObservableProperty]
    public partial int TimeFormatIndex { get; set; } = 1;

    /// <summary>便签排序: 0 更新时间 / 1 创建时间 / 2 标题.</summary>
    [ObservableProperty]
    public partial int SortIndex { get; set; }

    [ObservableProperty]
    public partial bool ConfirmBeforeDelete { get; set; } = true;

    // ---------- 常规 ----------

    /// <summary>主题: 0 跟随系统 / 1 浅色 / 2 深色.</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; set; }

    [ObservableProperty]
    public partial bool RememberWindowGeometry { get; set; }

    /// <summary>启动时最小化到托盘（不显示主窗口）.</summary>
    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    /// <summary>Called by the view before showing the panel.</summary>
    public async Task InitializeAsync()
    {
        _snapshot = await _settings.LoadAsync();

        _loading = true;
        DensityIndex = (int)_snapshot.ListDensity;
        TimeFormatIndex = (int)_snapshot.TimeFormat;
        SortIndex = (int)_snapshot.NoteSortOrder;
        ConfirmBeforeDelete = _snapshot.ConfirmBeforeDelete;
        ThemeIndex = _snapshot.ThemeMode switch { "light" => 1, "dark" => 2, _ => 0 };
        AlwaysOnTop = _snapshot.AlwaysOnTop;
        RememberWindowGeometry = _snapshot.RememberWindowGeometry;
        StartMinimized = _snapshot.StartMinimized;
        _loading = false;
    }

    partial void OnDensityIndexChanged(int value) => Save(_snapshot with { ListDensity = (NoteListDensity)value });

    partial void OnTimeFormatIndexChanged(int value) => Save(_snapshot with { TimeFormat = (NoteTimeFormat)value });

    partial void OnSortIndexChanged(int value) => Save(_snapshot with { NoteSortOrder = (NoteSortOrder)value });

    partial void OnConfirmBeforeDeleteChanged(bool value) => Save(_snapshot with { ConfirmBeforeDelete = value });

    partial void OnThemeIndexChanged(int value) =>
        Save(_snapshot with { ThemeMode = value switch { 1 => "light", 2 => "dark", _ => "system" } });

    partial void OnAlwaysOnTopChanged(bool value) => Save(_snapshot with { AlwaysOnTop = value });

    partial void OnRememberWindowGeometryChanged(bool value) => Save(_snapshot with { RememberWindowGeometry = value });

    partial void OnStartMinimizedChanged(bool value) => Save(_snapshot with { StartMinimized = value });

    private void Save(AppSettings next)
    {
        if (_loading)
            return;

        _snapshot = next;
        _ = PersistAsync(next);
    }

    private async Task PersistAsync(AppSettings next)
    {
        try
        {
            await _settings.SaveAsync(next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "保存设置失败");
        }
    }
}
