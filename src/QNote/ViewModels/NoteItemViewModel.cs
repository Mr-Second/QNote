using CommunityToolkit.Mvvm.ComponentModel;
using QNote.Models;

namespace QNote.ViewModels;

/// <summary>
/// List-item wrapper around a <see cref="NoteSummary"/> (never bind raw models —
/// mvvm-guidelines). Presentation-only; derives display strings from the summary.
/// </summary>
public partial class NoteItemViewModel : ObservableObject
{
    public NoteItemViewModel(NoteSummary summary)
    {
        Id = summary.Id;
        Uuid = summary.Uuid;
        Category = summary.Category;
        Title = summary.Title;
        Preview = summary.Preview;
        UpdatedAt = summary.UpdatedAt;
    }

    public long Id { get; }

    public string Uuid { get; }

    public string Category { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Preview { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeDisplay))]
    public partial DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Current search keyword (whole-keyword, case-insensitive). The list item's
    /// TextBlocks read this through the <c>TextBlockHighlighter</c> attached
    /// property to paint hit runs. Empty when not searching.
    /// </summary>
    [ObservableProperty]
    public partial string Keyword { get; set; } = string.Empty;

    /// <summary>Title with the empty-note fallback used by the Qt build.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "新便签" : Title;

    /// <summary>Local, tiered timestamp shown on the list card.</summary>
    public string TimeDisplay => FormatTime(UpdatedAt);

    /// <summary>Build a list item for a freshly created (blank) note.</summary>
    public static NoteItemViewModel FromNote(Note note) => new(new NoteSummary
    {
        Id = note.Id,
        Uuid = note.Uuid,
        Title = note.Title,
        Preview = string.Empty,
        Category = note.Category,
        UpdatedAt = note.UpdatedAt,
    });

    /// <summary>Refresh display fields after the note is saved.</summary>
    public void Apply(string title, string preview, DateTimeOffset updatedAt)
    {
        Title = title;
        Preview = preview;
        UpdatedAt = updatedAt;
    }

    private static string FormatTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date)
            return local.ToString("HH:mm");
        if (local.Year == now.Year)
            return local.ToString("MM-dd HH:mm");
        return local.ToString("yyyy-MM-dd");
    }
}
