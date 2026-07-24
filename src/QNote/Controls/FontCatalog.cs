namespace QNote.Controls;

/// <summary>A font-family option for the editor toolbar (family name + Chinese display name).</summary>
public sealed record FontOption(string Display, string Family)
{
    public override string ToString() => Display;
}

/// <summary>
/// The editor toolbar's fixed font whitelist and pixel sizes (Qt parity).
/// </summary>
public static class FontCatalog
{
    // ponytail: no installed-font filtering — FontFamily construction doesn't validate,
    // and missing fonts fall back at render time (same as the Qt build's whitelist).
    public static IReadOnlyList<FontOption> Families { get; } = new[]
    {
        new FontOption("微软雅黑 UI", "Microsoft YaHei UI"),
        new FontOption("微软雅黑", "Microsoft YaHei"),
        new FontOption("宋体", "SimSun"),
        new FontOption("黑体", "SimHei"),
        new FontOption("楷体", "KaiTi"),
        new FontOption("仿宋", "FangSong"),
        new FontOption("Consolas", "Consolas"),
        new FontOption("Arial", "Arial"),
        new FontOption("Segoe UI", "Segoe UI"),
        new FontOption("Tahoma", "Tahoma"),
    };

    /// <summary>Qt pixel steps; default 16px.</summary>
    public static IReadOnlyList<int> SizesPx { get; } = new[] { 12, 14, 16, 18, 20, 24, 28, 32 };

    public const int DefaultSizePx = 16;
}
