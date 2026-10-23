using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using QNote.EdgeHide;
using QNote.Models;
using QNote.Services;
using QNote.Sync;

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
    private readonly ITokenProvider _syncTokens;
    private readonly ILogger<SettingsViewModel> _log;

    private bool _loading = true;
    private bool _suppressStartupToggle;
    private AppSettings _snapshot = new();

    public SettingsViewModel(
        ISettingsService settings,
        IGlobalHotkey hotkey,
        IStartupTaskService startup,
        IGitHubUpdateService update,
        ITokenProvider syncTokens,
        bool isPackaged,
        ILogger<SettingsViewModel> log)
    {
        _settings = settings;
        _hotkey = hotkey;
        _startup = startup;
        _update = update;
        _syncTokens = syncTokens;
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

    /// <summary>语言: 0 跟随系统 / 1 中文 / 2 English. A change rebuilds the UI tree.</summary>
    [ObservableProperty]
    public partial int LanguageIndex { get; set; }

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; set; }

    [ObservableProperty]
    public partial bool RememberWindowGeometry { get; set; }

    /// <summary>启动时最小化到托盘（不显示主窗口）.</summary>
    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    /// <summary>
    /// 启动时检查更新（1.5.1）：首次激活约 5 秒后查询一次渠道更新源，有新版弹提示。
    /// 弹窗的「不再提示」勾选会把本开关写为关闭。
    /// </summary>
    [ObservableProperty]
    public partial bool CheckUpdatesOnStartup { get; set; } = true;

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

    // ---------- 待办 ----------

    /// <summary>智能清单可见性 (list-options ⋯ menu, 2026-10-16): the RESTORE path
    /// for 隐藏智能列表 — the ⋯ menu hides, these switches unhide. 全部 is the
    /// fallback view and never hideable.</summary>
    [ObservableProperty]
    public partial bool TodoTodayVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool TodoImportantVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool TodoPlannedVisible { get; set; } = true;

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
        LanguageIndex = _snapshot.LanguageMode switch { "zh" => 1, "en" => 2, _ => 0 };
        AlwaysOnTop = _snapshot.AlwaysOnTop;
        RememberWindowGeometry = _snapshot.RememberWindowGeometry;
        StartMinimized = _snapshot.StartMinimized;
        CheckUpdatesOnStartup = _snapshot.CheckUpdatesOnStartup;
        EdgeHideEnabled = _snapshot.EdgeHideEnabled;
        HideTaskbarIconOnEdgeHide = _snapshot.HideTaskbarIconOnEdgeHide;
        var hiddenSmart = TodoSmartViewOptions.ParseSet(_snapshot.TodoHiddenSmartLists);
        TodoTodayVisible = !hiddenSmart.Contains((int)TodoNavKind.Today);
        TodoImportantVisible = !hiddenSmart.Contains((int)TodoNavKind.Important);
        TodoPlannedVisible = !hiddenSmart.Contains((int)TodoNavKind.Planned);
        UpdateHotkeyDisplay();
        // Startup registration may already have failed (combo owned by another app).
        HotkeyError = _snapshot.EdgeHideHotkeyKey != 0 && !_hotkey.IsRegistered
            ? AppStrings.GetString("HotkeyRegisterFailed")
            : "";
        _loading = false;

        await RefreshStartupStateAsync();
        // 同步账号（MSAL 本地账号枚举，无网络）随面板打开一起刷新。
        await RefreshSyncAccountAsync();
    }

    partial void OnDensityIndexChanged(int value) => Save(_snapshot with { ListDensity = (NoteListDensity)value });

    partial void OnTimeFormatIndexChanged(int value) => Save(_snapshot with { TimeFormat = (NoteTimeFormat)value });

    partial void OnSortIndexChanged(int value) => Save(_snapshot with { NoteSortOrder = (NoteSortOrder)value });

    partial void OnConfirmBeforeDeleteChanged(bool value) => Save(_snapshot with { ConfirmBeforeDelete = value });

    partial void OnAutoSaveIndexChanged(int value) =>
        Save(_snapshot with { AutoSaveMilliseconds = AutoSaveOptions[Math.Clamp(value, 0, AutoSaveOptions.Length - 1)] });

    partial void OnThemeIndexChanged(int value) =>
        Save(_snapshot with { ThemeMode = value switch { 1 => "light", 2 => "dark", _ => "system" } });

    partial void OnLanguageIndexChanged(int value) =>
        Save(_snapshot with { LanguageMode = value switch { 1 => "zh", 2 => "en", _ => "system" } });

    partial void OnAlwaysOnTopChanged(bool value) => Save(_snapshot with { AlwaysOnTop = value });

    partial void OnRememberWindowGeometryChanged(bool value) => Save(_snapshot with { RememberWindowGeometry = value });

    partial void OnStartMinimizedChanged(bool value) => Save(_snapshot with { StartMinimized = value });

    partial void OnCheckUpdatesOnStartupChanged(bool value) => Save(_snapshot with { CheckUpdatesOnStartup = value });

    partial void OnTodoTodayVisibleChanged(bool value) => SaveSmartListVisibility((int)TodoNavKind.Today, value);

    partial void OnTodoImportantVisibleChanged(bool value) => SaveSmartListVisibility((int)TodoNavKind.Important, value);

    partial void OnTodoPlannedVisibleChanged(bool value) => SaveSmartListVisibility((int)TodoNavKind.Planned, value);

    /// <summary>Reflect one smart-list visibility switch into the hidden-kinds
    /// settings CSV (TodoSmartViewOptions).</summary>
    private void SaveSmartListVisibility(int kind, bool visible)
    {
        if (_loading)
            return;
        var hidden = new HashSet<int>(TodoSmartViewOptions.ParseSet(_snapshot.TodoHiddenSmartLists));
        if (visible)
            hidden.Remove(kind);
        else
            hidden.Add(kind);
        Save(_snapshot with { TodoHiddenSmartLists = TodoSmartViewOptions.FormatSet(hidden) });
    }

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
                    LaunchAtStartupHint = AppStrings.GetString("StartupEnabledByPolicy");
                    break;
                case StartupTaskStatus.DisabledByUser:
                    LaunchAtStartup = false;
                    LaunchAtStartupToggleEnabled = true;
                    LaunchAtStartupHint = AppStrings.GetString("StartupDisabledByUser");
                    break;
                case StartupTaskStatus.DisabledByPolicy:
                    LaunchAtStartup = false;
                    LaunchAtStartupToggleEnabled = false;
                    LaunchAtStartupHint = AppStrings.GetString("StartupDisabledByPolicy");
                    break;
                case StartupTaskStatus.Unavailable:
                    LaunchAtStartup = false;
                    LaunchAtStartupToggleEnabled = false;
                    LaunchAtStartupHint = AppStrings.GetString("StartupUnavailable");
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
    public void BeginHotkeyCapture() => HotkeyDisplay = AppStrings.GetString("HotkeyCaptureHint");

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
            : AppStrings.GetString("HotkeyRegisterFailed");
    }

    /// <summary>清空 = 禁用手动热键（Backspace/Delete 录入）.</summary>
    public void ClearHotkey() => CommitHotkey(0, 0);

    private void UpdateHotkeyDisplay() =>
        HotkeyDisplay = _snapshot.EdgeHideHotkeyKey == 0
            ? AppStrings.GetString("HotkeyNotSet")
            : HotkeyFormat.ToDisplay(_snapshot.EdgeHideHotkeyModifiers, _snapshot.EdgeHideHotkeyKey);

    // ---------- 检查更新（仅解包 / portable 模式显示入口） ----------

    /// <summary>是否显示「检查更新」行（由包身份驱动；packaged / Store 渠道隐藏）.</summary>
    [ObservableProperty]
    public partial bool UpdateCheckVisible { get; set; }

    /// <summary>检查更新按钮文字；命令运行中显示「检查中…」.</summary>
    [ObservableProperty]
    public partial string UpdateCheckButtonText { get; set; } = AppStrings.GetString("CheckUpdateButton");

    /// <summary>
    /// 检查完成（服务结果，非异常）。弹窗编排归视图：设置对话框先关闭，
    /// 再由 NotesPage 弹结果对话框（同一时刻只允许一个 ContentDialog）。
    /// </summary>
    public event Action<UpdateCheckResult>? UpdateCheckCompleted;

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateCheckButtonText = AppStrings.GetString("CheckingUpdateButton");
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
            UpdateCheckButtonText = AppStrings.GetString("CheckUpdateButton");
        }
    }

    // ---------- 同步（L2 OneDrive，task 10-06） ----------

    /// <summary>登录账号显示（未登录显示「未登录」）；作为登录/注销按钮的 ToolTip.</summary>
    [ObservableProperty]
    public partial string SyncAccountDisplay { get; set; } = "";

    /// <summary>是否已登录（驱动单个 登录/注销 按钮的文本与行为）.</summary>
    [ObservableProperty]
    public partial bool SyncSignedIn { get; set; }

    /// <summary>未登录按钮文本（视图绑定；语言切换会重建 UI 树）.</summary>
    public string SyncSignInText => AppStrings.GetString("SyncSignIn");

    /// <summary>已登录按钮文本.</summary>
    public string SyncSignOutText => AppStrings.GetString("SyncSignOut");

    /// <summary>登录/登出失败提示；空串 = 无错误.</summary>
    [ObservableProperty]
    public partial string SyncError { get; set; } = "";

    /// <summary>SyncError 非空 → 显示错误行（空串时收起，否则空行把按钮顶离垂直居中）.</summary>
    public bool SyncHasError => SyncError.Length > 0;

    partial void OnSyncErrorChanged(string value) => OnPropertyChanged(nameof(SyncHasError));

    /// <summary>
    /// 登录/注销成功。弹窗编排归视图：SettingsPanel 据此弹 ResultTip toast
    /// （成功=绿卡，自动消失）。失败不经过此事件 — 走内联 SyncError。
    /// </summary>
    public event Action<SyncAuthOutcome>? SyncAuthCompleted;

    /// <summary>SettingsPanel 打开时拉取当前账号（MSAL 账号枚举 = 本地缓存读取，无网络）.</summary>
    public async Task RefreshSyncAccountAsync()
    {
        try
        {
            var account = await _syncTokens.GetSignedInAccountAsync();
            SyncSignedIn = account is not null;
            SyncAccountDisplay = account?.Username is { Length: > 0 } username
                ? username
                : AppStrings.GetString("SyncNotSignedIn");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "读取同步账号失败");
            SyncSignedIn = false;
            SyncAccountDisplay = AppStrings.GetString("SyncNotSignedIn");
        }
    }

    /// <summary>
    /// 登录/注销切换按钮：未登录 → 交互式登录（WAM 账号选择器，必须已在 UI 线程上）；
    /// 已登录 → 注销并清空本地账号缓存。
    /// </summary>
    [RelayCommand]
    private async Task SyncAuthAsync()
    {
        SyncError = "";
        var wasSignedIn = SyncSignedIn; // pre-command state (same rule as the error mapping)
        try
        {
            // The button state flips from the OPERATION outcome, never from the
            // follow-up enumeration: WAM broker enumeration lags seconds or comes
            // back empty right after an interactive sign-in (live-caught), which
            // left the button reading 登录 until the panel was reopened.
            // RefreshSyncAccountAsync still reconciles afterwards.
            if (wasSignedIn)
            {
                await _syncTokens.SignOutAsync();
                SyncSignedIn = false;
                SyncAccountDisplay = AppStrings.GetString("SyncNotSignedIn");
                SyncAuthCompleted?.Invoke(new SyncAuthOutcome(
                    Success: true, SignedIn: false,
                    Title: AppStrings.GetString("SyncSignOutSuccess"),
                    Message: ""));
            }
            else
            {
                var result = await _syncTokens.AcquireInteractiveAsync();
                SyncSignedIn = true;
                SyncAccountDisplay = result.Account?.Username is { Length: > 0 } username
                    ? username
                    : AppStrings.GetString("SyncNotSignedIn");
                SyncAuthCompleted?.Invoke(new SyncAuthOutcome(
                    Success: true, SignedIn: true,
                    Title: AppStrings.GetString("SyncSignInSuccess"),
                    Message: result.Account?.Username ?? ""));
            }
            _log.LogInformation("同步账号状态切换成功（nowSignedIn={SignedIn}）", !wasSignedIn);
            await RefreshSyncAccountAsync();
        }
        catch (OperationCanceledException)
        {
            // 用户关闭了系统账号选择器 — 正常路径，不是错误。（WAM 取消已在
            // MsalTokenProvider 归一化为 OCE；这里无 ct，勿加 when 过滤。）
        }
        catch (Exception ex)
        {
            // wasSignedIn: true = a sign-out was attempted, false = a sign-in.
            _log.LogWarning(ex, wasSignedIn ? "退出同步账号失败" : "交互式同步登录失败");
            SyncError = AppStrings.GetString(wasSignedIn ? "SyncSignOutError" : "SyncSignInError");
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

/// <summary>登录/注销成功的 toast 载荷（失败走内联 SyncError，不弹 toast）.
/// SignedIn = 命令完成后的状态，视图据此立即重涂按钮（不等账号重枚举）.</summary>
public sealed record SyncAuthOutcome(bool Success, bool SignedIn, string Title, string Message);
