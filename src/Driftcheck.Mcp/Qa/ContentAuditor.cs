using System.Globalization;
using System.Text.RegularExpressions;
using Driftcheck.Mcp.Devices;

namespace Driftcheck.Mcp.Qa;

/// <summary>
/// Static audit of the text visible on a captured screen: leaked placeholders and programming values, raw
/// translation keys, float artifacts, whitespace slips, untranslated text and locale format mismatches.
/// Heuristics are conservative on purpose: a miss is cheaper than a false alarm.
/// </summary>
public static partial class ContentAuditor
{
    static readonly HashSet<string> NonLatinLanguages =
        ["uk", "ru", "be", "bg", "sr", "el", "ar", "he", "hi", "ja", "zh", "ko", "th", "ka", "hy"];

    static readonly string[] TldsAndExtensions =
    [
        "com", "org", "net", "io", "ua", "app", "dev", "co", "info", "me", "ru", "de", "fr", "uk", "us", "eu", "gov", "edu",
        "png", "jpg", "jpeg", "gif", "svg", "webp", "pdf", "txt", "json", "xml", "html", "htm", "js", "ts", "css", "md",
        "mp3", "mp4", "csv", "zip", "doc", "docx", "xls", "xlsx", "apk", "ipa", "log", "yml", "yaml", "kt", "swift", "java",
    ];

    // Unit-like words that are legitimately Latin in any UI.
    static readonly HashSet<string> LatinUnits = new(StringComparer.OrdinalIgnoreCase)
        { "kcal", "mbps", "kbps", "gbps", "kwh", "khz", "mhz", "ghz" };

    static readonly string[] EnglishMonthsFull =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    static readonly string[] EnglishMonthsAbbr =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Sept", "Oct", "Nov", "Dec"];

    public static IReadOnlyList<QaIssue> Audit(ScreenCapture capture, string? locale = null, IReadOnlyCollection<string>? allowlist = null)
    {
        var issues = new List<QaIssue>();
        var lang = Language(locale);
        var culture = LoadCulture(locale);
        var allow = (allowlist ?? [])
            .Select(a => a?.Trim() ?? "").Where(a => a.Length > 0)
            .OrderByDescending(a => a.Length).ToList();
        var allowRegexes = allow
            .Select(a => new Regex($@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(a)}(?![\p{{L}}\p{{N}}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
        var allowSet = new HashSet<string>(allow, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<(string, Rect)>();
        foreach (var element in capture.Elements)
        {
            foreach (var text in TextsOf(element))
            {
                if (!seen.Add((text, element.Bounds))) continue;
                if (allowSet.Contains(text.Trim())) continue;
                AuditText(issues, element, text, lang, culture, locale, allowRegexes, allowSet);
            }
        }
        return issues;
    }

    static IEnumerable<string> TextsOf(ScreenElement e)
    {
        if (!string.IsNullOrWhiteSpace(e.Text)) yield return e.Text!;
        if (!string.IsNullOrWhiteSpace(e.ContentDescription)) yield return e.ContentDescription!;
        var runtime = e.Runtime?.Text?.Value;
        if (!string.IsNullOrWhiteSpace(runtime) && runtime.Length > (e.Text?.Length ?? 0)) yield return runtime;
    }

    static void AuditText(List<QaIssue> issues, ScreenElement el, string text, string lang, CultureInfo? culture,
        string? locale, List<Regex> allowRegexes, HashSet<string> allowSet)
    {
        // Allowlisted words and phrases are blanked out so that no rule sees them.
        var scan = text;
        foreach (var r in allowRegexes) scan = r.Replace(scan, "\u0001");

        // 1. Leaked placeholders / programming values
        var tokens = Placeholder().Matches(scan).Select(m => m.Value).Distinct().ToList();
        if (tokens.Count > 0)
            issues.Add(Make(el, text, "placeholder", QaSeverity.Error,
                "Leaked placeholder or programming value: " + string.Join(", ", tokens.Take(4)),
                "Bind real data or a fallback before rendering; never show raw values, template tokens or escape sequences."));

        // 2. Raw i18n keys
        var isKey = LooksLikeI18nKey(scan.Trim());
        if (isKey)
            issues.Add(Make(el, text, "i18n-key", QaSeverity.Warning,
                "Looks like a raw translation key",
                "Add the missing translation or fix the key lookup for this string."));

        // 3. Float artifacts
        var floats = FloatArtifact().Matches(scan).Select(m => m.Value).Distinct().ToList();
        if (floats.Count > 0)
            issues.Add(Make(el, text, "number-format", QaSeverity.Warning,
                "Unrounded number (float artifact): " + string.Join(", ", floats.Take(3)),
                "Round or format the number (toFixed / NumberFormat) before showing it."));

        // 4. Whitespace
        var ws = WhitespaceProblems(text, lang);
        if (ws.Count > 0)
            issues.Add(Make(el, text, "whitespace", QaSeverity.Info,
                "Whitespace problem: " + string.Join(", ", ws),
                "Trim the string and normalize spaces."));

        if (isKey || tokens.Count > 0) return;

        // 5. Untranslated text
        if (lang.Length > 0 && lang != "en")
        {
            var found = Untranslated(scan, locale, lang);
            if (found.Count > 0)
                issues.Add(Make(el, text, "untranslated", QaSeverity.Warning,
                    $"Text doesn't look translated to {locale}: " + string.Join(", ", found.Take(4)),
                    "Translate the string, or add the word to the allowlist if it is a brand or proper name."));
        }

        // 6. Locale formats
        if (!string.IsNullOrWhiteSpace(locale))
            LocaleFormats(issues, el, text, scan, lang, culture, locale!, allowSet);
    }

    static QaIssue Make(ScreenElement el, string text, string category, QaSeverity severity, string title, string fix) =>
        new(category, severity, title, $"\"{text}\"", ElementLabel(el), el.Bounds, fix);

    static string? ElementLabel(ScreenElement el)
    {
        if (!string.IsNullOrWhiteSpace(el.Id)) return el.Id;
        var t = (el.Text ?? el.ContentDescription)?.Replace('\n', ' ').Trim();
        if (string.IsNullOrEmpty(t)) return el.ClassName;
        return t.Length > 30 ? t[..30] + "…" : t;
    }

    // ---- 2. i18n keys ----

    static bool LooksLikeI18nKey(string s)
    {
        if (s.Length < 5 || !I18nKey().IsMatch(s)) return false;
        var parts = s.Split('.', '_');
        if (!parts.Any(p => p.Length >= 3)) return false;
        var last = parts[^1];
        if (s.Contains('.') && Array.IndexOf(TldsAndExtensions, last) >= 0) return false;
        return true;
    }

    // ---- 4. whitespace ----

    static List<string> WhitespaceProblems(string text, string lang)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return problems;
        if (text[0] == ' ' || text[^1] == ' ') problems.Add("leading/trailing space");
        if (DoubleSpace().IsMatch(text)) problems.Add("double space");
        foreach (Match m in SpaceBeforePunct().Matches(text))
        {
            var ch = m.Value[^1];
            if (lang == "fr" && "!?:;".Contains(ch)) continue;
            problems.Add($"space before '{ch}'");
            break;
        }
        return problems;
    }

    // ---- 5. untranslated ----

    static List<string> Untranslated(string scan, string? locale, string lang)
    {
        var cleaned = UrlOrEmail().Replace(scan, " ");
        if (IsNonLatin(lang, locale))
        {
            var words = new List<string>();
            foreach (Match m in LatinWord().Matches(cleaned))
            {
                var w = m.Value;
                if (w.All(char.IsUpper) && w.Length <= 5) continue;
                if (LatinUnits.Contains(w)) continue;
                words.Add(w);
            }
            return words.Distinct().ToList();
        }

        var english = EnglishFunctionWord().Matches(cleaned).Select(m => m.Value.ToLowerInvariant()).ToList();
        return english.Count >= 2 ? english.Distinct().ToList() : [];
    }

    static bool IsNonLatin(string lang, string? locale)
    {
        if (!NonLatinLanguages.Contains(lang)) return false;
        if (lang != "sr") return true;
        var parts = SplitLocale(locale);
        return !parts.Any(p => p.Equals("Latn", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 6. locale formats ----

    static void LocaleFormats(List<QaIssue> issues, ScreenElement el, string text, string scan, string lang,
        CultureInfo? culture, string locale, HashSet<string> allowSet)
    {
        // (a) dates
        if (culture is not null && ParseDatePattern(culture.DateTimeFormat.ShortDatePattern) is { } pattern)
        {
            foreach (Match m in NumericDate().Matches(scan))
            {
                var sep = m.Groups[2].Value[0];
                var a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var b = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                var dmy = a is >= 1 and <= 31 && b is >= 1 and <= 12;
                var mdy = a is >= 1 and <= 12 && b is >= 1 and <= 31;
                if (!dmy && !mdy) continue;

                var orderOk = pattern.Order switch
                {
                    "dmy" => dmy,
                    "mdy" => mdy,
                    _ => false, // culture writes the year first
                };
                if (orderOk && sep == pattern.Separator) continue;

                issues.Add(Make(el, text, "locale-format", QaSeverity.Warning,
                    $"Date \"{m.Value}\" doesn't follow the {locale} format ({culture.DateTimeFormat.ShortDatePattern})",
                    "Format dates with the user's culture (DateTimeFormat / Intl.DateTimeFormat) instead of a fixed pattern."));
                break;
            }
        }

        // (b) English month names in a non-English UI
        if (lang != "en" && lang.Length > 0)
        {
            var localMonths = LocalMonthNames(culture);
            var bad = new List<string>();
            foreach (Match m in MonthWithNumber().Matches(scan))
            {
                var w = m.Groups["m"].Value.TrimEnd('.');
                if (IsEnglishMonth(w, abbreviation: true) && !localMonths.Contains(w) && !allowSet.Contains(w)) bad.Add(w);
            }
            foreach (Match m in FullMonthName().Matches(scan))
            {
                var w = m.Value;
                if (w == "May" || localMonths.Contains(w) || allowSet.Contains(w)) continue;
                bad.Add(w);
            }
            if (bad.Count > 0)
                issues.Add(Make(el, text, "locale-format", QaSeverity.Warning,
                    $"English month name in a {locale} UI: " + string.Join(", ", bad.Distinct().Take(3)),
                    "Format dates with the user's culture so month names are localized."));
        }

        // (c) number separators
        if (culture is not null)
        {
            var dec = culture.NumberFormat.NumberDecimalSeparator;
            if (dec is "," or ".")
            {
                var decChar = dec[0];
                string? offender = null;
                foreach (Match m in BothSeparators().Matches(scan))
                {
                    if (m.Value[Math.Max(m.Value.LastIndexOf('.'), m.Value.LastIndexOf(','))] != decChar) { offender = m.Value; break; }
                }
                if (offender is null)
                {
                    foreach (var re in new[] { CurrencyPrefixed(), CurrencySuffixed() })
                    {
                        foreach (Match m in re.Matches(scan))
                        {
                            var num = m.Groups["num"].Value;
                            if (num.Contains('.') && num.Contains(',')) continue; // handled above
                            if (num[^3] != decChar) { offender = num; break; }
                        }
                        if (offender is not null) break;
                    }
                }
                if (offender is not null)
                    issues.Add(Make(el, text, "locale-format", QaSeverity.Warning,
                        $"Number \"{offender}\" uses separators that contradict {locale} (decimal separator is '{decChar}')",
                        "Format numbers and amounts with the user's culture (NumberFormat / Intl.NumberFormat)."));
            }
        }
    }

    static bool IsEnglishMonth(string word, bool abbreviation) =>
        Array.Exists(abbreviation ? EnglishMonthsAbbr : EnglishMonthsFull, m => m == word)
        || (abbreviation && Array.Exists(EnglishMonthsFull, m => m == word));

    static HashSet<string> LocalMonthNames(CultureInfo? culture)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (culture is null) return set;
        var f = culture.DateTimeFormat;
        foreach (var n in f.MonthNames.Concat(f.AbbreviatedMonthNames).Concat(f.MonthGenitiveNames).Concat(f.AbbreviatedMonthGenitiveNames))
            if (!string.IsNullOrEmpty(n)) { set.Add(n); set.Add(n.TrimEnd('.')); }
        return set;
    }

    sealed record DatePattern(string Order, char Separator);

    static DatePattern? ParseDatePattern(string pattern)
    {
        var order = new List<char>();
        char? sep = null;
        foreach (var c in pattern)
        {
            if (c is 'd' or 'M' or 'y')
            {
                if (!order.Contains(c)) order.Add(c);
            }
            else if (sep is null && !char.IsLetter(c) && c != ' ' && c != '\'') sep = c;
        }
        if (order.Count != 3 || sep is not ('.' or '/' or '-')) return null;
        return new DatePattern(new string(order.ToArray()).Replace('M', 'm'), sep.Value);
    }

    // ---- locale helpers ----

    static string[] SplitLocale(string? locale) =>
        string.IsNullOrWhiteSpace(locale) ? [] : locale.Split('-', '_', StringSplitOptions.RemoveEmptyEntries);

    static string Language(string? locale) => SplitLocale(locale) is { Length: > 0 } p ? p[0].ToLowerInvariant() : "";

    static CultureInfo? LoadCulture(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        try { return CultureInfo.GetCultureInfo(locale.Replace('_', '-')); }
        catch (CultureNotFoundException) { return null; }
        catch (ArgumentException) { return null; }
    }

    // ---- regexes ----

    [GeneratedRegex(
        @"(?<![\w.\-])(?:null|undefined|NaN|Infinity)(?!\w)" +
        @"|\[object Object\]" +
        @"|Invalid Date" +
        @"|\{\{[^{}]*\}\}" +
        @"|\$\{[^{}]*\}" +
        @"|\{[A-Za-z0-9_][\w.]*\}" +
        @"|%(?:\d+\$)?(?:\.\d+)?(?:[sdf]\b|@)" +
        @"|(?i:lorem ipsum)" +
        @"|\b(?:TODO|FIXME)\b" +
        @"|(?<![A-Za-z]:)(?<!\\)\\[nt]" +
        @"|&(?:amp|nbsp|lt|gt|quot|apos|#\d{1,5}|#x[0-9A-Fa-f]{1,5});" +
        @"|</?(?i:b|i|u|br|p|strong|em|div|span|a|ul|ol|li|h[1-6]|font)(?:\s[^<>]*)?/?>" +
        @"|\*\*[^*\s](?:[^*]*[^*\s])?\*\*",
        RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:[._][a-z][a-z0-9]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex I18nKey();

    [GeneratedRegex(@"(?<![\d.])\d+\.\d{6,}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex FloatArtifact();

    [GeneratedRegex(@"(?<=\S) {2,}(?=\S)", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleSpace();

    [GeneratedRegex(@"(?<=[\p{L}\p{N}]) [,.!?:;](?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforePunct();

    [GeneratedRegex(@"https?://\S+|www\.\S+|\S+@\S+|\b[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.(?:com|org|net|io|ua|app|dev|co|info|me|ru|de|fr|uk|eu)\b\S*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlOrEmail();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Za-z]{3,}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex LatinWord();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_'\-])(?:the|and|your|you|to|of|for|with|from|please|this|here|now|more)(?![\p{L}\p{N}_\-])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EnglishFunctionWord();

    // d/m/yyyy-like with the same separator twice; ISO yyyy-MM-dd never matches (year must come last).
    [GeneratedRegex(@"(?<![\d./\-])(\d{1,2})([./\-])(\d{1,2})\2(\d{4})(?!\d)(?![./\-]\d)", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDate();

    // "Oct 24", "Oct. 24, 2028", "24 Oct"
    [GeneratedRegex(@"(?:(?<![\p{L}\p{N}])(?<m>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sept|Sep|Oct|Nov|Dec|January|February|March|April|June|July|August|September|October|November|December)\.?)\s+\d{1,2}(?!\d))" +
                    @"|(?:(?<!\d)\d{1,2}(?:st|nd|rd|th)?\s+(?<m>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sept|Sep|Oct|Nov|Dec|January|February|March|April|June|July|August|September|October|November|December)\.?)(?![\p{L}\p{N}]))",
        RegexOptions.CultureInvariant)]
    private static partial Regex MonthWithNumber();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:January|February|March|April|May|June|July|August|September|October|November|December)(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant)]
    private static partial Regex FullMonthName();

    [GeneratedRegex(@"(?<![\d.,])(?:\d{1,3}(?:,\d{3})+\.\d+|\d{1,3}(?:\.\d{3})+,\d+)(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex BothSeparators();

    [GeneratedRegex(@"(?:[$€£¥₴₽₹]|\b(?:USD|EUR|GBP|UAH|PLN|CHF|CZK|SEK|NOK|DKK|RUB)\b)[\s  ]?(?<num>(?<![\d.,])\d+(?:[.,]\d{3})*[.,]\d{2})(?!\d)(?![.,]\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPrefixed();

    [GeneratedRegex(@"(?<![\d.,])(?<num>\d+(?:[.,]\d{3})*[.,]\d{2})(?!\d)(?![.,]\d)[\s  ]?(?:[$€£¥₴₽₹]|\b(?:USD|EUR|GBP|UAH|PLN|CHF|CZK|SEK|NOK|DKK|RUB)\b|грн|zł|Kč|руб)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CurrencySuffixed();
}
