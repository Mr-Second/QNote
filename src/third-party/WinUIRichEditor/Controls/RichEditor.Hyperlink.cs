using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Phase 5: hyperlinks. A run's NavigateUri is rendered underlined (BuildTextLayout) and editable via a
// ContentDialog (context menu "Edit Link..."). Opening a link launches the system handler.
public partial class RichEditor
{
    /// <summary>Applies a hyperlink to the selection (or the caret word); pass null/empty to remove it.</summary>
    public void SetHyperlink(string? url)
    {
        var u = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        ApplyStyleToSelection(r => r.NavigateUri = u);
    }

    /// <summary>The hyperlink URL at the caret, or null if the caret is not on a linked run.</summary>
    public string? CurrentLinkUri()
    {
        if (_caret.Paragraph is not { } p) return null;
        return RunAtOffset(p, _caret.Offset > 0 ? _caret.Offset - 1 : 0)?.NavigateUri;
    }

    /// <summary>Opens a dialog to set/edit the hyperlink on the selection (or caret word). The dialog
    /// offers OK (apply), Remove (clear), and Cancel.</summary>
    public async Task EditHyperlinkAsync()
    {
        if (Document == null || IsReadOnly || XamlRoot == null) return;
        var box = new TextBox
        {
            Text = CurrentLinkUri() ?? "https://",
            AcceptsReturn = false,
            Width = 360,
        };
        var dlg = new ContentDialog
        {
            Title = RichEditorLocalization.GetString("Hyperlink"),
            Content = box,
            PrimaryButtonText = RichEditorLocalization.GetString("OK"),
            SecondaryButtonText = RichEditorLocalization.GetString("RemoveLink"),
            CloseButtonText = RichEditorLocalization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        // ShowAsync throws when another ContentDialog is already open — and every caller invokes this as
        // `_ = EditHyperlinkAsync()`, so the throw would die in an unobserved Task with nothing on screen
        // and nothing in the fault channel. Same fire-and-forget hole the toolbar's file actions had.
        // Ready to type: focus in the box with the caret after the address ("https://|"). The box opened with no
        // focus, so the address could not be typed until it was clicked (live check, 2026-09-14).
        dlg.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.Select(box.Text.Length, 0);
        };
        ContentDialogResult result;
        try { result = await dlg.ShowAsync(); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return; }
        if (result == ContentDialogResult.Primary) ApplyHyperlinkFromDialog(box.Text);
        else if (result == ContentDialogResult.Secondary) SetHyperlink(null);
        _canvas.Focus(FocusState.Programmatic); // back to the caret the dialog was opened from
    }

    // The link dialog's OK: the selection or the caret's word gets the link. With neither — a blank spot — the
    // address itself goes in as the link's text, as Word does: arming a link for "the next typed text" showed
    // nothing at all, so the inserted link looked lost (live check, 2026-09-14).
    private void ApplyHyperlinkFromDialog(string url)
    {
        var u = url.Trim();
        if (u.Length == 0) return;
        bool blank = ResolveStyleTarget().Pending;
        SetHyperlink(u);          // blank: arms the link for the text typed next
        if (blank) InsertText(u); // …which is the address
    }

    /// <summary>Identifies the <see cref="AutoLinkOnType"/> dependency property.</summary>
    public static readonly DependencyProperty AutoLinkOnTypeProperty = DependencyProperty.Register(
        nameof(AutoLinkOnType), typeof(bool), typeof(RichEditor), new PropertyMetadata(true));

    /// <summary>When true (default), typing a space/tab/Enter right after a URL-looking token
    /// (http://, https://, www.) turns it into a hyperlink (Word/HWP behavior).
    /// <para>A dependency property, like every other behaviour flag (<see cref="IsReadOnly"/>,
    /// <c>Allow*</c>), so it can be bound and styled rather than only assigned in code.</para></summary>
    public bool AutoLinkOnType
    {
        get => (bool)GetValue(AutoLinkOnTypeProperty);
        set => SetValue(AutoLinkOnTypeProperty, value);
    }

    // Called after a whitespace commit at `boundary` (the offset just past the token). Applies
    // NavigateUri to the completed token when it parses as an http(s) URL; runs inside the same undo
    // group as the typing. "www." tokens link to "https://" + token. Trailing sentence punctuation is
    // excluded, an already-linked token is left alone (don't clobber a manually edited link).
    private void TryAutoLink(Paragraph p, int boundary)
    {
        string plain = BuildPlain(p);
        int end = Math.Clamp(boundary, 0, plain.Length);
        int start = end;
        while (start > 0 && !char.IsWhiteSpace(plain[start - 1]) && plain[start - 1] != '￼') start--;
        if (end - start < 8) return; // shortest sensible candidate ("http://x", "www.a.bc")
        string token = TrimUrlTail(plain.Substring(start, end - start));
        if (token.Length < 8) return;

        bool www = token.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        bool http = token.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || token.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!www && !http) return;
        string url = www ? "https://" + token : token;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.Host.Contains('.')) return;
        if (RunAtOffset(p, start)?.NavigateUri is { Length: > 0 }) return;

        new Documents.TextRange(new Documents.TextPointer(p, start), new Documents.TextPointer(p, start + token.Length))
            .ApplyPropertyValue(r => r.NavigateUri = url);
    }

    // Sentence punctuation after a URL is not part of it — but a closing bracket that CLOSES one inside the
    // URL is: Wikipedia's .../wiki/Foo_(bar). Trimming ')' unconditionally linked ".../Foo_(bar", a different
    // page (measured; upstream has the same TrimEnd). A closer is dropped only while the token has more
    // closers than openers, so "(see https://example.com)" still loses its ')'.
    internal static string TrimUrlTail(string token)
    {
        while (token.Length > 0)
        {
            char c = token[^1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or '"' or '\'') { token = token[..^1]; continue; }
            if (c == ')' && Count(token, ')') > Count(token, '(')) { token = token[..^1]; continue; }
            if (c == ']' && Count(token, ']') > Count(token, '[')) { token = token[..^1]; continue; }
            break;
        }
        return token;

        static int Count(string s, char ch)
        {
            int n = 0;
            foreach (var x in s) if (x == ch) n++;
            return n;
        }
    }

    // The hyperlinked run under a document point: the character UNDER the pointer. Hover, Ctrl+click and the
    // read-only click all used to ask "is the run before the caret a link" — but the caret a click produces
    // is the NEAREST character boundary, so the left half of a link's first character landed on the
    // boundary before it (not a link) and the left half of the character just AFTER a link landed on its
    // end (a link): a click half a character outside a link opened it, one inside its first character did
    // not (measured). Upstream hit-tests the character itself (IsInside). This derives the same answer from
    // the one layout the caret already comes from (core rule #1): pointer left of the caret → the character
    // before it, right of it → the character at it, and none when that character is on another line — the
    // pointer is past a line's end or before its start.
    private Run? LinkRunAtPoint(Windows.Foundation.Point pt) => LinkHitAtPoint(pt)?.run;

    // LinkRunAtPoint with the character it hit — a right-click puts the caret just past it (CaretJustPast).
    private (Paragraph p, int ch, Run run)? LinkHitAtPoint(Windows.Foundation.Point pt)
    {
        if (GetPositionFromPoint(pt) is not { Paragraph: { } p } tp) return null;
        if (CaretToDocPoint(tp) is not { } c || pt.Y < c.LineTop || pt.Y > c.LineBottom) return null;
        int ch;
        if (pt.X >= c.X)
        {
            if (tp.AtLineEnd || tp.Offset >= GetParagraphLength(p) || BuildPlain(p)[tp.Offset] == '\n') return null;
            ch = tp.Offset;
        }
        else
        {
            if (tp.Offset == 0) return null;
            if (CaretToDocPoint(new TextPointer(p, tp.Offset - 1)) is not { } prev || Math.Abs(prev.LineTop - c.LineTop) > 0.5) return null;
            ch = tp.Offset - 1;
        }
        int pos = 0;
        foreach (var inl in p.Inlines)
        {
            int n = InlineLen(inl);
            if (ch < pos + n) return inl is Run { NavigateUri: { Length: > 0 } } r ? (p, ch, r) : null;
            pos += n;
        }
        return null;
    }

    // The caret just past character `ch` — inside a link when `ch` is one of its characters, which is what the
    // caret-based link actions (CurrentLinkUri, OpenLinkAtCaretAsync, EditHyperlinkAsync) read. Kept on `ch`'s
    // own line when that boundary is also a soft-wrap point (AtLineEnd), so the caret does not jump a line.
    private TextPointer CaretJustPast(Paragraph p, int ch)
    {
        var after = new TextPointer(p, ch + 1);
        if (CaretToDocPoint(after) is { } a && CaretToDocPoint(new TextPointer(p, ch)) is { } b
            && Math.Abs(a.LineTop - b.LineTop) > 0.5)
            after.AtLineEnd = true;
        return after;
    }

    /// <summary>Launches the hyperlink at the caret in the system browser, if any.</summary>
    public Task OpenLinkAtCaretAsync() => OpenUriAsync(CurrentLinkUri());

    // Test seam: set, the URI goes here instead of the system browser. WHICH gesture launches a link
    // (Ctrl+click when editable, a plain click in a viewer, and neither on a drag) had no automated test
    // at all, because asserting it means launching a browser on the test machine. The decision itself
    // (IsLaunchableLink) is tested directly; this covers the path that reaches it.
    internal Func<Uri, Task>? LaunchOverride { get; set; }

    // Launches a specific absolute URI. Callers that captured the link at press time (the read-only
    // plain-click path) pass it in rather than re-reading the caret, which may have moved since.
    internal async Task OpenUriAsync(string? url)
    {
        if (!IsLaunchableLink(url, out var uri)) return;
        try
        {
            if (LaunchOverride is { } launch) await launch(uri);
            else await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
    }

    // Only WEB links are launched. A document's links come from anywhere — a pasted web page, an RTF from
    // Word or HWP, someone else's file — and the launcher hands any scheme to whatever handler is registered
    // for it: search-ms:, ms-settings:, ms-msdt: (the Follina handler), an application's own protocol… with
    // no prompt, from one Ctrl+click, or in a read-only viewer a plain click. This used to launch whatever
    // Uri.TryCreate accepted. Upstream's OpenUrl already allows http/https only; converged on the same rule
    // (so mailto: links, too, are left to the host).
    internal static bool IsLaunchableLink(string? url, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (url is not { Length: > 0 } || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        uri = u;
        return true;
    }
}
