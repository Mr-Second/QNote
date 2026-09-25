using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
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

        // Startup self-check: log the OS StartupTask state (the single source of
        // truth for 开机自启动) — first thing to read when diagnosing startup issues.
        _ = LogStartupTaskStateAsync(Services);

        // Qt parity: count-only FTS reconcile on startup - notes vs FTS row count
        // diverges (e.g. a crash mid-write, a restored backup) → background rebuild.
        _ = ReconcileSearchIndexAsync(Services);

        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // StartMinimized: skip Activate (window stays hidden) and register the
        // tray icon explicitly — it only registers on Load/ForceCreate otherwise.
        var settings = await Services.GetRequiredService<ISettingsService>().LoadAsync();
        if (settings.StartMinimized)
            ((MainWindow)Window).ForceCreateTrayIcon();
        else
            Window.Activate();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Infrastructure
        var paths = new AppPaths();
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
        // Presentation-side impl of the Core abstraction (WinRT StartupTask — the OS
        // state is the single source of truth for launch-at-startup).
        services.AddSingleton<IStartupTaskService, StartupTaskService>();

        // ViewModels
        services.AddTransient<NotesPageViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
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
