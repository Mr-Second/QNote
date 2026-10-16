using CommunityToolkit.Mvvm.ComponentModel;
using QNote.Models;

namespace QNote.ViewModels;

/// <summary>Left-pane navigation kinds (smart views + the 我的清单 group cap + lists).</summary>
public enum TodoNavKind
{
    Today,
    Important,
    Planned,
    All,
    ListsCap,
    List,
}

/// <summary>
/// Left-pane navigation row (never bind raw models — mvvm-guidelines). Smart
/// views carry a resw <see cref="TitleKey"/> (resolved at display time so the
/// VM stays string-free and headless-testable); list rows carry the raw data
/// name plus the owning <see cref="List"/>. Presentation-only state: badge
/// count and selection.
/// </summary>
public partial class TodoNavItemViewModel : ObservableObject
{
    private TodoNavItemViewModel()
    {
    }

    /// <summary>A DB-backed list row.</summary>
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public TodoNavItemViewModel(TodoList list)
    {
        Kind = TodoNavKind.List;
        Id = list.Uuid;
        Title = list.Name;
        List = list;
    }

    public required TodoNavKind Kind { get; init; }

    /// <summary>Smart views: the kind name; lists: the list uuid.</summary>
    public required string Id { get; init; }

    /// <summary>resw key for smart views / the group cap; empty for list rows.</summary>
    public string TitleKey { get; init; } = string.Empty;

    /// <summary>Raw list name (data identity; resolved via TodoListDisplayNames at display time).</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>Segoe Fluent Icons glyph (smart views only).</summary>
    public string IconGlyph { get; init; } = string.Empty;

    /// <summary>Owning list row (null for smart views / the cap).</summary>
    public TodoList? List { get; }

    /// <summary>Accent color as <c>#RRGGBB</c>; empty = theme accent.</summary>
    public string ColorHex => List?.Color ?? string.Empty;

    [ObservableProperty]
    public partial int IncompleteCount { get; set; }

    /// <summary>Row selection state, synced by the page VM.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string CountText => IncompleteCount.ToString();

    public static TodoNavItemViewModel CreateSmart(TodoNavKind kind, string titleKey, string glyph) => new()
    {
        Kind = kind,
        Id = kind.ToString().ToLowerInvariant(),
        TitleKey = titleKey,
        IconGlyph = glyph,
    };

    public static TodoNavItemViewModel CreateCap(string titleKey) => new()
    {
        Kind = TodoNavKind.ListsCap,
        Id = "lists",
        TitleKey = titleKey,
    };
}
