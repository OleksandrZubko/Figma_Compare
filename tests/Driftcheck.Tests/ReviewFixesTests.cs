using Driftcheck.Mcp;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Tests;

[Collection(OutputDirectoryCollection.Name)]
public class ReviewFixesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public ReviewFixesTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("hello world", "'hello world'")]
    [InlineData("a&b;c", "'a&b;c'")]
    [InlineData("it's", "'it'\\''s'")]
    public void ShellQuoteKeepsTheValueOneArgument(string value, string quoted) =>
        Assert.Equal(quoted, AdbClient.ShellQuote(value));

    [Theory]
    [InlineData("Physical size: 1080x2400\nOverride size: 720x1600", "size", "720x1600")]
    [InlineData("Physical size: 1080x2400", "size", null)]
    [InlineData("Physical density: 420\nOverride density: 280", "density", "280")]
    public void ReadsTheWmOverrideToRestore(string output, string what, string? expected) =>
        Assert.Equal(expected, DeviceConfigurator.OverrideValue(output, what));

    [Fact]
    public void BaselineAtAFractionalDensityComparesCleanly()
    {
        // 1.75 px per dp (280 dpi): RenderScale would round the baseline PNG to 2x and misplace every glyph.
        const double density = 1.75;
        var png = Path.Combine(_dir, "base.png");
        var title = new Rect(20, 100, 200, 30);
        DrawGlyphs(png, (int)(400 * density), (int)(800 * density), title.Scale(density));

        var capture = new ScreenCapture("emu", png, (int)(400 * density), (int)(800 * density), density,
        [
            new ScreenElement("title", "android.widget.TextView", "Checkout", null, title),
        ]);
        var store = new BaselineStore(new Settings());
        store.Save("density", capture);
        var baseline = store.Load("density");

        var result = new ScreenComparer(new Settings()).Compare(BaselineStore.ToDesignFrame(baseline), baseline.Capture.ScreenshotPath,
            capture, tolerance: 2, colorTolerance: 24, checkA11y: false, designPxPerUnit: baseline.Capture.PixelsPerUnit);

        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void PaddingDoneWithChildMarginsIsNotReported()
    {
        // Figma: 16 of padding. Android: 8 of padding plus 8 of margin on the child, so the content sits where
        // Figma says; View.getPadding*() alone would report 8.
        var text = new DesignElement("Label", "TEXT", new Rect(36, 116, 200, 24), new Rgb(0, 0, 0), "Label", 16, "Inter", 400, null, NodeId: "1:1");
        var layout = new DesignLayout("Card", "1:0", new Rect(20, 100, 360, 56), IsHorizontal: false,
            Padding: new Insets(16, 16, 16, 16), ItemSpacing: 0, SpaceBetween: false,
            ChildBounds: [text.Bounds], ChildNodeIds: ["1:1"]);

        var result = Compare([text], [layout],
            [new ScreenElement("label", "android.widget.TextView", "Label", null, new Rect(36, 116, 200, 24))],
            [
                new RuntimeView("card", new Rect(20, 100, 360, 56),
                    new RuntimeProps("android.widget.LinearLayout", 1, null, new RuntimeBox(new Rgb(255, 255, 255), null, null, null), Padding: new Insets(8, 8, 8, 8))),
                new RuntimeView("label", new Rect(36, 116, 328, 24),
                    new RuntimeProps("android.widget.TextView", 1, new RuntimeText("Label", 16, 400, "Inter", false, null, null, null), null)),
            ]);

        Assert.DoesNotContain(result.Diffs, d => d.Property == "padding");
    }

    [Fact]
    public void GapIsNotMeasuredFromADescendantThatDoesntReachTheEdge()
    {
        // The first child is a card (unmatched) with a title at its top; the next child sits 16 below the card.
        // The card's bottom edge isn't known from the title, so no gap must be inferred from it.
        var cardTitle = new DesignElement("CardTitle", "TEXT", new Rect(20, 100, 200, 24), new Rgb(0, 0, 0), "Card", 16, "Inter", 400, null, NodeId: "2:2");
        var next = new DesignElement("Next", "TEXT", new Rect(20, 216, 200, 24), new Rgb(0, 0, 0), "Next", 16, "Inter", 400, null, NodeId: "2:3");
        var layout = new DesignLayout("Column", "2:0", new Rect(20, 100, 360, 140), IsHorizontal: false,
            Padding: default, ItemSpacing: 16, SpaceBetween: false,
            ChildBounds: [new Rect(20, 100, 360, 100), next.Bounds], ChildNodeIds: ["2:1", "2:3"],
            ChildDescendantIds: [["2:1", "2:2"], ["2:3"]]);

        var result = Compare([cardTitle, next], [layout],
        [
            new ScreenElement("card_title", "android.widget.TextView", "Card", null, cardTitle.Bounds),
            new ScreenElement("next", "android.widget.TextView", "Next", null, next.Bounds),
        ], []);

        Assert.DoesNotContain(result.Diffs, d => d.Property == "gap");
    }

    ComparisonResult Compare(List<DesignElement> elements, List<DesignLayout> layouts, List<ScreenElement> screen, List<RuntimeView> runtime)
    {
        var png = Path.Combine(_dir, $"{Guid.NewGuid():N}.png");
        DrawGlyphs(png, 400, 800, null);
        var design = new DesignFrame("key", "1:0", "Screen", new Rect(0, 0, 400, 800), elements, Layouts: layouts);
        var capture = new ScreenCapture("test", png, 400, 800, 1, screen, RuntimeViews: runtime);
        return new ScreenComparer(new Settings()).Compare(design, png, capture, tolerance: 2, colorTolerance: 24, checkA11y: false);
    }

    /// <summary>A white screen with vertical strokes standing for the letters of a text.</summary>
    static void DrawGlyphs(string path, int w, int h, Rect? text)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.White);
            if (text is { } t)
            {
                using var paint = new SKPaint { Color = SKColors.Black };
                for (var i = 0; i < 8; i++)
                    c.DrawRect(SKRect.Create((float)(t.X + t.Width * 0.05 + i * t.Width * 0.1), (float)(t.Y + t.Height * 0.2),
                        (float)(t.Width * 0.02), (float)(t.Height * 0.6)), paint);
            }
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
