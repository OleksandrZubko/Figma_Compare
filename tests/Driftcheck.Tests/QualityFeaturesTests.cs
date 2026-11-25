using Driftcheck.Mcp;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using Driftcheck.Mcp.Tools;
using SkiaSharp;

namespace Driftcheck.Tests;

[Collection(OutputDirectoryCollection.Name)]
public class QualityFeaturesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public QualityFeaturesTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void DynamicMaskIgnoresTextAndPixelsWhileCheckingLayout()
    {
        var mask = DynamicMask.Parse(@"balance, /\$\d+(\.\d{2})?/");
        Assert.NotNull(mask);

        var elements = new List<DesignElement>
        {
            new("Balance", "TEXT", new Rect(20, 100, 120, 28), new Rgb(0, 0, 0), "$10.00", 20, "Inter", 600, null),
            new("Price", "TEXT", new Rect(20, 160, 100, 24), new Rgb(0, 0, 0), "$49.99", 16, "Inter", 400, null),
        };
        var designPng = Path.Combine(_dir, "mask_design.png");
        var devicePng = Path.Combine(_dir, "mask_device.png");
        DrawBlank(designPng, 400, 800);
        DrawBlank(devicePng, 400, 800);

        var design = new DesignFrame("key", "1:1", "Wallet", new Rect(0, 0, 400, 800), elements);
        // Balance text changed + wider, Price changed text AND shifted down by 14 dp -> text ignored, y diff still caught!
        var capture = new ScreenCapture("test", devicePng, 400, 800, 1,
        [
            new ScreenElement("balance", "android.widget.TextView", "$9,999.99", null, new Rect(20, 100, 180, 28)),
            new ScreenElement("price", "android.widget.TextView", "$125.00", null, new Rect(20, 174, 130, 24)),
        ]);

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24,
            dynamicMask: mask, checkA11y: false);

        Assert.DoesNotContain(result.Diffs, d => d.Property == "text");
        Assert.DoesNotContain(result.Diffs, d => d.Name.StartsWith("Balance") && d.Property == "width");
        Assert.Contains(result.Diffs, d => d.Name.StartsWith("Price") && d.Property == "y");
    }

    [Fact]
    public void A11yAuditorDetectsContrastTapTargetMissingLabelKeyboardAndSafeArea()
    {
        var pngPath = Path.Combine(_dir, "a11y.png");
        DrawBlank(pngPath, 400, 800);
        using var bmp = SKBitmap.Decode(pngPath)!;

        // Light gray #AAAAAA on white #FFFFFF has contrast ~2.32:1 (< 4.5:1 WCAG AA).
        var capture = new ScreenCapture(
            "emu", pngPath, 400, 800, 1,
            [
                // Too small tap target (30x30 < 48x48) AND missing screen-reader label
                new ScreenElement("icon_btn", "android.widget.ImageButton", null, null, new Rect(340, 60, 30, 30), Clickable: true),
                // Low contrast text
                new ScreenElement("hint", "android.widget.TextView", "Low contrast", null, new Rect(20, 120, 200, 24),
                    new RuntimeProps("android.widget.TextView", 1,
                        new RuntimeText("Low contrast", 14, 400, "Inter", false, new Rgb(0xAA, 0xAA, 0xAA), null, null), null)),
                // Overlaps top status bar (safe top = 32 dp)
                new ScreenElement("top_title", "android.widget.TextView", "Header", null, new Rect(20, 10, 150, 24)),
                // Focused input occluded by 300dp keyboard (keyboard starts at y=500)
                new ScreenElement("email", "android.widget.EditText", "a@b.com", null, new Rect(20, 480, 360, 48), Clickable: true, Focused: true),
                // Submit button occluded by keyboard
                new ScreenElement("submit", "android.widget.Button", "Continue", null, new Rect(20, 600, 360, 48), Clickable: true),
            ],
            SafeArea: new Insets(0, 32, 0, 24),
            KeyboardHeightUnits: 300);

        var issues = A11yAuditor.Audit(capture, bmp);

        Assert.Contains(issues, i => i.Category == "tap target" && i.Element == "icon_btn");
        Assert.Contains(issues, i => i.Category == "missing label" && i.Element == "icon_btn");
        Assert.Contains(issues, i => i.Category == "contrast" && i.Element.StartsWith("hint"));
        Assert.Contains(issues, i => i.Category == "safe area" && i.Element.StartsWith("top_title"));
        Assert.Contains(issues, i => i.Category == "keyboard occlusion" && i.Element.StartsWith("email"));
        Assert.Contains(issues, i => i.Category == "keyboard occlusion" && i.Element.StartsWith("submit"));
    }

    [Fact]
    public void A11yAuditorChecksFullAtfRulesetAndWritesInteractiveHtmlReport()
    {
        var pngPath = Path.Combine(_dir, "a11y_atf.png");
        DrawBlank(pngPath, 400, 800);
        using var bmp = SKBitmap.Decode(pngPath)!;

        var capture = new ScreenCapture(
            "emu", pngPath, 400, 800, 1,
            [
                // EditableContentDescCheck
                new ScreenElement("name_input", "android.widget.EditText", "Alex", "Enter your name", new Rect(20, 60, 360, 48), Clickable: true),
                // RedundantDescriptionCheck ("Close button")
                new ScreenElement("close_btn", "android.widget.ImageButton", null, "Close button", new Rect(340, 120, 48, 48), Clickable: true),
                // DuplicateSpeakableTextCheck ("Delete" x2 at distinct positions)
                new ScreenElement("del_1", "android.widget.Button", "Delete", null, new Rect(20, 200, 120, 48), Clickable: true),
                new ScreenElement("del_2", "android.widget.Button", "Delete", null, new Rect(20, 260, 120, 48), Clickable: true),
                // DuplicateClickableBoundsCheck (same bounds as del_1)
                new ScreenElement("del_1_wrap", "android.widget.FrameLayout", null, "Delete wrapper", new Rect(20, 200, 120, 48), Clickable: true),
                // SmallTextSizeCheck (10 sp) + TextTruncationCheck (ellipsized)
                new ScreenElement("tiny", "android.widget.TextView", "Fine print…", null, new Rect(20, 340, 200, 20),
                    new RuntimeProps("android.widget.TextView", 1,
                        new RuntimeText("Fine print…", 10, 400, "Inter", false, new Rgb(0, 0, 0), null, null, Ellipsized: true), null)),
            ],
            RuntimeViews:
            [
                // ImageContrastCheck: faint #CCCCCC icon on #FFFFFF (~1.6:1 < 3:1)
                new RuntimeView("faint_icon", new Rect(20, 400, 24, 24),
                    new RuntimeProps("com.horcrux.svg.SvgView", 1, null, null, new RuntimeIcon([new Rgb(0xCC, 0xCC, 0xCC)], false))),
            ]);

        var issues = A11yAuditor.Audit(capture, bmp);

        Assert.Contains(issues, i => i.RuleId == "EditableContentDescCheck");
        Assert.Contains(issues, i => i.RuleId == "RedundantDescriptionCheck");
        Assert.Contains(issues, i => i.RuleId == "DuplicateSpeakableTextCheck");
        Assert.Contains(issues, i => i.RuleId == "DuplicateClickableBoundsCheck");
        Assert.Contains(issues, i => i.RuleId == "SmallTextSizeCheck");
        Assert.Contains(issues, i => i.RuleId == "TextTruncationCheck");
        Assert.Contains(issues, i => i.RuleId == "ImageContrastCheck");

        var report = A11yReport.Write(capture, bmp, issues, _dir);
        Assert.True(report.Score < 100);
        Assert.True(File.Exists(report.HtmlPath));
        Assert.True(File.Exists(report.AnnotatedPngPath));
        var html = File.ReadAllText(report.HtmlPath);
        Assert.Contains("EditableContentDescCheck", html);
        Assert.Contains("data:image/png;base64,", html);
    }

    [Fact]
    public void ParsesSystemInsetsAndKeyboardFromDumpsysWindow()
    {
        const string dumpsys = """
            InsetsSource type=statusBars frame=[0,0][1080,84] visible=true
            InsetsSource type=navigationBars frame=[0,2337][1080,2400] visible=true
            InsetsSource type=ime frame=[0,1500][1080,2400] visible=true
            """;

        var (safe, kb) = AdbClient.ParseSystemInsets(dumpsys, pxToDp: 1.0 / 3.0, screenHeightPx: 2400);

        Assert.NotNull(safe);
        Assert.Equal(28.0, safe.Value.Top);
        Assert.Equal(21.0, safe.Value.Bottom);
        Assert.Equal(300.0, kb);
    }

    [Fact]
    public void ScreenshotsMatchDetectsStableAndChangingFrames()
    {
        var p1 = Path.Combine(_dir, "s1.png");
        var p2 = Path.Combine(_dir, "s2.png");
        DrawBlank(p1, 100, 100);
        using (var bmp = new SKBitmap(100, 100))
        {
            using var c = new SKCanvas(bmp);
            c.Clear(SKColors.Black);
            using var d = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(p2, d.ToArray());
        }

        var b1 = File.ReadAllBytes(p1);
        var b2 = File.ReadAllBytes(p2);
        Assert.True(AdbClient.ScreenshotsMatch(b1, b1));
        Assert.False(AdbClient.ScreenshotsMatch(b1, b2));
    }

    [Fact]
    public void SupportsRtlConfigAndMatrixPreset()
    {
        var preset = DeviceConfig.FromPreset("rtl");
        Assert.True(preset.Rtl);
        Assert.Equal("RTL", preset.Summary());

        var variants = DriftcheckTools.ParseVariants("""[{"name":"arabic","locale":"ar-SA","rtl":true}]""",
            "https://www.figma.com/design/abc/App?node-id=1-1");
        Assert.Single(variants);
        Assert.True(variants[0].Config.Rtl);
        Assert.Equal("ar-SA", variants[0].Config.Locale);
    }

    [Fact]
    public void BaselineStoreSavesLoadsAndDetectsRegressionsWithoutFigma()
    {
        var store = new BaselineStore(new Settings());
        var pngV1 = Path.Combine(_dir, "v1.png");
        var pngV2 = Path.Combine(_dir, "v2.png");
        DrawBlank(pngV1, 400, 800);
        DrawBlank(pngV2, 400, 800);

        var capV1 = new ScreenCapture("emu", pngV1, 400, 800, 1,
        [
            new ScreenElement("title", "android.widget.TextView", "Checkout", null, new Rect(20, 100, 200, 30),
                new RuntimeProps("android.widget.TextView", 1, new RuntimeText("Checkout", 20, 600, "Inter", false, new Rgb(0, 0, 0), null, null), null)),
            new ScreenElement("subtitle", "android.widget.TextView", "Order summary", null, new Rect(20, 150, 200, 24)),
            new ScreenElement("pay_btn", "android.widget.Button", "Pay now", null, new Rect(20, 600, 360, 48), Clickable: true),
        ]);

        store.Save("checkout", capV1);
        var loaded = store.Load("checkout");
        var design = BaselineStore.ToDesignFrame(loaded);

        var comparer = new ScreenComparer(new Settings());
        // Same build -> 0 diffs
        var clean = comparer.Compare(design, loaded.Capture.ScreenshotPath, capV1, tolerance: 2, colorTolerance: 24, checkA11y: false);
        Assert.Empty(clean.Diffs);
        Assert.Equal(100, clean.Fidelity.Percent);

        // Next build: button shifted down by 16dp and font size shrank from 20 to 16
        var capV2 = new ScreenCapture("emu", pngV2, 400, 800, 1,
        [
            new ScreenElement("title", "android.widget.TextView", "Checkout", null, new Rect(20, 100, 200, 30),
                new RuntimeProps("android.widget.TextView", 1, new RuntimeText("Checkout", 16, 600, "Inter", false, new Rgb(0, 0, 0), null, null), null)),
            new ScreenElement("subtitle", "android.widget.TextView", "Order summary", null, new Rect(20, 150, 200, 24)),
            new ScreenElement("pay_btn", "android.widget.Button", "Pay now", null, new Rect(20, 616, 360, 48), Clickable: true),
        ]);
        var regressed = comparer.Compare(design, loaded.Capture.ScreenshotPath, capV2, tolerance: 2, colorTolerance: 24, checkA11y: false);
        Assert.Contains(regressed.Diffs, d => d is { Property: "font size", Expected: "20", Actual: "16" });
        Assert.Contains(regressed.Diffs, d => d is { Property: "y", Expected: "600", Actual: "616" });
    }

    static void DrawBlank(string path, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.White);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
