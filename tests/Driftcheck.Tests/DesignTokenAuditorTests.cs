using Driftcheck.Mcp;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using Driftcheck.Mcp.Qa;
using Xunit;

namespace Driftcheck.Tests;

public class DesignTokenAuditorTests
{
    static readonly Rgb Ink = new(0x26, 0x2B, 0x36);

    static DesignElement Text(Rgb? fill = null, double? size = null, string? family = null, double? weight = null, double? radius = null) =>
        new("t", "TEXT", new Rect(0, 100, 50, 20), fill, "x", size, family, weight, radius);

    static DesignFrame Frame(IEnumerable<DesignElement>? elements = null, IEnumerable<DesignElement>? shapes = null,
        Rgb? background = null, IEnumerable<DesignLayout>? layouts = null) =>
        new("f", "1:1", "Frame", new Rect(0, 0, 400, 800), (elements ?? []).ToList(), null, shapes?.ToList(), background, layouts?.ToList());

    static DesignPalette Palette(Rgb[]? colors = null, double[]? sizes = null, string[]? families = null,
        double[]? weights = null, double[]? radii = null, double[]? spacings = null) =>
        new(colors ?? [], sizes ?? [], families ?? [], weights ?? [], radii ?? [], spacings ?? []);

    static RuntimeView View(RuntimeProps props, Rect? bounds = null, string? id = null) =>
        new(id, bounds ?? new Rect(10, 100, 100, 40), props);

    static RuntimeProps Props(RuntimeText? text = null, RuntimeBox? box = null, RuntimeIcon? icon = null,
        Insets? padding = null, string cls = "android.widget.TextView") =>
        new(cls, 1, text, box, icon, Padding: padding);

    static RuntimeText Txt(string value = "Hello", double size = 16, double? weight = 400, string? family = "Inter", Rgb? color = null) =>
        new(value, size, weight, family, false, color, null, null);

    static ScreenCapture Capture(params RuntimeView[] views) =>
        new("test", "unused.png", 400, 800, 1, [], RuntimeViews: views);

    // ---------------------------------------------------------------- palette

    [Fact]
    public void Palette_collects_values_from_frames()
    {
        var frame = Frame(
            elements: [Text(Ink, 16, "Inter-SemiBold", 600, 8)],
            shapes: [new DesignElement("s", "RECT", new Rect(0, 0, 10, 10), new Rgb(255, 0, 0), null, null, null, null, 12,
                Stroke: new Rgb(0, 255, 0), IconColors: [new Rgb(0, 0, 255)])],
            background: new Rgb(250, 250, 250),
            layouts: [new DesignLayout("l", null, new Rect(0, 0, 10, 10), false, new Insets(16, 8, 16, 0), 12, false, [], [])]);
        var p = DesignTokenAuditor.BuildPalette([frame]);

        Assert.Equal(5, p.Colors.Count);
        Assert.Contains(Ink, p.Colors);
        Assert.Equal([16.0], p.FontSizes);
        Assert.Equal(["Inter-SemiBold"], p.FontFamilies);
        Assert.Equal([600.0], p.FontWeights);
        Assert.Equal([8.0, 12.0], p.Radii);
        Assert.Equal([8.0, 12.0, 16.0], p.Spacings);
    }

    [Fact]
    public void Palette_dedupes_colors_numbers_and_families()
    {
        var frame = Frame(elements:
        [
            Text(new Rgb(10, 10, 10), 16, "Inter-Bold", 700),
            Text(new Rgb(10, 10, 11), 16.005, "Inter", 700),
        ]);
        var p = DesignTokenAuditor.BuildPalette([frame, frame]);
        Assert.Single(p.Colors);
        Assert.Single(p.FontSizes);
        Assert.Equal(["Inter"], p.FontFamilies);
        Assert.Single(p.FontWeights);
    }

    [Fact]
    public void Palette_reads_flat_tokens()
    {
        var p = DesignTokenAuditor.BuildPalette([], """
            { "brand": "#262B36", "overlay": "#11223380", "font": "Inter" }
            """);
        Assert.Equal(["#112233", "#262B36"], p.Colors.Select(c => c.Hex));
        Assert.Equal(["Inter"], p.FontFamilies);
    }

    [Fact]
    public void Palette_reads_nested_value_type_tokens()
    {
        var p = DesignTokenAuditor.BuildPalette([], """
            {
              "color": { "primary": { "value": "#FF0000", "type": "color" } },
              "size": { "body": { "value": "16px", "type": "fontSizes" }, "h1": { "value": 32, "type": "fontSizes" } },
              "font": { "main": { "value": "Roboto", "type": "fontFamilies" } },
              "weight": { "bold": { "value": "700", "type": "fontWeights" } },
              "radius": { "m": { "value": "8px", "type": "borderRadius" } },
              "space": { "s": { "value": "4", "type": "spacing" }, "m": { "value": "8", "type": "dimension" } }
            }
            """);
        Assert.Equal(["#FF0000"], p.Colors.Select(c => c.Hex));
        Assert.Equal([16.0, 32.0], p.FontSizes);
        Assert.Equal(["Roboto"], p.FontFamilies);
        Assert.Equal([700.0], p.FontWeights);
        Assert.Equal([8.0], p.Radii);
        Assert.Equal([4.0, 8.0], p.Spacings);
    }

    [Fact]
    public void Palette_reads_w3c_dollar_tokens_with_inherited_group_type()
    {
        var p = DesignTokenAuditor.BuildPalette([], """
            {
              "colors": { "$type": "color", "bg": { "$value": "#FAFAFA" }, "fg": { "$value": "#101010FF" } },
              "radii": { "sm": { "$value": "4px", "$type": "borderRadius" } },
              "fontSizes": { "$type": "fontSizes", "md": { "$value": "14px" } }
            }
            """);
        Assert.Equal(["#101010", "#FAFAFA"], p.Colors.Select(c => c.Hex));
        Assert.Equal([4.0], p.Radii);
        Assert.Equal([14.0], p.FontSizes);
    }

    [Fact]
    public void Palette_merges_frames_and_tokens()
    {
        var p = DesignTokenAuditor.BuildPalette([Frame(elements: [Text(Ink, 16)])], """{ "x": "#FF0000" }""");
        Assert.Equal(2, p.Colors.Count);
    }

    // ---------------------------------------------------------------- checks

    [Fact]
    public void Color_off_palette_is_flagged_and_in_palette_is_not()
    {
        var palette = Palette(colors: [Ink]);
        var bad = DesignTokenAuditor.Audit(Capture(View(Props(Txt(color: new Rgb(0xAA, 0x00, 0x00))))), palette);
        var issue = Assert.Single(bad);
        Assert.Equal("token-color", issue.Category);
        Assert.Equal(QaSeverity.Warning, issue.Severity);
        Assert.Contains("#AA0000", issue.Title);
        Assert.Contains("#262B36", issue.Fix);

        var ok = DesignTokenAuditor.Audit(Capture(View(Props(Txt(color: new Rgb(0x26, 0x2B, 0x37))))), palette);
        Assert.Empty(ok);
    }

    [Fact]
    public void Background_stroke_and_icon_colors_are_checked()
    {
        var red = new Rgb(255, 0, 0);
        var green = new Rgb(0, 255, 0);
        var blue = new Rgb(0, 0, 255);
        var palette = Palette(colors: [Ink]);
        var capture = Capture(
            View(Props(box: new RuntimeBox(red, null, 1, green)), new Rect(0, 100, 50, 50)),
            View(Props(icon: new RuntimeIcon([blue], false), cls: "ImageView"), new Rect(0, 200, 24, 24)));
        var issues = DesignTokenAuditor.Audit(capture, palette);
        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.Equal("token-color", i.Category));
    }

    [Fact]
    public void Stroke_color_ignored_without_stroke_width()
    {
        var capture = Capture(View(Props(box: new RuntimeBox(Ink, null, 0, new Rgb(255, 0, 0)))));
        Assert.Empty(DesignTokenAuditor.Audit(capture, Palette(colors: [Ink])));
    }

    [Fact]
    public void Font_size_check()
    {
        var palette = Palette(sizes: [16]);
        Assert.Single(DesignTokenAuditor.Audit(Capture(View(Props(Txt(size: 13)))), palette), i => i.Category == "token-font-size");
        Assert.Empty(DesignTokenAuditor.Audit(Capture(View(Props(Txt(size: 16.4)))), palette));
    }

    [Fact]
    public void Font_family_check()
    {
        var palette = Palette(families: ["Inter"]);
        var bad = DesignTokenAuditor.Audit(Capture(View(Props(Txt(family: "Comic Sans")))), palette);
        Assert.Equal("token-font-family", Assert.Single(bad).Category);
        Assert.Empty(DesignTokenAuditor.Audit(Capture(View(Props(Txt(family: "Inter-SemiBold", weight: 400)))), palette));
    }

    [Fact]
    public void Font_weight_check_uses_family_name_weight()
    {
        var palette = Palette(weights: [400, 600]);
        Assert.Empty(DesignTokenAuditor.Audit(Capture(View(Props(Txt(family: "Inter-SemiBold", weight: 400)))), palette));
        var bad = DesignTokenAuditor.Audit(Capture(View(Props(Txt(family: "Inter", weight: 800)))), palette);
        Assert.Equal("token-font-weight", Assert.Single(bad).Category);
    }

    [Fact]
    public void Corner_radius_check_and_zero_ignored()
    {
        var palette = Palette(radii: [8]);
        var bad = DesignTokenAuditor.Audit(Capture(View(Props(box: new RuntimeBox(null, 5, null, null)))), palette);
        Assert.Equal("token-radius", Assert.Single(bad).Category);
        Assert.Empty(DesignTokenAuditor.Audit(Capture(View(Props(box: new RuntimeBox(null, 0, null, null)))), palette));
        Assert.Empty(DesignTokenAuditor.Audit(Capture(View(Props(box: new RuntimeBox(null, 8, null, null)))), palette));
    }

    [Fact]
    public void Padding_checked_against_spacing_tokens()
    {
        var palette = Palette(spacings: [8, 16]);
        var bad = DesignTokenAuditor.Audit(Capture(View(Props(padding: new Insets(16, 10, 16, 0)))), palette);
        var issue = Assert.Single(bad);
        Assert.Equal("token-spacing", issue.Category);
        Assert.Contains("10", issue.Title);
    }

    [Fact]
    public void Spacing_falls_back_to_grid_when_no_spacing_tokens()
    {
        var palette = Palette(colors: [Ink]);
        var bad = DesignTokenAuditor.Audit(Capture(View(Props(padding: new Insets(16, 10, 12, 0)))), palette, spacingGrid: 4);
        var issue = Assert.Single(bad);
        Assert.Equal(QaSeverity.Warning, issue.Severity);
        Assert.Contains("not on the 4 dp grid", issue.Title);
    }

    // ---------------------------------------------------------------- behaviour

    [Fact]
    public void Repeated_value_becomes_one_issue_with_count_and_examples()
    {
        var red = new Rgb(255, 0, 0);
        var views = Enumerable.Range(0, 5)
            .Select(i => View(Props(Txt(value: $"Item {i}", color: red)), new Rect(0, 100 + i * 50, 100, 40)))
            .ToArray();
        var issue = Assert.Single(DesignTokenAuditor.Audit(Capture(views), Palette(colors: [Ink])));
        Assert.Contains("5 time(s)", issue.Detail);
        Assert.Contains("Item 0", issue.Detail);
        Assert.Contains("Item 2", issue.Detail);
        Assert.DoesNotContain("Item 3", issue.Detail);
        Assert.Equal("Item 0", issue.Element);
        Assert.Equal(new Rect(0, 100, 100, 40), issue.Bounds);
    }

    [Fact]
    public void Same_view_in_runtime_views_and_elements_counts_once()
    {
        var props = Props(Txt(color: new Rgb(255, 0, 0)));
        var bounds = new Rect(10, 100, 100, 40);
        var capture = new ScreenCapture("test", "unused.png", 400, 800, 1,
            [new ScreenElement(null, "TextView", "Hello", null, bounds, props)],
            RuntimeViews: [new RuntimeView(null, bounds, props)]);
        var issue = Assert.Single(DesignTokenAuditor.Audit(capture, Palette(colors: [Ink])));
        Assert.Contains("1 time(s)", issue.Detail);
    }

    [Fact]
    public void Elements_without_runtime_view_are_audited()
    {
        var capture = new ScreenCapture("test", "unused.png", 400, 800, 1,
            [new ScreenElement(null, "TextView", "Hello", null, new Rect(0, 100, 50, 20), Props(Txt(size: 11)))]);
        Assert.Single(DesignTokenAuditor.Audit(capture, Palette(sizes: [16])));
    }

    [Fact]
    public void Almost_a_token_is_info()
    {
        var near = DesignTokenAuditor.Audit(Capture(View(Props(Txt(color: new Rgb(0x26, 0x2B, 0x36 + 8))))), Palette(colors: [Ink]));
        var issue = Assert.Single(near);
        Assert.Equal(QaSeverity.Info, issue.Severity);
        Assert.Contains("almost a token", issue.Detail);

        var far = DesignTokenAuditor.Audit(Capture(View(Props(Txt(color: new Rgb(0x26, 0x2B, 0x36 + 40))))), Palette(colors: [Ink]));
        Assert.Equal(QaSeverity.Warning, Assert.Single(far).Severity);
    }

    [Fact]
    public void Almost_a_token_for_numbers()
    {
        var issue = Assert.Single(DesignTokenAuditor.Audit(Capture(View(Props(Txt(size: 16.8)))), Palette(sizes: [16])));
        Assert.Equal(QaSeverity.Info, issue.Severity);
    }

    [Fact]
    public void Empty_palette_skips_checks_and_says_so()
    {
        var capture = Capture(View(Props(Txt(size: 11, weight: 900, family: "Weird", color: new Rgb(1, 2, 3)),
            new RuntimeBox(new Rgb(9, 9, 9), 5, null, null))));
        var empty = Palette();
        Assert.Empty(DesignTokenAuditor.Audit(capture, empty, spacingGrid: 4));

        var text = DesignTokenAuditor.Describe(empty);
        Assert.Contains("Skipped", text);
        Assert.Contains("colors", text);
        Assert.Contains("grid", text);
    }

    [Fact]
    public void Only_checks_with_tokens_run()
    {
        var capture = Capture(View(Props(Txt(size: 11, color: new Rgb(1, 2, 3)))));
        var issues = DesignTokenAuditor.Audit(capture, Palette(sizes: [16]));
        Assert.Equal("token-font-size", Assert.Single(issues).Category);
    }

    [Fact]
    public void System_ui_and_status_bar_are_ignored()
    {
        var bad = Props(Txt(size: 11, color: new Rgb(1, 2, 3)));
        var capture = Capture(
            View(bad with { ClassName = "com.android.systemui.StatusBar" }, new Rect(0, 300, 100, 20)),
            View(bad, new Rect(0, 0, 400, 24)));
        Assert.Empty(DesignTokenAuditor.Audit(capture, Palette(colors: [Ink], sizes: [16])));
    }

    [Fact]
    public void Transparent_views_are_ignored()
    {
        var props = Props(Txt(color: new Rgb(1, 2, 3))) with { Alpha = 0 };
        Assert.Empty(DesignTokenAuditor.Audit(Capture(View(props)), Palette(colors: [Ink])));
    }

    [Fact]
    public void Describe_lists_counts_and_values()
    {
        var text = DesignTokenAuditor.Describe(Palette(colors: [Ink], sizes: [16, 24], families: ["Inter"]));
        Assert.Contains("#262B36", text);
        Assert.Contains("16, 24", text);
        Assert.Contains("Inter", text);
    }

    [Fact]
    public void MulticolorArtworkIsNotCheckedAgainstTokens()
    {
        var palette = new DesignPalette([new Rgb(0x26, 0x2B, 0x36)], [], [], [], [], []);
        var flag = new RuntimeView(null, new Rect(300, 40, 38, 38), new RuntimeProps("com.horcrux.svg.SvgView", 1, null, null,
            new RuntimeIcon([new Rgb(0xBD, 0x3D, 0x44), new Rgb(0x19, 0x2F, 0x5D), new Rgb(255, 255, 255)], false)));
        var tinted = new RuntimeView(null, new Rect(20, 40, 24, 24), new RuntimeProps("com.horcrux.svg.SvgView", 1, null, null,
            new RuntimeIcon([new Rgb(0xE6, 0x1E, 0x4E)], false)));
        var capture = new ScreenCapture("test", "unused.png", 400, 800, 1, [], RuntimeViews: [flag, tinted]);

        var issue = Assert.Single(DesignTokenAuditor.Audit(capture, palette));
        Assert.Contains("#E61E4E", issue.Title);
    }
}
