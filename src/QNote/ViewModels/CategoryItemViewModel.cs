using CommunityToolkit.Mvvm.ComponentModel;
using QNote.Controls;
using QNote.Models;
using QNote.Services;

namespace QNote.ViewModels;

/// <summary>
/// Sidebar-item wrapper around a <see cref="Category"/> (never bind raw models —
/// mvvm-guidelines). Holds presentation-only state: selection, note-count badge,
/// and the category color as a plain hex string (UI brushes are derived in the
/// view via <c>HexToBrushConverter</c> — no UI types in VMs).
/// </summary>
public partial class CategoryItemViewModel : ObservableObject
{
    public CategoryItemViewModel(Category category, bool isBuiltIn, int noteCount)
    {
        Id = category.Id;
        Name = category.Name;
        IconKey = category.IconKey;
        ColorHex = category.Color;
        IsBuiltIn = isBuiltIn;
        NoteCount = noteCount;
    }

    private CategoryItemViewModel(int totalNotes)
    {
        Id = -1;
        IsAll = true;
        Name = AppStrings.GetString("CategoryAll");
        IconKey = "E8FD";
        ColorHex = "#0078D4"; // brand accent; close enough in both themes for the synthetic row
        NoteCount = totalNotes;
    }

    /// <summary>The synthetic "全部" row (never in the DB): no filter, no edit, pinned at top.</summary>
    public static CategoryItemViewModel CreateAll(int totalNotes) => new(totalNotes);

    public long Id { get; }

    /// <summary>Synthetic "全部" row — excluded from rename/delete/reorder persistence.</summary>
    public bool IsAll { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>
    /// Sidebar display name: built-in data names resolve through resw
    /// (工作/生活/重要 ↔ Work/Life/Important, see <see cref="CategoryDisplayNames"/>);
    /// the synthetic "全部" row (already localized at construction) and user
    /// rows show their name as is.
    /// </summary>
    public string DisplayName => CategoryDisplayNames.Resolve(Name);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconGlyph))]
    public partial string IconKey { get; set; } = string.Empty;

    /// <summary>Category accent color as <c>#RRGGBB</c>; empty = theme accent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveColorHex))]
    public partial string ColorHex { get; set; } = string.Empty;

    /// <summary>Seeded built-in (工作/生活/重要): no rename/delete.</summary>
    public bool IsBuiltIn { get; }

    public bool CanEdit => !IsBuiltIn && !IsAll;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int NoteCount { get; set; }

    /// <summary>Sidebar selection state, synced by the page VM on selection change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveColorHex))]
    public partial bool IsSelected { get; set; }

    /// <summary>Color hex while selected, <c>null</c> otherwise (→ default theme foreground).</summary>
    public string? ActiveColorHex => IsSelected && ColorHex.Length > 0 ? ColorHex : null;

    /// <summary>
    /// Re-raise <see cref="ActiveColorHex"/> so the view re-runs the brush converter —
    /// needed after a runtime theme switch (the fallback brush is theme-dependent
    /// but the property value itself did not change).
    /// </summary>
    public void RefreshActiveColor() => OnPropertyChanged(nameof(ActiveColorHex));

    public string CountText => NoteCount.ToString();

    public string IconGlyph => IconCatalog.ToGlyph(IconKey);

    /// <summary>Refresh display fields after the category is edited or counts change.</summary>
    public void Apply(Category category, int noteCount)
    {
        Name = category.Name;
        IconKey = category.IconKey;
        ColorHex = category.Color;
        NoteCount = noteCount;
    }
}
