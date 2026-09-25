using QNote.Services;

namespace QNote.Views;

/// <summary>
/// Maps <see cref="BackupErrorKind"/> to the localized Chinese summary shown in the
/// backup/restore result toasts (<see cref="QNote.Controls.AppToast"/>). Shared by
/// <see cref="BackupDialog"/> and <see cref="RestoreDialog"/> so both report
/// identical wording.
/// </summary>
internal static class BackupErrorText
{
    /// <summary>Expected failures (<see cref="BackupException"/>) get a friendly Chinese summary.</summary>
    public static string Describe(Exception ex) => ex is BackupException be
        ? be.Kind switch
        {
            BackupErrorKind.ArchiveNotFound => "备份文件不存在。",
            BackupErrorKind.NotAnArchive => "无法读取该文件：不是有效的备份。",
            BackupErrorKind.PasswordRequired => "该备份已加密，请输入密码。",
            BackupErrorKind.WrongPassword => "密码错误，或备份文件已损坏。",
            BackupErrorKind.CorruptArchive => be.Message,
            BackupErrorKind.UnsupportedVersion => be.Message,
            _ => be.Message,
        }
        : ex.Message;
}
