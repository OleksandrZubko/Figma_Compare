using Driftcheck.Mcp;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Tools;
using SkiaSharp;

namespace Driftcheck.Tests;

[Collection(OutputDirectoryCollection.Name)]
public class ScrollAndBatchTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public ScrollAndBatchTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ScrollStitcherDetectsFixedBandsScrollDeltaAndStitchesFrames()
    {
        // Build a synthetic 400x1200 tall screen with a 40px fixed header, 40px fixed footer,
        // and distinctive colored bands in the scrollable 1120px content area.
        const int width = 200, viewH = 400, headerH = 40, footerH = 40, scrollDelta = 160;
        var f0Path = Path.Combine(_dir, "scroll0.png");
        var f1Path = Path.Combine(_dir, "scroll1.png");
        var f2Path = Path.Combine(_dir, "scroll2.png"); // Same as f1 -> end of scroll.

        DrawScrollFrame(f0Path, width, viewH, headerH, footerH, scrollOffset: 0);
        DrawScrollFrame(f1Path, width, viewH, headerH, footerH, scrollOffset: scrollDelta);
        DrawScrollFrame(f2Path, width, viewH, headerH, footerH, scrollOffset: scrollDelta);

        var cap0 = new ScreenCapture("emu", f0Path, width, viewH, 1,
        [
            new ScreenElement("header", "TextView", "Title", null, new Rect(10, 10, 100, 20)),
            new ScreenElement("item1", "TextView", "Row 1", null, new Rect(10, 80, 120, 24)),
            new ScreenElement("item2", "TextView", "Row 2", null, new Rect(10, 240, 120, 24)),
            new ScreenElement("footer", "View", "Tab", null, new Rect(10, 370, 100, 20)),
        ]);
        var cap1 = new ScreenCapture("emu", f1Path, width, viewH, 1,
        [
            new ScreenElement("header", "TextView", "Title", null, new Rect(10, 10, 100, 20)),
            new ScreenElement("item2", "TextView", "Row 2", null, new Rect(10, 80, 120, 24)),
            new ScreenElement("item3", "TextView", "Row 3", null, new Rect(10, 240, 120, 24)),
            new ScreenElement("footer", "View", "Tab", null, new Rect(10, 370, 100, 20)),
        ]);

        using var bmp0 = SKBitmap.Decode(f0Path)!;
        using var bmp1 = SKBitmap.Decode(f1Path)!;
        using var bmp2 = SKBitmap.Decode(f2Path)!;

        var (topFixed, bottomFixed) = ScrollStitcher.FindFixedBands(bmp0, bmp1);
        Assert.InRange(topFixed, headerH - 2, headerH + 2);
        Assert.InRange(bottomFixed, footerH - 2, footerH + 2);

        var delta = ScrollStitcher.EstimateScrollDeltaPx(cap0, bmp0, cap1, bmp1, topFixed, bottomFixed);
        Assert.InRange(delta, scrollDelta - 2, scrollDelta + 2);

        // Identical next frame -> delta 0 (end of scroll).
        var endDelta = ScrollStitcher.EstimateScrollDeltaPx(cap1, bmp1, cap1 with { ScreenshotPath = f2Path }, bmp2, topFixed, bottomFixed);
        Assert.Equal(0, endDelta);

        var stitchedPath = Path.Combine(_dir, "stitched.png");
        var stitched = ScrollStitcher.Stitch([new(cap0, 0), new(cap1, delta)], stitchedPath, topFixed, bottomFixed);

        Assert.Equal(viewH + delta, stitched.HeightPx);
        Assert.True(File.Exists(stitchedPath));
        // Deduplicated: header (1), item1 (1), item2 (1), item3 (1), footer (1) = 5 elements.
        Assert.Equal(5, stitched.Elements.Count);
        Assert.Contains(stitched.Elements, e => e.Id == "item3" && Math.Abs(e.Bounds.Y - (240 + delta)) <= 2);
    }

    [Fact]
    public void ScrollStitcherHandlesStickyHeaderFloatingFabAndBottomSheet()
    {
        // 1. Scrollable bottom sheet: top 220px (55% of 400px height) is dimmed backdrop + sheet header,
        //    bottom 40px is a sticky CTA button, middle 140px scrolls by 80px.
        const int width = 200, viewH = 400, sheetTop = 220, stickyCta = 40, sheetDelta = 80;
        var bs0 = Path.Combine(_dir, "bs0.png");
        var bs1 = Path.Combine(_dir, "bs1.png");
        DrawScrollFrame(bs0, width, viewH, sheetTop, stickyCta, scrollOffset: 0);
        DrawScrollFrame(bs1, width, viewH, sheetTop, stickyCta, scrollOffset: sheetDelta);

        using var bsBmp0 = SKBitmap.Decode(bs0)!;
        using var bsBmp1 = SKBitmap.Decode(bs1)!;

        var (bsTop, bsBottom) = ScrollStitcher.FindFixedBands(bsBmp0, bsBmp1);
        Assert.InRange(bsTop, sheetTop - 2, sheetTop + 2);
        Assert.InRange(bsBottom, stickyCta - 2, stickyCta + 2);

        var bsCap0 = new ScreenCapture("emu", bs0, width, viewH, 1,
            [new ScreenElement("cta", "Button", "Apply", null, new Rect(16, 368, 168, 24))]);
        var bsCap1 = new ScreenCapture("emu", bs1, width, viewH, 1,
            [new ScreenElement("cta", "Button", "Apply", null, new Rect(16, 368, 168, 24))]);

        var measuredBsDelta = ScrollStitcher.EstimateScrollDeltaPx(bsCap0, bsBmp0, bsCap1, bsBmp1, bsTop, bsBottom);
        Assert.InRange(measuredBsDelta, sheetDelta - 2, sheetDelta + 2);

        // 2. Floating sticky button (FAB) inside scrollable region: detected and deduplicated.
        const int fabHeader = 40, fabFooter = 20, fabDelta = 120;
        var fabBox = new Rect(140, 290, 44, 44);
        var f0 = Path.Combine(_dir, "fab0.png");
        var f1 = Path.Combine(_dir, "fab1.png");
        DrawScrollFrameWithFab(f0, width, viewH, fabHeader, fabFooter, 0, fabBox);
        DrawScrollFrameWithFab(f1, width, viewH, fabHeader, fabFooter, fabDelta, fabBox);

        var fabCap0 = new ScreenCapture("emu", f0, width, viewH, 1,
        [
            new ScreenElement("row1", "TextView", "Row 1", null, new Rect(10, 80, 100, 24)),
            new ScreenElement("fab", "ImageButton", null, "Add", fabBox),
        ]);
        var fabCap1 = new ScreenCapture("emu", f1, width, viewH, 1,
        [
            new ScreenElement("row2", "TextView", "Row 2", null, new Rect(10, 200, 100, 24)),
            new ScreenElement("fab", "ImageButton", null, "Add", fabBox),
        ]);

        using var fabBmp0 = SKBitmap.Decode(f0)!;
        using var fabBmp1 = SKBitmap.Decode(f1)!;

        var (tFix, bFix) = ScrollStitcher.FindFixedBands(fabBmp0, fabBmp1);
        var overlays = ScrollStitcher.FindStickyOverlaysPx(fabCap0, fabBmp0, fabCap1, fabBmp1, tFix, bFix);
        Assert.NotEmpty(overlays);
        Assert.Contains(overlays, o => Math.Abs(o.X - fabBox.X) <= 2 && Math.Abs(o.Y - fabBox.Y) <= 2);

        var measuredFabDelta = ScrollStitcher.EstimateScrollDeltaPx(fabCap0, fabBmp0, fabCap1, fabBmp1, tFix, bFix, overlays);
        Assert.InRange(measuredFabDelta, fabDelta - 2, fabDelta + 2);

        var stitchedFab = ScrollStitcher.Stitch(
            [new(fabCap0, 0, tFix, bFix, overlays), new(fabCap1, measuredFabDelta, tFix, bFix, overlays)],
            Path.Combine(_dir, "fab_stitched.png"));
        // FAB appears only once (at the bottom), not duplicated in every scrolled slice.
        Assert.Single(stitchedFab.Elements, e => e.Id == "fab");

        // 3. ParseScrollableBoundsPx targets the bottom sheet's scrollable container.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hierarchy rotation="0">
              <node class="android.widget.FrameLayout" scrollable="false" bounds="[0,0][1080,2400]">
                <node class="androidx.recyclerview.widget.RecyclerView" scrollable="true" bounds="[0,1100][1080,2200]" />
              </node>
            </hierarchy>
            """;
        var scrollBounds = AdbClient.ParseScrollableBoundsPx(xml);
        Assert.Equal(new Rect(0, 1100, 1080, 1100), scrollBounds);
    }

    [Fact]
    public void ParsesStatesAndMatrixVariants()
    {
        const string statesJson = """
            [
              { "name": "empty", "figmaUrl": "https://www.figma.com/design/abc/App?node-id=1-2", "actions": [{ "type": "deeplink", "value": "myapp://empty" }] },
              { "name": "focused", "figmaUrl": "https://www.figma.com/design/abc/App?node-id=1-3", "scroll": true,
                "actions": [{ "type": "tap_text", "value": "Email" }, { "type": "input_text", "value": "test@example.com" }] }
            ]
            """;

        var states = DriftcheckTools.ParseStates(statesJson);
        Assert.Equal(2, states.Count);
        Assert.Equal("empty", states[0].Name);
        Assert.Single(states[0].Actions);
        Assert.Equal("deeplink", states[0].Actions[0].Type);
        Assert.True(states[1].Scroll);
        Assert.Equal(2, states[1].Actions.Count);

        const string variantsJson = """
            [
              { "name": "narrow", "preset": "narrow" },
              { "name": "large-font", "fontScale": 1.3 },
              { "name": "dark", "theme": "dark", "figmaUrl": "https://www.figma.com/design/abc/App?node-id=2-1" },
              { "name": "german", "locale": "de-DE" }
            ]
            """;
        var variants = DriftcheckTools.ParseVariants(variantsJson, "https://www.figma.com/design/abc/App?node-id=1-1");
        Assert.Equal(4, variants.Count);
        Assert.Equal(360, variants[0].Config.WidthDp);
        Assert.Equal(1.3, variants[1].Config.FontScale);
        Assert.Equal("dark", variants[2].Config.Theme);
        Assert.Equal("https://www.figma.com/design/abc/App?node-id=2-1", variants[2].FigmaUrl);
        Assert.Equal("de-DE", variants[3].Config.Locale);
    }

    [Fact]
    public void BatchReportWritesMarkdownAndHtmlSummary()
    {
        var fakeResult = new ComparisonResult(
            "report", "sbs.png", "diff.png", 3,
            [new ElementDiff("Title", "text overflow", "no truncation", "ellipsized")],
            Path.Combine(_dir, "run1", "report.html"),
            new FidelityScore(2, 3, null),
            [new ElementResult("Title", null, ElementStatus.Differs, default, default, [])],
            0.05, false, []);

        var (md, htmlPath) = BatchReport.Write("Matrix Run", _dir,
        [
            new BatchReport.Entry("large-font", "emulator-5554", "font 1.3x", fakeResult),
            new BatchReport.Entry("dark", "emulator-5554", "dark", null, "device offline"),
        ]);

        Assert.Contains("Matrix Run", md);
        Assert.Contains("Overflows/truncations: **1**", md);
        Assert.Contains("device offline", md);
        Assert.True(File.Exists(htmlPath));
        var html = File.ReadAllText(htmlPath);
        Assert.Contains("large-font", html);
        Assert.Contains("device offline", html);
    }

    static void DrawScrollFrame(string path, int width, int height, int headerH, int footerH, int scrollOffset)
    {
        using var bmp = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);

            // Scrollable content stripes (unique color every 40px so template matching is unambiguous).
            SKColor[] stripes =
            [
                new(220, 60, 60), new(60, 180, 75), new(65, 105, 225), new(245, 130, 48),
                new(145, 30, 180), new(70, 240, 240), new(240, 50, 230), new(210, 245, 60),
                new(250, 190, 212), new(0, 128, 128), new(220, 190, 255), new(170, 110, 40),
                new(255, 250, 200), new(128, 0, 0), new(170, 255, 195), new(128, 128, 0),
            ];
            for (var i = 0; i < stripes.Length; i++)
            {
                var y = headerH + i * 40 - scrollOffset;
                using var p = new SKPaint { Color = stripes[i] };
                canvas.DrawRect(0, y, width, 40, p);
            }

            // Fixed header & footer drawn on top.
            using var headerPaint = new SKPaint { Color = new SKColor(30, 30, 40) };
            canvas.DrawRect(0, 0, width, headerH, headerPaint);
            using var footerPaint = new SKPaint { Color = new SKColor(235, 235, 240) };
            canvas.DrawRect(0, height - footerH, width, footerH, footerPaint);
        }

        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    static void DrawScrollFrameWithFab(string path, int width, int height, int headerH, int footerH, int scrollOffset, Rect fab)
    {
        DrawScrollFrame(path, width, height, headerH, footerH, scrollOffset);
        using var bmp = SKBitmap.Decode(path)!;
        using (var canvas = new SKCanvas(bmp))
        {
            using var p = new SKPaint { Color = new SKColor(25, 118, 210), IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Create((float)fab.X, (float)fab.Y, (float)fab.Width, (float)fab.Height), 22, 22, p);
            using var plus = new SKPaint { Color = SKColors.White, StrokeWidth = 4 };
            var cx = (float)(fab.X + fab.Width / 2);
            var cy = (float)(fab.Y + fab.Height / 2);
            canvas.DrawLine(cx - 10, cy, cx + 10, cy, plus);
            canvas.DrawLine(cx, cy - 10, cx, cy + 10, plus);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
