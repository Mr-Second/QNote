using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using WinUIRichEditor;
using WinUIRichEditor.Controls;

namespace QNote.Controls;

/// <summary>
/// Floating find/replace bar for the bare WRE <see cref="RichEditor"/>, opened by the
/// engine's Ctrl+F / Ctrl+H (<see cref="RichEditor.FindRequested"/>, wired in NotesPage).
/// Behavior mirrors the vendored RichEditorView.FindBar reference: live highlight-all
/// while typing, "n/m" counter, Enter/F3 = next, Shift+Enter/Shift+F3 = previous, Esc =
/// close, VS Code focus discipline (buttons never take focus from the query box), and
/// deferred focus on open (WinUI ignores Focus on an element that just became Visible
/// in the same tick). View wiring only — no view-model (mvvm-guidelines).
/// </summary>
public sealed partial class EditorFindBar : UserControl
{
    /// <summary>Raised after a 全部替换 with the number of replacements made.</summary>
    public event Action<int>? ReplaceAllCompleted;

    /// <summary>The editor this bar searches; set once by the host page.</summary>
    public RichEditor? Target { get; set; }

    private static string L(string key) => RichEditorLocalization.GetString(key);

    public EditorFindBar()
    {
        InitializeComponent();

        // Strings come from the vendored editor localization (zh-Hans table lives in
        // EditorLocalization.cs) — the same lookup mechanism the editor toolbar uses.
        // The replace buttons are codicon icon buttons now, so their tooltips carry
        // the text the v1 buttons showed as content.
        FindBox.PlaceholderText = L("Find");
        ToolTipService.SetToolTip(ExpandReplace, L("ToggleReplace") + " (Ctrl+H)");
        ToolTipService.SetToolTip(PrevButton, L("FindPrevious"));
        ToolTipService.SetToolTip(NextButton, L("FindNext") + " (F3)");
        ToolTipService.SetToolTip(MatchCase, L("MatchCase"));
        ToolTipService.SetToolTip(CloseButton, L("Cancel"));
        ReplaceBox.PlaceholderText = L("Replace");
        ToolTipService.SetToolTip(ReplaceNextButton, L("Replace"));
        ToolTipService.SetToolTip(ReplaceAllButton, L("ReplaceAll"));

        AutomationProperties.SetName(FindBox, L("Find"));
        AutomationProperties.SetName(ReplaceBox, L("Replace"));
        AutomationProperties.SetName(PrevButton, L("FindPrevious"));
        AutomationProperties.SetName(NextButton, L("FindNext"));
        AutomationProperties.SetName(MatchCase, L("MatchCase"));
        AutomationProperties.SetName(CloseButton, L("Cancel"));
        AutomationProperties.SetName(ReplaceNextButton, L("Replace"));
        AutomationProperties.SetName(ReplaceAllButton, L("ReplaceAll"));

        // Keep the counter's theme-resolved brushes correct on a live theme flip.
        ActualThemeChanged += (_, _) => UpdateMatchLabel();
    }

    /// <summary>
    /// Opens the bar — with the replace row when <paramref name="withReplace"/> and the editor
    /// is editable — pre-fills the last query, lights up its matches, updates the counter, then
    /// focuses the query box DEFERRED: the bar just became Visible in THIS tick (inside the
    /// editor's Ctrl+F KeyDown), and WinUI silently ignores Focus on an element that hasn't
    /// completed layout — typing kept going into the document instead of the query box.
    /// </summary>
    public void Show(bool withReplace)
    {
        if (Target is not { AllowFindReplace: true } editor) return;
        SetReplaceVisible(withReplace && !editor.IsReadOnly);
        // Read-only can't replace, so the chevron would be a dead control — hide it entirely.
        ExpandReplace.Visibility = editor.IsReadOnly ? Visibility.Collapsed : Visibility.Visible;
        Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(FindBox.Text) && editor.LastFindQuery is { } last) FindBox.Text = last;
        // Light up all matches of the (pre-filled) query right away; the counter follows.
        editor.SetFindHighlight(FindBox.Text, MatchCase.IsChecked == true);
        UpdateMatchLabel();
        var box = FindBox;
        DispatcherQueue.TryEnqueue(() =>
        {
            box.SelectAll();
            box.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>
    /// Hides the bar, clears the highlight-all overlay, and returns focus to the editor.
    /// FocusEditor, not Focus: the control itself is not a tab stop (focus lives on its
    /// inner canvas), so Focus() would leave the caret unpainted and the keyboard dead.
    /// </summary>
    public void Hide()
    {
        if (Target is not { } editor) return;
        Visibility = Visibility.Collapsed;
        // The highlight-all overlay lives only while the bar is open.
        editor.ClearFindHighlight();
        editor.FocusEditor();
    }

    /// <summary>
    /// Re-applies the highlight + counter after the document was swapped (note switch) while
    /// the bar is open — the query itself carries over, but the cached "n/m" must not.
    /// </summary>
    public void Refresh()
    {
        if (Visibility != Visibility.Visible || Target is not { } editor) return;
        editor.SetFindHighlight(FindBox.Text, MatchCase.IsChecked == true);
        UpdateMatchLabel();
    }

    // Single place that drives the replace row + the chevron's glyph state, so opening
    // via Ctrl+H and toggling the chevron can never disagree. The glyph flips codicon
    // chevron-right (EAB6) → chevron-down (EAB4).
    private void SetReplaceVisible(bool show)
    {
        ReplaceRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ExpandGlyph.Glyph = show ? "\uEAB4" : "\uEAB6";
    }

    // Refreshes the "n/m" counter ("m" when the selection isn't on a match, "0" when there are
    // no matches). VS Code .no-results: a NON-EMPTY query with zero matches turns the counter
    // error-red. Brushes resolve against the BAR's OWN ActualTheme (the previous
    // HexToBrushConverter.ResolveThemeBrush walks App.Window's theme — stale under the
    // in-app theme override, which rendered the dark counter black), re-resolved on
    // ActualThemeChanged. Assigning literal null (instead of the XAML default) would
    // make WinUI render the text with no brush at all — always assign a resolved brush.
    private void UpdateMatchLabel()
    {
        if (Target is not { } editor)
        {
            MatchLabel.Text = "0";
            return;
        }
        var (cur, total) = editor.GetFindMatchPosition();
        MatchLabel.Text = total == 0 ? "0" : cur > 0 ? $"{cur}/{total}" : total.ToString();
        MatchLabel.Foreground = FindBox.Text.Length > 0 && total == 0
            ? ResolveBrush("SystemFillColorCriticalBrush")
            : ResolveBrush("TextFillColorSecondaryBrush");
    }

    /// <summary>
    /// Resolves a theme-dictionary brush against THIS control's actual theme (Light /
    /// Default / HighContrast when the system is in high contrast) — the app-level
    /// lookup path follows the application theme and ignores the window's
    /// RequestedTheme override.
    /// </summary>
    private Brush ResolveBrush(string key)
    {
        var hc = new AccessibilitySettings().HighContrast;
        string themeKey = hc ? "HighContrast" : ActualTheme == ElementTheme.Light ? "Light" : "Default";
        if (TryThemeDict(Application.Current.Resources, themeKey, key, out var brush)) return brush;
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
            if (TryThemeDict(merged, themeKey, key, out brush)) return brush;
        return (Brush)Application.Current.Resources[key];

        static bool TryThemeDict(ResourceDictionary dict, string themeKey, string key, out Brush brush)
        {
            brush = null!;
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var themed)
                && themed is ResourceDictionary themeDict
                && themeDict.TryGetValue(key, out var value)
                && value is Brush b)
            {
                brush = b;
                return true;
            }
            return false;
        }
    }

    // The boxes' keys, split from their handlers so a test can press them (KeyRoutedEventArgs
    // has no public constructor). F3 is handled here too: after Enter the focus stays in the
    // query box, so F3 never reaches the editor's own F3 and would do nothing.
    internal bool FindBoxKey(VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.Enter:
            case VirtualKey.F3: DoFind(backwards: shift); return true;
            case VirtualKey.Escape: Hide(); return true;
            default: return false;
        }
    }

    internal bool ReplaceBoxKey(VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.Enter: DoReplace(); return true;
            case VirtualKey.F3: DoFind(backwards: shift); return true;
            case VirtualKey.Escape: Hide(); return true;
            default: return false;
        }
    }

    private static bool IsShiftDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            & CoreVirtualKeyStates.Down) != 0;

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FindBoxKey(e.Key, IsShiftDown())) e.Handled = true;
    }

    private void ReplaceBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ReplaceBoxKey(e.Key, IsShiftDown())) e.Handled = true;
    }

    // Live highlight-all while the user types (browser find behavior); the counter tracks it.
    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Target?.SetFindHighlight(FindBox.Text, MatchCase.IsChecked == true);
        UpdateMatchLabel();
    }

    private void MatchCase_Click(object sender, RoutedEventArgs e)
    {
        Target?.SetFindHighlight(FindBox.Text, MatchCase.IsChecked == true);
        UpdateMatchLabel();
    }

    // Leading chevron (VS Code / browser convention): expands the replace row in place, so a
    // search started with Ctrl+F doesn't have to be reopened with Ctrl+H.
    private void ExpandReplace_Click(object sender, RoutedEventArgs e)
    {
        bool show = ReplaceRow.Visibility != Visibility.Visible && Target is { IsReadOnly: false };
        SetReplaceVisible(show);
        if (show)
        {
            // Deferred like Show's focus: the row just became Visible in this tick, and
            // WinUI ignores Focus on an element that hasn't completed layout yet.
            var box = ReplaceBox;
            DispatcherQueue.TryEnqueue(() => box.Focus(FocusState.Programmatic));
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => DoFind(backwards: true);

    private void NextButton_Click(object sender, RoutedEventArgs e) => DoFind(backwards: false);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void ReplaceNextButton_Click(object sender, RoutedEventArgs e) => DoReplace();

    private void ReplaceAllButton_Click(object sender, RoutedEventArgs e) => DoReplaceAll();

    private void DoFind(bool backwards)
    {
        string q = FindBox.Text;
        if (q.Length == 0 || Target is not { } editor) return;
        bool mc = MatchCase.IsChecked == true;
        if (backwards) editor.FindPrev(q, mc); else editor.FindNext(q, mc);
        UpdateMatchLabel();
    }

    private void DoReplace()
    {
        string q = FindBox.Text;
        if (q.Length == 0 || Target is not { } editor || editor.IsReadOnly) return;
        editor.ReplaceNext(q, ReplaceBox.Text, MatchCase.IsChecked == true);
        UpdateMatchLabel();
    }

    private void DoReplaceAll()
    {
        string q = FindBox.Text;
        if (q.Length == 0 || Target is not { } editor || editor.IsReadOnly) return;
        int count = editor.ReplaceAll(q, ReplaceBox.Text, MatchCase.IsChecked == true);
        UpdateMatchLabel();
        ReplaceAllCompleted?.Invoke(count);
    }
}
