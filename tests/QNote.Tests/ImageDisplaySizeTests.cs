using QNote.Text;

namespace QNote.Tests;

/// <summary>"Shrink only" display sizing for inserted images (Qt parity).</summary>
public sealed class ImageDisplaySizeTests
{
    [Fact]
    public void Fit_KeepsNaturalSize_WhenNarrowerThanEditor()
    {
        Assert.Equal((400, 300), ImageDisplaySize.Fit(400, 300, 800));
    }

    [Fact]
    public void Fit_ShrinksToEditorWidth_PreservingAspect()
    {
        // 1920x1080 → 600 wide → 337.5 → 338 (round).
        Assert.Equal((600, 338), ImageDisplaySize.Fit(1920, 1080, 600));
    }

    [Fact]
    public void Fit_NeverUpscales_WhenEditorIsWider()
    {
        Assert.Equal((200, 100), ImageDisplaySize.Fit(200, 100, 4000));
    }

    [Fact]
    public void Fit_FallsBackToNaturalWidth_ForNonPositiveAvailableWidth()
    {
        Assert.Equal((500, 250), ImageDisplaySize.Fit(500, 250, 0));
    }

    [Fact]
    public void Fit_ClampsDegenerateInput()
    {
        Assert.Equal((ImageDisplaySize.MinWidthDip, ImageDisplaySize.MinWidthDip),
            ImageDisplaySize.Fit(0, 0, 600));
    }
}
