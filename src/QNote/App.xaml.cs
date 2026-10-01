using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using QNote.Data;
using QNote.Data.Schema;
using QNote.Infrastructure;
using QNote.Infrastructure.Logging;
using QNote.Services;
using QNote.ViewModels;
using QNote.Views;

namespace QNote;

/// <summary>
/// Application entry point and DI composition root. This is the only place that
/// wires services, repositories, and view-models together.
/// </summary>
public partial class App : Application
{
    /// <summary>The main application window.</summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>The UI thread dispatcher.</summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>The native window handle (HWND) for pickers / WinRT interop.</summary>
    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>Application service provider. Resolve view-models and services from here.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        // Register the zh-Hans string table for the vendored editor's chrome before
        // any editor is built (QNote UI is zh-Hans source — see project-context).
        QNote.Controls.EditorLocalization.Register();
        // Install QNote's Lucide icon provider for the vendored editor chrome (toolbar
        // buttons; the context menu keeps its Segoe glyphs). Must run before the first
        // toolbar is built — same timing as the localization table above.
        WinUIRichEditor.Controls.RichEditorIcons.Provider = icon => QNote.Controls.QNoteIcons.Create(icon);
        Services = ConfigureServices();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Infrastructure bring-up: data directories, crash capture, DB schema.
        var paths = Services.GetRequiredService<AppPaths>();
        paths.EnsureCreated();

        var crash = Services.GetRequiredService<CrashHandler>();
        crash.Register();
        UnhandledException += (_, e) => crash.Capture("App.UnhandledException", e.Exception);

        Services.GetRequiredService<SchemaInitializer>().EnsureCreated();

        Services.GetRequiredService<ILogger<App>>()
            .LogInformation("QNote starting. Data root: {Root}", paths.Root);

        // Portable (unpackaged) mode logging: which root was chosen and why.
        // The decision is registered only for unpackaged runs — null = packaged.
        if (Services.GetService<PortableDataRootDecision>() is { } portable)
        {
            var logger = Services.GetRequiredService<ILogger<App>>();
            if (portable.RootOverride is { } portableRoot)
                logger.LogInformation("Portable mode: data root {Root}.", portableRoot);
            else
                logger.LogWarning(portable.ProbeError,
                    "Portable data root {Root} is not writable; falling back to the Roaming root.",
                    portable.ProbedPath);
        }

        // Startup milestone (perf R1): process start ≈ Main entry (see StartupClock).
        Services.GetRequiredService<ILogger<App>>()
            .LogInformation("Startup milestone: OnLaunched at {ElapsedMs:0} ms since process start.",
                StartupClock.ElapsedMs);

        // Startup self-check: log the OS StartupTask state (the single source of
        // truth for 开机自启动) — first thing to read when diagnosing startup issues.
        _ = LogStartupTaskStateAsync(Services);

        // Unpackaged self-heal: portable folders move, so while launch-at-startup
        // is enabled the Run value is rewritten with the CURRENT exe path on
        // every start. Best-effort — registry failures are logged and never
        // block or crash startup.
        _ = HealStartupRunValueAsync(Services);

        // Qt parity: count-only FTS reconcile on startup - notes vs FTS row count
        // diverges (e.g. a crash mid-write, a restored backup) → background rebuild.
        _ = ReconcileSearchIndexAsync(Services);

        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Single-instance (guard in Program): a second launch redirects its
        // activation here; surface the window exactly like the tray "show" path.
        AppInstance.GetCurrent().Activated += OnRedirectedActivation;

        // StartMinimized: skip Activate (window stays hidden) and register the
        // tray icon explicitly — it only registers on Load/ForceCreate otherwise.
        var settings = await Services.GetRequiredService<ISettingsService>().LoadAsync();
        if (settings.StartMinimized)
        {
            var mainWindow = (MainWindow)Window;
            mainWindow.ForceCreateTrayIcon();
            // Hidden since launch: engage the tiered working-set trim (perf R2).
            mainWindow.NotifyHiddenSinceLaunch();
            Services.GetRequiredService<ILogger<App>>()
                .LogInformation("Startup milestone: tray icon ready (StartMinimized) at {ElapsedMs:0} ms since process start.",
                    StartupClock.ElapsedMs);
        }
        else
        {
            Window.Activate();
        }

        // Portable fallback notice: one-time per data root, only when the
        // unpackaged probe failed and we fell back to the Roaming root.
        if (Services.GetService<PortableDataRootDecision>() is { IsPortable: false })
            _ = ShowPortableFallbackNoticeAsync(paths);
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Infrastructure. Package identity is the portable-mode switch (no
        // marker files): packaged runs (Store / MSIX / dev-registered) behave
        // EXACTLY as before — default %APPDATA% root + WinRT StartupTask;
        // unpackaged runs are portable (<exedir>\data + HKCU Run key + manual
        // update check).
        var isPackaged = PackageIdentity.IsPackaged();

        // Data-root selection (see CreateAppPaths). The probe decision is
        // registered only for unpackaged runs so OnLaunched can log the
        // outcome and show the one-time fallback notice.
        var paths = CreateAppPaths(services, isPackaged);
        services.AddSingleton(paths);
        // The provider is a shared singleton so CrashHandler can read
        // CurrentLogFilePath for its triage .txt (Qt Logger::currentLogFilePath parity).
        var logProvider = new FileLoggerProvider(paths.LogsDir);
        services.AddSingleton(logProvider);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(logProvider);
        });
        services.AddSingleton(sp => new CrashHandler(
            paths,
            sp.GetRequiredService<ILogger<CrashHandler>>(),
            () => sp.GetRequiredService<FileLoggerProvider>().CurrentLogFilePath));

        // Data
        services.AddSingleton(sp => new DbConnectionFactory(sp.GetRequiredService<AppPaths>().DatabasePath));
        services.AddSingleton<SchemaInitializer>();
        services.AddSingleton<INoteRepository, NoteRepository>();

        // Services — ports of the Qt "managers"
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ICategoryService, CategoryService>();
        services.AddSingleton<INoteService, NoteService>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IImageService, ImageService>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        // Presentation-side impl of the Core abstraction (window-level Win32 hotkey).
        services.AddSingleton<IGlobalHotkey, GlobalHotkeyService>();
        // Presentation-side impl of the Core abstraction (the OS state is the
        // single source of truth for launch-at-startup). The implementation
        // follows package identity: WinRT StartupTask (MSIX extension) when
        // packaged, the HKCU Run key when unpackaged (portable).
        if (isPackaged)
            services.AddSingleton<IStartupTaskService, StartupTaskService>();
        else
            services.AddSingleton<IStartupTaskService, RegistryStartupTaskService>();
        // Manual update check against GitHub Releases: the settings entry is
        // visible in unpackaged runs only — the Store channel owns updates
        // (and external update prompts are a Store-policy risk) there.
        services.AddSingleton<IGitHubUpdateService, GitHubUpdateService>();

        // ViewModels
        services.AddTransient<NotesPageViewModel>();
        // SettingsViewModel takes the identity flag so its update-check row can
        // hide in packaged runs.
        services.AddTransient(sp => new SettingsViewModel(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IGlobalHotkey>(),
            sp.GetRequiredService<IStartupTaskService>(),
            sp.GetRequiredService<IGitHubUpdateService>(),
            isPackaged,
            sp.GetRequiredService<ILogger<SettingsViewModel>>()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Data-root selection by package identity: packaged runs get the default
    /// Roaming root (unchanged behavior); unpackaged (portable) runs probe
    /// <c>&lt;exedir&gt;\data</c> for writability and either adopt it or fall
    /// back to the Roaming root (the decision is registered so OnLaunched can
    /// log it and show the one-time fallback notice).
    /// </summary>
    private static AppPaths CreateAppPaths(IServiceCollection services, bool isPackaged)
    {
        if (isPackaged)
            return new AppPaths();

        var decision = PortableDataRoot.Resolve(PortableDataRoot.ExeDirectory());
        services.AddSingleton(decision);
        return decision.RootOverride is { } portableRoot ? new AppPaths(portableRoot) : new AppPaths();
    }

    /// <summary>
    /// Redirected activation from a second launch (see <see cref="Program"/>).
    /// Raised off the UI thread — marshal to the DispatcherQueue and show the
    /// window the same way the tray "show" command does (covers hidden /
    /// minimized / edge-hidden / StartMinimized states).
    /// </summary>
    private static void OnRedirectedActivation(object? sender, AppActivationArguments args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Services.GetRequiredService<ILogger<App>>()
                .LogInformation("Activation redirected from a second launch (kind: {Kind}).", args.Kind);
            if (Window is MainWindow mainWindow)
                mainWindow.ShowFromTray();
        });
    }

    private static async Task LogStartupTaskStateAsync(IServiceProvider services)
    {
        try
        {
            var state = await services.GetRequiredService<IStartupTaskService>().GetStateAsync();
            services.GetRequiredService<ILogger<App>>()
                .LogInformation("StartupTask state: {State}", state);
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "StartupTask state self-check failed.");
        }
    }

    /// <summary>
    /// Rewrites the HKCU Run value with the current exe path when
    /// launch-at-startup is enabled (portable folders move between machines
    /// and directories). No-op for packaged runs (the WinRT StartupTask owns
    /// the state there). Fire-and-forget: failures are logged, never fatal.
    /// </summary>
    private static async Task HealStartupRunValueAsync(IServiceProvider services)
    {
        try
        {
            if (services.GetRequiredService<IStartupTaskService>() is RegistryStartupTaskService registry)
                await Task.Run(registry.HealRunValue);
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "StartupTask Run-value self-heal failed.");
        }
    }

    /// <summary>
    /// One-time "portable probe failed, data lives in the Roaming root" notice
    /// (unpackaged runs whose exe directory is read-only, e.g. Program Files).
    /// The marker lives in the CHOSEN data root — the notice fires once per
    /// root, not once ever, and is recorded only AFTER a successful show (a
    /// first run killed mid-dialog re-notices next launch). Fire-and-forget:
    /// failures are logged, never fatal.
    /// </summary>
    private static async Task ShowPortableFallbackNoticeAsync(AppPaths paths)
    {
        try
        {
            if (PortableDataRoot.NoticeShown(paths.Root))
                return;

            if (Window.Content is not FrameworkElement root)
                return; // no dialog surface yet — retried next launch

            // The window content element exists before it enters the visual
            // tree, so its XamlRoot can still be null in the first moments
            // after activation — ShowAsync then throws ArgumentException
            // ("This element does not have a XamlRoot", verified on-machine
            // 2026-10-01 on the portable fallback path). Wait for the
            // one-time Loaded and re-read; on a tray-only run the await
            // simply persists until the first window show.
            if (root.XamlRoot is null && !root.IsLoaded)
            {
                var loaded = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                root.Loaded += OnRootLoaded;
                try { await loaded.Task; }
                finally { root.Loaded -= OnRootLoaded; }

                void OnRootLoaded(object sender, RoutedEventArgs e) => loaded.TrySetResult(null);
            }

            if (root.XamlRoot is not { } xamlRoot)
                return; // still no dialog surface — retried next launch

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
                RequestedTheme = root.ActualTheme,
                Title = "数据目录提示",
                Content = $"程序所在目录不可写，无法使用便携数据目录。\n便签数据将保存在：\n{paths.Root}",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();

            PortableDataRoot.MarkNoticeShown(paths.Root);
            Services.GetRequiredService<ILogger<App>>()
                .LogInformation("Portable fallback notice shown; marker recorded.");
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "Portable fallback notice failed (shown again next launch).");
        }
    }

    /// <summary>
    /// Fire-and-forget FTS reconcile on startup. Never crashes the app: any failure
    /// is logged and swallowed (the index self-heals on the next launch or the next
    /// successful write). Runs off the UI thread.
    /// </summary>
    private static async Task ReconcileSearchIndexAsync(IServiceProvider services)
    {
        try
        {
            await Task.Run(() => services.GetRequiredService<ISearchService>().ReconcileAsync());
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<App>>()
                .LogError(ex, "Startup FTS reconcile failed; index will rebuild next launch.");
        }
    }
}
