namespace QNote.Views;

/// <summary>
/// Final outcome of a backup/restore dialog run. The dialog closes itself on a
/// final outcome and the caller (NotesPage) surfaces it as a floating toast
/// (<see cref="Controls.ToastTip"/>) instead of an inline banner.
/// </summary>
public sealed record OperationResult(bool Success, string Title, string Message);
