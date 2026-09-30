using System;

namespace WinUIRichEditor.Documents;

/// <summary>Text decorations applied to a <see cref="Run"/>. Only underline and strikethrough are
/// supported (the full set the editor ever uses). Replaces Avalonia's <c>TextDecorationCollection</c>;
/// at the render boundary these map to <c>CanvasTextLayout.SetUnderline</c>/<c>SetStrikethrough</c>.</summary>
[Flags]
public enum TextDecorationFlags
{
    /// <summary>No decorations.</summary>
    None = 0,
    /// <summary>Underline.</summary>
    Underline = 1,
    /// <summary>Strikethrough.</summary>
    Strikethrough = 2,
}
