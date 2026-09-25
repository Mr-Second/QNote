namespace QNote.EdgeHide;

/// <summary>
/// Physical-pixel rectangle in the AppWindow / Win32 screen coordinate space.
/// Pure value type so the edge-hide judgment logic stays headless-testable in Core.
/// </summary>
public readonly record struct RectPx(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public bool IntersectsWith(RectPx other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}
