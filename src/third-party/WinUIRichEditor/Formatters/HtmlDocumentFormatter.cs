using HtmlAgilityPack;
using System.Collections.Generic;
using System.Linq;
using WinUIRichEditor.Documents;
using System;
using System.Text;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;

namespace WinUIRichEditor.Formatters;

/// <summary>Converts between <see cref="FlowDocument"/> and HTML.
/// Supports full round-trip for bold/italic/underline/strikethrough, colors, sizes, alignment,
/// headings, lists, tables (with cell merge and per-cell background), images, hyperlinks, and
/// horizontal rules.
/// <para>HTML has no inline table, so an <see cref="InlineTable"/> is emitted as a <c>&lt;table&gt;</c>
/// carrying a <c>data-are-inline</c> marker and sized to its own columns
/// (<c>display:inline-table</c>), so it sits in the text line in browsers and Word. This parser reads
/// that marker back onto the text line; HTML from other applications carries no marker and keeps
/// producing a block-level <see cref="TableBlock"/>.</para></summary>
public static class HtmlDocumentFormatter
{
    private static readonly HashSet<string> BlockOrMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        "div","p","table","ul","ol","li","img","h1","h2","h3","h4","h5","h6",
        "section","article","figure","figcaption","header","footer","main","aside","tr","blockquote","hr","pre"
    };

    private static readonly HashSet<string> BlockLeaf = new(StringComparer.OrdinalIgnoreCase)
    {
        "p","h1","h2","h3","h4","h5","h6","li","blockquote","div","section","article",
        "figure","figcaption","header","footer","main","aside","pre","caption"
    };

    // 20MB response cap: a hostile/huge <img src="http:…"> can't balloon memory during a paste.
    private static readonly System.Net.Http.HttpClient Http = new()
    { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 20 * 1024 * 1024 };
    private static readonly TimeSpan RemoteImageBudget = TimeSpan.FromSeconds(5);
    [ThreadStatic] private static bool _blockLocalFileImages;
    [ThreadStatic] private static bool _blockRemoteImages;
    [ThreadStatic] private static bool _allowTempFileImages;
    [ThreadStatic] private static Dictionary<string, byte[]?>? _prefetchedRemoteImages;
    // Per-parse memo for HasBlockOrMedia: the naive Descendants().Any() per node is O(n²) on deep
    // documents (Word/Docs HTML nests hard); the memoized child recursion is O(n) total.
    [ThreadStatic] private static Dictionary<HtmlNode, bool>? _blockOrMediaMemo;

    // A normalized bold/normal weight: HTML parsing only ever needs the two states, and the WinRT
    // FontWeight struct can't be a default parameter value, so we carry default(FontWeight) around and
    // collapse it here at the point a Run is built.
    private static FontWeight NormalizeWeight(FontWeight w) => w.IsBold() ? FontWeightValues.Bold : FontWeightValues.Normal;

    /// <summary>Parses an HTML string into a <see cref="FlowDocument"/>.
    /// When <paramref name="allowLocalFileImages"/> is false, <c>file://</c> image sources are skipped —
    /// EXCEPT files under the user's temp directory, which still load: Word and HWP clipboard HTML
    /// reference the pictures the copy itself just wrote there, and refusing them drops every image
    /// pasted from those applications. (The editor's own <see cref="Controls.RichEditor.LoadHtml"/> and
    /// <see cref="Controls.RichEditor.InsertHtml"/> do not take this exemption.)
    /// <para>This overload performs NO network I/O: remote (<c>http</c>) images are skipped, because
    /// fetching them here would block the calling thread — typically the UI thread, via
    /// <see cref="Controls.RichEditor.LoadHtml"/>/<see cref="Controls.RichEditor.InsertHtml"/>. Use
    /// <see cref="ParseHtmlAsync(string, bool, bool)"/> (or <see cref="Controls.RichEditor.LoadHtmlAsync"/>) to include them;
    /// <c>data:</c> and <c>file:</c> images load on both paths.</para></summary>
    public static FlowDocument ParseHtml(string html, bool allowLocalFileImages = true, bool allowRemoteImages = true)
        => ParseHtml(html, allowLocalFileImages, allowRemoteImages, allowTempFileImages: true);

    // allowTempFileImages: whether files under %TEMP% still load when local files are blocked. That is the
    // PASTE exemption (see the public overload), and it used to apply to every caller — so an editor with
    // AllowLocalFileImages=false, whose contract is "file:// images are not loaded by LoadHtml/InsertHtml",
    // still read whatever sat under %TEMP% (other applications' temp files included). The public entry
    // points keep the exemption (a test pins it and hosts may rely on it); the editor's LoadHtml/InsertHtml
    // pass false. Upstream has no exemption at all.
    internal static FlowDocument ParseHtml(string html, bool allowLocalFileImages, bool allowRemoteImages, bool allowTempFileImages)
    {
        _blockLocalFileImages = !allowLocalFileImages;
        _blockRemoteImages = !allowRemoteImages;
        _allowTempFileImages = allowTempFileImages;
        _prefetchedRemoteImages = null;
        _blockOrMediaMemo = null; // fresh memo per parse
        var doc = LoadHtmlDoc(ref html);
        try { return Finish(BuildDocument(doc, html)); }
        finally { _blockOrMediaMemo = null; } // don't retain the DOM past the parse
    }

    // The reader writes every heading's bold and size onto its runs (HeadingSize, from the <h1>..<h6> it
    // came from), so the document is marked as carrying them: the editor must not convert it again — that
    // would re-bold heading text this library exported un-bolded (font-weight:normal).
    private static FlowDocument Finish(FlowDocument doc)
    {
        doc.HeadingFormatsApplied = true;
        return RunNormalizer.Compact(doc);
    }

    /// <summary>Same as <see cref="ParseHtml(string, bool, bool)"/> but downloads remote (<c>http</c>) images concurrently
    /// off the UI thread first, so a slow network can't freeze the UI while pasting web content.
    /// <paramref name="allowRemoteImages"/> false skips the network entirely (privacy: pasting HTML
    /// otherwise issues HTTP requests, e.g. to tracking pixels).</summary>
    public static System.Threading.Tasks.Task<FlowDocument> ParseHtmlAsync(string html, bool allowLocalFileImages = true, bool allowRemoteImages = true)
        => ParseHtmlAsync(html, allowLocalFileImages, allowRemoteImages, allowTempFileImages: true);

    // See the synchronous overload for allowTempFileImages.
    internal static async System.Threading.Tasks.Task<FlowDocument> ParseHtmlAsync(string html, bool allowLocalFileImages, bool allowRemoteImages, bool allowTempFileImages)
    {
        var doc = LoadHtmlDoc(ref html);
        var prefetched = allowRemoteImages
            ? await PrefetchRemoteImagesAsync(doc).ConfigureAwait(true)
            : new Dictionary<string, byte[]?>();
        _blockLocalFileImages = !allowLocalFileImages;
        _blockRemoteImages = !allowRemoteImages;
        _allowTempFileImages = allowTempFileImages;
        _prefetchedRemoteImages = prefetched;
        _blockOrMediaMemo = null; // fresh memo per parse
        try { return Finish(BuildDocument(doc, html)); }
        finally { _prefetchedRemoteImages = null; _blockOrMediaMemo = null; }
    }

    // Excel's CF_HTML fragment markers can sit inside the <table>, leaving orphan <tr>/<td> with no
    // wrapping <table>. Wrap it so it's recognized.
    private static HtmlDocument LoadHtmlDoc(ref string html)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(html, "<tr[\\s>]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(html, "<table", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            html = "<table>" + html + "</table>";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    private static FlowDocument BuildDocument(HtmlDocument doc, string html)
    {
        var flowDoc = new FlowDocument();
        var root = doc.DocumentNode.Descendants("body").FirstOrDefault() ?? doc.DocumentNode;
        WalkBlocks(root, flowDoc);

        if (flowDoc.Blocks.Count == 0)
        {
            // The fallback is for input that was never markup — a caller handing us plain text should
            // get that text, not an empty document. It must NOT fire for input that WAS markup and
            // simply had no content: our own export of an empty document is `<p style="…"></p>`, whose
            // walk yields no block, and dumping the source then put the editor's own tags on screen as
            // literal body text (save an empty document as HTML, reopen it, and there they were).
            // An element node anywhere is the discriminator: markup in, empty paragraph out.
            bool wasMarkup = root.Descendants().Any(n => n.NodeType == HtmlNodeType.Element);
            var p = new Paragraph();
            if (!wasMarkup) p.Inlines.Add(new Run { Text = HtmlEntity.DeEntitize(html) });
            flowDoc.Blocks.Add(p);
        }
        return flowDoc;
    }

    private static async System.Threading.Tasks.Task<Dictionary<string, byte[]?>> PrefetchRemoteImagesAsync(HtmlDocument doc)
    {
        var result = new Dictionary<string, byte[]?>();
        var urls = doc.DocumentNode.Descendants("img")
            .Select(n => n.GetAttributeValue("src", ""))
            .Where(s => s.StartsWith("http"))
            .Distinct()
            .ToList();
        if (urls.Count == 0) return result;

        using var cts = new System.Threading.CancellationTokenSource(RemoteImageBudget);
        async System.Threading.Tasks.Task<(string, byte[]?)> Fetch(string url)
        {
            try { return (url, await Http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false)); }
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); return (url, null); }
        }
        foreach (var (url, bytes) in await System.Threading.Tasks.Task.WhenAll(urls.Select(Fetch)).ConfigureAwait(false))
            result[url] = bytes;
        return result;
    }

    // Recursively walks the DOM, emitting Paragraph/TableBlock/ImageBlock as it goes. Consecutive inline
    // siblings accumulate into a single paragraph, flushed whenever a block-level element is encountered.
    private static void WalkBlocks(HtmlNode node, FlowDocument flow, string? linkUri = null)
    {
        Paragraph? current = null;

        // A whitespace-only #text between inline siblings is a WORD SEPARATOR, not layout padding:
        // `<span>a</span> <span>b</span>` reads "a b" everywhere, and dropping it merged them ("ab").
        // MergeCells joins a covered cell's text with exactly that space, which is how a merged cell
        // lost a word boundary on the second HTML round trip.
        //
        // It is DEFERRED rather than appended on sight, and that distinction is the whole design: the
        // same whitespace before `</p>` is padding, which a browser drops — appending eagerly grew a
        // trailing space on every cycle, the "separator becomes content and accumulates" failure this
        // codebase has now hit four times. A separator is only real once more inline content follows it.
        bool pendingSpace = false;
        void Flush()
        {
            if (current != null && current.Inlines.Count > 0) flow.Blocks.Add(current);
            current = null;
            pendingSpace = false; // never carries across a block boundary
        }

        // Call immediately before adding inline content, once that content is certain.
        void TakeSpace()
        {
            if (!pendingSpace) return;
            pendingSpace = false;
            if (current is { } p && p.Inlines.Count > 0 && p.Inlines[^1] is Run prev
                && !string.IsNullOrEmpty(prev.Text)
                && !prev.Text.EndsWith(" ", StringComparison.Ordinal)
                && !prev.Text.EndsWith("\n", StringComparison.Ordinal))
                p.Inlines.Add(new Run { Text = " " });
        }

        foreach (var child in node.ChildNodes)
        {
            string name = child.Name.ToLowerInvariant();

            string? childLink = linkUri;
            if (name == "a")
            {
                var href = child.GetAttributeValue("href", "");
                if (!string.IsNullOrEmpty(href)) childLink = SafeHref(href);
            }
            bool hasLink = !string.IsNullOrEmpty(childLink);

            if (name == "table")
            {
                var tbl = ParseTable(child);
                // Our own export marks a table that was inline (see EmitTable): put it back on the text
                // line instead of flushing the paragraph, following the same ladder as the small-icon
                // <img> case — the pending paragraph, else the preceding one, else a new one. Without
                // this an inline table came back as a BLOCK table and permanently split its host
                // paragraph on every save/load. Foreign HTML carries no marker and still lands as a block.
                if (tbl != null && child.GetAttributeValue("data-are-inline", "") == "1")
                {
                    var it = new InlineTable { Table = tbl };
                    // `data-are-opens` says the table was the FIRST thing in its paragraph. There is then
                    // no earlier paragraph of its own to rejoin, and taking the preceding one merges two
                    // paragraphs and swallows it — and "a paragraph holding nothing but the table" is the
                    // ordinary shape of an inline table, so this is the common case.
                    bool opensParagraph = child.GetAttributeValue("data-are-opens", "") == "1";
                    if (current == null && !opensParagraph && flow.Blocks.Count > 0 && flow.Blocks[^1] is Paragraph lastPara)
                    {
                        // Reopen that paragraph as the pending one (Flush re-adds it): HTML parsers close
                        // a <p> when a <table> starts, so the text that followed the table arrives as a
                        // later sibling and has to land back on the same line.
                        flow.Blocks.RemoveAt(flow.Blocks.Count - 1);
                        current = lastPara;
                    }
                    current ??= new Paragraph();
                    TakeSpace();
                    current.Inlines.Add(it);
                    continue;
                }
                Flush();
                if (tbl != null) flow.Blocks.Add(tbl);
            }
            else if (name == "img")
            {
                var (bytes, w, h, alt) = LoadImage(child);
                if (bytes != null)
                {
                    if (w < IconMaxSize && h < IconMaxSize)
                    {
                        // Small icon/logo -> keep on a text line rather than its own block.
                        var icon = new InlineImage { Width = w, Height = h, AltText = alt };
                        icon.SetImageData(bytes, ImageMime.Detect(bytes));
                        TakeSpace();
                        // Same rule as an inline table: `data-are-opens` says the image began its own
                        // paragraph. A <p> holding nothing but an image is walked as a block (an <img> is
                        // block-or-media), so `current` is null by the time we get here, and rejoining the
                        // PRECEDING paragraph swallowed the image's line — on every second round trip a
                        // picture on its own line jumped up into the paragraph above it.
                        bool imgOpens = child.GetAttributeValue("data-are-opens", "") == "1";
                        if (current != null)
                            current.Inlines.Add(icon);
                        else if (!imgOpens && flow.Blocks.Count > 0 && flow.Blocks[flow.Blocks.Count - 1] is Paragraph lastP)
                            lastP.Inlines.Add(icon);
                        else
                        {
                            current = new Paragraph();
                            current.Inlines.Add(icon);
                        }
                    }
                    else
                    {
                        Flush();
                        var ib = new ImageBlock { Width = w, Height = h, AltText = alt };
                        ib.SetImageData(bytes, ImageMime.Detect(bytes));
                        flow.Blocks.Add(ib);
                    }
                }
            }
            else if (name == "ul" || name == "ol")
            {
                Flush();
                ParseList(child, flow, name == "ol" ? ListKind.Ordered : ListKind.Bullet, 0, linkUri);
            }
            else if (name == "hr")
            {
                Flush();
                flow.Blocks.Add(new DividerBlock());
            }
            else if (name == "br")
            {
                // A bare space before a <br/> is padding: it renders at the end of a line, invisibly.
                // A space this library MEANT to keep there is written as &nbsp; (see EmitInline), so it
                // arrives as content and never reaches this branch.
                pendingSpace = false;
                current ??= new Paragraph();
                current.Inlines.Add(new Run { Text = "\n" });
            }
            else if (name == "#text")
            {
                string t = HtmlEntity.DeEntitize(child.InnerText);
                if (!IsCollapsibleWhitespace(t))
                {
                    TakeSpace();
                    current ??= new Paragraph();
                    current.Inlines.Add(new Run
                    {
                        Text = CollapseWhitespace(t),
                        NavigateUri = linkUri,
                        Foreground = hasLink ? Colors.Blue : (Color?)null
                    });
                }
                else if (current is { Inlines.Count: > 0 })
                {
                    // Only between inline siblings: after a Flush() there is no pending paragraph, so
                    // the newlines a pretty-printer puts BETWEEN blocks stay ignored as before.
                    pendingSpace = true;
                }
            }
            else if (name == "#comment" || name == "script" || name == "style" || name == "head" || name == "meta" || name == "link")
            {
                // ignore
            }
            else if (HasBlockOrMedia(child))
            {
                Flush();
                // A paragraph element whose only block-or-media content is MEDIA is still a paragraph.
                // This branch runs before the BlockLeaf one below, so `<p style="…">text<img/></p>` was
                // walked as a mere container and the element's own paragraph formatting — alignment,
                // indent, fill, heading level, quote, line height — was dropped on the way in. Every
                // picture with a caption line lost it, in a cell or not.
                //
                // A container with real BLOCK children (a <div> wrapping <p>s) is deliberately left as it
                // was: whether formatting should inherit down to them is a separate question, and foreign
                // HTML depends on today's answer.
                if (BlockLeaf.Contains(name) && !HasBlockChild(child))
                {
                    int at = flow.Blocks.Count;
                    WalkBlocks(child, flow, childLink);
                    for (int i = at; i < flow.Blocks.Count; i++)
                        if (flow.Blocks[i] is Paragraph made) ApplyBlockLeafFormat(child, name, made);
                }
                else WalkBlocks(child, flow, childLink);
            }
            else if (BlockLeaf.Contains(name))
            {
                Flush();
                var p = new Paragraph();
                ApplyBlockLeafFormat(child, name, p);
                double size = HeadingSize(name, out var headingWeight);
                ParseInlines(child, p, headingWeight, FontStyle.Normal, null, childLink, size, hasLink,
                    pre: name == "pre"); // <pre> keeps its whitespace/newlines verbatim
                // Empty elements are dropped — foreign HTML uses them for spacing — unless this export
                // marked one as a blank line the author actually typed (see data-are-empty).
                bool markedEmpty = child.GetAttributeValue("data-are-empty", "") == "1";
                // A marked paragraph carries a <br> so outside renderers give it a line; that <br> is the blank
                // line's rendering, not its content, and reading it back would turn one blank line into two.
                if (markedEmpty) p.Inlines.Clear();
                if (p.Inlines.Count > 0 || markedEmpty)
                    flow.Blocks.Add(p);
            }
            else
            {
                current ??= new Paragraph();
                // Unlike the branches above, this one may contribute NOTHING (an empty or ignorable
                // element), and a separator with no content after it is the trailing space again — so
                // take it back if nothing followed.
                int before = current.Inlines.Count;
                TakeSpace();
                int afterSpace = current.Inlines.Count;
                // The node itself (not just its children) goes through the inline path, so a bare
                // formatted element with no block wrapper keeps its own tag/style formatting.
                // Parent link context is passed; the node's own <a>/style is read inside.
                ParseInlineNode(child, current, uri: linkUri, inLink: !string.IsNullOrEmpty(linkUri));
                if (afterSpace > before && current.Inlines.Count == afterSpace)
                    current.Inlines.RemoveAt(afterSpace - 1);
            }
        }

        Flush();
    }

    private static void ParseList(HtmlNode listNode, FlowDocument flow, ListKind kind, int level, string? linkUri)
    {
        var marker = ListMarkerFromCss(ReadStyleValue(listNode, "list-style-type"));

        // ONE pass, in document order. Two things live side by side here and the order between them is
        // the content's order, not a category order:
        //   <li>            — an item at this level.
        //   <ul>/<ol>       — a sublist that is a DIRECT child, with no <li> wrapping it. Our own export
        //                     makes exactly that shape whenever the deeper item follows a shallower one
        //                     (A / B-indented / C emits <ul><li>A</li><ul><li>B</li></ul><li>C</li></ul>),
        //                     and also for an item with no shallower item above it at all (indent the
        //                     only list item in a document and you get <ol><ol><li>…).
        // Handling these in two separate passes — sublists first, then items — is what a previous fix for
        // the second shape did, and it silently REORDERED the first: every nested item was emitted ahead
        // of the item it belongs under, so an ordinary sub-bullet moved above its parent on every HTML
        // round trip. Walk the children once and the order takes care of itself.
        foreach (var child in listNode.ChildNodes)
        {
            bool isSub = child.Name.Equals("ul", StringComparison.OrdinalIgnoreCase)
                      || child.Name.Equals("ol", StringComparison.OrdinalIgnoreCase);
            if (isSub)
            {
                ParseList(child, flow, child.Name.Equals("ol", StringComparison.OrdinalIgnoreCase) ? ListKind.Ordered : ListKind.Bullet,
                          level + 1, linkUri);
                continue;
            }
            if (!child.Name.Equals("li", StringComparison.OrdinalIgnoreCase)) continue;

            var p = new Paragraph { ListType = kind, ListLevel = level, ListMarker = marker };
            // An <li> that was also a heading (see the export's data-are-h): HTML has no tag for both.
            int liHeading = child.GetAttributeValue("data-are-h", 0);
            if (liHeading >= 1 && liHeading <= 6) p.HeadingLevel = liHeading;
            // A list item is a paragraph and carries paragraph formatting like any other. This read only
            // the line height, so an item's FILL, indent, alignment and spacing were written by the
            // exporter and then dropped on the way back in — `<li style="…background-color:…">` went out
            // and came back plain. Found by the 1.1 consumer smoke test, which combined "is a list item"
            // with "has a fill"; the unit tests had only ever checked those two separately, and the fuzz
            // could not see it because the loss is IDEMPOTENT (cycle 2 loses exactly what cycle 1 did).
            // `name` is passed as "li" so the heading-from-tag rule does not disturb data-are-h above.
            ApplyBlockLeafFormat(child, "li", p);
            ParseInlines(child, p, uri: linkUri, inLink: !string.IsNullOrEmpty(linkUri));
            // An empty item is dropped like any empty element — unless our export marked it as a blank item
            // the author made (data-are-empty, as for paragraphs), and then its <br> is rendering, not content.
            // Dropped regardless, a blank numbered item vanished on the first round trip, and the items either
            // side could merge into one list on the second and lose a marker (fuzz seed 8178, 2026-09-24).
            bool markedEmpty = child.GetAttributeValue("data-are-empty", "") == "1";
            if (markedEmpty) p.Inlines.Clear();
            if (p.Inlines.Count > 0 || markedEmpty) flow.Blocks.Add(p);

            // A sublist nested INSIDE the item (the shape most other producers emit) still follows it.
            foreach (var nested in child.ChildNodes.Where(n => n.Name.Equals("ul", StringComparison.OrdinalIgnoreCase) || n.Name.Equals("ol", StringComparison.OrdinalIgnoreCase)))
                ParseList(nested, flow, nested.Name.Equals("ol", StringComparison.OrdinalIgnoreCase) ? ListKind.Ordered : ListKind.Bullet, level + 1, linkUri);
        }
    }

    private static string? ReadStyleValue(HtmlNode node, string prop)
    {
        var style = node.GetAttributeValue("style", "");
        if (string.IsNullOrEmpty(style)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(style, prop + @"\s*:\s*([^;]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static TextAlignment ReadAlign(HtmlNode node)
    {
        string a = node.GetAttributeValue("align", "").ToLowerInvariant();
        var style = node.GetAttributeValue("style", "").ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(style, "text-align\\s*:\\s*(left|center|right|justify)");
        if (m.Success) a = m.Groups[1].Value;
        return a switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
    }

    // Heading sizes in points (pt), mirroring RichEditor.HeadingFontSize.
    private static double HeadingSize(string name, out FontWeight weight)
    {
        switch (name)
        {
            case "h1": weight = FontWeightValues.Bold; return 20;
            case "h2": weight = FontWeightValues.Bold; return 16;
            case "h3": weight = FontWeightValues.Bold; return 14;
            case "h4": weight = FontWeightValues.Bold; return 12;
            case "h5": weight = FontWeightValues.Bold; return 11;
            case "h6": weight = FontWeightValues.Bold; return 10;
            default: weight = FontWeightValues.Normal; return 10;
        }
    }

    // Block-level content only — BlockOrMedia without <img>. The distinction is what separates "this
    // element is a container of paragraphs" from "this element IS a paragraph that happens to hold a
    // picture", and only the second can carry its formatting onto what the walk produces.
    private static bool HasBlockChild(HtmlNode n)
    {
        foreach (var c in n.ChildNodes)
            if ((BlockOrMedia.Contains(c.Name) && !c.Name.Equals("img", StringComparison.OrdinalIgnoreCase))
                || HasBlockChild(c)) return true;
        return false;
    }

    // A Paragraph's MarginBottom default (0, as in HWP). A paragraph at the default writes no margin at
    // all, which is what keeps an ordinary document's HTML unchanged.
    private const double DefaultMarginBottom = 0;

    // The paragraph-level formatting an element carries. Only values the element actually states are
    // written, so applying this to a paragraph the walk already produced cannot clobber that
    // paragraph's own formatting with defaults.
    private static void ApplyBlockLeafFormat(HtmlNode node, string name, Paragraph p)
    {
        if (name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6') p.HeadingLevel = name[1] - '0';
        if (ReadBackground(node) is { } bg) p.Background = bg;
        if (ReadIndentPx(node) is > 0 and var ind) p.Indent = ind;
        if (name == "blockquote") p.IsQuote = true;
        if (ReadAlign(node) is var al && al != TextAlignment.Left) p.TextAlignment = al;
        ApplyLineHeightStyle(node, p);
        ApplyMarginMarker(node, p);
    }

    // Paragraph spacing, from this library's own marker only (see EmitParagraphElement for why foreign
    // CSS margins are deliberately not read).
    private static void ApplyMarginMarker(HtmlNode node, Paragraph p)
    {
        var v = node.GetAttributeValue("data-are-m", "");
        if (string.IsNullOrEmpty(v)) return;
        var parts = v.Split(',');
        if (parts.Length != 3) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out double t)) p.MarginTop = t;
        if (double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out double b)) p.MarginBottom = b;
        if (double.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out double r)) p.MarginRight = r;
    }

    private static bool HasBlockOrMedia(HtmlNode n)
    {
        var memo = _blockOrMediaMemo ??= new Dictionary<HtmlNode, bool>();
        if (memo.TryGetValue(n, out var known)) return known;
        bool result = false;
        foreach (var c in n.ChildNodes)
            if (BlockOrMedia.Contains(c.Name) || HasBlockOrMedia(c)) { result = true; break; }
        memo[n] = result;
        return result;
    }

    // HTML collapses runs of COLLAPSIBLE whitespace to one space. A non-breaking space is not
    // collapsible — that is the whole point of it, and the export relies on it to carry the editor's
    // consecutive spaces (see PreserveRunsOfSpaces). Regex `\s` matches U+00A0 (Unicode class Zs), so
    // the old `\s+` folded exactly the character that was there to survive folding.
    //
    // The model has no non-breaking space of its own, so an nbsp becomes a plain space AFTER the fold.
    // Foreign HTML gains from this too: Word and HWP pad with runs of &nbsp;, which used to arrive as a
    // single space and now keep their width.
    private static string CollapseWhitespace(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, "[ \t\r\n\f\v]+", " ");
        return s.Replace(' ', ' ');
    }

    // Whitespace that HTML would collapse away, i.e. what makes a text node pure layout rather than
    // content. A node of nothing but &nbsp; is CONTENT and must not be mistaken for a separator, which
    // is why this is not string.IsNullOrWhiteSpace (that counts U+00A0 as whitespace).
    private static bool IsCollapsibleWhitespace(string s)
    {
        if (s.Length == 0) return true;
        foreach (char ch in s)
            if (ch is not (' ' or '\t' or '\r' or '\n' or '\f' or '\v')) return false;
        return true;
    }

    // Ceiling on the column count an imported table may claim. Foreign HTML controls colspan, and the
    // occupancy grid (and then the TableBlock) is allocated from it. Far beyond any real document —
    // Word tops out at 63 columns — and matched to the JSON importer's own cap.
    private const int MaxTableColumns = 1000;

    private static TableBlock? ParseTable(HtmlNode node)
    {
        var rows = node.Descendants("tr")
            .Where(tr => tr.Ancestors("table").FirstOrDefault() == node)
            .ToList();
        if (rows.Count == 0) return null;
        int R = rows.Count;

        var cellNodes = new List<List<HtmlNode>>();
        foreach (var tr in rows)
            cellNodes.Add(tr.ChildNodes.Where(n => n.Name == "td" || n.Name == "th").ToList());

        var occupied = new List<List<bool>>();
        for (int i = 0; i < R; i++) occupied.Add(new List<bool>());
        var placements = new List<List<(int col, int cs, int rs, HtmlNode node)>>();
        for (int i = 0; i < R; i++) placements.Add(new List<(int, int, int, HtmlNode)>());
        int colCount = 0;

        static void Ensure(List<bool> row, int upTo) { while (row.Count <= upTo) row.Add(false); }

        for (int r = 0; r < R; r++)
        {
            int col = 0;
            foreach (var td in cellNodes[r])
            {
                Ensure(occupied[r], col);
                while (col < occupied[r].Count && occupied[r][col]) col++;
                // Both spans are attacker-controlled (any pasted web page is foreign input) and the
                // occupancy grid is sized from them, so both need a ceiling. rowspan is naturally bounded
                // by the rows that actually exist; colspan had none, so a single colspan="100000000" grew
                // the grid — and then the TableBlock — until the process ran out of memory.
                int cs = Math.Clamp(td.GetAttributeValue("colspan", 1), 1, MaxTableColumns);
                int rs = Math.Max(1, Math.Min(td.GetAttributeValue("rowspan", 1), R - r));
                placements[r].Add((col, cs, rs, td));
                for (int rr = r; rr < r + rs; rr++)
                {
                    Ensure(occupied[rr], col + cs - 1);
                    for (int cc = col; cc < col + cs; cc++) occupied[rr][cc] = true;
                }
                col += cs;
                colCount = Math.Max(colCount, col);
            }
        }
        if (colCount == 0) return null;
        if (colCount > MaxTableColumns) colCount = MaxTableColumns;

        var tb = new TableBlock(R, colCount);
        var colNodes = node.Descendants("col")
            .Where(co => co.Ancestors("table").FirstOrDefault() == node)
            .ToList();
        if (colNodes.Count > 0)
        {
            tb.ColumnWidths.Clear();
            for (int c = 0; c < colCount; c++)
            {
                double cw = c < colNodes.Count ? ReadPx(colNodes[c], "width", "width") : 0;
                tb.ColumnWidths.Add(cw > 0 ? cw : 100);
            }
        }
        for (int r = 0; r < R; r++)
            foreach (var (col, cs, rs, td) in placements[r])
            {
                if (cs > 1 || rs > 1) tb.SetSpan(r, col, cs, rs);
                var cell = tb.Cells[r][col];
                cell.Background = ReadBackground(td);
                cell.VerticalAlignment = ReadCellVAlign(td);
                var cellFlow = new FlowDocument();
                WalkBlocks(td, cellFlow);
                cell.Blocks.Clear();
                foreach (var b in cellFlow.Blocks) cell.Blocks.Add(b);
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "" } } });
            }
        return tb;
    }

    // Images below this size (px, both dimensions) are treated as inline icons/logos/emoji.
    private const double IconMaxSize = 64;

    // Loads an <img> and returns the original encoded bytes plus its intended display size (declared px
    // when present, otherwise the natural size read from the header) and its alt text. The actual GPU
    // decode is deferred to the render layer. Returns (null,0,0,null) on failure/unsupported source.
    private static (byte[]?, double, double, string?) LoadImage(HtmlNode node)
    {
        var src = node.GetAttributeValue("src", "");
        if (string.IsNullOrEmpty(src)) return (null, 0, 0, null);

        string altAttr = node.GetAttributeValue("alt", "");
        string? alt = string.IsNullOrWhiteSpace(altAttr) ? null : HtmlEntity.DeEntitize(altAttr);
        double declW = ReadPx(node, "width", "width");
        double declH = ReadPx(node, "height", "height");

        try
        {
            byte[]? bytes = null;
            if (src.StartsWith("data:image"))
            {
                var comma = src.IndexOf(',');
                if (comma >= 0) bytes = System.Convert.FromBase64String(src.Substring(comma + 1));
            }
            else if (src.StartsWith("http"))
            {
                if (_blockRemoteImages) return (null, 0, 0, null);
                // Remote images are fetched ONLY by the async entry point, which downloads them up front
                // and hands them in here. The synchronous ParseHtml used to block on
                // GetByteArrayAsync().GetAwaiter().GetResult() — a network round trip on whatever thread
                // called it, i.e. a frozen UI for callers like LoadHtml/InsertHtml. Use ParseHtmlAsync /
                // RichEditor.LoadHtmlAsync when remote images are wanted; data: and file: still load here.
                if (_prefetchedRemoteImages == null) return (null, 0, 0, null);
                _prefetchedRemoteImages.TryGetValue(src, out bytes);
                if (bytes == null) return (null, 0, 0, null);
            }
            else if (src.StartsWith("file:") || src.StartsWith("ms-clipboard-file:", StringComparison.OrdinalIgnoreCase))
            {
                // HtmlFormatHelper.GetStaticFragment (the CF_HTML fragment extractor the paste path
                // uses) REWRITES file:/// references to the ms-clipboard-file: scheme — treat it as
                // file:, or every HWP/Word picture reference silently misses this branch.
                if (src.StartsWith("ms-clipboard-file:", StringComparison.OrdinalIgnoreCase))
                    src = "file:" + src.Substring("ms-clipboard-file:".Length);
                var uri = new Uri(src);
                // A file on ANOTHER machine (file://host/share/…) is a UNC path, and touching it opens an SMB
                // connection that offers the user's NTLM credentials to that host — from a paste, with an <img src>
                // the copied page chose. Local file images are about this machine; a network share is never read,
                // whatever the flags say (upstream round 34; measured here: a 21 s connect attempt).
                if (uri.IsUnc) return (null, 0, 0, null);
                var path = uri.LocalPath;
                // Even when local files are blocked (the paste default), allow paths under %TEMP% — when the
                // caller takes the paste exemption: Word/HWP CF_HTML reference the pictures the COPY itself
                // just wrote there, and refusing them silently drops every image pasted from those apps.
                // Anything outside the temp directory (a hostile page referencing user files) stays blocked,
                // and so does temp itself for a caller that did not ask for the exemption.
                if (_blockLocalFileImages && !(_allowTempFileImages && IsTempPath(path))) return (null, 0, 0, null);
                if (System.IO.File.Exists(path)) bytes = System.IO.File.ReadAllBytes(path);
            }
            if (bytes == null) return (null, 0, 0, null);
            var (natW, natH) = ImageInfo.GetPixelSize(bytes);
            // Unknown natural size with no declared size -> NaN (= "natural", resolved at render): this
            // also makes the icon test below fail, so an unsized image becomes a block, not a 0px icon.
            double w = (!double.IsNaN(declW) && declW > 0) ? declW : (natW > 0 ? natW : double.NaN);
            double h = (!double.IsNaN(declH) && declH > 0) ? declH : (natH > 0 ? natH : double.NaN);
            return (bytes, w, h, alt);
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return (null, 0, 0, null); }
    }

    // True when the path resolves under the user's temp directory (clipboard image files live there).
    private static bool IsTempPath(string path)
    {
        try
        {
            string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            return System.IO.Path.GetFullPath(path).StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return false; }
    }

    // A link's address, or null for a script link (javascript:, vbscript:, data:). The text stays; only the link
    // goes. Kept, a pasted page's script link was written back out as an <a href> into exported and clipboard HTML
    // (upstream round 34, user decision: drop on read). Checked the way a browser reads a scheme: entities decoded,
    // case ignored, whitespace and control characters (a tab inside "java\tscript:") not counted.
    internal static string? SafeHref(string href)
    {
        var sb = new StringBuilder();
        foreach (char ch in HtmlEntity.DeEntitize(href))
        {
            if (ch <= ' ') continue;
            if (ch == ':') break;
            sb.Append(char.ToLowerInvariant(ch));
            if (sb.Length > 16) break; // longer than any scheme below
        }
        string scheme = sb.ToString();
        return scheme is "javascript" or "vbscript" or "data" ? null : href;
    }

    private static double ReadPx(HtmlNode node, string attr, string cssProp)
    {
        var a = node.GetAttributeValue(attr, "").Trim();
        // HWP emits nonstandard unit suffixes in the ATTRIBUTES (width="168pt"); accept pt (→px ×4/3)
        // and px so those sizes aren't silently dropped. Percentages still fall through (NaN).
        double mult = 1;
        if (a.EndsWith("pt", StringComparison.OrdinalIgnoreCase)) { mult = 96.0 / 72.0; a = a[..^2]; }
        else if (a.EndsWith("px", StringComparison.OrdinalIgnoreCase)) a = a[..^2];
        if (double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v * mult;
        var style = node.GetAttributeValue("style", "");
        if (!string.IsNullOrEmpty(style))
        {
            var m = System.Text.RegularExpressions.Regex.Match(style, cssProp + "\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)px",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var px)) return px;
        }
        return double.NaN;
    }

    private static void ParseInlines(HtmlNode node, Paragraph p, FontWeight weight = default, FontStyle style = FontStyle.Normal, Color? color = null, string? uri = null, double baseSize = 10, bool inLink = false, Color? background = null, string? family = null, bool underline = false, bool strike = false, bool pre = false, bool ownColor = false)
    {
        foreach (var child in node.ChildNodes)
            ParseInlineNode(child, p, weight, style, color, uri, baseSize, inLink, background, family, underline, strike, pre, ownColor);
    }

    // Parses ONE node as inline content, applying the node's OWN tag/style formatting before descending
    // into its children. Split out of ParseInlines so WalkBlocks can feed a formatted inline element that
    // has no block wrapper (a bare <span style=…>/<b> directly under body — the shape CF_HTML fragments
    // take for small copies) through the same path; handing such an element to ParseInlines directly
    // dropped its own formatting, since ParseInlines only reads formatting off the nodes it descends INTO.
    // `ownColor` is set once a `data-are-fg` span has been entered: the colour in scope is the document's
    // own, so the link-blue rule below must leave it alone. It is INHERITED rather than re-read per node,
    // because the marker sits on the span while the text it colours is a child of that span.
    private static void ParseInlineNode(HtmlNode child, Paragraph p, FontWeight weight = default, FontStyle style = FontStyle.Normal, Color? color = null, string? uri = null, double baseSize = 10, bool inLink = false, Color? background = null, string? family = null, bool underline = false, bool strike = false, bool pre = false, bool ownColor = false)
    {
        {
            var cw = weight;
            var cs = style;
            var cc = color;
            var cu = uri;
            double sz = baseSize;
            var cbg = background;
            var cfam = family;
            bool cunder = underline;
            bool cstrike = strike;

            string name = child.Name.ToLowerInvariant();

            if (name == "br") { p.Inlines.Add(new Run { Text = "\n" }); return; }
            if (name == "ul" || name == "ol") return;

            if (name == "b" || name == "strong") cw = FontWeightValues.Bold;
            if (name == "i" || name == "em") cs = FontStyle.Italic;
            if (name == "u") cunder = true;
            if (name == "s" || name == "strike" || name == "del") cstrike = true;
            if (name == "h1") { cw = FontWeightValues.Bold; sz = 20; } // pt, mirrors HeadingSize
            if (name == "h2") { cw = FontWeightValues.Bold; sz = 16; }
            if (name == "h3") { cw = FontWeightValues.Bold; sz = 14; }

            bool childInLink = inLink || name == "a";
            if (name == "a")
            {
                var href = child.GetAttributeValue("href", "");
                if (!string.IsNullOrEmpty(href)) cu = SafeHref(href);
            }

            bool childOwnColor = ownColor || child.GetAttributeValue("data-are-fg", "") == "1";

            ApplyInlineStyle(child.GetAttributeValue("style", ""), ref cw, ref cs, ref cc, ref sz, ref cbg, ref cfam, ref cunder, ref cstrike);

            // Links stay visually distinct (blue) regardless of the SITE'S own inline color — foreign
            // pages style anchors dark, or white for button text, and either disappears in this editor.
            // A colour this library wrote is not a site's styling, and overriding it lost the user's own
            // choice of link colour on every HTML save/load; `data-are-fg` marks that case.
            if (childInLink && !childOwnColor) cc = Colors.Blue;

            if (name == "#text")
            {
                string text = HtmlEntity.DeEntitize(child.InnerText);
                if (pre)
                {
                    // Preformatted: whitespace and newlines are content (code blocks); default to a
                    // monospace face when nothing more specific was inherited.
                    if (text.Length > 0)
                        p.Inlines.Add(new Run { Text = text.Replace("\r\n", "\n").Replace('\r', '\n'), FontWeight = NormalizeWeight(cw), FontStyle = cs, Foreground = cc, FontSize = sz, NavigateUri = cu, Background = cbg, FontFamily = cfam ?? "Consolas", TextDecorations = MakeDecorations(cunder, cstrike) });
                    return;
                }
                if (IsCollapsibleWhitespace(text))
                {
                    if (p.Inlines.Count > 0 && p.Inlines[^1] is Run last && last.Text != null &&
                        !last.Text.EndsWith(" ") && !last.Text.EndsWith("\n"))
                        p.Inlines.Add(new Run { Text = " " });
                    return;
                }
                p.Inlines.Add(new Run { Text = CollapseWhitespace(text), FontWeight = NormalizeWeight(cw), FontStyle = cs, Foreground = cc, FontSize = sz, NavigateUri = cu, Background = cbg, FontFamily = cfam, TextDecorations = MakeDecorations(cunder, cstrike) });
            }
            else if (name == "img")
            {
                var (bytes, w, h, alt) = LoadImage(child);
                if (bytes != null)
                {
                    var im = new InlineImage { Width = w, Height = h, AltText = alt };
                    im.SetImageData(bytes, ImageMime.Detect(bytes));
                    p.Inlines.Add(im);
                }
            }
            else
            {
                ParseInlines(child, p, cw, cs, cc, cu, sz, childInLink, cbg, cfam, cunder, cstrike, pre, childOwnColor);
            }
        }
    }

    private static TextDecorationFlags MakeDecorations(bool underline, bool strike)
    {
        var d = TextDecorationFlags.None;
        if (underline) d |= TextDecorationFlags.Underline;
        if (strike) d |= TextDecorationFlags.Strikethrough;
        return d;
    }

    private static void ApplyInlineStyle(string styleAttr, ref FontWeight weight, ref FontStyle style, ref Color? color, ref double size, ref Color? background, ref string? family, ref bool underline, ref bool strike)
    {
        if (string.IsNullOrEmpty(styleAttr)) return;
        string s = styleAttr.ToLowerInvariant();

        // Scope the test to the font-weight DECLARATION'S VALUE. Scanning the whole style string let an
        // unrelated declaration decide the weight: "font-weight:normal;width:600px" contains ":600" and
        // came out bold. A numeric compare also covers weights the old fixed list missed (e.g. 650).
        var wm = System.Text.RegularExpressions.Regex.Match(s, @"font-weight\s*:\s*([^;]+)");
        if (wm.Success)
        {
            string wv = wm.Groups[1].Value.Trim();
            bool numeric = int.TryParse(wv, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int wnum);
            if (wv.Contains("bold") || (numeric && wnum >= 600)) // bold / bolder
                weight = FontWeightValues.Bold;
            // Normal UN-bolds what an enclosing element made bold — CSS does, and this library's own export
            // relies on it for un-bolded heading text (<h1> is bold). It was ignored: Google Docs wraps a
            // paste in <b style="font-weight:normal">, and everything came in bold.
            else if (wv is "normal" or "lighter" || (numeric && wnum < 600))
                weight = FontWeightValues.Normal;
        }
        if (s.Contains("font-style:italic") || s.Contains("font-style: italic")) style = FontStyle.Italic;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "text-decoration[^;]*underline")) underline = true;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "text-decoration[^;]*line-through")) strike = true;

        var m = System.Text.RegularExpressions.Regex.Match(s, "(?<!background-)color\\s*:\\s*([^;]+)");
        if (m.Success)
        {
            var col = ParseCssColor(m.Groups[1].Value.Trim());
            if (col != null) color = col;
        }

        var bm = System.Text.RegularExpressions.Regex.Match(s, "background(?:-color)?\\s*:\\s*([^;]+)");
        if (bm.Success)
        {
            var col = ParseCssColor(bm.Groups[1].Value.Trim());
            if (col != null) background = col;
        }

        var famMatch = System.Text.RegularExpressions.Regex.Match(styleAttr, "font-family\\s*:\\s*([^;]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (famMatch.Success)
        {
            string fam = famMatch.Groups[1].Value.Split(',')[0].Trim().Trim('\'', '"').Trim();
            if (!string.IsNullOrEmpty(fam)) family = fam;
        }

        // Accept px and pt; the model stores pt, so pt passes through and px converts px->pt (횞0.75).
        var fm = System.Text.RegularExpressions.Regex.Match(s, "font-size\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)\\s*(px|pt)?");
        if (fm.Success && double.TryParse(fm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double val) && val > 0)
            size = fm.Groups[2].Value == "pt" ? val : val * 72.0 / 96.0;
    }

    // Reads CSS line-height into the paragraph: "150%" or a unitless multiplier ("1.5") map to the
    // proportional LineSpacing; "24px" maps to the absolute LineHeight. "normal"/absent = leave unset.
    // The inverse of ToHtml's line-height emission, closing the round-trip (line spacing used to be
    // dropped entirely on HTML export/import).
    private static void ApplyLineHeightStyle(HtmlNode node, Paragraph p)
    {
        var v = ReadStyleValue(node, "line-height")?.Trim();
        if (string.IsNullOrEmpty(v) || v.Equals("normal", StringComparison.OrdinalIgnoreCase)) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles fl = System.Globalization.NumberStyles.Float;
        if (v.EndsWith("%", StringComparison.Ordinal)
            && double.TryParse(v[..^1], fl, inv, out var pct) && pct > 0)
            p.LineSpacing = pct / 100.0;
        else if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(v[..^2], fl, inv, out var px) && px > 0)
            p.LineHeight = px;
        else if (double.TryParse(v, fl, inv, out var mult) && mult > 0)
            p.LineSpacing = mult; // unitless multiplier
    }

    private static double ReadIndentPx(HtmlNode node)
    {
        var style = node.GetAttributeValue("style", "").ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(style, "margin-left\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)\\s*(px|pt)?");
        if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(style, "padding-left\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)\\s*(px|pt)?");
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
            return m.Groups[2].Value == "pt" ? v * 96.0 / 72.0 : v;
        return 0;
    }

    // A cell's vertical alignment from the valign attribute or CSS vertical-align.
    private static CellVerticalAlignment ReadCellVAlign(HtmlNode td)
    {
        string v = td.GetAttributeValue("valign", "").ToLowerInvariant();
        if (v.Length == 0) v = ReadStyleValue(td, "vertical-align")?.ToLowerInvariant() ?? "";
        return v switch
        {
            "middle" or "center" => CellVerticalAlignment.Center,
            "bottom" => CellVerticalAlignment.Bottom,
            _ => CellVerticalAlignment.Top,
        };
    }

    // Background color from a node's style="background[-color]:..." or legacy bgcolor="..." attr.
    private static Color? ReadBackground(HtmlNode node)
    {
        var style = node.GetAttributeValue("style", "");
        if (!string.IsNullOrEmpty(style))
        {
            var m = System.Text.RegularExpressions.Regex.Match(style.ToLowerInvariant(), "background(?:-color)?\\s*:\\s*([^;]+)");
            if (m.Success)
            {
                var b = ParseCssColor(m.Groups[1].Value.Trim());
                if (b != null) return b;
            }
        }
        var bg = node.GetAttributeValue("bgcolor", "");
        return string.IsNullOrEmpty(bg) ? null : ParseCssColor(bg.Trim());
    }

    // CSS color value -> Color (hex / rgb() / named), or null. Delegates to the shared parser.
    private static Color? ParseCssColor(string value) => ColorUtil.Parse(value);

    private static string CssListStyle(ListKind kind, ListMarkerStyle marker) => marker switch
    {
        ListMarkerStyle.Circle => "circle",
        ListMarkerStyle.Square => "square",
        ListMarkerStyle.LowerAlpha => "lower-alpha",
        ListMarkerStyle.UpperAlpha => "upper-alpha",
        ListMarkerStyle.LowerRoman => "lower-roman",
        _ => kind == ListKind.Ordered ? "decimal" : "disc",
    };

    private static ListMarkerStyle ListMarkerFromCss(string? cssValue) => (cssValue ?? "").Trim().ToLowerInvariant() switch
    {
        "circle" => ListMarkerStyle.Circle,
        "square" => ListMarkerStyle.Square,
        "lower-alpha" or "lower-latin" => ListMarkerStyle.LowerAlpha,
        "upper-alpha" or "upper-latin" => ListMarkerStyle.UpperAlpha,
        "lower-roman" => ListMarkerStyle.LowerRoman,
        _ => ListMarkerStyle.Default,
    };

    /// <summary>Serializes <paramref name="doc"/> to an HTML string.</summary>
    public static string ToHtml(FlowDocument doc)
    {
        var sb = new StringBuilder(EstimateCapacity(doc));
        AppendHtml(sb, doc);
        return sb.ToString();
    }

    // Pictures are written as base64 data URIs, so a document's HTML is about 4/3 of its image bytes in chars.
    // A builder sized for that up front is ONE allocation of the payload; left to grow, it re-allocated on the
    // way up, and every picture's tag used to be built as its own strings first (the base64, then the <img>,
    // then the <p> around it). Measured 2026-09-16 (CopyAllocationProbeTests): copying one 10 MB picture
    // allocated 166 MB building its HTML.
    internal static int EstimateCapacity(FlowDocument doc)
    {
        long chars = 4096;
        foreach (var bytes in BlockWalk.PictureBytes(doc.Blocks)) chars += (bytes.Length + 2) / 3 * 4 + 256;
        return (int)Math.Min(chars, int.MaxValue / 2);
    }

    /// <summary>Appends the HTML for <paramref name="doc"/> to <paramref name="sb"/> — for a caller wrapping it
    /// (the clipboard's selection &lt;div&gt;), which would otherwise copy the whole payload once more.</summary>
    internal static void AppendHtml(StringBuilder sb, FlowDocument doc)
    {
        var lists = new ListNesting(sb);

        foreach (var block in doc.Blocks)
        {
            if (block is Paragraph p)
            {
                if (p.IsListItem) lists.Sync(p.ListType, p.ListMarker, p.ListLevel);
                else lists.CloseAll();
                EmitParagraphElement(sb, p, newlineAfter: true);
            }
            else if (block is DividerBlock)
            {
                lists.CloseAll();
                sb.Append("<hr/>\n");
            }
            else if (block is TableBlock tb)
            {
                lists.CloseAll();
                EmitTable(sb, tb);
            }
            else if (block is ImageBlock ib && (ib.RawBytes != null || ib.Image != null))
            {
                lists.CloseAll();
                sb.Append("<p>");
                AppendImgTag(sb, ib.RawBytes, ib.MimeType, ib.RawBytes == null ? ib.Image : null, ib.Width, ib.Height, ib.AltText);
                sb.Append("</p>\n");
            }
        }
        lists.CloseAll();
    }

    // Opens and closes the <ul>/<ol> nesting around a run of list-item paragraphs. One instance per block
    // list — the document's top level has its own, and so does each <td>, because a list inside a cell has
    // to open and close inside that cell.
    private sealed class ListNesting
    {
        private readonly StringBuilder _sb;
        private readonly List<ListKind> _open = new();
        // Inside a <td> the pretty-printing newlines are not decoration: the cell's content is parsed as
        // inline, so each one becomes a whitespace text node and comes back as content.
        private readonly string _nl;

        public ListNesting(StringBuilder sb, bool tight = false) { _sb = sb; _nl = tight ? "" : "\n"; }

        private void CloseOne()
        {
            _sb.Append(_open[^1] == ListKind.Ordered ? "</ol>" : "</ul>").Append(_nl);
            _open.RemoveAt(_open.Count - 1);
        }

        public void CloseAll() { while (_open.Count > 0) CloseOne(); }

        public void Sync(ListKind kind, ListMarkerStyle marker, int level)
        {
            while (_open.Count > level + 1) CloseOne();
            if (_open.Count == level + 1 && _open[^1] != kind) CloseOne();
            while (_open.Count < level + 1)
            {
                string lst = CssListStyle(kind, marker);
                _sb.Append(kind == ListKind.Ordered ? $"<ol style=\"list-style-type:{lst}\">" : $"<ul style=\"list-style-type:{lst}\">").Append(_nl);
                _open.Add(kind);
            }
        }
    }

    // One paragraph as its own HTML element, carrying the paragraph-level formatting the reader knows how
    // to read back. Shared by the document's top level and by table cells, which is the point: a cell
    // paragraph used to go out as bare inlines, and bare inlines can express NOTHING paragraph-level, so
    // a bulleted / centred / indented / shaded / heading cell paragraph lost all of it on export. The
    // reader has always handled the element form inside a <td> (foreign Word tables with bulleted cells
    // parse correctly) — only the writer could not produce it.
    //
    // newlineAfter is false inside a <td>, where a pretty-printing newline is not decoration: the cell's
    // content is parsed as inline, so it becomes a whitespace text node and comes back as content.
    private static void EmitParagraphElement(StringBuilder sb, Paragraph p, bool newlineAfter)
    {
        string tag = p.IsListItem ? "li"
            : p.IsQuote ? "blockquote"
            : (p.HeadingLevel >= 1 && p.HeadingLevel <= 6 ? $"h{p.HeadingLevel}" : "p");
        string align = p.TextAlignment switch { TextAlignment.Center => "center", TextAlignment.Right => "right", TextAlignment.Justify => "justify", _ => "left" };
        string pStyle = $"text-align:{align};";
        if (p.Background is { } pbg) pStyle += $"background-color:{ColorUtil.ToCss(pbg)};";
        if (p.Indent > 0) pStyle += $"margin-left:{p.Indent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}px;";
        // Line spacing round-trips as CSS line-height (% for proportional, px for absolute).
        if (!double.IsNaN(p.LineSpacing) && p.LineSpacing > 0)
            pStyle += $"line-height:{(p.LineSpacing * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%;";
        else if (!double.IsNaN(p.LineHeight) && p.LineHeight > 0)
            pStyle += $"line-height:{p.LineHeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}px;";
        // A paragraph can be a list item AND a heading, but the tag can only be one of <li>/<h1..6>,
        // and <li> wins because the list structure is what HTML cannot otherwise express. The
        // heading level would then be dropped outright, so it rides along as a marker.
        string extraAttr = p.IsListItem && p.HeadingLevel >= 1 && p.HeadingLevel <= 6
            ? $" data-are-h=\"{p.HeadingLevel}\"" : "";
        // Paragraph spacing (the context menu's margin submenu sets all three) went out as nothing at all
        // and came back as the defaults. It goes out TWICE on purpose: as real CSS so a browser or Word
        // shows the spacing, and as a marker because only the marker is read back. Reading foreign
        // margin-top/bottom would give every web paste that page's vertical rhythm — the same reason
        // data-are-empty exists. margin-left is not here: it is Indent, and reading it from foreign HTML
        // is long-standing behaviour.
        string Px(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (p.MarginTop != 0) pStyle += $"margin-top:{Px(p.MarginTop)}px;";
        if (p.MarginBottom != DefaultMarginBottom) pStyle += $"margin-bottom:{Px(p.MarginBottom)}px;";
        if (p.MarginRight != 0) pStyle += $"margin-right:{Px(p.MarginRight)}px;";
        if (p.MarginTop != 0 || p.MarginBottom != DefaultMarginBottom || p.MarginRight != 0)
            extraAttr += $" data-are-m=\"{Px(p.MarginTop)},{Px(p.MarginBottom)},{Px(p.MarginRight)}\"";
        // An empty paragraph is a blank LINE the author put there. The importer drops elements
        // that produce no inline, and it has to: foreign HTML is full of empty <p>/<div> used for
        // spacing, and keeping those adds a blank line to every web paste. The marker separates
        // "this document's blank line" from "that page's layout scaffolding".
        if (p.Inlines.Count == 0) extraAttr += " data-are-empty=\"1\"";
        sb.Append($"<{tag}{extraAttr} style=\"{pStyle}\">");
        // `first` tells an inline table it OPENS this paragraph: an HTML parser closes the <p>
        // when the <table> starts, so on import there is no pending paragraph and the marker's
        // reattachment would otherwise grab whatever paragraph precedes it.
        for (int i = 0; i < p.Inlines.Count; i++)
            EmitInline(sb, p.Inlines[i], i == 0, i == p.Inlines.Count - 1, tag[0] == 'h' ? p.HeadingLevel : 0);
        // The marker tells only THIS reader about the blank line. An element with no content has zero height, so
        // in a browser the author's blank line (or blank list item) was invisible — upstream measured the gap
        // across it as the same 16px as between any two paragraphs (round 9; ported 2026-09-24). The <br> gives it
        // a line everywhere else — it is what contenteditable editors emit — and the reader drops it when the
        // marker is present, so one blank line does not become two.
        if (p.Inlines.Count == 0) sb.Append("<br/>");
        sb.Append($"</{tag}>");
        if (newlineAfter) sb.Append('\n');
    }

    // Whether a cell paragraph has to go out as its own element. The bare-inline form is kept for plain
    // cell text — it is what makes a one-line cell come back as one line, and its whitespace rules were
    // hard-won — so only a paragraph carrying something the bare form cannot express is promoted.
    private static bool NeedsOwnElement(Paragraph p)
        => p.IsListItem || p.IsQuote
           || (p.HeadingLevel >= 1 && p.HeadingLevel <= 6)
           || p.TextAlignment != TextAlignment.Left
           || p.Background != null
           || p.Indent > 0
           || (!double.IsNaN(p.LineSpacing) && p.LineSpacing > 0)
           || (!double.IsNaN(p.LineHeight) && p.LineHeight > 0)
           || p.MarginTop != 0 || p.MarginBottom != DefaultMarginBottom || p.MarginRight != 0;

    private static double SumColumnWidths(TableBlock tb)
    {
        double w = 0;
        for (int c = 0; c < tb.Columns; c++) w += c < tb.ColumnWidths.Count ? tb.ColumnWidths[c] : 100;
        return w;
    }

    // Emits a table as an HTML <table>. Shared by block tables and inline tables.
    // asInline: an InlineTable (marked + sized to its own columns so it sits in a text line).
    // tight: suppress the pretty-printing newline after </table> because the table is emitted somewhere
    // whose content parses as inline (a text line, or a <td>), where that whitespace becomes content.
    private static void EmitTable(StringBuilder sb, TableBlock tb, bool asInline = false, bool opensParagraph = false, bool tight = false)
    {
        // `data-are-inline` is ours: HTML has no inline table, so an InlineTable came back from our own
        // export as a BLOCK table, permanently splitting the paragraph it lived in. External HTML never
        // carries the attribute and keeps landing as a block table, as before.
        string mark = asInline ? " data-are-inline=\"1\"" + (opensParagraph ? " data-are-opens=\"1\"" : "") : "";
        // A block table fills the text column; an inline table is a character-sized object, so stretching
        // it to 100% turned it into a full-width band on its own line in every consumer but our own
        // importer. Size it to its own columns and let it sit in the line instead.
        string sizing = asInline
            ? $"width:{(int)Math.Max(1, SumColumnWidths(tb))}px; display:inline-table; vertical-align:middle;"
            : "width:100%;";
        sb.Append($"<table{mark} border=\"1\" style=\"border-collapse:collapse; {sizing}\">\n");
        if (tb.ColumnWidths.Count > 0)
        {
            sb.Append("<colgroup>");
            for (int c = 0; c < tb.Columns; c++)
            {
                double cw = c < tb.ColumnWidths.Count ? tb.ColumnWidths[c] : 100;
                sb.Append($"<col style=\"width:{(int)System.Math.Max(1, cw)}px\"/>");
            }
            sb.Append("</colgroup>\n");
        }
        for (int r = 0; r < tb.Rows; r++)
        {
            sb.Append("<tr>\n");
            for (int c = 0; c < tb.Columns; c++)
            {
                if (tb.IsCovered(r, c)) continue;
                var cell = tb.Cells[r][c];
                var (cs, rs) = tb.SpanOf(r, c);
                var span = (cs > 1 ? $" colspan=\"{cs}\"" : "") + (rs > 1 ? $" rowspan=\"{rs}\"" : "");
                if (cell.VerticalAlignment != CellVerticalAlignment.Top)
                    span += $" valign=\"{(cell.VerticalAlignment == CellVerticalAlignment.Center ? "middle" : "bottom")}\"";
                if (cell.Background is { } cbg)
                    sb.Append($"<td{span} style=\"background-color:{ColorUtil.ToCss(cbg)}\">");
                else
                    sb.Append($"<td{span}>");
                // A cell can hold list items, so it needs its own nesting — opened and closed inside
                // this <td>, never spanning cells.
                var cellLists = new ListNesting(sb, tight: true);
                // A cell's paragraphs used to go out as bare inlines joined by <br>. Two things were wrong
                // with that, and both were invisible to the round-trip fuzz because both are IDEMPOTENT —
                // once collapsed, they stay collapsed:
                //   * <br> is not a paragraph boundary to the reader. It comes back as a newline INSIDE one
                //     paragraph, so a two-paragraph cell became one paragraph with a soft break.
                //   * bare inlines carry nothing paragraph-level, so a bulleted / centred / indented /
                //     shaded / heading cell paragraph lost all of it.
                // So a paragraph goes out as an ELEMENT whenever the bare form cannot represent it — more
                // than one paragraph in the cell, or formatting on this one. The reader has always split
                // and read those correctly (foreign Word tables with bulleted cells prove it). A lone plain
                // paragraph keeps the bare form, so the common cell's bytes do not change and the
                // whitespace rules earned there still stand.
                // An <img> is INLINE, so a bare paragraph followed by a block image in the same cell shared the
                // image's line — the picture sat beside the caption instead of under it. Counting paragraphs
                // missed it: the cell has only ONE. <hr> and <table> are block-level and break the line by
                // themselves, so only an image forces the issue (upstream round 9; ported 2026-09-24).
                bool needsElementForm = cell.Blocks.Count(b => b is Paragraph) > 1 || cell.Blocks.Any(b => b is ImageBlock);
                foreach (var cblk in cell.Blocks)
                {
                    if (cblk is Paragraph cpara && (needsElementForm || NeedsOwnElement(cpara)))
                    {
                        if (cpara.IsListItem) cellLists.Sync(cpara.ListType, cpara.ListMarker, cpara.ListLevel);
                        else cellLists.CloseAll();
                        EmitParagraphElement(sb, cpara, newlineAfter: false);
                    }
                    else if (cblk is Paragraph plain)
                    {
                        cellLists.CloseAll();
                        // Same boundary rule as a top-level paragraph: a <td>'s content is parsed as
                        // inline, so a space at either end of it is dropped unless it goes out non-breaking.
                        for (int i = 0; i < plain.Inlines.Count; i++)
                            EmitInline(sb, plain.Inlines[i], i == 0, i == plain.Inlines.Count - 1);
                    }
                    else if (cblk is ImageBlock cib && (cib.RawBytes != null || cib.Image != null))
                    { cellLists.CloseAll(); AppendImgTag(sb, cib.RawBytes, cib.MimeType, cib.RawBytes == null ? cib.Image : null, cib.Width, cib.Height, cib.AltText); }
                    else if (cblk is TableBlock nt)
                        // tight: a <td>'s content is parsed as inline, so the pretty-printing newline
                        // after a nested </table> lands INSIDE the cell as a whitespace text node and
                        // comes back as content — one more newline per save/load cycle.
                    { cellLists.CloseAll(); EmitTable(sb, nt, tight: true); }
                    else if (cblk is DividerBlock)
                    { cellLists.CloseAll(); sb.Append("<hr/>"); }
                }
                cellLists.CloseAll();
                sb.Append("</td>\n");
            }
            sb.Append("</tr>\n");
        }
        // An inline table sits INSIDE a text line, so the pretty-printing newline after </table> becomes a
        // whitespace text node between the table and the text that follows it — which the parser normalizes
        // to a space, inserting one more after every inline table on each save/load cycle.
        //
        // `tight` says the same thing about a NESTED table, and this method never read it: the call site
        // has always passed it, with a comment explaining why, and nothing consulted it.
        //
        // Honest about what this line is: NO failing case demonstrates it. The reader flushes at a block
        // table, so `current` is null when the stray newline arrives and the newline is ignored — the one
        // fuzz seed that did gain a space there is fixed by the cell-paragraph change above, and reverting
        // this line alone leaves every test and 20000 fuzz seeds green. It is honoured anyway because a
        // documented parameter that nothing consults is worse than no parameter: the comment made the case
        // look handled, and the next writer change that puts bare text after a nested cell table would
        // pay for that.
        sb.Append(asInline || tight ? "</table>" : "</table>\n");
    }

    // `opensParagraph`/`closesParagraph` mark the first and last inline of their paragraph. The first
    // drives the inline-table marker; the last gates the trailing-space encoding below, because HTML
    // drops whitespace at the end of a block and a space there would not come back.
    private static void EmitInline(StringBuilder sb, Inline inline, bool opensParagraph = false, bool closesParagraph = false, int heading = 0)
    {
        if (inline is InlineImage im && (im.RawBytes != null || im.Image != null))
        {
            AppendImgTag(sb, im.RawBytes, im.MimeType, im.RawBytes == null ? im.Image : null, im.Width, im.Height, im.AltText, opensParagraph);
            return;
        }
        if (inline is InlineTable itbl)
        {
            EmitTable(sb, itbl.Table, asInline: true, opensParagraph); // marked + inline-sized so it round-trips inline
            return;
        }
        if (inline is not Run r || r.Text == null) return;

        string t = HtmlEntity.Entitize(r.Text);
        t = PreserveRunsOfSpaces(t);
        t = PreserveDroppableSpaces(t, closesParagraph);
        // Soft line breaks (Shift+Enter, stored as '\n' in run text) must survive as <br/> — a literal
        // newline collapses to a space in HTML, silently losing the break on export/copy. The parser's
        // inverse (ParseInlines) already turns <br> back into a "\n" run, so this closes the round-trip.
        t = t.Replace("\n", "<br/>");

        var styles = new List<string>();
        if (!string.IsNullOrEmpty(r.FontFamily)) styles.Add($"font-family:'{AttrEscape(r.FontFamily).Replace("'", "")}'");
        // A size goes out when it differs from what the reader assumes for this element: the body default,
        // or inside <h1>..<h6> the heading's size, which the reader gives everything in it. 10pt in a
        // heading used to be dropped as "the default" and came back at the heading's size.
        bool inHeading = HeadingStyle.IsHeading(heading);
        double assumed = inHeading ? HeadingStyle.Size(heading) : 10;
        if (r.FontSize > 0 && System.Math.Abs(r.FontSize - assumed) > 0.01)
            styles.Add($"font-size:{r.FontSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}pt");
        // <h1>..<h6> is bold to every reader (browsers, Word, this one), so un-bolded heading text says so.
        if (inHeading && !r.FontWeight.IsBold()) styles.Add("font-weight:normal");
        if (r.Foreground is { } fg) styles.Add($"color:{ColorUtil.ToCss(fg)}");
        if (r.Background is { } bg) styles.Add($"background-color:{ColorUtil.ToCss(bg)}");

        // `data-are-fg` says the colour on this span is the DOCUMENT'S, not a site's styling. The reader
        // paints links blue on top of whatever colour the source declared (a deliberate rule: foreign
        // pages give anchors dark or white button text that would vanish here), and that rule used to
        // eat the user's own choice of link colour on every save/load. The marker is what tells the two
        // apart — same idiom as data-are-inline for inline tables. Emitted only where it can matter.
        bool markOwnColor = r.Foreground is not null && !string.IsNullOrEmpty(r.NavigateUri);
        if (styles.Count > 0)
            t = $"<span{(markOwnColor ? " data-are-fg=\"1\"" : "")} style=\"{string.Join(";", styles)}\">{t}</span>";
        // Underline/strikethrough as <u>/<s> TAGS, not CSS text-decoration: clipboard importers
        // (Word/HWP) reliably honour the tags but routinely drop CSS text-decoration.
        if (r.TextDecorations.HasFlag(TextDecorationFlags.Underline)) t = $"<u>{t}</u>";
        if (r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough)) t = $"<s>{t}</s>";
        if (r.FontWeight.IsBold()) t = $"<b>{t}</b>";
        if (r.FontStyle == FontStyle.Italic) t = $"<i>{t}</i>";
        // The readers drop script links, but a host's SetHyperlink reaches here without passing one — the same
        // check, so no script link leaves in exported or clipboard HTML whatever put it in the document.
        if (!string.IsNullOrEmpty(r.NavigateUri) && SafeHref(r.NavigateUri) is { } href)
            t = $"<a href=\"{AttrEscape(href)}\">{t}</a>";
        sb.Append(t);
    }

    // HTML collapses a run of whitespace to ONE space, so `a  b` came back as `a b` — the editor's own
    // double space, gone on the first save/load. Encode every space that FOLLOWS a space as &nbsp;,
    // which is what Word emits and what every browser renders identically.
    //
    // Why alternate instead of making them all non-breaking: a solid run of &nbsp; is unbreakable, so a
    // line padded with spaces would refuse to wrap and push the layout wide. Keeping the first space of
    // each run collapsible leaves a legal wrap point exactly where one belongs.
    //
    // Only runs of two or more are touched, so ordinary prose exports byte-for-byte as before.
    private static string PreserveRunsOfSpaces(string s)
    {
        if (s.Length < 2 || !s.Contains("  ", StringComparison.Ordinal)) return s;
        var sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == ' ' && i > 0 && s[i - 1] == ' ') sb.Append("&nbsp;");
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // Spaces that land where HTML throws whitespace away, made non-breaking so they come back. Two
    // positions, and both were found by the fuzz rather than by reading:
    //
    // · Before a soft break. `t.Replace("\n", "<br/>")` splits the run's text, so `" \n셀"` leaves a
    //   whitespace-ONLY text node in front of the <br/>; when that node opens the paragraph there is no
    //   previous inline to hang a separator on and the space is simply gone (seeds 6239, 14651).
    // · At the very end of a block, which HTML drops outright. A paragraph ending in a plain `" "` run
    //   — what MergeCells leaves when it joins a covered cell — lost it on every other round trip.
    //
    // · A run of NOTHING BUT SPACES, wherever it sits. It goes out as a whitespace-only text node, and
    //   the reader cannot tell that from a pretty-printer's indentation, so its separator logic decides
    //   the fate of authored content: at the start of a paragraph there is no previous inline to attach
    //   to (seed 14651), and after a run that already ends in a space the de-duplication guard drops it
    //   (seed 11639). A run made only of spaces is authored by construction — it exists as its own run —
    //   so it is written as content and the question never arises. MergeCells' join separator is exactly
    //   this shape, which is why merged-cell text kept losing word boundaries in the first place.
    //   Cost, accepted: that one space is non-breaking, so a line cannot wrap at it.
    //
    // Deliberately NOT every boundary space, for two separate reasons.
    // · Scope: making every run-boundary space non-breaking would weld words together and stop the line
    //   wrapping between them, which is the one thing &nbsp; must not be used for.
    // · Measured: encoding the leading spaces of any opening run was tried and reverted — it turned one
    //   leading space into two on the next cycle in 71 of 3000 seeds. A leading space with content
    //   behind it in the same run already survives, so encoding it only added one; hence "the whole run
    //   is spaces" below rather than "the run starts with a space".
    private static string PreserveDroppableSpaces(string s, bool atEnd)
    {
        if (s.Length == 0) return s;
        if (s.AsSpan().TrimStart(' ').Length == 0)
            return string.Concat(Enumerable.Repeat("&nbsp;", s.Length));
        if (s.Contains(" \n", StringComparison.Ordinal))
        {
            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == ' ' && i + 1 < s.Length && s[i + 1] == '\n') sb.Append("&nbsp;");
                else sb.Append(s[i]);
            }
            s = sb.ToString();
        }
        if (!atEnd || s.Length == 0 || s[^1] != ' ') return s;
        int j = s.Length;
        while (j > 0 && s[j - 1] == ' ') j--;
        return s[..j] + string.Concat(Enumerable.Repeat("&nbsp;", s.Length - j));
    }

    private static string AttrEscape(string s) =>
        s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Emits a data: URI <img>. RawBytes (with their MIME type) are used verbatim when present;
    // a bitmap set without bytes is PNG-encoded. `alt` round-trips the accessibility description.
    // `opensParagraph` carries the same meaning as it does for an inline table: this image was the FIRST
    // thing in its paragraph, so on import there is no earlier paragraph of its own to rejoin.
    private static void AppendImgTag(StringBuilder sb, byte[]? raw, string? mime, Microsoft.Graphics.Canvas.CanvasBitmap? bmp, double w, double h, string? alt = null, bool opensParagraph = false)
    {
        byte[] data;
        string m;
        if (raw != null)
        {
            data = raw;
            m = mime ?? "image/png";
        }
        else if (bmp != null)
        {
            var encoded = ImageEncoder.ToPngBytes(bmp);
            if (encoded == null) return;
            data = encoded;
            m = "image/png";
        }
        else return;
        sb.Append("<img src=\"data:").Append(m).Append(";base64,");
        AppendBase64(sb, data);
        sb.Append('"');
        if (!double.IsNaN(w) && w > 0) sb.Append(" width=\"").Append((int)w).Append('"');
        if (!double.IsNaN(h) && h > 0) sb.Append(" height=\"").Append((int)h).Append('"');
        if (!string.IsNullOrEmpty(alt)) sb.Append(" alt=\"").Append(AttrEscape(alt)).Append('"');
        if (opensParagraph) sb.Append(" data-are-opens=\"1\"");
        sb.Append("/>");
    }

    // Base64 straight into the builder, a slice at a time: no string of the whole payload is ever made.
    // Slices are whole multiples of 3 bytes, so no padding appears between them.
    internal static void AppendBase64(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        const int SliceBytes = 3 * 16 * 1024;
        char[] chars = System.Buffers.ArrayPool<char>.Shared.Rent(SliceBytes / 3 * 4);
        try
        {
            while (!data.IsEmpty)
            {
                var slice = data[..Math.Min(SliceBytes, data.Length)];
                Convert.TryToBase64Chars(slice, chars, out int written);
                sb.Append(chars, 0, written);
                data = data[slice.Length..];
            }
        }
        finally { System.Buffers.ArrayPool<char>.Shared.Return(chars); }
    }
}
