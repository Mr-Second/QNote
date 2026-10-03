using CommunityToolkit.Mvvm.ComponentModel;
using QNote.Models;
using QNote.Services;
using QNote.Text;

namespace QNote.ViewModels;

/// <summary>
/// List-item wrapper around a <see cref="NoteSummary"/> (never bind raw models —
/// mvvm-guidelines). Presentation-only; derives display strings from the summary.
/// <see cref="TimeFormat"/> / <see cref="Density"/> mirror the display settings so a
/// settings change re-renders every card without reloading the list.
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
        CreatedAt = summary.CreatedAt;
        UpdatedAt = summary.UpdatedAt;
    }

    public long Id { get; }

    public string Uuid { get; }

    /// <summary>Linked category name; settable so moving a note between categories refreshes the item.</summary>
    [ObservableProperty]
    public partial string Category { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Preview { get; set; } = string.Empty;

    /// <summary>Creation timestamp (drives the "创建时间" list sort).</summary>
    public DateTimeOffset CreatedAt { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeDisplay))]
    public partial DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Timestamp style from settings; re-renders <see cref="TimeDisplay"/> on change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeDisplay))]
    public partial NoteTimeFormat TimeFormat { get; set; } = NoteTimeFormat.Tiered;

    /// <summary>Row density from settings; the item template binds padding/spacing to it.</summary>
    [ObservableProperty]
    public partial NoteListDensity Density { get; set; } = NoteListDensity.Standard;

    /// <summary>
    /// Current search keyword (whole-keyword, case-insensitive). The list item's
    /// TextBlocks read this through the <c>TextBlockHighlighter</c> attached
    /// property to paint hit runs. Empty when not searching.
    /// </summary>
    [ObservableProperty]
    public partial string Keyword { get; set; } = string.Empty;

    /// <summary>Title with the empty-note fallback used by the Qt build.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? AppStrings.GetString("UntitledNote")
        : Title;

    /// <summary>Local timestamp shown on the list card, styled per <see cref="TimeFormat"/>.</summary>
    public string TimeDisplay => NoteTimeFormatter.Format(UpdatedAt, TimeFormat, strings: AppLanguage.TimeStrings);

    /// <summary>Refresh display fields after the note is saved.</summary>
    public void Apply(string title, string preview, DateTimeOffset updatedAt)
    {
        Title = title;
        Preview = preview;
        UpdatedAt = updatedAt;
    }
}
