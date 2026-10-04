using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace QNote.Views;

/// <summary>
/// The About dialog (settings panel → 关于 → 关于…): identity block, external
/// links and the community line. The version caption reads the main module's
/// VERSIONINFO — the SAME single version source the manual update check uses
/// (the csproj FileVersion pins it; release.yml patches it together with the
/// manifest, so packaged and portable displays never drift). Opened by
/// <see cref="NotesPage"/> after the settings dialog closes (one-dialog rule).
/// </summary>
public sealed partial class AboutDialog : ContentDialog
{
    public AboutDialog()
    {
        InitializeComponent();
        SetVersionCaption();
    }

    /// <summary>
    /// Running version, e.g. "v1.5.0.0 · WinUI 3 · .NET 10 · NativeAOT". The
    /// stack suffix is language-neutral and intentionally hard-coded; every
    /// user-facing string goes through x:Uid instead.
    /// </summary>
    private void SetVersionCaption()
    {
        const string stack = "WinUI 3 · .NET 10 · NativeAOT";
        try
        {
            var exePath = Environment.ProcessPath;
            var version = exePath is null
                ? null
                : FileVersionInfo.GetVersionInfo(exePath).FileVersion;
            AboutVersionText.Text = string.IsNullOrEmpty(version)
                ? stack
                : $"v{version} · {stack}";
        }
        catch
        {
            AboutVersionText.Text = stack; // cosmetic fallback — never crash the dialog over a caption
        }
    }
}
