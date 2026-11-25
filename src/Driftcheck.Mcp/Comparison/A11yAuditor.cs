using System.Globalization;
using System.Text.RegularExpressions;
using Driftcheck.Mcp.Devices;
using SkiaSharp;

namespace Driftcheck.Mcp.Comparison;

public enum A11ySeverity { Error, Warning }

/// <summary>An accessibility, keyboard occlusion, or safe-area issue found on the captured screen.</summary>
public sealed record A11yIssue(
    string Category,
    string Element,
    string Detail,
    Rect Bounds,
    A11ySeverity Severity = A11ySeverity.Error,
    string RuleId = "",
    string Wcag = "",
    string Fix = "");

/// <summary>
/// Full mobile accessibility auditor implementing Google Accessibility Test Framework (ATF) and WCAG 2.1 AA rules
/// on any Android or iOS screen without modifying app source code:
/// TouchTargetSize, TextContrast (Frida + pixel fallback), ImageContrast (non-text 3:1), SpeakableTextPresent,
/// DuplicateSpeakableText, DuplicateClickableBounds, EditableContentDesc, RedundantDescription, SmallTextSize,
/// TextTruncation, KeyboardOcclusion, and SafeAreaOverlap.
/// </summary>
public static partial class A11yAuditor
{
    static readonly string[] RedundantRoleWords =
    [
        "button", "btn", "icon", "link", "checkbox", "switch", "tab",
        "кнопка", "іконка", "посилання", "перемикач",
    ];

    public static IReadOnlyList<A11yIssue> Audit(ScreenCapture capture, SKBitmap screenshot)
    {
        var issues = new List<A11yIssue>();
        var minTarget = capture.Platform == DevicePlatform.Ios ? 44.0 : 48.0;
        var u = capture.Unit;
        var pixels = screenshot.Pixels;
        int imgW = screenshot.Width, imgH = screenshot.Height;
        var pxPerUnit = capture.PixelsPerUnit;

        var clickableElements = new List<(ScreenElement Element, string Label, string? Speakable)>();

        foreach (var e in capture.Elements)
        {
            var label = Label(e);
            var speakable = SpeakableText(e, capture.Elements);

            if (e.Clickable && e.Enabled)
                clickableElements.Add((e, label, speakable));

            // 1. TouchTargetSizeCheck (WCAG 2.5.5 / 2.5.8, Google ATF)
            if (e.Clickable && e.Enabled && (e.Bounds.Width < minTarget - 1 || e.Bounds.Height < minTarget - 1))
            {
                var sev = e.Bounds.Width < minTarget - 12 || e.Bounds.Height < minTarget - 12
                    ? A11ySeverity.Error
                    : A11ySeverity.Warning;
                issues.Add(new A11yIssue(
                    "tap target", label,
                    $"{F(e.Bounds.Width)}×{F(e.Bounds.Height)} {u} is smaller than the {F(minTarget)}×{F(minTarget)} {u} minimum",
                    e.Bounds, sev, "TouchTargetSizeCheck", "WCAG 2.5.8 / ATF",
                    $"Increase touch area or padding to at least {F(minTarget)}×{F(minTarget)} {u}"));
            }

            // 2. SpeakableTextPresentCheck (WCAG 1.1.1 / 4.1.2, Google ATF)
            if (e.Clickable && e.Enabled && string.IsNullOrWhiteSpace(speakable))
            {
                issues.Add(new A11yIssue(
                    "missing label", label,
                    "Clickable element has no visible text or contentDescription/accessibilityLabel for screen readers",
                    e.Bounds, A11ySeverity.Error, "SpeakableTextPresentCheck", "WCAG 4.1.2 / ATF",
                    "Add android:contentDescription (Android) or accessibilityLabel (iOS)"));
            }

            // 3. EditableContentDescCheck (Google ATF): EditText should use hint, not contentDescription.
            if (IsEditable(e) && !string.IsNullOrWhiteSpace(e.ContentDescription))
            {
                issues.Add(new A11yIssue(
                    "editable content-desc", label,
                    $"Editable field sets contentDescription (\"{Trim(e.ContentDescription)}\"), which overrides typed text in TalkBack",
                    e.Bounds, A11ySeverity.Warning, "EditableContentDescCheck", "WCAG 4.1.2 / ATF",
                    "Remove contentDescription on EditText and use android:hint or a separate labelFor TextView"));
            }

            // 4. RedundantDescriptionCheck (Google ATF): contentDescription includes role word ("button", "icon"...)
            if (!string.IsNullOrWhiteSpace(e.ContentDescription) && HasRedundantRoleWord(e.ContentDescription, out var roleWord))
            {
                issues.Add(new A11yIssue(
                    "redundant description", label,
                    $"contentDescription \"{Trim(e.ContentDescription)}\" contains role word \"{roleWord}\" (screen readers announce the role automatically)",
                    e.Bounds, A11ySeverity.Warning, "RedundantDescriptionCheck", "ATF",
                    $"Remove \"{roleWord}\" from contentDescription and describe only the action/item"));
            }

            // 5. TextContrastCheck (WCAG 1.4.3 AA): uses Frida color when available, falls back to pixel sampling!
            if (e.Enabled && !string.IsNullOrWhiteSpace(e.Text ?? e.Runtime?.Text?.Value))
            {
                var rt = e.Runtime?.Text;
                Rgb? textColor = rt?.Color;
                Rgb? bgColor = e.Runtime?.Box?.Background;

                if (textColor is null || bgColor is null)
                {
                    var (sampledBg, sampledFg) = SampleTextColors(pixels, imgW, imgH, e.Bounds.Scale(pxPerUnit), textColor);
                    bgColor ??= sampledBg;
                    textColor ??= sampledFg;
                }

                if (textColor is { } fg && bgColor is { } bg)
                {
                    var ratio = ContrastRatio(fg, bg);
                    var sizeSp = rt?.SizeSp ?? 14;
                    var isLarge = sizeSp >= 18 || (sizeSp >= 14 && (rt is not null ? (RuntimeFonts.Weight(rt) ?? 400) : 400) >= 700);
                    var required = isLarge ? 3.0 : 4.5;
                    if (ratio < required)
                    {
                        issues.Add(new A11yIssue(
                            "contrast", label,
                            $"Contrast {ratio:0.##}:1 ({fg.Hex} on {bg.Hex}) is below WCAG AA {required:0.#}:1",
                            e.Bounds, A11ySeverity.Error, "TextContrastCheck", "WCAG 1.4.3 AA",
                            $"Darken text or lighten background to reach at least {required:0.#}:1 contrast"));
                    }
                }
            }

            // 6. SmallTextSizeCheck (WCAG 1.4.4)
            if (e.Runtime?.Text is { SizeSp: > 0 and < 12 } smallText && !string.IsNullOrWhiteSpace(e.Text ?? smallText.Value))
            {
                issues.Add(new A11yIssue(
                    "small text", label,
                    $"Text size {F(smallText.SizeSp)} sp is smaller than the recommended 12 sp minimum for readability",
                    e.Bounds, A11ySeverity.Warning, "SmallTextSizeCheck", "WCAG 1.4.4",
                    "Increase text size to at least 12 sp and use sp units so it scales with font_scale"));
            }

            // 7. TextTruncationCheck (WCAG 1.4.4)
            if (e.Runtime?.Text is { } rtext && (rtext.Ellipsized || rtext.Clipped))
            {
                issues.Add(new A11yIssue(
                    "text truncation", label,
                    rtext.Ellipsized ? "Text is ellipsized (cut off with …)" : "Text overflows and is clipped by its view bounds",
                    e.Bounds, A11ySeverity.Error, "TextTruncationCheck", "WCAG 1.4.4",
                    "Allow multi-line wrapping or expand the container height so text does not clip"));
            }

            // 8. KeyboardOcclusionCheck
            if (capture.KeyboardHeightUnits is > 0 and var kbH)
            {
                var kbTop = capture.HeightUnits - kbH;
                var occluded = e.Bounds.Y + e.Bounds.Height > kbTop + 2;
                if (occluded && e.Focused)
                {
                    issues.Add(new A11yIssue(
                        "keyboard occlusion", label,
                        $"Focused field extends to y={F(e.Bounds.Y + e.Bounds.Height)} {u}, covered by keyboard starting at y={F(kbTop)} {u}",
                        e.Bounds, A11ySeverity.Error, "KeyboardOcclusionCheck", "WCAG 2.4.11",
                        "Enable adjustResize / IME inset padding so the focused input scrolls above the keyboard"));
                }
                else if (occluded && e.Clickable && IsButtonLike(e))
                {
                    issues.Add(new A11yIssue(
                        "keyboard occlusion", label,
                        $"Button extends to y={F(e.Bounds.Y + e.Bounds.Height)} {u}, covered by keyboard starting at y={F(kbTop)} {u}",
                        e.Bounds, A11ySeverity.Warning, "KeyboardOcclusionCheck", "WCAG 2.4.11",
                        "Pin CTA above the IME inset or make the form scrollable while the keyboard is open"));
                }
            }

            // 9. SafeAreaOverlapCheck (Edge-to-Edge): skip full-screen root containers that intentionally span the window.
            var isFullScreenContainer = e.Bounds.Width >= capture.WidthUnits * 0.9 && e.Bounds.Height >= capture.HeightUnits * 0.9;
            if (!isFullScreenContainer && capture.SafeArea is { } safe && (e.Clickable || !string.IsNullOrWhiteSpace(e.Text)))
            {
                if (safe.Top > 0 && e.Bounds.Y < safe.Top - 2 && e.Bounds.Y + e.Bounds.Height > 2)
                {
                    issues.Add(new A11yIssue(
                        "safe area", label,
                        $"Element starts at y={F(e.Bounds.Y)} {u}, overlapping top status bar / cutout ({F(safe.Top)} {u})",
                        e.Bounds, A11ySeverity.Error, "SafeAreaOverlapCheck", "Edge-to-Edge",
                        "Apply top WindowInsets / SafeArea padding"));
                }
                if (safe.Bottom > 0 && e.Bounds.Y + e.Bounds.Height > capture.HeightUnits - safe.Bottom + 2 &&
                    e.Bounds.Y < capture.HeightUnits - 2)
                {
                    issues.Add(new A11yIssue(
                        "safe area", label,
                        $"Element extends to y={F(e.Bounds.Y + e.Bounds.Height)} {u}, overlapping bottom navigation/gesture bar ({F(safe.Bottom)} {u})",
                        e.Bounds, A11ySeverity.Error, "SafeAreaOverlapCheck", "Edge-to-Edge",
                        "Apply bottom navigation-bar WindowInsets / SafeArea padding"));
                }
            }
        }

        // 10. ImageContrastCheck (WCAG 1.4.11 Non-text contrast 3:1) for Frida-detected icons
        foreach (var rv in capture.RuntimeViews ?? [])
        {
            if (rv.Props.Icon is not { Gradient: false, Colors.Count: 1 } icon) continue;
            var iconColor = icon.Colors[0];
            var (bg, _) = SampleTextColors(pixels, imgW, imgH, rv.Bounds.Scale(pxPerUnit), iconColor);
            if (bg is { } bgColor)
            {
                var ratio = ContrastRatio(iconColor, bgColor);
                if (ratio < 3.0)
                {
                    issues.Add(new A11yIssue(
                        "icon contrast",
                        rv.ResourceId ?? $"Icon ({F(rv.Bounds.X)}, {F(rv.Bounds.Y)})",
                        $"Icon contrast {ratio:0.##}:1 ({iconColor.Hex} on {bgColor.Hex}) is below WCAG 1.4.11 3:1 minimum",
                        rv.Bounds, A11ySeverity.Warning, "ImageContrastCheck", "WCAG 1.4.11 AA",
                        "Increase icon tint contrast to at least 3:1 against its background"));
                }
            }
        }

        // 11. DuplicateSpeakableTextCheck (Google ATF)
        foreach (var group in clickableElements
                     .Where(c => !string.IsNullOrWhiteSpace(c.Speakable))
                     .GroupBy(c => c.Speakable!.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var items = group.ToList();
            // Skip if they are nested (parent + child with same text) — that's DuplicateClickableBounds or hierarchy.
            var distinct = new List<(ScreenElement Element, string Label, string? Speakable)>();
            foreach (var item in items)
            {
                if (!distinct.Any(d => Contains(d.Element.Bounds, item.Element.Bounds) || Contains(item.Element.Bounds, d.Element.Bounds)))
                    distinct.Add(item);
            }
            if (distinct.Count <= 1) continue;

            foreach (var item in distinct.Skip(1))
            {
                issues.Add(new A11yIssue(
                    "duplicate label", item.Label,
                    $"{distinct.Count} clickable elements share the same screen-reader label \"{Trim(group.Key)}\"",
                    item.Element.Bounds, A11ySeverity.Warning, "DuplicateSpeakableTextCheck", "WCAG 2.4.6 / ATF",
                    "Make each control's contentDescription distinct (e.g. include the item name)"));
            }
        }

        // 12. DuplicateClickableBoundsCheck (Google ATF)
        for (var i = 0; i < clickableElements.Count; i++)
        {
            for (var j = i + 1; j < clickableElements.Count; j++)
            {
                var a = clickableElements[i].Element.Bounds;
                var b = clickableElements[j].Element.Bounds;
                if (Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 &&
                    Math.Abs(a.Width - b.Width) <= 2 && Math.Abs(a.Height - b.Height) <= 2)
                {
                    issues.Add(new A11yIssue(
                        "duplicate clickable", clickableElements[j].Label,
                        $"Shares identical clickable bounds with {clickableElements[i].Label}, causing double focus in TalkBack",
                        b, A11ySeverity.Warning, "DuplicateClickableBoundsCheck", "ATF",
                        "Keep clickable=true on only one view (either the container or the child, not both)"));
                }
            }
        }

        return issues;
    }

    /// <summary>Computes an overall 0–100 accessibility score for the audited screen.</summary>
    public static int ComputeScore(int elementCount, IReadOnlyList<A11yIssue> issues)
    {
        if (issues.Count == 0) return 100;
        var errors = issues.Count(i => i.Severity == A11ySeverity.Error);
        var warnings = issues.Count(i => i.Severity == A11ySeverity.Warning);
        var penalty = errors * 12 + warnings * 5;
        var scale = Math.Max(1.0, elementCount / 10.0);
        return Math.Clamp((int)Math.Round(100.0 - penalty / scale), 0, 100);
    }

    /// <summary>WCAG 2.1 contrast ratio between two sRGB colors (1.0 to 21.0).</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    static double RelativeLuminance(Rgb c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    static (Rgb? Background, Rgb? Foreground) SampleTextColors(SKColor[] pixels, int width, int height, Rect px, Rgb? knownFg)
    {
        int x1 = Math.Clamp((int)px.X, 0, width - 1);
        int y1 = Math.Clamp((int)px.Y, 0, height - 1);
        int x2 = Math.Clamp((int)(px.X + px.Width), 0, width - 1);
        int y2 = Math.Clamp((int)(px.Y + px.Height), 0, height - 1);
        if (x2 <= x1 || y2 <= y1) return (null, knownFg);

        // Background from border pixels of the view.
        var counts = new Dictionary<int, (int Count, long R, long G, long B)>();
        void AddBg(int x, int y)
        {
            var c = pixels[y * width + x];
            if (knownFg is { } kfg && new Rgb(c.Red, c.Green, c.Blue).DistanceTo(kfg) < 20) return;
            var key = (c.Red >> 3) << 10 | (c.Green >> 3) << 5 | (c.Blue >> 3);
            counts.TryGetValue(key, out var acc);
            counts[key] = (acc.Count + 1, acc.R + c.Red, acc.G + c.Green, acc.B + c.Blue);
        }

        for (var x = x1; x <= x2; x += 2) { AddBg(x, y1); AddBg(x, y2); }
        for (var y = y1; y <= y2; y += 2) { AddBg(x1, y); AddBg(x2, y); }
        if (counts.Count == 0) return (null, knownFg);

        var top = counts.Values.MaxBy(v => v.Count);
        var bg = new Rgb((byte)(top.R / top.Count), (byte)(top.G / top.Count), (byte)(top.B / top.Count));
        if (knownFg is not null) return (bg, knownFg);

        // Estimate foreground (ink) from pixels furthest from the background inside the box.
        var ink = new List<(double Dist, SKColor Color)>();
        var stepX = Math.Max(1, (x2 - x1) / 48);
        var stepY = Math.Max(1, (y2 - y1) / 24);
        for (var y = y1 + 1; y < y2; y += stepY)
        for (var x = x1 + 1; x < x2; x += stepX)
        {
            var c = pixels[y * width + x];
            var dist = new Rgb(c.Red, c.Green, c.Blue).DistanceTo(bg);
            if (dist > 18) ink.Add((dist, c));
        }

        if (ink.Count < 4) return (bg, null);
        var core = ink.OrderByDescending(p => p.Dist).Take(Math.Max(4, ink.Count / 5)).ToList();
        var fg = new Rgb(
            (byte)core.Average(p => p.Color.Red),
            (byte)core.Average(p => p.Color.Green),
            (byte)core.Average(p => p.Color.Blue));
        return (bg, fg);
    }

    static string? SpeakableText(ScreenElement e, IReadOnlyList<ScreenElement> all)
    {
        if (!string.IsNullOrWhiteSpace(e.ContentDescription)) return e.ContentDescription;
        if (!string.IsNullOrWhiteSpace(e.Text)) return e.Text;
        var child = all.FirstOrDefault(other =>
            !ReferenceEquals(other, e) &&
            (!string.IsNullOrWhiteSpace(other.Text) || !string.IsNullOrWhiteSpace(other.ContentDescription)) &&
            Contains(e.Bounds, other.Bounds));
        return child?.ContentDescription ?? child?.Text;
    }

    static bool HasRedundantRoleWord(string desc, out string matched)
    {
        var words = WordRegex().Matches(desc.ToLowerInvariant()).Select(m => m.Value).ToHashSet();
        foreach (var rw in RedundantRoleWords)
        {
            if (words.Contains(rw)) { matched = rw; return true; }
        }
        matched = "";
        return false;
    }

    static bool IsEditable(ScreenElement e) =>
        e.ClassName.Contains("EditText", StringComparison.OrdinalIgnoreCase) ||
        e.ClassName is "TextField" or "SecureTextField" or "SearchField";

    static bool IsButtonLike(ScreenElement e) =>
        e.ClassName.Contains("Button", StringComparison.OrdinalIgnoreCase) ||
        e.ClassName.Equals("Button", StringComparison.OrdinalIgnoreCase);

    static bool Contains(Rect outer, Rect inner) =>
        inner.X >= outer.X - 2 && inner.Y >= outer.Y - 2 &&
        inner.X + inner.Width <= outer.X + outer.Width + 2 &&
        inner.Y + inner.Height <= outer.Y + outer.Height + 2;

    static string Label(ScreenElement e)
    {
        var text = e.Text ?? e.ContentDescription;
        var cls = e.ClassName.Split('.').Last();
        if (e.Id is { } id && text is { } t) return $"{id} \"{Trim(t)}\"";
        if (e.Id is { } idOnly) return idOnly;
        if (text is { } tOnly) return $"{cls} \"{Trim(tOnly)}\"";
        return $"{cls} (x={F(e.Bounds.X)}, y={F(e.Bounds.Y)})";
    }

    static string Trim(string s) => s.Length > 30 ? s[..30] + "…" : s;
    static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex WordRegex();
}
