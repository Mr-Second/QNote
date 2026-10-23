using QNote.Services;
using QNote.Sync;

namespace QNote.Views;

/// <summary>The manual-sync button's toast payload: style + localized wording.</summary>
internal sealed record SyncOutcome(bool Success, string Title, string Message);

/// <summary>
/// Maps the engine's cycle diagnostics onto the toast wording (services-guidelines
/// §sync: Presentation owns user-visible copy). Consumed by the NotesPage header's
/// sync button; a null cycle result = the composition root's account gate skipped.
/// </summary>
internal static class SyncOutcomeText
{
    public static string DestinationDetail(SyncDestination destination) => destination.FolderUrl is { } folder
        ? AppStrings.GetFormat("SyncDestinationWebDav", destination.Account, folder)
        : !destination.IsReady
            ? AppStrings.GetString("SyncDestinationOneDriveSignedOut")
            : string.IsNullOrEmpty(destination.Account) ? destination.Provider
                : AppStrings.GetFormat("SyncDestinationOneDrive", destination.Account);

    private static string Title(string genericKey, string targetKey, SyncDestination? destination) => destination is null
        ? AppStrings.GetString(genericKey)
        : AppStrings.GetFormat(targetKey, destination.Provider);

    public static SyncOutcome Describe(SyncCycleResult? result)
    {
        if (result is null)
            return new SyncOutcome(
                Success: false,
                Title: AppStrings.GetString("SyncNotStarted"),
                Message: AppStrings.GetString("SyncSkippedNotSignedIn"));
        if (result.AbortKind != SyncCycleAbortKind.None)
            return new SyncOutcome(
                Success: false,
                Title: Title("SyncToastFailed", "SyncToastFailedAtTarget", result.Destination),
                Message: result.AbortKind switch
                {
                    SyncCycleAbortKind.ListingIncomplete => AppStrings.GetString("SyncAbortListingIncomplete"),
                    SyncCycleAbortKind.UnsupportedManifest => AppStrings.GetString("SyncAbortUnsupportedManifest"),
                    SyncCycleAbortKind.E2eeKeyMismatch => AppStrings.GetString("SyncAbortE2eeKeyMismatch"),
                    SyncCycleAbortKind.PairingRequired => AppStrings.GetString("SyncPairingRequired"),
                    _ => AppStrings.GetString("SyncAbortGeneric"),
                });
        if (result.Errors.Count > 0)
            return new SyncOutcome(
                Success: false,
                Title: Title("SyncToastFailed", "SyncToastFailedAtTarget", result.Destination),
                Message: AppStrings.GetFormat("SyncFinishedWithErrors", result.Errors.Count));
        var moved = result.Uploaded + result.Downloaded + result.DeletedRemote
            + result.TombstonesApplied + result.ConflictCopies;
        return new SyncOutcome(
            Success: true,
            Title: Title("SyncToastDone", "SyncToastDoneAtTarget", result.Destination),
            Message: moved == 0
                ? AppStrings.GetString("SyncUpToDate")
                : AppStrings.GetFormat("SyncFinished", moved));
    }
}
