namespace QNote.Models;

/// <summary>
/// Typed snapshot of app settings. Persisted as key/value TEXT rows in the
/// <c>settings</c> table (23 keys in the Qt build). Only the skeleton-relevant
/// fields are modeled here; the full set arrives with the settings task.
/// </summary>
public sealed record AppSettings
{
    /// <summary>"system" | "light" | "dark".</summary>
    public string ThemeMode { get; init; } = "system";

    public bool AlwaysOnTop { get; init; }

    public bool LaunchAtStartup { get; init; }

    // TODO(settings-task): model the full 23-key set (see FGH-settings-theme-i18n.md).
}
