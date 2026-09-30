using WinUIRichEditor.Controls;

namespace WinUIRichEditor.Documents;

/// <summary>Per-document page setup: paper size, orientation, page boundaries, header/footer, and page
/// numbers. Persisted in the JSON/.flow format and applied to the editor on load (like a word processor's
/// page setup). View-only state such as zoom is deliberately NOT part of this — it isn't a document property.
/// <para>The page-size/orientation enums live in the Controls namespace but are plain value enums (no WinUI
/// runtime), so the headless model/formatters can use them freely.</para></summary>
public class PageSetup
{
    /// <summary>Paper size. <see cref="RichEditorPageSize.Continuous"/> (the default) reflows to width.</summary>
    public RichEditorPageSize PageSize { get; set; } = RichEditorPageSize.Continuous;
    /// <summary>Page orientation (ignored for Continuous).</summary>
    public RichEditorPageOrientation Orientation { get; set; } = RichEditorPageOrientation.Portrait;
    /// <summary>Whether page boundaries are drawn for a concrete paper size.</summary>
    public bool ShowPageBoundaries { get; set; } = true;
    /// <summary>Header text drawn in each page's top margin (null/empty = none).</summary>
    public string? Header { get; set; }
    /// <summary>Footer text drawn in each page's bottom margin (null/empty = none).</summary>
    public string? Footer { get; set; }
    /// <summary>Whether "page / total" is drawn in the bottom margin.</summary>
    public bool ShowPageNumbers { get; set; }

    /// <summary>The page margins, in MILLIMETRES: the band between the paper's edge and the text, which
    /// the header, the footer and the page number are drawn in. Four sides, as Word, HWP and RTF have
    /// them. Defaults to <see cref="DefaultMargin"/>.</summary>
    public PageMargins Margin { get; set; } = DefaultMargin;

    /// <summary>The margins a document starts with: 15 mm on every side.</summary>
    public static PageMargins DefaultMargin { get; } = new(DefaultMarginMm);

    // Here rather than on the control because the RTF writer needs them too, and touching any RichEditor
    // static from a formatter would run that type's static constructor — which registers dependency
    // properties and therefore requires the WinUI runtime. The formatters must stay headless.
    //
    // 15 mm, not the 12.7 x 10.6 this editor drew while the margins were two constants in device pixels
    // (48 x 40 DIP): millimetres are the unit a page is set in, and the default should be a number someone
    // would choose. It is also the middle step of the toolbar's picker, so a new document shows "Normal".
    internal const double DefaultMarginMm = 15;

    // A margin a document (or an RTF from another word processor) states has to leave a content box to
    // put text in: negative, NaN/infinite, or two sides that together swallow the paper all describe a
    // page nothing can be laid out on. Such a value is dropped rather than clamped — a document that
    // means "no margins" says 0, and silently halving someone's 80 mm margin is its own surprise.
    internal static bool IsUsableMargin(PageMargins m, double paperWmm, double paperHmm)
    {
        foreach (double v in new[] { m.Left, m.Top, m.Right, m.Bottom })
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) return false;
        return m.Left + m.Right < paperWmm && m.Top + m.Bottom < paperHmm;
    }

    /// <summary>Device-independent pixels per millimetre: this library lays out at 96 DPI, and page
    /// geometry is stated in millimetres, so every conversion between the two goes through here.</summary>
    public const double DipsPerMm = 96.0 / 25.4;

    // 1440 twips per inch, 25.4 mm per inch: page geometry is millimetres in the model and twips in RTF.
    internal const double TwipsPerMm = 1440.0 / 25.4;
    internal static int MmToTwips(double mm) => (int)System.Math.Round(mm * TwipsPerMm);

    // The way back. A twip is 0.0176 mm, so twips / TwipsPerMm is almost never the millimetres that were written:
    // 15 mm goes out as 850 twips and came back as 14.993 - every step of the toolbar's picker missed its own
    // preset after an RTF round trip, and the JSON then stored a "custom" margin (2026-09-24). A value that is a
    // whole tenth of a millimetre AND writes back to the same twips is taken as what was meant; anything else
    // (Word's 1.25 inch = 1800 twips = 31.75 mm) keeps its exact length, since rounding it would move it.
    internal static double TwipsToMm(int twips)
    {
        double mm = twips / TwipsPerMm;
        double tenth = System.Math.Round(mm, 1);
        return MmToTwips(tenth) == twips ? tenth : mm;
    }

    /// <summary>Paper size in millimetres for a page size + orientation.</summary>
    public static (double W, double H) PaperMillimetres(RichEditorPageSize size, RichEditorPageOrientation orientation)
    {
        var (w, h) = PaperDips(size, orientation);
        return (w / DipsPerMm, h / DipsPerMm);
    }

    /// <summary>Paper size in DIPs for a page size + orientation. Single source: the control's layout and
    /// the RTF writer's tab stops must agree, and two copies of a table like this drift.</summary>
    internal static (double W, double H) PaperDips(RichEditorPageSize size, RichEditorPageOrientation orientation)
    {
        var (w, h) = size switch
        {
            RichEditorPageSize.A3 => (1123.0, 1587.0),
            RichEditorPageSize.A5 => (559.0, 794.0),
            RichEditorPageSize.B4 => (971.0, 1376.0),
            RichEditorPageSize.B5 => (688.0, 971.0),
            RichEditorPageSize.Letter => (816.0, 1056.0),
            RichEditorPageSize.Legal => (816.0, 1344.0),
            RichEditorPageSize.Tabloid => (1056.0, 1632.0),
            _ => (794.0, 1123.0), // A4, and the Continuous fallback
        };
        return orientation == RichEditorPageOrientation.Landscape ? (h, w) : (w, h);
    }

    public PageSetup Clone() => new()
    {
        PageSize = PageSize,
        Orientation = Orientation,
        ShowPageBoundaries = ShowPageBoundaries,
        Header = Header,
        Footer = Footer,
        ShowPageNumbers = ShowPageNumbers,
        Margin = Margin,
    };

    /// <summary>True when the setup carries no real information (Continuous paper, no header/footer/page
    /// numbers). The editor leaves such a setup off a document when its host's defaults are plain too, so plain
    /// documents keep their original format; a document that carries one — even a default-looking one — has it
    /// written, because a missing setup reads back as the host's defaults, which may not be Continuous.</summary>
    public bool IsDefault =>
        PageSize == RichEditorPageSize.Continuous
        && string.IsNullOrEmpty(Header)
        && string.IsNullOrEmpty(Footer)
        && !ShowPageNumbers
        && Margin.Equals(DefaultMargin);
}
