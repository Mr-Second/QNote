using CommunityToolkit.Mvvm.ComponentModel;
using QNote.Models;

namespace QNote.ViewModels;

/// <summary>
/// Detail-pane wrapper for one <see cref="TodoStep"/> (never bind raw models).
/// Identity is immutable; <see cref="IsCompleted"/> is observable so the row's
/// checkbox/strikethrough react to toggles without a collection rebuild.
/// </summary>
public partial class TodoStepItemViewModel : ObservableObject
{
    public TodoStepItemViewModel(TodoStep step)
    {
        Id = step.Id;
        Uuid = step.Uuid;
        Title = step.Title;
        IsCompleted = step.CompletedAt is not null;
    }

    public long Id { get; }

    public string Uuid { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }
}
