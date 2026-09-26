using Microsoft.Windows.AppLifecycle;

namespace QNote;

/// <summary>
/// Entry point. Replaces the XAML compiler's generated Main
/// (DISABLE_XAML_GENERATED_MAIN) so the single-instance guard runs BEFORE
/// Application.Start: a second launch redirects its activation to the already
/// running instance and exits immediately — no window, services, or DI come up
/// in the second process. AppLifecycle works both packaged and unpackaged, so
/// the dev inner loop (<c>dotnet run</c>) is unaffected when no instance runs.
/// </summary>
internal static class Program
{
    /// <summary>Stable key that identifies the single application instance.</summary>
    private const string InstanceKey = "QNoteMain";

    // NOTE: Main must stay SYNCHRONOUS. An `async Task Main` state machine breaks
    // WinUI's input-stack (TSF) initialization and IME dies app-wide — every text
    // control falls back to Latin-only input (confirmed 2026-09-26 by A/B: same
    // single-instance code, sync Main = IME works, async Main = IME dead).
    [STAThread]
    private static void Main()
    {
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!keyInstance.IsCurrent)
        {
            // Hand the activation to the running instance (it shows its window
            // via App.OnRedirectedActivation), then exit without starting the app.
            // Blocking wait is fine: this second process exits right after.
            try
            {
                keyInstance.RedirectActivationToAsync(
                    AppInstance.GetCurrent().GetActivatedEventArgs()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // A failed redirect must still exit — falling through to
                // Application.Start here would run two instances against one
                // SQLite store. No logger exists this early (DI never starts on
                // this path), so write to stderr and exit non-zero.
                Console.Error.WriteLine($"QNote: activation redirect failed: {ex}");
                Environment.Exit(1);
            }
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
