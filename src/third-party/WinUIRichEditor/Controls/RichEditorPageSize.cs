namespace WinUIRichEditor.Controls;

/// <summary>Paper size for a <see cref="RichEditor"/>'s document layout. Pixel dimensions are at
/// 96 DPI; B4/B5 use the JIS series (common in Korea/Japan), not ISO B.</summary>
public enum RichEditorPageSize
{
    /// <summary>Continuous layout (no fixed paper): the text column reflows to the control's width.</summary>
    Continuous = 0,
    /// <summary>A4 (210 × 297 mm → 794 × 1123 px).</summary>
    A4 = 1,
    /// <summary>A3 (297 × 420 mm → 1123 × 1587 px).</summary>
    A3 = 2,
    /// <summary>A5 (148 × 210 mm → 559 × 794 px).</summary>
    A5 = 3,
    /// <summary>JIS B4 (257 × 364 mm → 971 × 1376 px).</summary>
    B4 = 4,
    /// <summary>JIS B5 (182 × 257 mm → 688 × 971 px).</summary>
    B5 = 5,
    /// <summary>US Letter (8.5 × 11 in → 816 × 1056 px).</summary>
    Letter = 6,
    /// <summary>US Legal (8.5 × 14 in → 816 × 1344 px).</summary>
    Legal = 7,
    /// <summary>Tabloid / Ledger (11 × 17 in → 1056 × 1632 px).</summary>
    Tabloid = 8,
}

/// <summary>Page orientation for a <see cref="RichEditor"/> with a concrete <see cref="RichEditorPageSize"/>.
/// Landscape swaps the paper's width and height (affects both the editor view and print/PDF output).</summary>
public enum RichEditorPageOrientation
{
    /// <summary>Portrait: the paper's natural (taller-than-wide) dimensions.</summary>
    Portrait = 0,
    /// <summary>Landscape: width and height swapped.</summary>
    Landscape = 1,
}
