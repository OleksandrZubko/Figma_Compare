using System.Text.RegularExpressions;

namespace Driftcheck.Mcp.Devices;

/// <summary>
/// Properties read from the running app (on Android, through Frida) that the accessibility tree doesn't expose.
/// Sizes in logical units (dp), text size in sp.
/// </summary>
/// <param name="Alpha">Effective opacity: the view's alpha multiplied by its parents'.</param>
/// <param name="Icon">Colors of a drawn icon (react-native-svg, or a tinted ImageView).</param>
/// <param name="Elevation">Android elevation in dp, which draws a shadow.</param>
/// <param name="Shadow">Draws a shadow: elevation, or a React Native boxShadow.</param>
/// <param name="Padding">View padding in dp (left, top, right, bottom); null when all sides are 0.</param>
public sealed record RuntimeProps(string ClassName, double Alpha, RuntimeText? Text, RuntimeBox? Box, RuntimeIcon? Icon = null,
    double Elevation = 0, bool Shadow = false, Insets? Padding = null);

/// <summary>A view of the app with its runtime properties and visible bounds in dp (as uiautomator reports them).</summary>
public sealed record RuntimeView(string? ResourceId, Rect Bounds, RuntimeProps Props);

/// <param name="Weight">Weight of the typeface (400, 700…); for a font whose family name carries the weight
/// ("GeneralSansSemibold"), see <see cref="RuntimeFonts.Weight"/>.</param>
/// <param name="LetterSpacingEm">Letter spacing in em, like Android's android:letterSpacing.</param>
/// <param name="Ellipsized">True when the Android TextView Layout ellipsized at least one line.</param>
/// <param name="Clipped">True when the rendered text overflows the TextView's content box.</param>
/// <param name="LineCount">Number of laid-out lines in the TextView, if available.</param>
public sealed record RuntimeText(
    string? Value, double SizeSp, double? Weight, string? Family, bool Italic, Rgb? Color,
    double? LineHeightDp, double? LetterSpacingEm,
    bool Ellipsized = false, bool Clipped = false, int? LineCount = null);

/// <summary>What the view's background draws.</summary>
public sealed record RuntimeBox(Rgb? Background, double? CornerRadius, double? StrokeWidth, Rgb? StrokeColor);

/// <summary>Colors an icon paints (fills and strokes, without the nearly transparent ones).</summary>
/// <param name="Gradient">Some shape is painted with a gradient or pattern, which has no single color.</param>
public sealed record RuntimeIcon(IReadOnlyList<Rgb> Colors, bool Gradient);

/// <summary>Font names as the app reports them, compared with Figma's.</summary>
public static partial class RuntimeFonts
{
    // Longest first: "semibold" must win over "bold", "extralight" over "light".
    static readonly (string Word, int Weight)[] WeightWords =
    [
        ("extralight", 200), ("ultralight", 200), ("semibold", 600), ("demibold", 600),
        ("extrabold", 800), ("ultrabold", 800), ("thin", 100), ("hairline", 100), ("light", 300),
        ("regular", 400), ("normal", 400), ("book", 400), ("medium", 500), ("bold", 700),
        ("black", 900), ("heavy", 900),
    ];

    /// <summary>
    /// The weight to compare: the one in the family name if it has one (custom fonts are usually one file per
    /// weight, "Inter-SemiBold", and React Native reports them like that with weight 400), otherwise the
    /// typeface's.
    /// </summary>
    public static double? Weight(RuntimeText text) => WeightFromFamily(text.Family) ?? text.Weight;

    public static int? WeightFromFamily(string? family)
    {
        if (string.IsNullOrEmpty(family)) return null;
        var compact = Compact(family);
        foreach (var (word, weight) in WeightWords)
            if (compact.EndsWith(word, StringComparison.Ordinal) || compact.Contains(word + "italic", StringComparison.Ordinal))
                return weight;
        return null;
    }

    /// <summary>"Inter" matches "Inter-SemiBold", "inter_bold" or "Inter Display"; "Roboto" matches "sans-serif".</summary>
    public static bool SameFamily(string figma, string app)
    {
        var a = BaseName(figma);
        var b = BaseName(app);
        if (a.Length == 0 || b.Length == 0) return true;
        return a == b || a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
    }

    /// <summary>Family without weight or style words, lowercase and without separators.</summary>
    public static string BaseName(string family)
    {
        var compact = Compact(family);
        // Android's system font families: all of them are Roboto on stock Android.
        if (compact.StartsWith("sansserif", StringComparison.Ordinal) || compact == "default") return "roboto";

        compact = compact.Replace("italic", "", StringComparison.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var (word, _) in WeightWords)
            {
                if (compact.Length > word.Length && compact.EndsWith(word, StringComparison.Ordinal))
                {
                    compact = compact[..^word.Length];
                    changed = true;
                }
            }
        } while (changed);
        return compact;
    }

    static string Compact(string s) => NonLetters().Replace(s.ToLowerInvariant(), "");

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonLetters();
}
