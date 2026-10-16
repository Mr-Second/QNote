using CommunityToolkit.Mvvm.ComponentModel;
using QNote.Models;
using QNote.Text;

namespace QNote.ViewModels;

/// <summary>
/// List-item wrapper around a <see cref="TodoTaskSummary"/> / <see cref="TodoTask"/>
/// (never bind raw models — mvvm-guidelines). Presentation-only; string-free so
/// the page VM family stays headless-testable (display text is composed by the
/// view layer via <c>Converters.TodoText</c>). <see cref="IsOverdue"/> is
/// computed once per data refresh (midnight staleness accepted, MS To Do parity)
/// and re-raised on <see cref="RefreshThemeBrushes"/> because brush-yielding
/// bindings do not re-run on a live theme flip.
/// </summary>
public partial class TodoTaskItemViewModel : ObservableObject
{
    private readonly Func<DateTimeOffset> _clock;

    public TodoTaskItemViewModel(TodoTaskSummary summary, Func<DateTimeOffset> clock)
    {
        _clock = clock;
        Id = summary.Id;
        Uuid = summary.Uuid;
        ListUuid = summary.ListUuid;
        Title = summary.Title;
        DueAt = summary.DueAt;
        IsAllDay = summary.IsAllDay;
        ReminderOn = summary.ReminderOn;
        IsImportant = summary.IsImportant;
        IsCompleted = summary.CompletedAt is not null;
        RecurrenceRule = summary.RecurrenceRule;
        StepsTotal = summary.StepsTotal;
        StepsDone = summary.StepsDone;
        CreatedAt = summary.CreatedAt;
        ComputeOverdue();
    }

    public long Id { get; }

    public string Uuid { get; }

    public string ListUuid { get; }

    /// <summary>Creation instant (immutable; 排序依据/创建日期 sorts on it).</summary>
    public DateTimeOffset CreatedAt { get; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDue))]
    public partial DateTimeOffset? DueAt { get; set; }

    [ObservableProperty]
    public partial bool IsAllDay { get; set; }

    [ObservableProperty]
    public partial bool ReminderOn { get; set; }

    [ObservableProperty]
    public partial bool IsImportant { get; set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    /// <summary>Recurrence rule on the task; null = one-off (row shows the repeat glyph).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecurrence))]
    public partial string? RecurrenceRule { get; set; }

    public bool HasRecurrence => RecurrenceRule is not null;

    /// <summary>Step count on the task (0 = none; the row shows "N/M" when non-zero).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSteps))]
    public partial int StepsTotal { get; set; }

    [ObservableProperty]
    public partial int StepsDone { get; set; }

    public bool HasSteps => StepsTotal > 0;

    /// <summary>Computed at load/refresh time; incomplete + past due (all-day = past DAY).</summary>
    [ObservableProperty]
    public partial bool IsOverdue { get; set; }

    public bool HasDue => DueAt is not null;

    /// <summary>Refresh from a full record (detail mutations return the updated task).</summary>
    public void Apply(TodoTask task)
    {
        Title = task.Title;
        DueAt = task.DueAt;
        IsAllDay = task.IsAllDay;
        ReminderOn = task.ReminderOn;
        IsImportant = task.IsImportant;
        IsCompleted = task.CompletedAt is not null;
        RecurrenceRule = task.RecurrenceRule;
        ComputeOverdue();
    }

    /// <summary>Refresh from a list projection.</summary>
    public void Apply(TodoTaskSummary summary)
    {
        Title = summary.Title;
        DueAt = summary.DueAt;
        IsAllDay = summary.IsAllDay;
        ReminderOn = summary.ReminderOn;
        IsImportant = summary.IsImportant;
        IsCompleted = summary.CompletedAt is not null;
        RecurrenceRule = summary.RecurrenceRule;
        StepsTotal = summary.StepsTotal;
        StepsDone = summary.StepsDone;
        ComputeOverdue();
    }

    /// <summary>Detail-pane step mutations update the row badge in place (no reload).</summary>
    public void UpdateSteps(int total, int done)
    {
        StepsTotal = total;
        StepsDone = done;
    }

    /// <summary>
    /// Re-raise the properties that feed brush-returning x:Bind functions —
    /// function bindings do not re-run when only the THEME changed (same args),
    /// so a live Light/Dark flip would leave stale brushes on the cards.
    /// </summary>
    public void RefreshThemeBrushes()
    {
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(IsImportant));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(HasDue));
    }

    private void ComputeOverdue() =>
        IsOverdue = !IsCompleted && DueAt is { } due && TodoDueFormatter.IsOverdue(due, IsAllDay, _clock());
}
