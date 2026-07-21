using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using QNote.Services;

namespace QNote.ViewModels;

/// <summary>
/// Placeholder root view-model proving the DI + MVVM wiring end to end. Real screen
/// VMs (NoteListViewModel, EditorViewModel, …) arrive with their feature tasks.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ILogger<MainViewModel> _log;

    public MainViewModel(ISettingsService settings, ILogger<MainViewModel> log)
    {
        _settings = settings;
        _log = log;
        _log.LogInformation("MainViewModel constructed via DI.");
    }

    [ObservableProperty]
    public partial string Title { get; set; } = "QNote";

    [ObservableProperty]
    public partial string Status { get; set; } = "骨架就绪 · Skeleton ready";

    /// <summary>Load persisted UI state. Called by the shell once navigation is wired.</summary>
    public async Task InitializeAsync()
    {
        var themeMode = await _settings.GetAsync("themeMode");
        if (!string.IsNullOrEmpty(themeMode))
            _log.LogInformation("Persisted theme mode: {ThemeMode}", themeMode);
    }
}
