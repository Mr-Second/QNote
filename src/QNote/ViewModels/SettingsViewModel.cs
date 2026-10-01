using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using QNote.EdgeHide;
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
    private readonly IGlobalHotkey _hotkey;
    private readonly IStartupTaskService _startup;
    private readonly IGitHubUpdateService _update;
    private readonly ILogger<SettingsViewModel> _log;

    private bool _loading = true;
    private bool _suppressStartupToggle;
    private AppSettings _snapshot = new();

    public SettingsViewModel(
        ISettingsService settings,
        IGlobalHotkey hotkey,
        IStartupTaskService startup,
        IGitHubUpdateService update,
        bool isPackaged,
        ILogger<SettingsViewModel> log)
    {
        _settings = settings;
        _hotkey = hotkey;
        _startup = startup;
        _update = update;
        _log = log;

        // 检查更新入口仅解包（portable）模式显示：Store 渠道自带自动更新。
        UpdateCheckVisible = !isPackaged;
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

    /// <summary>Auto-save interval choice: 0=关闭, then 0.5s / 1s / 2s / 5s / 10s / 30s.</summary>
    [ObservableProperty]
    public partial int AutoSaveIndex { get; set; }

    private static readonly int[] AutoSaveOptions = { 0, 500, 1000, 2000, 5000, 10000, 30000 };

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

    /// <summary>
    /// 开机自启动。Reflects the OS StartupTask state directly (single source of truth);
    /// NOT part of the <see cref="AppSettings"/> snapshot.
    /// </summary>
    [ObservableProperty]
    public partial bool LaunchAtStartup { get; set; }

    /// <summary>开机自启动开关可用性（策略锁定 / 不可用时禁用）.</summary>
    [ObservableProperty]
    public partial bool LaunchAtStartupToggleEnabled { get; set; } = true;

    /// <summary>开机自启动提示（被系统/策略禁用时）；空串 = 无提示.</summary>
    [ObservableProperty]
    public partial string LaunchAtStartupHint { get; set; } = "";

    /// <summary>贴边自动隐藏：窗口拖到屏幕顶边后自动滑出隐藏.</summary>
    [ObservableProperty]
    public partial bool EdgeHideEnabled { get; set; }

    /// <summary>贴边隐藏时同时隐藏任务栏图标.</summary>
    [ObservableProperty]
    public partial bool HideTaskbarIconOnEdgeHide { get; set; }

    /// <summary>当前热键显示文本（如 "Win + `"）；录入态显示提示语.</summary>
    [ObservableProperty]
    public partial string HotkeyDisplay { get; set; } = "";

    /// <summary>热键注册失败 / 录入校验提示；空串 = 无错误.</summary>
    [ObservableProperty]
    public partial string HotkeyError { get; set; } = "";

    /// <summary>Called by the view before showing the panel.</summary>
    public async Task InitializeAsync()
    {
        _snapshot = await _settings.LoadAsync();

        _loading = true;
        DensityIndex = (int)_snapshot.ListDensity;
        TimeFormatIndex = (int)_snapshot.TimeFormat;
        SortIndex = (int)_snapshot.NoteSortOrder;
        ConfirmBeforeDelete = _snapshot.ConfirmBeforeDelete;
        AutoSaveIndex = Math.Max(0, Array.IndexOf(AutoSaveOptions, _snapshot.AutoSaveMilliseconds));
        ThemeIndex = _snapshot.ThemeMode switch { "light" => 1, "dark" => 2, _ => 0 };
        AlwaysOnTop = _snapshot.AlwaysOnTop;
        RememberWindowGeometry = _snapshot.RememberWindowGeometry;
        StartMinimized = _snapshot.StartMinimized;
        EdgeHideEnabled = _snapshot.EdgeHideEnabled;
        HideTaskbarIconOnEdgeHide = _snapshot.HideTaskbarIconOnEdgeHide;
        UpdateHotkeyDisplay();
        // Startup registration may already have failed (combo owned by another app).
        HotkeyError = _snapshot.EdgeHideHotkeyKey != 0 && !_hotkey.IsRegistered
            ? "热键注册失败：可能已被其他程序占用，请更换按键"
            : "";
        _loading = false;

        await RefreshStartupStateAsync();
    }

    partial void OnDensityIndexChanged(int value) => Save(_snapshot with { ListDensity = (NoteListDensity)value });

    partial void OnTimeFormatIndexChanged(int value) => Save(_snapshot with { TimeFormat = (NoteTimeFormat)value });

    partial void OnSortIndexChanged(int value) => Save(_snapshot with { NoteSortOrder = (NoteSortOrder)value });

    partial void OnConfirmBeforeDeleteChanged(bool value) => Save(_snapshot with { ConfirmBeforeDelete = value });

    partial void OnAutoSaveIndexChanged(int value) =>
        Save(_snapshot with { AutoSaveMilliseconds = AutoSaveOptions[Math.Clamp(value, 0, AutoSaveOptions.Length - 1)] });

    partial void OnThemeIndexChanged(int value) =>
        Save(_snapshot with { ThemeMode = value switch { 1 => "light", 2 => "dark", _ => "system" } });

    partial void OnAlwaysOnTopChanged(bool value) => Save(_snapshot with { AlwaysOnTop = value });

    partial void OnRememberWindowGeometryChanged(bool value) => Save(_snapshot with { RememberWindowGeometry = value });

    partial void OnStartMinimizedChanged(bool value) => Save(_snapshot with { StartMinimized = value });

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_loading || _suppressStartupToggle)
            return;
        _ = ApplyStartupAsync(value);
    }

    /// <summary>
    /// Toggle → drive the OS startup task, then reconcile the UI with the resulting
    /// OS state (RequestEnableAsync can be refused, e.g. DisabledByUser).
    /// </summary>
    private async Task ApplyStartupAsync(bool enable)
    {
        try
        {
            _ = enable
                ? await _startup.RequestEnableAsync()
                : await _startup.DisableAsync();
        }
        finally
        {
            await RefreshStartupStateAsync();
        }
    }

    /// <summary>Pull the OS StartupTask state into the toggle + hint (source of truth).</summary>
    private async Task RefreshStartupStateAsync()
    {
        var state = await _startup.GetStateAsync();

        _suppressStartupToggle = true;
        try
        {
            switch (state)
            {
                case StartupTaskStatus.Enabled:
                    LaunchAtStartup = true;
                    LaunchAtStartupToggleEnabled = true;
                    LaunchAtStartupHint = "";
                    break;
                case StartupTaskStatus.EnabledByPolicy:
                    LaunchAtStartup = true;
                    LaunchAtStartupToggleEnabled = false;
                    LaunchAtStartupHint = "已由系统策略启用";
                    break;
                case StartupTaskStatus.DisabledByUser:
                    LaunchAtStartup = false;
                    LaunchAtStartupToggleEnabled = true;
                    LaunchAtStartupHint = "已被系统禁用，请在任务管理器→启动应用 中重新启用";
                    break;
                case StartupTaskStatus.DisabledByPolicy:
                    LaunchAtStartup = false;
                    LaunchAtStartupToggleEnabled = false;
                    LaunchAtStartupHint = "已被系统策略禁用";
                    break;
                case StartupTaskStatus.Unavailable:
                    LaunchAtStartup = false;
                    LaunchAtStartupToggleEnabled = false;
                    LaunchAtStartupHint = "当前环境不支持开机自启动";
                    break;
                default: // Disabled
                    LaunchAtStartup = false;
                    LaunchAtStartupToggleEnabled = true;
                    LaunchAtStartupHint = "";
                    break;
            }
        }
        finally
        {
            _suppressStartupToggle = false;
        }
    }

    partial void OnEdgeHideEnabledChanged(bool value) => Save(_snapshot with { EdgeHideEnabled = value });

    partial void OnHideTaskbarIconOnEdgeHideChanged(bool value) => Save(_snapshot with { HideTaskbarIconOnEdgeHide = value });

    // ---------- 热键录入（view calls these; capture mechanics live in the view） ----------

    /// <summary>进入录入态：按钮显示提示语.</summary>
    public void BeginHotkeyCapture() => HotkeyDisplay = "按下快捷键…";

    /// <summary>Esc 取消：恢复显示当前热键.</summary>
    public void CancelHotkeyCapture() => UpdateHotkeyDisplay();

    /// <summary>
    /// 录入完成：持久化并重新注册（先注销旧键）；注册失败时提示但不回滚设置、不崩应用。
    /// </summary>
    public void CommitHotkey(int modifiers, int virtualKey)
    {
        Save(_snapshot with { EdgeHideHotkeyModifiers = modifiers, EdgeHideHotkeyKey = virtualKey });
        UpdateHotkeyDisplay();
        HotkeyError = _hotkey.TryRegister(modifiers, virtualKey)
            ? ""
            : "热键注册失败：可能已被其他程序占用，请更换按键";
    }

    /// <summary>清空 = 禁用手动热键（Backspace/Delete 录入）.</summary>
    public void ClearHotkey() => CommitHotkey(0, 0);

    private void UpdateHotkeyDisplay() =>
        HotkeyDisplay = _snapshot.EdgeHideHotkeyKey == 0
            ? "未设置"
            : HotkeyFormat.ToDisplay(_snapshot.EdgeHideHotkeyModifiers, _snapshot.EdgeHideHotkeyKey);

    // ---------- 检查更新（仅解包 / portable 模式显示入口） ----------

    /// <summary>是否显示「检查更新」行（由包身份驱动；packaged / Store 渠道隐藏）.</summary>
    [ObservableProperty]
    public partial bool UpdateCheckVisible { get; set; }

    /// <summary>检查更新按钮文字；命令运行中显示「检查中…」.</summary>
    [ObservableProperty]
    public partial string UpdateCheckButtonText { get; set; } = "检查更新…";

    /// <summary>
    /// 检查完成（服务结果，非异常）。弹窗编排归视图：设置对话框先关闭，
    /// 再由 NotesPage 弹结果对话框（同一时刻只允许一个 ContentDialog）。
    /// </summary>
    public event Action<UpdateCheckResult>? UpdateCheckCompleted;

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateCheckButtonText = "检查中…";
        try
        {
            var result = await _update.CheckLatestAsync();
            UpdateCheckCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            // Defensive only — the service contract is result-not-exception. A
            // violation still surfaces as a Failed result instead of crashing.
            _log.LogError(ex, "检查更新命令失败");
            UpdateCheckCompleted?.Invoke(new UpdateCheckResult(UpdateCheckStatus.Failed, null, null, ex.Message));
        }
        finally
        {
            UpdateCheckButtonText = "检查更新…";
        }
    }

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
