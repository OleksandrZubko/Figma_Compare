using System.Text.RegularExpressions;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;

namespace Driftcheck.Mcp.Comparison;

/// <summary>
/// Rules for masking dynamic content (prices, balances, dates, avatars, banners) during visual comparison.
/// Masked elements still have their position, size, typography, color and overflow checked, while text content,
/// image content and raw pixel diffs inside their boxes are ignored.
/// </summary>
public sealed class DynamicMask
{
    readonly HashSet<string> _names;
    readonly List<Regex> _patterns;

    DynamicMask(HashSet<string> names, List<Regex> patterns)
    {
        _names = names;
        _patterns = patterns;
    }

    public bool IsEmpty => _names.Count == 0 && _patterns.Count == 0;

    /// <summary>
    /// Parses a comma-separated list of layer names / AutomationIds and/or regex patterns wrapped in <c>/.../</c>.
    /// Example: <c>"price, user_avatar, /\$\d+/, /\d{2}:\d{2}/"</c>.
    /// </summary>
    public static DynamicMask? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patterns = new List<Regex>();

        foreach (var raw in SplitSpec(spec))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;

            if (token.Length >= 2 && token[0] == '/' && token[^1] == '/')
            {
                var body = token[1..^1];
                patterns.Add(new Regex(body, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            }
            else
            {
                names.Add(Normalize(token));
            }
        }

        return names.Count == 0 && patterns.Count == 0 ? null : new DynamicMask(names, patterns);
    }

    public bool Matches(DesignElement d, ScreenElement? s = null)
    {
        if (_names.Contains(Normalize(d.Name))) return true;
        if (s?.Id is { } id && _names.Contains(Normalize(id))) return true;

        foreach (var rx in _patterns)
        {
            if (d.Text is { } dt && rx.IsMatch(dt)) return true;
            if (s?.Text is { } st && rx.IsMatch(st)) return true;
            if (s?.ContentDescription is { } cd && rx.IsMatch(cd)) return true;
        }
        return false;
    }

    static IEnumerable<string> SplitSpec(string spec)
    {
        // Split on commas outside /regex/ literals so regexes like /\d{1,3}/ stay intact.
        var inRegex = false;
        var start = 0;
        for (var i = 0; i < spec.Length; i++)
        {
            var ch = spec[i];
            if (ch == '/' && (i == 0 || spec[i - 1] != '\\')) inRegex = !inRegex;
            else if (ch == ',' && !inRegex)
            {
                yield return spec[start..i];
                start = i + 1;
            }
        }
        if (start <= spec.Length) yield return spec[start..];
    }

    static string Normalize(string s) =>
        string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
