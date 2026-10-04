namespace QNote.Infrastructure;

/// <summary>
/// QNote's Microsoft Store identity links. The ProductId is stable across
/// releases (AboutDialog's rating link and the update dialog's Store-page
/// action both derive from it; keep the two usages in sync manually — the
/// XAML NavigateUri cannot reference a C# constant).
/// </summary>
public static class StoreLink
{
    /// <summary>Partner Center product id (QNote - Sticky Notes).</summary>
    public const string ProductId = "9NV57VJPTPCZ";

    /// <summary>
    /// ms-windows-store deep link that opens the Store app directly on QNote's
    /// product page (used by the startup update dialog's confirm action).
    /// </summary>
    public static Uri ProductPage => new($"ms-windows-store://pdp/?ProductId={ProductId}");
}
