using Driftcheck.Mcp;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Tests;

public class FridaProbeTests
{
    // Density 1.75 (280 dpi): 1 dp = 1.75 px, like the emulator the agent was written against.
    const double PxToDp = 1 / 1.75;

    const string Dump = """
        {
          "metrics": { "density": 1.75, "fontScale": 1 },
          "views": [
            { "id": null, "cls": "com.facebook.react.views.view.ReactViewGroup", "bounds": [35, 1098, 685, 1196], "alpha": 1,
              "background": { "kind": "rn", "color": "#FF262B36", "radius": 35, "strokeWidth": 1.75, "strokeColor": "#FFE2E2E2" } },
            { "id": "co.app:id/title", "cls": "com.facebook.react.views.text.ReactTextView", "bounds": [280, 1127, 440, 1166], "alpha": 1,
              "text": { "text": "Go to Shop", "size": 31.5, "color": "#FFFFFFFF", "weight": 400, "family": "GeneralSansSemibold",
                        "letterSpacingPx": 3.15, "lineHeight": 61.25 } },
            { "id": null, "cls": "android.view.View", "bounds": [0, 0, 0, 10], "alpha": 1 }
          ]
        }
        """;

    [Fact]
    public void ParsesSizesInDpAndSp()
    {
        var result = FridaProbe.Parse(Dump, PxToDp);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Views!.Count); // The empty view is skipped.

        var box = result.Views[0];
        Assert.Equal(new Rect(20, 627.4, 371.4, 56), Round(box.Bounds));
        Assert.Equal(new Rgb(0x26, 0x2B, 0x36), box.Props.Box!.Background);
        Assert.Equal(20, box.Props.Box.CornerRadius);
        Assert.Equal(1, box.Props.Box.StrokeWidth);
        Assert.Equal(new Rgb(0xE2, 0xE2, 0xE2), box.Props.Box.StrokeColor);

        var text = result.Views[1].Props.Text!;
        Assert.Equal(18, text.SizeSp);
        Assert.Equal(35, text.LineHeightDp);
        Assert.Equal(0.1, text.LetterSpacingEm);
        Assert.Equal(new Rgb(255, 255, 255), text.Color);
        Assert.Equal(600, RuntimeFonts.Weight(text)); // From the family name, not the typeface's 400.
    }

    [Fact]
    public void ParsesSvgIconColorsWithoutFaintOnes()
    {
        const string json = """
            { "metrics": { "density": 1.75, "fontScale": 1 }, "views": [
              { "cls": "com.horcrux.svg.SvgView", "bounds": [629, 77, 671, 119], "alpha": 1,
                "svg": { "fills": ["#FF262B36", "#1AFFFFFF"], "strokes": ["#FF262B36", "#FFBD3D44"], "gradient": false, "viewBox": [20, 20] } },
              { "cls": "android.widget.ImageView", "bounds": [0, 0, 42, 42], "alpha": 1,
                "image": { "drawable": { "kind": "vector" }, "tint": "#FF9E56FF" } },
              { "cls": "android.widget.ImageView", "bounds": [50, 0, 92, 42], "alpha": 1,
                "image": { "drawable": { "kind": "bitmap" }, "tint": null } }
            ] }
            """;

        var views = FridaProbe.Parse(json, PxToDp).Views!;

        Assert.Equal([new Rgb(0x26, 0x2B, 0x36), new Rgb(0xBD, 0x3D, 0x44)], views[0].Props.Icon!.Colors);
        Assert.Equal(new Rect(359.4, 44, 24, 24), Round(views[0].Bounds));
        Assert.Equal([new Rgb(0x9E, 0x56, 0xFF)], views[1].Props.Icon!.Colors);
        Assert.Null(views[2].Props.Icon);
    }

    [Fact]
    public void ReportsTheAgentError()
    {
        var result = FridaProbe.Parse("""{"error": "frida-server is not running on the device."}""", PxToDp);

        Assert.Null(result.Views);
        Assert.Equal("frida-server is not running on the device.", result.Error);
    }

    [Fact]
    public void TransparentColorIsNoColor()
    {
        Assert.Null(FridaProbe.Color("#00000000"));
        Assert.Equal(new Rgb(0x26, 0x2B, 0x36), FridaProbe.Color("#80262B36"));
        Assert.Null(FridaProbe.Color("#262B36"));
    }

    [Fact]
    public void AttachesTextAndBoxOfViewsWithTheSameBounds()
    {
        var bounds = new Rect(20, 100, 200, 40);
        var views = new List<RuntimeView>
        {
            new(null, bounds, new RuntimeProps("ReactViewGroup", 1, null, new RuntimeBox(new Rgb(1, 2, 3), 12, null, null))),
            new(null, new Rect(20.4, 100.3, 199.8, 40.2), new RuntimeProps("ReactTextView", 1,
                new RuntimeText("Pay", 16, 400, "Inter-Bold", false, null, null, null), null)),
            new(null, new Rect(0, 0, 50, 50), new RuntimeProps("ImageView", 1, null, null)),
        };
        var elements = new List<ScreenElement>
        {
            new(null, "android.widget.TextView", "Pay", null, bounds),
            new("other", "android.view.View", null, null, new Rect(300, 300, 10, 10)),
        };

        var attached = FridaProbe.Attach(elements, views);

        var runtime = attached[0].Runtime!;
        Assert.Equal("ReactTextView", runtime.ClassName);
        Assert.Equal(16, runtime.Text!.SizeSp);
        Assert.Equal(12, runtime.Box!.CornerRadius);
        Assert.Null(attached[1].Runtime);
    }

    [Theory]
    [InlineData("Inter", "Inter-SemiBold", true)]
    [InlineData("Inter", "inter_bold", true)]
    [InlineData("Roboto", "sans-serif-medium", true)]
    [InlineData("General Sans", "GeneralSansSemibold", true)]
    [InlineData("Inter", "Lato-Regular", false)]
    [InlineData("Inter", "sans-serif", false)]
    public void ComparesFamiliesWithoutWeightWords(string figma, string app, bool same) =>
        Assert.Equal(same, RuntimeFonts.SameFamily(figma, app));

    [Theory]
    [InlineData("Inter-SemiBold", 600)]
    [InlineData("Inter-ExtraLight", 200)]
    [InlineData("sans-serif-medium", 500)]
    [InlineData("Inter-BoldItalic", 700)]
    [InlineData("Inter", null)]
    public void ReadsTheWeightFromTheFamilyName(string family, int? weight) =>
        Assert.Equal(weight, RuntimeFonts.WeightFromFamily(family));

    static Rect Round(Rect r) => new(Math.Round(r.X, 1), Math.Round(r.Y, 1), Math.Round(r.Width, 1), Math.Round(r.Height, 1));
}

[Collection(OutputDirectoryCollection.Name)]
public class RuntimeComparisonTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public RuntimeComparisonTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ReportsFontAndRadiusFromRuntimeProperties()
    {
        var (design, png) = Design();
        var capture = Capture(
            new RuntimeText("Bienvenido", 22, 400, "Lato-Regular", false, null, null, null),
            new RuntimeBox(new Rgb(0x19, 0x76, 0xD2), 4, null, null));

        var result = new ScreenComparer(new Settings()).Compare(design, png, capture, tolerance: 2, colorTolerance: 24,
            UiFramework.AndroidViews);

        Assert.Contains(result.Diffs, d => d is { Name: "Title \"Bienvenido\"", Property: "font size", Expected: "24", Actual: "22" });
        Assert.Contains(result.Diffs, d => d is { Property: "font weight", Expected: "700", Actual: "400" });
        Assert.Contains(result.Diffs, d => d is { Property: "font family", Expected: "Inter", Actual: "Lato-Regular" });
        Assert.Contains(result.Diffs, d => d is { Name: "Login Button", Property: "corner radius", Expected: "12", Actual: "4" });
        Assert.Contains(result.Diffs, d => d.Property == "font size" && d.Fix!.Contains("android:textSize=\"24sp\""));
    }

    [Fact]
    public void MatchingRuntimePropertiesAddNoDiffs()
    {
        var (design, png) = Design();
        var capture = Capture(
            new RuntimeText("Bienvenido", 24.2, 400, "Inter-Bold", false, null, null, null),
            new RuntimeBox(new Rgb(0x19, 0x76, 0xD2), 12, null, null));

        var result = new ScreenComparer(new Settings()).Compare(design, png, capture, tolerance: 2, colorTolerance: 24);

        Assert.Empty(result.Diffs);
    }

    (DesignFrame, string) Design()
    {
        var blue = new Rgb(0x19, 0x76, 0xD2);
        var elements = new List<DesignElement>
        {
            new("Title", "TEXT", new Rect(20, 100, 200, 30), new Rgb(0, 0, 0), "Bienvenido", 24, "Inter", 700, null),
            new("Login Button", "FRAME", new Rect(20, 600, 360, 48), blue, null, null, null, null, 12),
        };
        var png = Path.Combine(_dir, "design.png");
        Draw(png, blue);
        return (new DesignFrame("key", "1:2", "Login", new Rect(0, 0, 400, 800), elements), png);
    }

    ScreenCapture Capture(RuntimeText text, RuntimeBox box)
    {
        var png = Path.Combine(_dir, "device.png");
        Draw(png, new Rgb(0x19, 0x76, 0xD2));
        return new ScreenCapture("test", png, 400, 800, 1,
        [
            new ScreenElement("Title", "android.widget.TextView", "Bienvenido", null, new Rect(20, 100, 200, 30),
                new RuntimeProps("android.widget.TextView", 1, text, null)),
            new ScreenElement("LoginButton", "android.widget.Button", null, null, new Rect(20, 600, 360, 48),
                new RuntimeProps("android.widget.Button", 1, null, box)),
        ]);
    }

    static void Draw(string path, Rgb button)
    {
        using var bitmap = new SKBitmap(400, 800);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = new SKColor(button.R, button.G, button.B) };
            canvas.DrawRect(SKRect.Create(20, 600, 360, 48), paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
