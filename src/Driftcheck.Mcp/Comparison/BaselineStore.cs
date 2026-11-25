using System.Text.Json;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;

namespace Driftcheck.Mcp.Comparison;

/// <summary>
/// Saves and loads local screen baselines (screenshot + element tree + Frida runtime properties) for
/// visual regression testing without Figma (<c>save_baseline</c> / <c>compare_baseline</c>).
/// </summary>
public sealed class BaselineStore(Settings settings)
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed record SavedBaseline(
        string Name,
        DateTime SavedAtUtc,
        ScreenCapture Capture);

    public string Save(string name, ScreenCapture capture)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Baseline name is required.", nameof(name));

        var dir = BaselineDir(name);
        Directory.CreateDirectory(dir);
        var pngPath = Path.Combine(dir, "baseline.png");
        File.Copy(capture.ScreenshotPath, pngPath, overwrite: true);

        var stored = new SavedBaseline(name.Trim(), DateTime.UtcNow, capture with { ScreenshotPath = pngPath });
        var jsonPath = Path.Combine(dir, "baseline.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(stored, JsonOptions));
        return dir;
    }

    public SavedBaseline Load(string name)
    {
        var dir = BaselineDir(name);
        var jsonPath = Path.Combine(dir, "baseline.json");
        var pngPath = Path.Combine(dir, "baseline.png");
        if (!File.Exists(jsonPath) || !File.Exists(pngPath))
            throw new InvalidOperationException($"Baseline '{name}' not found. Run save_baseline first.");

        var loaded = JsonSerializer.Deserialize<SavedBaseline>(File.ReadAllText(jsonPath), JsonOptions)
            ?? throw new InvalidOperationException($"Baseline '{name}' is corrupted.");
        return loaded with { Capture = loaded.Capture with { ScreenshotPath = pngPath } };
    }

    /// <summary>
    /// Converts a saved baseline capture into a <see cref="DesignFrame"/> so the full <see cref="ScreenComparer"/>
    /// pipeline (element matching, Frida typography/effects, dynamic masking, A11y audit, diff.png, report.html)
    /// runs identically against a previous build snapshot.
    /// </summary>
    public static DesignFrame ToDesignFrame(SavedBaseline baseline)
    {
        var cap = baseline.Capture;
        var elements = new List<DesignElement>();
        var index = 0;

        foreach (var e in cap.Elements)
        {
            index++;
            var text = e.Text ?? e.ContentDescription;
            var isText = !string.IsNullOrWhiteSpace(text);
            var name = e.Id ?? (isText ? $"BaselineText{index}" : $"BaselineView{index}");
            var rt = e.Runtime;
            var t = rt?.Text;
            var box = rt?.Box;

            elements.Add(new DesignElement(
                Name: name,
                Type: isText ? "TEXT" : "FRAME",
                Bounds: e.Bounds,
                Fill: isText ? t?.Color : box?.Background,
                Text: text,
                FontSize: t?.SizeSp,
                FontFamily: t?.Family,
                FontWeight: t is not null ? RuntimeFonts.Weight(t) : null,
                CornerRadius: box?.CornerRadius,
                NodeId: $"baseline:{index}",
                Stroke: box?.StrokeColor,
                LineHeight: t?.LineHeightDp,
                LetterSpacing: t?.LetterSpacingEm is { } ls && t.SizeSp > 0 ? Math.Round(ls * t.SizeSp, 2) : null,
                StrokeWeight: box?.StrokeWidth,
                Shadow: rt?.Shadow ?? false,
                Opacity: rt?.Alpha ?? 1));
        }

        return new DesignFrame(
            FileKey: "baseline",
            NodeId: Sanitize(baseline.Name),
            Name: $"Baseline: {baseline.Name}",
            Bounds: new Rect(0, 0, cap.WidthUnits, cap.HeightUnits),
            Elements: elements,
            CachedAt: baseline.SavedAtUtc.ToLocalTime());
    }

    string BaselineDir(string name) =>
        Path.Combine(settings.OutputDirectory, "baselines", Sanitize(name));

    static string Sanitize(string s) =>
        string.Concat(s.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
