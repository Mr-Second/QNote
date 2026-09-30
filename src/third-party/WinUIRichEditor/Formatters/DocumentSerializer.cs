using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.UI.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Formatters;

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FlowDocumentDto))]
internal partial class DocumentJsonContext : JsonSerializerContext { }

/// <summary>Serializes and deserializes a <see cref="FlowDocument"/> to/from the library's JSON format.
/// Images are stored as base64 of their original encoded bytes (no re-encoding); bitmaps
/// set without raw bytes fall back to base64 PNG. Uses AOT-safe source-generated JSON.</summary>
public static class DocumentSerializer
{
    /// <summary>Current document-format version written by <see cref="Serialize"/> (a SemVer string).
    /// The reader does not branch on it — it is an informational stamp — and accepts both the new string
    /// form and the legacy integer form.</summary>
    public const string CurrentSchemaVersion = "1.0";

    /// <summary>Serializes <paramref name="document"/> to a JSON string.</summary>
    public static string Serialize(FlowDocument document)
    {
        var (dto, images) = BuildDto(document);
        return SerializeDto(dto, images);
    }

    // Builds the DTO + image pool from the model.
    internal static (FlowDocumentDto Dto, Dictionary<string, (byte[] Bytes, string Mime)> Images) BuildDto(FlowDocument document)
    {
        var images = new Dictionary<string, (byte[] Bytes, string Mime)>();
        var dto = ToDto(document, images);
        return (dto, images);
    }

    // Pure-data half of Serialize: base64-encodes the image pool into the DTO and writes JSON.
    internal static string SerializeDto(FlowDocumentDto dto, Dictionary<string, (byte[] Bytes, string Mime)> images)
    {
        if (images.Count > 0)
        {
            dto.Images = new Dictionary<string, ImagePoolDto>();
            foreach (var (key, img) in images)
                dto.Images[key] = new ImagePoolDto { Data = Convert.ToBase64String(img.Bytes), MimeType = img.Mime };
        }
        return JsonSerializer.Serialize(dto, DocumentJsonContext.Default.FlowDocumentDto);
    }

    // Builds the wire DTO; image bytes land in `images` keyed by content hash (the dto references
    // them via ImageRef). The caller picks the byte representation: base64 in the JSON pool
    // (Serialize) or zip entries (DocumentPackage).
    internal static FlowDocumentDto ToDto(FlowDocument document, Dictionary<string, (byte[] Bytes, string Mime)> images)
    {
        var blocks = new List<BlockDto>();
        foreach (var block in document.Blocks) blocks.Add(BlockToDto(block, images));
        // The marker only for a document that really carries its heading formats: a host's own model that no
        // editor has converted yet must stay "legacy" in the file, or it would load back with plain headings.
        var dto = new FlowDocumentDto { Version = CurrentSchemaVersion, Blocks = blocks, HeadingFormat = document.HeadingFormatsApplied ? 1 : null };
        // A document with no page setup is written without one, so plain documents keep their original format.
        // One that CARRIES a setup is written even when it looks default: an editor whose host defaults to A4
        // keeps a chosen Continuous on the document, and dropping it here reopened the file as A4 (measured
        // 2026-09-14) — "no setup" means "the host's", which is not "Continuous". The editor itself leaves the
        // setup off only when both the document's and the host's are plain (CapturePageSetupToDocument).
        if (document.PageSetup is { } ps)
            dto.PageSetup = new PageSetupDto
            {
                PageSize = ps.PageSize.ToString(),
                Orientation = ps.Orientation.ToString(),
                ShowPageBoundaries = ps.ShowPageBoundaries,
                Header = string.IsNullOrEmpty(ps.Header) ? null : ps.Header,
                Footer = string.IsNullOrEmpty(ps.Footer) ? null : ps.Footer,
                ShowPageNumbers = ps.ShowPageNumbers,
                MarginLeft = ps.Margin.Left == PageSetup.DefaultMargin.Left ? null : ps.Margin.Left,
                MarginTop = ps.Margin.Top == PageSetup.DefaultMargin.Top ? null : ps.Margin.Top,
                MarginRight = ps.Margin.Right == PageSetup.DefaultMargin.Right ? null : ps.Margin.Right,
                MarginBottom = ps.Margin.Bottom == PageSetup.DefaultMargin.Bottom ? null : ps.Margin.Bottom,
            };
        return dto;
    }

    /// <summary>Deserializes a <see cref="FlowDocument"/> from a JSON string produced by <see cref="Serialize"/>.
    /// Image decoding is deferred to first render.</summary>
    /// <exception cref="JsonException"><paramref name="json"/> is not valid JSON. Malformed input is
    /// reported rather than silently read as an empty document, so a host cannot mistake a damaged file
    /// for an empty one and overwrite it. A literal <c>null</c> is valid JSON and does yield an empty
    /// document.</exception>
    public static FlowDocument Deserialize(string json)
    {
        var (dto, pool) = ParseJson(json);
        return FromDto(dto, pool);
    }

    // The editor's load paths (LoadJson/LoadJsonAsync): Deserialize plus the check that the JSON is a
    // document of this library at all. Any JSON OBJECT used to load — unknown properties are ignored — so
    // opening some other application's .json replaced the open document with an empty one, marked it saved,
    // and the next save wrote the blank over the original (decided 2026-09-12). The public Deserialize keeps
    // its lenient behaviour.
    internal static FlowDocument DeserializeDocument(string json)
    {
        var (dto, pool) = ParseJson(json);
        EnsureDocumentShape(dto);
        return FromDto(dto, pool);
    }

    // Every document this library writes has "Blocks" (ToDto always sets it, even when empty); an object
    // without it is something else. A literal null stays an empty document, as Deserialize documents.
    internal static void EnsureDocumentShape(FlowDocumentDto? dto)
    {
        if (dto != null && dto.Blocks == null)
            throw new JsonException("The JSON is not a WinUIRichEditor document: it has no \"Blocks\".");
    }

    // Thread-free half of Deserialize: JSON parsing + base64 decode only, no model objects.
    internal static (FlowDocumentDto? Dto, Dictionary<string, (byte[] Bytes, string Mime)> Pool) ParseJson(string json)
    {
        // Malformed input is NOT swallowed. Reading a damaged file as an empty document is the worst
        // outcome available: the host cannot tell "this file was empty" from "this file is damaged",
        // shows a blank editor, and a save then overwrites a recoverable file with nothing.
        var dto = JsonSerializer.Deserialize(json, DocumentJsonContext.Default.FlowDocumentDto);
        var pool = new Dictionary<string, (byte[] Bytes, string Mime)>();
        if (dto?.Images != null)
            foreach (var (key, entry) in dto.Images)
            {
                if (entry == null) continue; // `"Images":{"k":null}` threw NullReferenceException out of the load
                var bytes = TryFromBase64(entry.Data);
                if (bytes != null) pool[key] = (bytes, entry.MimeType ?? "image/png");
            }
        return (dto, pool);
    }

    // Rebuilds the document from a DTO plus the resolved image pool.
    internal static FlowDocument FromDto(FlowDocumentDto? dto, Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        var doc = new FlowDocument { HeadingFormatsApplied = dto?.HeadingFormat >= 1 };
        if (dto?.Blocks != null)
            foreach (var bd in dto.Blocks)
            {
                var b = DtoToBlock(bd, pool);
                if (b != null) doc.Blocks.Add(b);
            }
        if (dto?.PageSetup is { } psd)
            doc.PageSetup = new PageSetup
            {
                PageSize = Enum.TryParse<RichEditorPageSize>(psd.PageSize, out var sz) ? sz : RichEditorPageSize.Continuous,
                Orientation = Enum.TryParse<RichEditorPageOrientation>(psd.Orientation, out var or) ? or : RichEditorPageOrientation.Portrait,
                ShowPageBoundaries = psd.ShowPageBoundaries,
                Header = psd.Header,
                Footer = psd.Footer,
                ShowPageNumbers = psd.ShowPageNumbers,
                Margin = ReadMargin(psd),
            };
        // A file without the HeadingFormat marker (older, or upstream's) is read as it is — the marker's
        // absence stays on the document, and the EDITOR converts it when it receives it (OnDocumentAssigned).
        // Converting here broke the native formats' contract, a document read back exactly as it was saved
        // (DocumentFuzzTests caught it on the first run).
        return RunNormalizer.Compact(doc);
    }

    // ---- model -> dto ----

    private static BlockDto BlockToDto(Block block, Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        switch (block)
        {
            case Paragraph p:
                return ParagraphToDto(p, pool);
            case DividerBlock dv:
                return new BlockDto
                {
                    Type = "Divider",
                    MarginTop = double.IsNaN(dv.MarginTop) ? null : dv.MarginTop, // see the table branch
                    MarginBottom = dv.MarginBottom,
                };
            case ImageBlock img:
                return new BlockDto
                {
                    Type = "Image",
                    ImageRef = PoolImage(pool, img.RawBytes, img.MimeType, img.RawBytes == null ? img.Image : null),
                    Width = NanToNull(img.Width),
                    Height = NanToNull(img.Height),
                    Alt = img.AltText,
                    Indent = img.Indent,
                    MarginTop = double.IsNaN(img.MarginTop) ? null : img.MarginTop, // see the table branch
                    MarginBottom = img.MarginBottom
                };
            case TableBlock tb:
                var td = new BlockDto
                {
                    Type = "Table",
                    Rows = tb.Rows,
                    Columns = tb.Columns,
                    Indent = tb.Indent,
                    // NaN is "let the editor choose the gap" (Block.AutoTopMargin) and JSON has no NaN: it goes
                    // out as no field at all, and comes back as NaN on the next read.
                    MarginTop = double.IsNaN(tb.MarginTop) ? null : tb.MarginTop,
                    MarginBottom = tb.MarginBottom,
                    ColumnWidths = new List<double>(tb.ColumnWidths),
                    RowHeights = new List<double>(tb.RowHeights),
                    Cells = new List<List<BlockDto>>()
                };
                foreach (var row in tb.Cells)
                {
                    var rd = new List<BlockDto>();
                    foreach (var cell in row)
                    {
                        // Backward-compatible cell encoding: a plain one-paragraph cell stays the legacy
                        // single block DTO with the cell background on it; a multi-block/non-paragraph cell
                        // is wrapped in a "Cell" DTO whose Blocks carry the full list (recursively).
                        //
                        // The legacy form has ONE Background field standing for two different fills, so it
                        // is only usable when the paragraph has no fill of its own: otherwise the
                        // assignment below overwrites the paragraph's with the cell's, and when the cell
                        // has none that means writing null — the paragraph's fill is erased outright, in
                        // this editor's own save format. A paragraph fill inside a cell arrives whenever
                        // HTML with a styled <p> in a <td> is pasted. The wrapper form keeps the two fills
                        // on separate DTOs, and readers already understand it (so does the upstream peer,
                        // which shares this bug and this encoding), so nothing about the format changes.
                        BlockDto cdto;
                        if (cell.Blocks.Count == 1 && cell.Blocks[0] is Paragraph cpara && cpara.Background == null)
                        {
                            cdto = ParagraphToDto(cpara, pool);
                        }
                        else
                        {
                            cdto = new BlockDto { Type = "Cell", Blocks = new List<BlockDto>() };
                            foreach (var b in cell.Blocks) cdto.Blocks.Add(BlockToDto(b, pool));
                        }
                        cdto.Background = ColorToString(cell.Background);
                        if (cell.VerticalAlignment != CellVerticalAlignment.Top)
                            cdto.VAlign = cell.VerticalAlignment.ToString(); // omit the default, keeping old docs byte-identical
                        rd.Add(cdto);
                    }
                    td.Cells.Add(rd);
                }
                td.ColSpans = new List<List<int>>();
                td.RowSpans = new List<List<int>>();
                foreach (var row in tb.ColSpans) td.ColSpans.Add(new List<int>(row));
                foreach (var row in tb.RowSpans) td.RowSpans.Add(new List<int>(row));
                return td;
            default:
                return new BlockDto { Type = "Paragraph", Inlines = new List<InlineDto>() };
        }
    }

    private static BlockDto ParagraphToDto(Paragraph p, Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        var d = new BlockDto
        {
            Type = "Paragraph",
            Inlines = new List<InlineDto>(),
            TextAlignment = p.TextAlignment.ToString(),
            LineHeight = NanToNull(p.LineHeight),
            LineSpacing = NanToNull(p.LineSpacing),
            MarginTop = p.MarginTop,
            MarginBottom = p.MarginBottom,
            MarginRight = p.MarginRight,
            ListType = p.ListType.ToString(),
            ListMarker = p.ListMarker == ListMarkerStyle.Default ? null : p.ListMarker.ToString(),
            HeadingLevel = p.HeadingLevel,
            Background = ColorToString(p.Background),
            Indent = p.Indent,
            IsQuote = p.IsQuote,
            ListLevel = p.ListLevel
        };
        foreach (var inline in p.Inlines)
        {
            if (inline is Run r)
                d.Inlines.Add(new InlineDto
                {
                    Type = "Run",
                    Text = r.Text,
                    Bold = r.FontWeight.IsBold(),
                    Italic = r.FontStyle == FontStyle.Italic,
                    FontSize = r.FontSize,
                    Foreground = ColorToString(r.Foreground),
                    Background = ColorToString(r.Background),
                    FontFamily = r.FontFamily,
                    NavigateUri = r.NavigateUri,
                    Underline = r.TextDecorations.HasFlag(TextDecorationFlags.Underline),
                    Strikethrough = r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough)
                });
            else if (inline is InlineImage im)
                d.Inlines.Add(new InlineDto
                {
                    Type = "Image",
                    ImageRef = PoolImage(pool, im.RawBytes, im.MimeType, im.RawBytes == null ? im.Image : null),
                    Width = NanToNull(im.Width),
                    Height = NanToNull(im.Height),
                    Alt = im.AltText
                });
            else if (inline is InlineTable it)
                d.Inlines.Add(new InlineDto { Type = "Table", Table = BlockToDto(it.Table, pool) });
        }
        return d;
    }

    // ---- dto -> model ----

    // Ceiling on a table's declared column count, which pads short rows and so gets allocated. The rows
    // themselves need no cap: they come from the cells present in the file, which the input size bounds.
    // Far beyond any real document, and matched to the HTML importer's own cap.
    private const int MaxTableDimension = 1000;

    // A file is untrusted input (it reaches here by Open and by paste), so a margin that leaves no page to
    // write on is dropped whole rather than per side — half a stated margin is not what the file meant.
    // Paper: what the file itself declares, since the margins are read alongside it.
    private static PageMargins ReadMargin(PageSetupDto psd)
    {
        var d = PageSetup.DefaultMargin;
        var m = new PageMargins(psd.MarginLeft ?? d.Left, psd.MarginTop ?? d.Top,
                                psd.MarginRight ?? d.Right, psd.MarginBottom ?? d.Bottom);
        var size = Enum.TryParse<RichEditorPageSize>(psd.PageSize, out var sz) ? sz : RichEditorPageSize.Continuous;
        var orient = Enum.TryParse<RichEditorPageOrientation>(psd.Orientation, out var or) ? or : RichEditorPageOrientation.Portrait;
        var (w, h) = PageSetup.PaperMillimetres(size, orient);
        return PageSetup.IsUsableMargin(m, w, h) ? m : d;
    }

    private static Block? DtoToBlock(BlockDto? d, Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        if (d == null) return null; // a JSON null inside "Blocks" must not take the load down
        switch (d.Type)
        {
            case "Divider":
                return new DividerBlock { MarginTop = d.MarginTop ?? Block.AutoTopMargin, MarginBottom = d.MarginBottom ?? 0 };
            case "Image":
                {
                    var ib = new ImageBlock
                    {
                        Width = d.Width ?? double.NaN,
                        Height = d.Height ?? double.NaN,
                        AltText = d.Alt,
                        Indent = d.Indent,
                        MarginTop = d.MarginTop ?? Block.AutoTopMargin, // absent = the editor's own gap
                        MarginBottom = d.MarginBottom ?? 10
                    };
                    if (ResolveImage(d.ImageRef, d.ImageBase64, d.MimeType, pool) is { } img)
                        ib.SetImageData(img.Bytes, img.Mime);
                    return ib;
                }
            case "Table":
                // 1x1, NOT the declared size: everything the constructor builds is cleared on the next
                // three lines and rebuilt from the cells that actually exist, so sizing it from
                // attacker-controlled numbers only ever wasted memory — a file claiming 100000000 rows
                // exhausted it before a single cell was read.
                var tb = new TableBlock(1, 1);
                tb.Indent = d.Indent;
                tb.MarginTop = d.MarginTop ?? Block.AutoTopMargin; // absent = the editor's own gap
                tb.MarginBottom = d.MarginBottom ?? 10;
                tb.Cells.Clear();
                tb.ColumnWidths.Clear();
                tb.RowHeights.Clear();
                if (d.ColumnWidths != null) tb.ColumnWidths.AddRange(d.ColumnWidths);
                if (d.RowHeights != null) tb.RowHeights.AddRange(d.RowHeights);
                if (d.Cells != null)
                    foreach (var rowD in d.Cells)
                    {
                        var row = new List<TableCell>();
                        if (rowD == null) { tb.Cells.Add(row); continue; } // JSON null row
                        foreach (var cd in rowD)
                        {
                            TableCell cell;
                            if (cd == null) { row.Add(new TableCell()); continue; } // JSON null cell
                            if (cd.Type == "Cell")
                            {
                                cell = new TableCell { Background = ColorUtil.Parse(cd.Background) };
                                cell.Blocks.Clear();
                                if (cd.Blocks != null)
                                    foreach (var bd in cd.Blocks)
                                        if (DtoToBlock(bd, pool) is { } bb) { bb.Parent = cell; cell.Blocks.Add(bb); }
                                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "" } } });
                            }
                            else
                            {
                                var para = DtoToParagraph(cd, pool);
                                cell = new TableCell(para) { Background = para.Background };
                                para.Background = null;
                            }
                            if (Enum.TryParse<CellVerticalAlignment>(cd.VAlign, out var va))
                                cell.VerticalAlignment = va;
                            row.Add(cell);
                        }
                        tb.Cells.Add(row);
                    }
                tb.Rows = tb.Cells.Count;
                // The declared width pads short rows, so it is allocated too — cap it. The widest row
                // that really exists always wins below, so a legitimate document is unaffected.
                int maxCols = Math.Clamp(d.Columns, 1, MaxTableDimension);
                foreach (var row in tb.Cells) if (row.Count > maxCols) maxCols = row.Count;
                tb.Columns = maxCols;
                foreach (var row in tb.Cells)
                    while (row.Count < tb.Columns) row.Add(new TableCell());
                tb.ColSpans.Clear();
                tb.RowSpans.Clear();
                for (int r = 0; r < tb.Rows; r++)
                {
                    var cs = new List<int>();
                    var rs = new List<int>();
                    for (int c = 0; c < tb.Columns; c++)
                    {
                        // A JSON null row (`"ColSpans":[null]`) threw NullReferenceException out of the load — not
                        // the JsonException the load paths promise — so it reads as "no spans" like a missing row.
                        cs.Add(d.ColSpans is { } dcs && r < dcs.Count && dcs[r] is { } csr && c < csr.Count ? csr[c] : 1);
                        rs.Add(d.RowSpans is { } drs && r < drs.Count && drs[r] is { } rsr && c < rsr.Count ? rsr[c] : 1);
                    }
                    tb.ColSpans.Add(cs);
                    tb.RowSpans.Add(rs);
                }
                tb.EnsureSpanConsistency(); // the file's span VALUES are data too — see there
                return tb;
            default:
                return DtoToParagraph(d, pool);
        }
    }

    private static Paragraph DtoToParagraph(BlockDto d, Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        var p = new Paragraph
        {
            LineHeight = d.LineHeight ?? double.NaN,
            LineSpacing = d.LineSpacing ?? double.NaN,
            MarginTop = d.MarginTop ?? 0,
            MarginBottom = d.MarginBottom ?? 0,
            MarginRight = d.MarginRight ?? 0,
            HeadingLevel = d.HeadingLevel,
            Background = ColorUtil.Parse(d.Background),
            Indent = d.Indent,
            IsQuote = d.IsQuote,
            ListLevel = d.ListLevel,
            ListType = Enum.TryParse<ListKind>(d.ListType, out var lk) ? lk : (d.IsListItem ? ListKind.Bullet : ListKind.None),
            ListMarker = Enum.TryParse<ListMarkerStyle>(d.ListMarker, out var lm) ? lm : ListMarkerStyle.Default
        };
        if (Enum.TryParse<TextAlignment>(d.TextAlignment, out var ta)) p.TextAlignment = ta;
        if (d.Inlines != null)
            foreach (var id in d.Inlines)
            {
                if (id == null) continue; // JSON null inside "Inlines"
                if (id.Type == "Image")
                {
                    var im = new InlineImage
                    {
                        Width = id.Width ?? 16,
                        Height = id.Height ?? 16,
                        AltText = id.Alt
                    };
                    if (ResolveImage(id.ImageRef, id.ImageBase64, id.MimeType, pool) is { } img)
                        im.SetImageData(img.Bytes, img.Mime);
                    p.Inlines.Add(im);
                }
                else if (id.Type == "Table" && id.Table != null)
                {
                    if (DtoToBlock(id.Table, pool) is TableBlock itb)
                        p.Inlines.Add(new InlineTable { Table = itb });
                }
                else
                    p.Inlines.Add(new Run
                    {
                        Text = id.Text,
                        FontWeight = id.Bold ? FontWeightValues.Bold : FontWeightValues.Normal,
                        FontStyle = id.Italic ? FontStyle.Italic : FontStyle.Normal,
                        FontSize = id.FontSize <= 0 ? 10 : id.FontSize, // pt; body default
                        Foreground = ColorUtil.Parse(id.Foreground),
                        Background = ColorUtil.Parse(id.Background),
                        FontFamily = id.FontFamily,
                        // A file is untrusted input: a script link is dropped as the HTML reader drops it (2026-09-24).
                        NavigateUri = id.NavigateUri is { } uri ? HtmlDocumentFormatter.SafeHref(uri) : null,
                        TextDecorations = BuildDecorations(id.Underline, id.Strikethrough)
                    });
            }
        return p;
    }

    // ---- helpers ----

    private static double? NanToNull(double v) => double.IsNaN(v) ? (double?)null : v;

    private static TextDecorationFlags BuildDecorations(bool underline, bool strikethrough)
    {
        var d = TextDecorationFlags.None;
        if (underline) d |= TextDecorationFlags.Underline;
        if (strikethrough) d |= TextDecorationFlags.Strikethrough;
        return d;
    }

    // The JSON storage form for a color is "#AARRGGBB" (matches Color.ToString()); ColorUtil.Parse reads it back.
    private static string? ColorToString(Windows.UI.Color? c) => c.HasValue ? ColorUtil.ToHexArgb(c.Value) : null;

    // Adds image bytes to the document pool (deduplicated by content hash) and returns the pool key,
    // or null when there is no image data. RawBytes are stored as-is; a bitmap without bytes is PNG-encoded.
    private static string? PoolImage(Dictionary<string, (byte[] Bytes, string Mime)> pool, byte[]? rawBytes, string? mimeType, CanvasBitmap? bmp)
    {
        byte[]? bytes = rawBytes ?? ImageEncoder.ToPngBytes(bmp);
        if (bytes == null) return null;
        string mime = rawBytes != null ? (mimeType ?? "image/png") : "image/png";
        string key = Convert.ToHexString(SHA256.HashData(bytes));
        if (!pool.ContainsKey(key)) pool[key] = (bytes, mime);
        return key;
    }

    private static (byte[] Bytes, string Mime)? ResolveImage(string? imageRef, string? inlineBase64, string? mimeType,
        Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        if (imageRef != null && pool.TryGetValue(imageRef, out var pooled)) return pooled;
        var bytes = TryFromBase64(inlineBase64);
        return bytes != null ? (bytes, mimeType ?? "image/png") : null;
    }

    private static byte[]? TryFromBase64(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return Convert.FromBase64String(value); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return null; }
    }
}

internal class FlowDocumentDto
{
    [JsonConverter(typeof(SchemaVersionConverter))]
    public string Version { get; set; } = "1";
    // Null by default so a READ can tell "no Blocks property" (not a document of this library) from "no
    // blocks" — ToDto always sets it, so every document this library writes has it (see EnsureDocumentShape).
    public List<BlockDto>? Blocks { get; set; }
    public Dictionary<string, ImagePoolDto>? Images { get; set; }
    // Optional page setup; absent for plain (Continuous) documents, so the format is unchanged for them.
    public PageSetupDto? PageSetup { get; set; }
    // 1 = heading formatting is on the runs (bold and the heading size, written when the heading was set).
    // Absent in files written before 2026-09-12 and by upstream, whose renderers forced both; the EDITOR then
    // writes them onto the runs once when it receives the document (HeadingStyle.Materialize) — the reader
    // leaves the model as it was saved. Upstream ignores unknown properties.
    public int? HeadingFormat { get; set; }
}

// Enums are serialized as their names (matching the rest of the format), so unknown future values degrade
// gracefully to defaults on read. The original AvaloniaRichEditor ignores this object (content-only format).
internal class PageSetupDto
{
    public string? PageSize { get; set; }
    public string? Orientation { get; set; }
    public bool ShowPageBoundaries { get; set; } = true;
    public string? Header { get; set; }
    public string? Footer { get; set; }
    public bool ShowPageNumbers { get; set; }
    // Page margins in MILLIMETRES, one per side. Omitted when they are the default, so a document that never
    // touched them keeps its bytes; a reader that predates them (or a file that omits one side) falls back to
    // the default for that side. Same fields and unit as upstream.
    public double? MarginLeft { get; set; }
    public double? MarginTop { get; set; }
    public double? MarginRight { get; set; }
    public double? MarginBottom { get; set; }
}

internal class ImagePoolDto
{
    public string? Data { get; set; } // base64 of the original encoded bytes
    public string? MimeType { get; set; }
}

internal class BlockDto
{
    public string Type { get; set; } = "Paragraph";

    // Paragraph
    public List<InlineDto>? Inlines { get; set; }
    public string? TextAlignment { get; set; }
    public double? LineHeight { get; set; }
    public double? LineSpacing { get; set; }
    public double? MarginTop { get; set; }
    public double? MarginBottom { get; set; }
    public double? MarginRight { get; set; }
    public bool IsListItem { get; set; } // legacy (read fallback); replaced by ListType
    public string? ListType { get; set; }
    public string? ListMarker { get; set; }
    public int HeadingLevel { get; set; }
    public string? Background { get; set; }
    public double Indent { get; set; }
    public bool IsQuote { get; set; }
    public int ListLevel { get; set; }

    // Image block
    public string? ImageRef { get; set; }
    public string? ImageBase64 { get; set; }
    public string? MimeType { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string? Alt { get; set; } // accessibility description; omitted when null (format unchanged)

    // Table block
    public int Rows { get; set; }
    public int Columns { get; set; }
    public List<double>? ColumnWidths { get; set; }
    public List<double>? RowHeights { get; set; }
    public List<List<BlockDto>>? Cells { get; set; }
    public List<List<int>>? ColSpans { get; set; }
    public List<List<int>>? RowSpans { get; set; }

    // Table cell (Type == "Cell"): the cell's block list.
    public List<BlockDto>? Blocks { get; set; }
    // Table cell vertical alignment ("Center"/"Bottom"; null = Top). On both cell encodings.
    public string? VAlign { get; set; }
}

internal class InlineDto
{
    public string Type { get; set; } = "Run";

    // Run
    public string? Text { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public double FontSize { get; set; } = 10; // pt; body default
    public string? Foreground { get; set; }
    public string? Background { get; set; }
    public string? FontFamily { get; set; }
    public string? NavigateUri { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }

    // Inline image
    public string? ImageRef { get; set; }
    public string? ImageBase64 { get; set; }
    public string? MimeType { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string? Alt { get; set; } // accessibility description; omitted when null

    // Inline table (Type == "Table"): the wrapped grid serialized as a block table DTO.
    public BlockDto? Table { get; set; }
}

// Reads the document-format version as the legacy integer form (1, 2) or the current SemVer string ("1.0").
// Always writes the string form.
internal sealed class SchemaVersionConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "1",
            JsonTokenType.Number => reader.TryGetInt64(out var n)
                ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "1"
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
