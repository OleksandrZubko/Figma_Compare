using Driftcheck.Mcp;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Tests;

[Collection(OutputDirectoryCollection.Name)]
public class ExtendedComparisonTests : IDisposable
{
    static readonly Rgb Page = new(0xF5, 0xF5, 0xF5);
    static readonly Rgb Blue = new(0x19, 0x76, 0xD2);
    static readonly Rgb Red = new(0xE5, 0x39, 0x35);
    static readonly Rgb Green = new(0x34, 0xA8, 0x53);
    static readonly Rgb Yellow = new(0xFB, 0xBC, 0x05);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public ExtendedComparisonTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ParsesPaddingElevationShadowAndOverflowFromFrida()
    {
        const string json = """
            {
              "metrics": { "density": 2.0, "fontScale": 1.3 },
              "views": [
                {
                  "id": "co.app:id/card",
                  "cls": "android.widget.LinearLayout",
                  "bounds": [40, 200, 760, 400],
                  "alpha": 0.5,
                  "elevation": 8.0,
                  "padding": [32, 24, 32, 24],
                  "background": { "kind": "rn", "color": "#FFFFFFFF", "radius": 24, "strokeWidth": 4, "strokeColor": "#FF1976D2", "shadow": true }
                },
                {
                  "id": "co.app:id/label",
                  "cls": "android.widget.TextView",
                  "bounds": [72, 224, 300, 280],
                  "alpha": 1,
                  "text": { "text": "Long label…", "size": 41.6, "color": "#FF000000", "weight": 600, "family": "Inter",
                            "lineHeight": 48, "letterSpacingPx": 2.08, "ellipsized": true, "clipped": true, "lineCount": 1 }
                }
              ]
            }
            """;

        var result = FridaProbe.Parse(json, 0.5);
        Assert.Null(result.Error);
        var card = result.Views![0];
        Assert.Equal(0.5, card.Props.Alpha);
        Assert.Equal(4.0, card.Props.Elevation);
        Assert.True(card.Props.Shadow);
        Assert.Equal(new Insets(16, 12, 16, 12), card.Props.Padding);
        Assert.Equal(2.0, card.Props.Box!.StrokeWidth);
        Assert.Equal(Blue, card.Props.Box.StrokeColor);

        var label = result.Views[1].Props.Text!;
        Assert.Equal(16, label.SizeSp);
        Assert.Equal(24, label.LineHeightDp);
        Assert.True(label.Ellipsized);
        Assert.True(label.Clipped);
        Assert.Equal(1, label.LineCount);
    }

    [Fact]
    public void DetectsLineHeightLetterSpacingShadowOpacityAndBorderWidthDiffs()
    {
        var elements = new List<DesignElement>
        {
            new("Title", "TEXT", new Rect(20, 100, 200, 30), new Rgb(0, 0, 0), "Hello", 16, "Inter", 400, null,
                LineHeight: 24, LetterSpacing: 1.5, Opacity: 0.8),
            new("Card", "FRAME", new Rect(20, 200, 360, 80), Blue, null, null, null, null, 12,
                Stroke: Red, StrokeWeight: 2, Shadow: true, Opacity: 1),
        };

        var designPng = Path.Combine(_dir, "design.png");
        DrawRect(designPng, 400, 800, new Rect(20, 200, 360, 80), Blue);
        var devicePng = Path.Combine(_dir, "device.png");
        DrawRect(devicePng, 400, 800, new Rect(20, 200, 360, 80), Blue);

        var design = new DesignFrame("key", "1:1", "Screen", new Rect(0, 0, 400, 800), elements);
        var capture = new ScreenCapture("test", devicePng, 400, 800, 1,
        [
            new ScreenElement("Title", "android.widget.TextView", "Hello", null, new Rect(20, 100, 200, 30),
                new RuntimeProps("android.widget.TextView", 1.0,
                    new RuntimeText("Hello", 16, 400, "Inter", false, null, LineHeightDp: 18, LetterSpacingEm: 0), null)),
            new ScreenElement("Card", "android.view.View", null, null, new Rect(20, 200, 360, 80),
                new RuntimeProps("android.view.View", 1.0, null,
                    new RuntimeBox(Blue, 12, StrokeWidth: 4, StrokeColor: Green), Shadow: false)),
        ]);

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24, UiFramework.Compose);

        Assert.Contains(result.Diffs, d => d is { Property: "line height", Expected: "24", Actual: "18" } && d.Fix!.Contains("lineHeight"));
        Assert.Contains(result.Diffs, d => d is { Property: "letter spacing", Expected: "1.5", Actual: "0" } && d.Fix!.Contains("letterSpacing"));
        Assert.Contains(result.Diffs, d => d is { Property: "opacity", Expected: "0.8", Actual: "1" } && d.Fix!.Contains("Modifier.alpha"));
        Assert.Contains(result.Diffs, d => d is { Property: "border width", Expected: "2", Actual: "4" });
        Assert.Contains(result.Diffs, d => d is { Property: "border", Expected: "#E53935", Actual: "#34A853" });
        Assert.Contains(result.Diffs, d => d is { Property: "shadow", Expected: "drop shadow", Actual: "none" });
    }

    [Fact]
    public void ComparesMulticolorIconPaletteAgainstFrida()
    {
        var iconBox = new Rect(190, 300, 24, 24);
        var shapes = new List<DesignElement>
        {
            new("GoogleIcon", "FRAME", iconBox, null, null, null, null, null, null, "1:10", ShapeKind.Icon,
                MultiColor: true, IconColors: [Blue, Red, Yellow, Green]),
        };
        var design = new DesignFrame("key", "1:0", "Login", new Rect(0, 0, 400, 800), [], Shapes: shapes, Background: Page);

        var designPng = Path.Combine(_dir, "design_mc.png");
        DrawIcon(designPng, iconBox, Blue);
        var devicePng = Path.Combine(_dir, "device_mc.png");
        DrawIcon(devicePng, iconBox, Blue);

        // Matching palette (in different order) -> no icon color diff.
        var okCapture = new ScreenCapture("test", devicePng, 400, 800, 1, [],
            RuntimeViews: [new RuntimeView(null, iconBox, new RuntimeProps("com.horcrux.svg.SvgView", 1, null, null,
                new RuntimeIcon([Green, Blue, Yellow, Red], false)))]);
        var okResult = new ScreenComparer(new Settings()).Compare(design, designPng, okCapture, tolerance: 2, colorTolerance: 24);
        Assert.DoesNotContain(okResult.Diffs, d => d.Property == "icon color");

        // Missing Green -> reports multicolor icon palette mismatch.
        var badCapture = new ScreenCapture("test", devicePng, 400, 800, 1, [],
            RuntimeViews: [new RuntimeView(null, iconBox, new RuntimeProps("com.horcrux.svg.SvgView", 1, null, null,
                new RuntimeIcon([Blue, Red, Yellow], false)))]);
        var badResult = new ScreenComparer(new Settings()).Compare(design, designPng, badCapture, tolerance: 2, colorTolerance: 24);
        Assert.Contains(badResult.Diffs, d => d.Property == "icon color" && d.Expected.Contains("#34A853"));
    }

    [Fact]
    public void DetectsAutoLayoutGapAndPaddingMismatches()
    {
        var elements = new List<DesignElement>
        {
            new("Item1", "TEXT", new Rect(36, 116, 200, 24), new Rgb(0, 0, 0), "First", 16, "Inter", 400, null, NodeId: "1:1"),
            new("Item2", "TEXT", new Rect(36, 156, 200, 24), new Rgb(0, 0, 0), "Second", 16, "Inter", 400, null, NodeId: "1:2"),
        };
        var layouts = new List<DesignLayout>
        {
            new("FormColumn", "1:0", new Rect(20, 100, 360, 100), IsHorizontal: false,
                Padding: new Insets(16, 16, 16, 16), ItemSpacing: 16, SpaceBetween: false,
                ChildBounds: [elements[0].Bounds, elements[1].Bounds],
                ChildNodeIds: ["1:1", "1:2"]),
        };

        var designPng = Path.Combine(_dir, "design_al.png");
        DrawBlank(designPng, 400, 800);
        var devicePng = Path.Combine(_dir, "device_al.png");
        DrawBlank(devicePng, 400, 800);

        var design = new DesignFrame("key", "1:0", "Form", new Rect(0, 0, 400, 800), elements, Layouts: layouts);
        // App has gap 12 instead of 16 (Item2 starts at 116+24+12 = 152) and padding 8 instead of 16.
        var capture = new ScreenCapture("test", devicePng, 400, 800, 1,
        [
            new ScreenElement("Item1", "android.widget.TextView", "First", null, new Rect(36, 116, 200, 24)),
            new ScreenElement("Item2", "android.widget.TextView", "Second", null, new Rect(36, 152, 200, 24)),
        ],
        RuntimeViews:
        [
            new RuntimeView("FormColumn", new Rect(20, 100, 360, 100),
                new RuntimeProps("android.widget.LinearLayout", 1, null, null, Padding: new Insets(8, 8, 8, 8))),
        ]);

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24, UiFramework.Compose);

        Assert.Contains(result.Diffs, d => d is { Name: "FormColumn", Property: "gap", Expected: "16", Actual: "12" } && d.Fix!.Contains("Arrangement.spacedBy(16.dp)"));
        Assert.Contains(result.Diffs, d => d is { Name: "FormColumn", Property: "padding", Expected: "16", Actual: "8" });
    }

    [Fact]
    public void DetectsTextOverflowScreenOverflowOverlapAndSupportsIgnoreTextContent()
    {
        var elements = new List<DesignElement>
        {
            new("Title", "TEXT", new Rect(20, 100, 300, 24), new Rgb(0, 0, 0), "Welcome", 16, "Inter", 400, null),
            new("Subtitle", "TEXT", new Rect(20, 140, 300, 24), new Rgb(0, 0, 0), "Details", 14, "Inter", 400, null),
        };
        var designPng = Path.Combine(_dir, "design_ov.png");
        DrawBlank(designPng, 400, 800);
        var devicePng = Path.Combine(_dir, "device_ov.png");
        DrawBlank(devicePng, 400, 800);

        var design = new DesignFrame("key", "1:0", "LocaleTest", new Rect(0, 0, 400, 800), elements);
        // Translated German text: ellipsized, extends past 400dp screen width, and Subtitle overlaps Title.
        var capture = new ScreenCapture("test", devicePng, 400, 800, 1,
        [
            new ScreenElement("Title", "android.widget.TextView", "Willkommen bei der Anwendung…", null, new Rect(20, 100, 410, 24)),
            new ScreenElement("Subtitle", "android.widget.TextView", "Einzelheiten", null, new Rect(20, 108, 300, 24)),
        ]);

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24,
            ignoreTextContent: true);

        Assert.DoesNotContain(result.Diffs, d => d.Property == "text");
        Assert.Contains(result.Diffs, d => d is { Property: "text overflow", Actual: "ellipsized" });
        Assert.Contains(result.Diffs, d => d.Property == "overflow");
        Assert.Contains(result.Diffs, d => d.Property == "overlap");
    }

    static void DrawBlank(string path, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.White);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    static void DrawRect(string path, int w, int h, Rect rect, Rgb color)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.White);
            using var p = new SKPaint { Color = new SKColor(color.R, color.G, color.B) };
            c.DrawRect(SKRect.Create((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height), p);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    static void DrawIcon(string path, Rect iconBox, Rgb color)
    {
        using var bmp = new SKBitmap(400, 800);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(Page.R, Page.G, Page.B));
            using var p = new SKPaint { Color = new SKColor(color.R, color.G, color.B), IsAntialias = true };
            c.DrawCircle((float)(iconBox.X + iconBox.Width / 2), (float)(iconBox.Y + iconBox.Height / 2), (float)(iconBox.Width / 2), p);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
