using System.Globalization;
using System.Text;
using System.Text.Json;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;

namespace Driftcheck.Mcp.Qa;

/// <summary>The values the design system allows: everything the Figma frames (and an optional tokens file) use.</summary>
public sealed record DesignPalette(
    IReadOnlyList<Rgb> Colors,
    IReadOnlyList<double> FontSizes,
    IReadOnlyList<string> FontFamilies,
    IReadOnlyList<double> FontWeights,
    IReadOnlyList<double> Radii,
    IReadOnlyList<double> Spacings);

/// <summary>
/// "Design token lint": checks that the running app only uses colors, type and spacing that exist in the design
/// system, without needing the Figma frame of that particular screen.
/// </summary>
public static class DesignTokenAuditor
{
    const double NumberTolerance = 0.5;
    const double WeightTolerance = 50;
    const double StatusBarDp = 24;

    // ---------------------------------------------------------------- palette

    public static DesignPalette BuildPalette(IEnumerable<DesignFrame> frames, string? tokensJson = null)
    {
        var colors = new List<Rgb>();
        var sizes = new List<double>();
        var families = new Dictionary<string, string>(); // base name -> display name
        var weights = new List<double>();
        var radii = new List<double>();
        var spacings = new List<double>();

        foreach (var frame in frames)
        {
            if (frame.Background is { } bg) colors.Add(bg);
            foreach (var e in frame.Elements.Concat(frame.Shapes ?? []))
            {
                if (e.Fill is { } f) colors.Add(f);
                if (e.Stroke is { } s) colors.Add(s);
                if (e.IconColors is { } ic) colors.AddRange(ic);
                if (e.FontSize is > 0 and var fs) sizes.Add(fs);
                if (!string.IsNullOrWhiteSpace(e.FontFamily)) AddFamily(families, e.FontFamily);
                if (e.FontWeight is > 0 and var fw) weights.Add(fw);
                if (e.CornerRadius is > 0 and var cr) radii.Add(cr);
            }
            foreach (var l in frame.Layouts ?? [])
            {
                foreach (var p in new[] { l.Padding.Left, l.Padding.Top, l.Padding.Right, l.Padding.Bottom })
                    if (p > 0) spacings.Add(p);
                if (l.ItemSpacing > 0) spacings.Add(l.ItemSpacing);
            }
        }

        if (!string.IsNullOrWhiteSpace(tokensJson))
        {
            using var doc = JsonDocument.Parse(tokensJson, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            WalkTokens(doc.RootElement, [], null, colors, sizes, families, weights, radii, spacings);
        }

        return new DesignPalette(
            DedupeColors(colors),
            DedupeNumbers(sizes),
            families.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Value).ToList(),
            DedupeNumbers(weights),
            DedupeNumbers(radii),
            DedupeNumbers(spacings));
    }

    static void AddFamily(Dictionary<string, string> families, string name)
    {
        var key = RuntimeFonts.BaseName(name);
        if (key.Length == 0) return;
        // Keep the shortest display name of the group ("Inter" over "Inter-SemiBold").
        if (!families.TryGetValue(key, out var existing) || name.Length < existing.Length)
            families[key] = name;
    }

    static List<Rgb> DedupeColors(IEnumerable<Rgb> colors)
    {
        var result = new List<Rgb>();
        foreach (var c in colors.OrderBy(c => c.Hex, StringComparer.Ordinal))
            if (!result.Any(r => r.DistanceTo(c) <= 1)) result.Add(c);
        return result;
    }

    static List<double> DedupeNumbers(IEnumerable<double> values)
    {
        var result = new List<double>();
        foreach (var v in values.OrderBy(v => v))
            if (!result.Any(r => Math.Abs(r - v) <= 0.01)) result.Add(v);
        return result;
    }

    enum TokenKind { Unknown, Color, FontSize, FontFamily, FontWeight, Radius, Spacing, Dimension }

    static void WalkTokens(JsonElement node, List<string> path, TokenKind? inherited,
        List<Rgb> colors, List<double> sizes, Dictionary<string, string> families,
        List<double> weights, List<double> radii, List<double> spacings)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Name is "$type" or "type" or "$description" or "description" or "$extensions" or "extensions") continue;

            var childPath = new List<string>(path) { prop.Name };
            var value = prop.Value;

            if (value.ValueKind == JsonValueKind.Object)
            {
                if (TryGetLeafValue(value, out var leafValue))
                {
                    var type = TypeOf(value) ?? inherited;
                    AddToken(leafValue, type, childPath, colors, sizes, families, weights, radii, spacings);
                }
                else
                {
                    WalkTokens(value, childPath, TypeOf(value) ?? inherited, colors, sizes, families, weights, radii, spacings);
                }
            }
            else if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                AddToken(value, inherited, childPath, colors, sizes, families, weights, radii, spacings);
            }
        }
    }

    static bool TryGetLeafValue(JsonElement obj, out JsonElement value)
    {
        foreach (var name in new[] { "$value", "value" })
            if (obj.TryGetProperty(name, out value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return true;
        value = default;
        return false;
    }

    static TokenKind? TypeOf(JsonElement obj)
    {
        foreach (var name in new[] { "$type", "type" })
        {
            if (obj.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String)
            {
                var kind = ParseKind(t.GetString());
                if (kind != TokenKind.Unknown) return kind;
            }
        }
        return null;
    }

    static TokenKind ParseKind(string? type) => (type ?? "").Replace("-", "").Replace("_", "").ToLowerInvariant() switch
    {
        "color" => TokenKind.Color,
        "fontsizes" or "fontsize" => TokenKind.FontSize,
        "fontfamilies" or "fontfamily" => TokenKind.FontFamily,
        "fontweights" or "fontweight" => TokenKind.FontWeight,
        "borderradius" or "radius" or "radii" => TokenKind.Radius,
        "spacing" => TokenKind.Spacing,
        "dimension" or "sizing" or "number" => TokenKind.Dimension,
        _ => TokenKind.Unknown,
    };

    static TokenKind KindFromPath(IEnumerable<string> path)
    {
        var joined = string.Join("/", path).Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
        if (joined.Contains("fontsize") || joined.Contains("textsize")) return TokenKind.FontSize;
        if (joined.Contains("fontweight")) return TokenKind.FontWeight;
        if (joined.Contains("fontfamil")) return TokenKind.FontFamily;
        if (joined.Contains("radius") || joined.Contains("radii") || joined.Contains("corner")) return TokenKind.Radius;
        if (joined.Contains("spacing") || joined.Contains("space") || joined.Contains("gap") || joined.Contains("padding") || joined.Contains("margin"))
            return TokenKind.Spacing;
        return TokenKind.Unknown;
    }

    static void AddToken(JsonElement value, TokenKind? declared, List<string> path,
        List<Rgb> colors, List<double> sizes, Dictionary<string, string> families,
        List<double> weights, List<double> radii, List<double> spacings)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        var number = ParseNumber(value);

        // A hex string is a color whatever the declared type says; a declared color that isn't hex is ignored.
        if (text is not null && TryParseColor(text, out var color))
        {
            if (declared is null or TokenKind.Color or TokenKind.Unknown) colors.Add(color);
            return;
        }
        if (declared == TokenKind.Color) return;

        var kind = declared is null or TokenKind.Unknown ? KindFromPath(path) : declared.Value;
        if (kind == TokenKind.Dimension)
        {
            var hint = KindFromPath(path);
            kind = hint is TokenKind.FontSize or TokenKind.Radius ? hint : TokenKind.Spacing;
        }

        switch (kind)
        {
            case TokenKind.FontFamily:
                if (text is { Length: > 0 }) AddFamily(families, text);
                break;
            case TokenKind.FontWeight:
                if (number is > 0) weights.Add(number.Value);
                else if (RuntimeFonts.WeightFromFamily(text) is { } w) weights.Add(w);
                break;
            case TokenKind.FontSize when number is > 0: sizes.Add(number.Value); break;
            case TokenKind.Radius when number is > 0: radii.Add(number.Value); break;
            case TokenKind.Spacing when number is > 0: spacings.Add(number.Value); break;
            case TokenKind.Unknown:
                // Untyped non-numeric string in a flat file: assume a font family ("Inter").
                if (number is null && text is { Length: > 0 }) AddFamily(families, text);
                break;
        }
    }

    static double? ParseNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind != JsonValueKind.String) return null;
        var s = value.GetString()!.Trim().ToLowerInvariant();
        foreach (var unit in new[] { "px", "dp", "pt", "sp", "rem" })
            if (s.EndsWith(unit, StringComparison.Ordinal)) { s = s[..^unit.Length].Trim(); break; }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    static bool TryParseColor(string s, out Rgb color)
    {
        color = default;
        if (s.Length == 0 || s[0] != '#') return false;
        var hex = s[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        else if (hex.Length == 8) hex = hex[..6]; // drop alpha
        return hex.Length == 6 && Rgb.TryParse("#" + hex, out color);
    }

    // ---------------------------------------------------------------- audit

    sealed record Usage(string Label, Rect Bounds);

    sealed class Bucket(string display)
    {
        public string Display { get; } = display;
        public double Number;
        public Rgb Color;
        public int Count;
        public List<Usage> Examples { get; } = [];
        readonly HashSet<string> labels = [];

        public void Add(Usage u)
        {
            Count++;
            if (Examples.Count < 3 && labels.Add(u.Label)) Examples.Add(u);
            else if (Examples.Count == 0) Examples.Add(u);
        }
    }

    public static IReadOnlyList<QaIssue> Audit(ScreenCapture capture, DesignPalette palette, double colorTolerance = 6, int spacingGrid = 4)
    {
        var colorBuckets = new Dictionary<string, Bucket>();
        var sizeBuckets = new Dictionary<string, Bucket>();
        var familyBuckets = new Dictionary<string, Bucket>();
        var weightBuckets = new Dictionary<string, Bucket>();
        var radiusBuckets = new Dictionary<string, Bucket>();
        var spacingBuckets = new Dictionary<string, Bucket>();

        var checkColors = palette.Colors.Count > 0;
        var checkSizes = palette.FontSizes.Count > 0;
        var checkFamilies = palette.FontFamilies.Count > 0;
        var checkWeights = palette.FontWeights.Count > 0;
        var checkRadii = palette.Radii.Count > 0;

        void Color(Rgb? c, Usage u)
        {
            if (!checkColors || c is not { } rgb) return;
            if (palette.Colors.Any(p => p.DistanceTo(rgb) <= colorTolerance)) return;
            GetBucket(colorBuckets, rgb.Hex, rgb.Hex, b => b.Color = rgb).Add(u);
        }

        void Number(Dictionary<string, Bucket> buckets, double value, IReadOnlyList<double> allowed, Usage u)
        {
            if (allowed.Any(a => Math.Abs(a - value) <= NumberTolerance)) return;
            var key = value.ToString("0.#", CultureInfo.InvariantCulture);
            GetBucket(buckets, key, key, b => b.Number = value).Add(u);
        }

        void Visit(string className, string? id, string? text, Rect bounds, RuntimeProps p)
        {
            if (p.Alpha <= 0) return;
            var usage = new Usage(Label(className, id, text), bounds);

            if (p.Text is { } t)
            {
                Color(t.Color, usage);
                if (checkSizes && t.SizeSp > 0) Number(sizeBuckets, t.SizeSp, palette.FontSizes, usage);
                if (checkFamilies && !string.IsNullOrWhiteSpace(t.Family) &&
                    !palette.FontFamilies.Any(f => RuntimeFonts.SameFamily(f, t.Family)))
                    GetBucket(familyBuckets, RuntimeFonts.BaseName(t.Family), t.Family, _ => { }).Add(usage);
                if (checkWeights && RuntimeFonts.Weight(t) is { } w && w > 0 &&
                    !palette.FontWeights.Any(a => Math.Abs(a - w) <= WeightTolerance))
                {
                    var key = ((int)Math.Round(w)).ToString(CultureInfo.InvariantCulture);
                    GetBucket(weightBuckets, key, key, b => b.Number = w).Add(usage);
                }
            }

            if (p.Box is { } box)
            {
                Color(box.Background, usage);
                if (box.StrokeWidth is > 0) Color(box.StrokeColor, usage);
                if (checkRadii && box.CornerRadius is > 0 and var r) Number(radiusBuckets, r, palette.Radii, usage);
            }

            if (p.Icon is { } icon)
                foreach (var c in icon.Colors) Color(c, usage);

            if (p.Padding is { } pad)
            {
                foreach (var side in new[] { pad.Left, pad.Top, pad.Right, pad.Bottom })
                {
                    if (side <= 0) continue;
                    if (palette.Spacings.Count > 0) Number(spacingBuckets, side, palette.Spacings, usage);
                    else if (spacingGrid > 0 && !OnGrid(side, spacingGrid))
                    {
                        var key = side.ToString("0.#", CultureInfo.InvariantCulture);
                        GetBucket(spacingBuckets, key, key, b => b.Number = side).Add(usage);
                    }
                }
            }
        }

        var seen = new HashSet<string>();
        bool Fresh(string cls, Rect b) => seen.Add($"{cls}|{b.X:0.##}|{b.Y:0.##}|{b.Width:0.##}|{b.Height:0.##}");

        foreach (var v in capture.RuntimeViews ?? [])
        {
            if (IsSystemUi(v.Props.ClassName, v.Bounds) || !Fresh(v.Props.ClassName, v.Bounds)) continue;
            Visit(v.Props.ClassName, v.ResourceId, v.Props.Text?.Value, v.Bounds, v.Props);
        }
        foreach (var e in capture.Elements)
        {
            if (e.Runtime is not { } p) continue;
            var cls = string.IsNullOrEmpty(p.ClassName) ? e.ClassName : p.ClassName;
            if (IsSystemUi(cls, e.Bounds) || IsSystemUi(e.ClassName, e.Bounds) || !Fresh(cls, e.Bounds)) continue;
            Visit(cls, e.Id, p.Text?.Value ?? e.Text, e.Bounds, p);
        }

        var issues = new List<QaIssue>();

        foreach (var b in colorBuckets.Values.OrderByDescending(b => b.Count))
        {
            var (nearest, dist) = palette.Colors.Select(c => (c, d: c.DistanceTo(b.Color))).OrderBy(x => x.d).First();
            var info = dist <= colorTolerance * 2;
            issues.Add(Make("token-color", info, $"Color {b.Display} is not in the design system", b,
                $"nearest token: {nearest.Hex} (distance {dist.ToString("0.0", CultureInfo.InvariantCulture)})",
                $"Use the token {nearest.Hex}"));
        }

        AddNumberIssues(issues, sizeBuckets, palette.FontSizes, "token-font-size", "Font size", "sp", NumberTolerance * 2);
        AddNumberIssues(issues, radiusBuckets, palette.Radii, "token-radius", "Corner radius", "dp", NumberTolerance * 2);
        AddNumberIssues(issues, weightBuckets, palette.FontWeights, "token-font-weight", "Font weight", "", WeightTolerance * 2);

        foreach (var b in familyBuckets.Values.OrderByDescending(b => b.Count))
            issues.Add(Make("token-font-family", false, $"Font family {b.Display} is not in the design system", b,
                $"design system families: {string.Join(", ", palette.FontFamilies)}",
                $"Use one of: {string.Join(", ", palette.FontFamilies)}"));

        if (palette.Spacings.Count > 0)
            AddNumberIssues(issues, spacingBuckets, palette.Spacings, "token-spacing", "Spacing", "dp", NumberTolerance * 2);
        else
            foreach (var b in spacingBuckets.Values.OrderByDescending(b => b.Count))
            {
                var near = Math.Max(spacingGrid, Math.Round(b.Number / spacingGrid) * spacingGrid);
                issues.Add(Make("token-spacing", false, $"Spacing {b.Display}dp is not on the {spacingGrid} dp grid", b,
                    $"nearest grid value: {QaReport.F(near)}dp", $"Use {QaReport.F(near)}dp"));
            }

        return issues;
    }

    static void AddNumberIssues(List<QaIssue> issues, Dictionary<string, Bucket> buckets, IReadOnlyList<double> allowed,
        string category, string what, string unit, double almost)
    {
        foreach (var b in buckets.Values.OrderByDescending(b => b.Count))
        {
            var nearest = allowed.OrderBy(a => Math.Abs(a - b.Number)).First();
            var diff = Math.Abs(nearest - b.Number);
            issues.Add(Make(category, diff <= almost, $"{what} {b.Display}{unit} is not in the design system", b,
                $"nearest token: {QaReport.F(nearest)}{unit}", $"Use the token {QaReport.F(nearest)}{unit}"));
        }
    }

    static QaIssue Make(string category, bool info, string title, Bucket b, string nearestText, string fix)
    {
        var examples = string.Join(", ", b.Examples.Select(e => $"\"{e.Label}\""));
        var detail = $"Used {b.Count} time(s), e.g. {examples}. {nearestText}";
        if (info) detail += " (almost a token)";
        return new QaIssue(category, info ? QaSeverity.Info : QaSeverity.Warning, title, detail,
            b.Examples[0].Label, b.Examples[0].Bounds, fix);
    }

    static Bucket GetBucket(Dictionary<string, Bucket> dict, string key, string display, Action<Bucket> init)
    {
        if (!dict.TryGetValue(key, out var b))
        {
            b = new Bucket(display);
            init(b);
            dict[key] = b;
        }
        return b;
    }

    static bool OnGrid(double v, int grid)
    {
        var r = v % grid;
        return Math.Min(r, grid - r) <= 0.25;
    }

    static string Label(string className, string? id, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) return text.Length > 40 ? text[..40] + "…" : text;
        var cls = className.Contains('.') ? className[(className.LastIndexOf('.') + 1)..] : className;
        return string.IsNullOrWhiteSpace(id) ? cls : $"{cls} {id}";
    }

    static bool IsSystemUi(string className, Rect b) =>
        className.StartsWith("com.android.systemui", StringComparison.Ordinal) ||
        (b.Y + b.Height <= StatusBarDp && b.Height > 0);

    // ---------------------------------------------------------------- describe

    public static string Describe(DesignPalette palette)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Token | Count | Values |");
        sb.AppendLine("|---|---|---|");
        Row(sb, "Colors", palette.Colors.Count, string.Join(", ", palette.Colors.Select(c => c.Hex)));
        Row(sb, "Font sizes", palette.FontSizes.Count, string.Join(", ", palette.FontSizes.Select(QaReport.F)));
        Row(sb, "Font families", palette.FontFamilies.Count, string.Join(", ", palette.FontFamilies));
        Row(sb, "Font weights", palette.FontWeights.Count, string.Join(", ", palette.FontWeights.Select(QaReport.F)));
        Row(sb, "Radii", palette.Radii.Count, string.Join(", ", palette.Radii.Select(QaReport.F)));
        Row(sb, "Spacings", palette.Spacings.Count, string.Join(", ", palette.Spacings.Select(QaReport.F)));

        var skipped = new List<string>();
        if (palette.Colors.Count == 0) skipped.Add("colors");
        if (palette.FontSizes.Count == 0) skipped.Add("font sizes");
        if (palette.FontFamilies.Count == 0) skipped.Add("font families");
        if (palette.FontWeights.Count == 0) skipped.Add("font weights");
        if (palette.Radii.Count == 0) skipped.Add("corner radii");
        if (skipped.Count > 0)
            sb.AppendLine().AppendLine($"Skipped (no tokens in the palette): {string.Join(", ", skipped)}.");
        if (palette.Spacings.Count == 0)
            sb.AppendLine().AppendLine("No spacing tokens: padding is checked against the dp grid instead.");
        return sb.ToString();
    }

    static void Row(StringBuilder sb, string name, int count, string values) =>
        sb.AppendLine($"| {name} | {count} | {(count == 0 ? "(none, check skipped)" : QaReport.Cell(values))} |");
}
