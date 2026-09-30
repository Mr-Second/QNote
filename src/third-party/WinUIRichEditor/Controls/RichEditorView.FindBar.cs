using System;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WinUIRichEditor.Controls;

// Built-in find/replace bar for RichEditorView: opened by the editor's Ctrl+F (find) / Ctrl+H (find +
// replace), Enter = next, Shift+Enter = previous, Esc = close. Built lazily in code (no XAML) so hosts
// that never search pay nothing. A host that wants its own find UI subscribes to
// RichEditor.FindRequested itself and can hide this via ShowBuiltInFindBar = false.
public partial class RichEditorView
{
    private Border _findBarHost = null!; // created in the ctor (row 1), collapsed until first use
    private StackPanel? _findBar;        // lazy content
    private TextBox? _findBox, _replaceBox;
    private ToggleButton? _matchCase;
    private ToggleButton? _expandReplace; // ▸/▾ chevron: shows the replace row without reopening via Ctrl+H
    private StackPanel? _replaceRow;
    private TextBlock? _matchLabel;      // "n/m" match counter (browser-style)

    /// <summary>When false, Ctrl+F/Ctrl+H do not open the built-in bar (for hosts with their own find
    /// UI subscribed to <see cref="RichEditor.FindRequested"/>). Default true.</summary>
    public bool ShowBuiltInFindBar
    {
        get => (bool)GetValue(ShowBuiltInFindBarProperty);
        set => SetValue(ShowBuiltInFindBarProperty, value);
    }

    /// <summary>Identifies the <see cref="ShowBuiltInFindBar"/> dependency property.</summary>
    public static readonly DependencyProperty ShowBuiltInFindBarProperty = DependencyProperty.Register(
        nameof(ShowBuiltInFindBar), typeof(bool), typeof(RichEditorView), new PropertyMetadata(true));

    private static string L(string key) => RichEditorLocalization.GetString(key);

    /// <summary>Opens the find bar (with the replace row when <paramref name="withReplace"/> and the
    /// editor is editable) and focuses the query box, pre-filled with the last query.</summary>
    public void ShowFindBar(bool withReplace)
    {
        if (!ShowBuiltInFindBar || !Editor.AllowFindReplace) return;
        BuildFindBar();
        SetReplaceVisible(withReplace && !Editor.IsReadOnly);
        // Read-only can't replace, so the chevron would be a dead control — hide it entirely.
        _expandReplace!.Visibility = Editor.IsReadOnly ? Visibility.Collapsed : Visibility.Visible;
        _findBarHost.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(_findBox!.Text) && Editor.LastFindQuery is { } last) _findBox.Text = last;
        // Light up all matches of the (pre-filled) query right away; the counter follows.
        Editor.SetFindHighlight(_findBox.Text, _matchCase?.IsChecked == true);
        UpdateMatchLabel();
        // Deferred focus: the bar just became Visible in THIS tick (usually inside the editor's Ctrl+F
        // KeyDown), and WinUI silently ignores Focus() on an element that hasn't completed layout yet —
        // typing kept going into the document instead of the query box.
        var box = _findBox;
        DispatcherQueue.TryEnqueue(() =>
        {
            box.SelectAll();
            box.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>Hides the find bar and returns focus to the editor.</summary>
    public void HideFindBar()
    {
        _findBarHost.Visibility = Visibility.Collapsed;
        Editor.ClearFindHighlight(); // the highlight-all overlay lives only while the bar is open
        // FocusEditor, not Focus: the control itself is not a tab stop (focus lives on its inner canvas),
        // so Focus() here left the caret unpainted and the keyboard dead after closing the bar.
        Editor.FocusEditor();
    }

    // Single place that drives the replace row + the chevron's checked/glyph state, so opening via Ctrl+H
    // and toggling the chevron can never disagree.
    private void SetReplaceVisible(bool show)
    {
        _replaceRow!.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _expandReplace!.IsChecked = show;
        _expandReplace.Content = show ? "▾" : "▸";
    }

    // Refreshes the "n/m" counter from the editor ("m" when the selection isn't on a match).
    private void UpdateMatchLabel()
    {
        if (_matchLabel == null) return;
        var (cur, total) = Editor.GetFindMatchPosition();
        _matchLabel.Text = total == 0 ? "0" : cur > 0 ? $"{cur}/{total}" : total.ToString();
    }

    private void BuildFindBar()
    {
        if (_findBar != null) return;

        _findBox = new TextBox { MinWidth = 200, PlaceholderText = L("Find"), FontSize = 12 };
        _findBox.KeyDown += (_, e) => { if (FindBoxKey(e.Key, IsShiftDown())) e.Handled = true; };
        // Live highlight-all while the user types (browser find behavior); counter tracks it.
        _findBox.TextChanged += (_, _) =>
        {
            Editor.SetFindHighlight(_findBox.Text, _matchCase?.IsChecked == true);
            UpdateMatchLabel();
        };

        _matchCase = new ToggleButton { Content = "Aa", FontSize = 12, Padding = new Thickness(6, 2, 6, 2) };
        _matchCase.AllowFocusOnInteraction = false;
        ToolTipService.SetToolTip(_matchCase, L("MatchCase"));
        _matchCase.Click += (_, _) =>
        {
            Editor.SetFindHighlight(_findBox.Text, _matchCase.IsChecked == true);
            UpdateMatchLabel();
        };

        _matchLabel = new TextBlock { FontSize = 12, MinWidth = 40, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };

        Button Btn(string text, string tip, Action act)
        {
            var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
            // Keep focus where it is (the search box), VS Code / browser style: clicking Next should not
            // move focus onto the button, or the next Enter re-fires the button instead of searching on.
            b.AllowFocusOnInteraction = false;
            ToolTipService.SetToolTip(b, tip);
            b.Click += (_, _) => act();
            return b;
        }

        // Leading chevron (VS Code / browser convention): expands the replace row in place, so a search
        // started with Ctrl+F or the toolbar's Find button doesn't have to be reopened with Ctrl+H.
        _expandReplace = new ToggleButton { Content = "▸", FontSize = 12, Padding = new Thickness(6, 2, 6, 2) };
        _expandReplace.AllowFocusOnInteraction = false;
        ToolTipService.SetToolTip(_expandReplace, L("ToggleReplace") + " (Ctrl+H)");
        _expandReplace.Click += (_, _) =>
        {
            bool show = _expandReplace.IsChecked == true && !Editor.IsReadOnly;
            SetReplaceVisible(show);
            if (show) _replaceBox?.Focus(FocusState.Programmatic);
        };

        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        findRow.Children.Add(_expandReplace);
        findRow.Children.Add(_findBox);
        findRow.Children.Add(_matchLabel);
        findRow.Children.Add(Btn("◀", L("FindPrevious"), () => DoFind(backwards: true)));
        findRow.Children.Add(Btn("▶", L("FindNext") + " (F3)", () => DoFind(backwards: false)));
        findRow.Children.Add(_matchCase);
        findRow.Children.Add(Btn("✕", L("Cancel"), HideFindBar));

        _replaceBox = new TextBox { MinWidth = 200, PlaceholderText = L("Replace"), FontSize = 12 };
        _replaceBox.KeyDown += (_, e) => { if (ReplaceBoxKey(e.Key, IsShiftDown())) e.Handled = true; };

        _replaceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _replaceRow.Children.Add(_replaceBox);
        _replaceRow.Children.Add(Btn(L("Replace"), L("Replace"), DoReplace));
        _replaceRow.Children.Add(Btn(L("ReplaceAll"), L("ReplaceAll"), DoReplaceAll));

        _findBar = new StackPanel { Spacing = 4, Padding = new Thickness(8, 6, 8, 6) };
        _findBar.Children.Add(findRow);
        _findBar.Children.Add(_replaceRow);
        _findBarHost.Child = _findBar;
    }

    // The boxes' keys, split from their handlers so a test can press them (KeyRoutedEventArgs has no public
    // constructor). F3 is handled here too: after Enter the focus stays in the query box, so F3 never reached the
    // editor's own F3 and did nothing (live check, 2026-09-19 — found in the upstream port of this bar first).
    internal bool FindBoxKey(VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.Enter: case VirtualKey.F3: DoFind(backwards: shift); return true;
            case VirtualKey.Escape: HideFindBar(); return true;
            default: return false;
        }
    }

    internal bool ReplaceBoxKey(VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.Enter: DoReplace(); return true;
            case VirtualKey.F3: DoFind(backwards: shift); return true;
            case VirtualKey.Escape: HideFindBar(); return true;
            default: return false;
        }
    }

    private static bool IsShiftDown()
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private void DoFind(bool backwards)
    {
        string q = _findBox?.Text ?? "";
        if (q.Length == 0) return;
        bool mc = _matchCase?.IsChecked == true;
        if (backwards) Editor.FindPrev(q, mc); else Editor.FindNext(q, mc);
        UpdateMatchLabel();
    }

    private void DoReplace()
    {
        string q = _findBox?.Text ?? "";
        if (q.Length == 0 || Editor.IsReadOnly) return;
        Editor.ReplaceNext(q, _replaceBox?.Text ?? "", _matchCase?.IsChecked == true);
        UpdateMatchLabel();
    }

    private void DoReplaceAll()
    {
        string q = _findBox?.Text ?? "";
        if (q.Length == 0 || Editor.IsReadOnly) return;
        Editor.ReplaceAll(q, _replaceBox?.Text ?? "", _matchCase?.IsChecked == true);
        UpdateMatchLabel();
    }
}
