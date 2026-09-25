using QNote.EdgeHide;
using QNote.Models;

namespace QNote.Tests;

/// <summary>
/// Typed settings façade: defaults on an empty table, full roundtrip, change
/// notification, and garbage-row tolerance (unknown/invalid values fall back).
/// </summary>
public sealed class SettingsServiceTests
{
    [Fact]
    public async Task LoadAsync_EmptyTable_ReturnsDefaults()
    {
        using var db = new TestDatabase();
        var settings = db.NewSettingsService();

        var s = await settings.LoadAsync();

        Assert.Equal("system", s.ThemeMode);
        Assert.False(s.AlwaysOnTop);
        Assert.False(s.RememberWindowGeometry);
        Assert.False(s.StartMinimized);
        Assert.Equal(-1, s.WindowX);
        Assert.Equal(940, s.WindowWidth);
        Assert.Equal(NoteListDensity.Standard, s.ListDensity);
        Assert.Equal(NoteTimeFormat.Tiered, s.TimeFormat);
        Assert.Equal(NoteSortOrder.Updated, s.NoteSortOrder);
        Assert.True(s.ConfirmBeforeDelete);
        Assert.Equal(SearchSortOrder.Relevance, s.SearchSortOrder);
        Assert.False(s.EdgeHideEnabled);
        Assert.True(s.HideTaskbarIconOnEdgeHide); // hidden window keeps no taskbar slot by default
        Assert.Equal(HotkeyFormat.ModWin, s.EdgeHideHotkeyModifiers);
        Assert.Equal(HotkeyFormat.VkOem3, s.EdgeHideHotkeyKey);
    }

    [Fact]
    public async Task SaveAsync_ThenLoad_RoundtripsAllFields()
    {
        using var db = new TestDatabase();
        var settings = db.NewSettingsService();

        var expected = new AppSettings
        {
            ThemeMode = "dark",
            AlwaysOnTop = true,
            RememberWindowGeometry = true,
            StartMinimized = true,
            WindowX = 100,
            WindowY = 80,
            WindowWidth = 1024,
            WindowHeight = 768,
            ListDensity = NoteListDensity.Compact,
            TimeFormat = NoteTimeFormat.Full,
            NoteSortOrder = NoteSortOrder.Title,
            ConfirmBeforeDelete = false,
            SearchSortOrder = SearchSortOrder.OldestFirst,
            EdgeHideEnabled = true,
            HideTaskbarIconOnEdgeHide = true,
            EdgeHideHotkeyModifiers = HotkeyFormat.ModControl | HotkeyFormat.ModShift,
            EdgeHideHotkeyKey = 0x41, // A
        };
        await settings.SaveAsync(expected);

        Assert.Equal(expected, await settings.LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_RaisesChanged_WithSavedSnapshot()
    {
        using var db = new TestDatabase();
        var settings = db.NewSettingsService();

        AppSettings? received = null;
        settings.Changed += s => received = s;

        var next = new AppSettings { ThemeMode = "light" };
        await settings.SaveAsync(next);

        Assert.Equal(next, received);
    }

    [Fact]
    public async Task LoadAsync_InvalidEnumValue_FallsBackToDefault()
    {
        using var db = new TestDatabase();
        var settings = db.NewSettingsService();

        await settings.SetAsync("timeFormat", "99");
        await settings.SetAsync("confirmBeforeDelete", "not-a-bool");

        var s = await settings.LoadAsync();
        Assert.Equal(NoteTimeFormat.Tiered, s.TimeFormat);
        Assert.True(s.ConfirmBeforeDelete);
    }

    [Fact]
    public async Task SetAsync_RawWrite_InvalidatesTypedCache()
    {
        using var db = new TestDatabase();
        var settings = db.NewSettingsService();

        Assert.Equal("system", (await settings.LoadAsync()).ThemeMode);

        await settings.SetAsync("themeMode", "dark");

        Assert.Equal("dark", (await settings.LoadAsync()).ThemeMode);
    }
}
