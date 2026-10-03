using QNote.Services;

namespace QNote.Views;

/// <summary>
/// Maps <see cref="BackupErrorKind"/> to the localized summary shown in the
/// backup/restore result toasts (<see cref="QNote.Controls.AppToast"/>). Shared by
/// <see cref="BackupDialog"/> and <see cref="RestoreDialog"/> so both report
/// identical wording. Core's <see cref="BackupException"/> messages are English
/// diagnostics for the log — user-visible text NEVER passes through
/// <see cref="Exception.Message"/>: every kind resolves here, with the version
/// numbers interpolated through <see cref="BackupException.SchemaFound"/> /
/// <see cref="BackupException.SchemaSupported"/> for UnsupportedVersion.
/// </summary>
internal static class BackupErrorText
{
    public static string Describe(Exception ex) => ex switch
    {
        BackupException be => be.Kind switch
        {
            BackupErrorKind.ArchiveNotFound => AppStrings.GetString("BackupErrorArchiveNotFound"),
            BackupErrorKind.NotAnArchive => AppStrings.GetString("BackupErrorNotAnArchive"),
            BackupErrorKind.PasswordRequired => AppStrings.GetString("BackupErrorPasswordRequired"),
            BackupErrorKind.WrongPassword => AppStrings.GetString("BackupErrorWrongPassword"),
            BackupErrorKind.MissingDatabase => AppStrings.GetString("BackupErrorMissingDatabase"),
            BackupErrorKind.CorruptArchive => AppStrings.GetString("BackupErrorCorruptArchive"),
            BackupErrorKind.UnsupportedVersion when be.SchemaFound is { } found && be.SchemaSupported is { } supported
                => AppStrings.GetFormat(
                    found > supported ? "BackupErrorNewerVersion" : "BackupErrorTooOld",
                    found, supported),
            BackupErrorKind.UnsupportedVersion => AppStrings.GetString("BackupErrorUnsupportedVersion"),
            _ => AppStrings.GetString("BackupErrorUnexpected"),
        },
        // Unexpected infrastructure failures (IO, disk full…) carry no stable
        // wording — a generic localized summary; the detail goes to the log.
        _ => AppStrings.GetString("BackupErrorUnexpected"),
    };
}
