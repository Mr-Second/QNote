using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.Graphics.Canvas.Text;

namespace WinUIRichEditor.Controls;

// System font enumeration (parity with AvaloniaRichEditor). The installed font families — with names
// localized to the OS UI language (e.g. "맑은 고딕" on Korean Windows, "Malgun Gothic" on English) — feed
// the toolbar combo and the right-click font submenu. Both Win2D and Avalonia sit on DirectWrite, so the
// localized family names match and resolve back through the same font system for display AND application.
public partial class RichEditor
{
    // The OS default UI font (Windows message font) so unstyled documents look native rather than
    // defaulting to a fixed face. Falls back to "Segoe UI" when the query is unavailable.
    internal static string SystemDefaultFontFamily()
        => SystemFontInfo.MessageFontFaceName() ?? "Segoe UI";

    // Cached system font list. DirectWrite reports family names localized to the UI language, and those
    // names resolve back through the same font set, so they're used as-is for both display and application.
    private static IReadOnlyList<string>? _systemFontChoices;

    // The names come from the system collection's family list, in the UI language when a family has it, then
    // the bare language, then en-us. The list used to be read face by face (CanvasFontSet.Fonts → FamilyNames),
    // which opens every installed font: measured 2026-09-27 (MemBaseline fonts mode), +31.6 MB of Private
    // bytes and 843 ms on the UI thread the first time a toolbar asked, held until a GC finalized the
    // undisposed faces — and a quiet app, whose managed heap grew by 0.6 MB, has no reason to run one. This
    // way: +0.7 MB, 140 ms, and the same names (TheFontList_… compares them with that walk, name by name).
    // ⚠ Two things that look equivalent FAIL UNDER NATIVE AOT, silently (the catch below falls back to six
    // stock names), both found by the demo's --pageprobe "fonts" line diffed between JIT and AOT:
    //   · CanvasFontSet.GetPropertyValues — returns CanvasFontProperty[], a non-blittable struct array
    //     (two strings); the same marshalling gap as LineMetrics.
    //   · a collection expression `[locale, lang, "en-us"]` for the locale list — the compiler synthesizes a
    //     read-only list type CsWinRT has no CCW for (InvalidCastException). A real string[] marshals.

    private static IReadOnlyList<string> SystemFontChoices()
    {
        if (_systemFontChoices != null) return _systemFontChoices;
        var names = new List<string>();
        try
        {
            string locale = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();      // e.g. "ko-kr"
            string lang = locale.Length >= 2 ? locale.Substring(0, 2) : locale;          // e.g. "ko"
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in CanvasTextFormat.GetSystemFontFamilies(new string[] { locale, lang, "en-us" }))
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) names.Add(name);
            names.Sort(StringComparer.Create(CultureInfo.CurrentUICulture, ignoreCase: true));
        }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            names.Clear();
        }
        // Platforms/environments without font enumeration: widely-available fallback families.
        if (names.Count == 0)
            names.AddRange(new[] { "Segoe UI", "Arial", "Times New Roman", "Courier New", "Verdana", "Georgia" });
        return _systemFontChoices = names;
    }

    /// <summary>Font families offered in the font pickers (toolbar combo and right-click submenu). Defaults
    /// to the installed system fonts, with names localized by — and sorted for — the OS UI language. Assign a
    /// non-empty list to curate the offered set.</summary>
    public static readonly DependencyProperty FontFamilyChoicesProperty = DependencyProperty.Register(
        nameof(FontFamilyChoices), typeof(IReadOnlyList<string>), typeof(RichEditor),
        new PropertyMetadata(Array.Empty<string>(), OnFontFamilyChoicesChanged));

    // The toolbar's font combo is built from these. A host curating the list at run time kept seeing the old one
    // until something else rebuilt the toolbar — this property raised no signal at all (measured 2026-09-14;
    // upstream's toolbar listens for its property change).
    internal event EventHandler? FontFamilyChoicesChanged;

    private static void OnFontFamilyChoicesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RichEditor)d).FontFamilyChoicesChanged?.Invoke(d, EventArgs.Empty);

    /// <summary>Font families offered in the font pickers; defaults to the installed system fonts.</summary>
    public IReadOnlyList<string> FontFamilyChoices
    {
        get
        {
            var v = (IReadOnlyList<string>)GetValue(FontFamilyChoicesProperty);
            return v.Count > 0 ? v : SystemFontChoices();
        }
        set => SetValue(FontFamilyChoicesProperty, value);
    }
}
