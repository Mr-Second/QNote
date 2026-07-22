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

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
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

        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Infrastructure
        var paths = new AppPaths();
        services.AddSingleton(paths);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(paths.LogsDir));
        });
        services.AddSingleton<CrashHandler>();

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

        // ViewModels
        services.AddTransient<NotesPageViewModel>();

        return services.BuildServiceProvider();
    }
}
