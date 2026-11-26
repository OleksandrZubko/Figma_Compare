using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Driftcheck.Mcp.Android;

namespace Driftcheck.Mcp.Qa;

/// <summary>A secret or piece of personal data found in a text. <paramref name="Masked"/> never holds the full value.</summary>
/// <param name="Kind">jwt, bearer, google-api-key, aws-key, stripe-key, private-key, password, email, card-number, phone.</param>
public sealed record SecretMatch(string Kind, string Masked, string Where);

public sealed record SecurityReport(IReadOnlyList<QaIssue> Issues, IReadOnlyList<QaReport.Section> Sections);

/// <summary>
/// Defensive checks of the app under test: secrets in the log, FLAG_SECURE, plaintext local storage, debuggable /
/// backup-enabled builds, WebView debugging and cleartext HTTP. Each check is independent: if one can't run it
/// reports an Info issue and the rest go on.
/// </summary>
public sealed partial class SecurityAuditor(AdbClient adb)
{
    const string Cat = "security";

    public async Task<SecurityReport> AuditAsync(string serial, string package, bool screenIsSensitive = false,
        IReadOnlyList<string>? observedUrls = null, CancellationToken ct = default)
    {
        var issues = new List<QaIssue>();
        var rows = new List<(string Check, string Result)>();

        async Task Run(string name, Func<Task<string>> check)
        {
            try { rows.Add((name, await check())); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                issues.Add(new QaIssue(Cat, QaSeverity.Info, $"could not check {name}: {ex.Message}"));
                rows.Add((name, "not checked"));
            }
        }

        IReadOnlyList<int> pids = [];
        try { pids = await LogcatMonitor.PidsAsync(adb, serial, package, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        await Run("Secrets in logcat", async () =>
        {
            var text = await adb.ShellAsync(serial, "logcat -d -v epoch -t 2000", ct);
            var seen = new HashSet<(string, string)>();
            foreach (var e in LogcatMonitor.Parse(text).Where(e => pids.Contains(e.Pid) || e.Tag == "ReactNativeJS"))
                foreach (var m in FindSecrets(e.Message, e.Tag))
                {
                    if (!seen.Add((m.Kind, m.Masked))) continue;
                    var pii = m.Kind is "email" or "phone";
                    issues.Add(new QaIssue("logs", pii ? QaSeverity.Warning : QaSeverity.Error,
                        pii ? $"Personal data ({m.Kind}) in logs" : $"Secret ({m.Kind}) written to logs",
                        m.Masked, e.Tag, Fix: "Don't log tokens, credentials or personal data; strip such logs from release builds."));
                }
            return seen.Count == 0 ? "OK: nothing found" : $"{seen.Count} finding(s)";
        });

        await Run("FLAG_SECURE", async () =>
        {
            var (secure, found) = ParseFlagSecure(await adb.ShellAsync(serial, "dumpsys window windows", ct), package);
            var sensitive = screenIsSensitive;
            if (!sensitive)
            {
                await adb.ShellAsync(serial, "uiautomator dump /sdcard/sec_dump.xml", ct);
                var xml = await adb.ShellAsync(serial, "cat /sdcard/sec_dump.xml", ct);
                sensitive = xml.Contains("password=\"true\"", StringComparison.Ordinal);
            }
            if (!found) return "app window not found";
            if (sensitive && !secure)
            {
                issues.Add(new QaIssue(Cat, QaSeverity.Warning, "Sensitive screen can be screenshotted / appears in Recents",
                    "The window of the screen with a password field or sensitive data doesn't set FLAG_SECURE.",
                    Fix: "Set WindowManager.LayoutParams.FLAG_SECURE on that screen."));
                return "WARNING: sensitive screen without FLAG_SECURE";
            }
            return secure ? "FLAG_SECURE set" : "not set (screen not sensitive)";
        });

        await Run("Local storage", async () =>
        {
            var access = await StorageAccessAsync(serial, package, ct);
            if (access is null)
            {
                issues.Add(new QaIssue(Cat, QaSeverity.Info, "storage not readable (needs root or debuggable build)"));
                return "not readable";
            }
            var files = new List<string>();
            var prefs = $"/data/data/{package}/shared_prefs";
            foreach (var f in (await adb.ShellAsync(serial, access($"ls {AdbClient.ShellQuote(prefs)}"), ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (f.Trim().EndsWith(".xml", StringComparison.Ordinal)) files.Add($"{prefs}/{f.Trim()}");
            foreach (var f in new[] { "RKStorage", "RKStorage-wal" })
                files.Add($"/data/data/{package}/databases/{f}");

            var seen = new HashSet<(string, string, string)>();
            var scanned = 0;
            foreach (var path in files)
            {
                var b64 = await adb.ShellAsync(serial, access($"head -c 8000000 {AdbClient.ShellQuote(path)} | base64"), ct);
                byte[] bytes;
                try { bytes = Convert.FromBase64String(Regex.Replace(b64, @"\s+", "")); }
                catch (FormatException) { continue; }
                if (bytes.Length == 0) continue;
                scanned++;
                var name = path[(path.LastIndexOf('/') + 1)..];
                // Latin1 keeps every byte, so ASCII secrets inside a binary sqlite file survive; UTF-8 catches the rest.
                foreach (var m in FindSecrets(Encoding.Latin1.GetString(bytes), name).Concat(FindSecrets(Encoding.UTF8.GetString(bytes), name)))
                    if (m.Kind is not ("email" or "phone" or "card-number") && seen.Add((m.Kind, m.Masked, name)))
                        issues.Add(new QaIssue(Cat, QaSeverity.Error, "Token/secret stored in plaintext",
                            $"{m.Kind}: {m.Masked}", name,
                            Fix: "Keep tokens in the Android Keystore (react-native-keychain / expo-secure-store), not in SharedPreferences or AsyncStorage."));
            }
            return seen.Count == 0 ? $"OK: {scanned} file(s) scanned" : $"{seen.Count} finding(s) in {scanned} file(s)";
        });

        await Run("Package flags", async () =>
        {
            var flags = ParsePackageFlags(await adb.ShellAsync(serial, $"dumpsys package {AdbClient.ShellQuote(package)}", ct));
            if (flags.Contains("DEBUGGABLE"))
                issues.Add(new QaIssue(Cat, QaSeverity.Warning, "App is debuggable", "android:debuggable=true: anyone can attach a debugger and read the app's data.",
                    Fix: "Release builds must not be debuggable."));
            if (flags.Contains("ALLOW_BACKUP"))
                issues.Add(new QaIssue(Cat, QaSeverity.Info, "Backup allowed", "ALLOW_BACKUP: app data can be extracted with adb backup.",
                    Fix: "Set android:allowBackup=\"false\" or add data extraction rules."));
            return flags.Count == 0 ? "no flags" : string.Join(", ", flags);
        });

        await Run("WebView debugging", async () =>
        {
            var found = HasWebViewDevtools(await adb.ShellAsync(serial, "cat /proc/net/unix", ct), pids);
            if (found)
                issues.Add(new QaIssue(Cat, QaSeverity.Warning, "WebView debugging is enabled", "A devtools socket of the app is open.",
                    Fix: "Don't call WebView.setWebContentsDebuggingEnabled(true) in release builds."));
            return found ? "WARNING: devtools socket open" : "OK";
        });

        await Run("Cleartext traffic", () =>
        {
            if (observedUrls is null) return Task.FromResult("no network recording given");
            var bad = observedUrls.Where(IsCleartext).Distinct(StringComparer.Ordinal).ToList();
            foreach (var u in bad)
                issues.Add(new QaIssue("network", QaSeverity.Error, "Cleartext HTTP request", Redact(u), Fix: "Use HTTPS for every request."));
            return Task.FromResult(bad.Count == 0 ? $"OK: {observedUrls.Count} URL(s)" : $"{bad.Count} cleartext URL(s)");
        });

        var table = new StringBuilder("| Check | Result |\n|---|---|\n");
        foreach (var (check, result) in rows) table.AppendLine($"| {QaReport.Cell(check)} | {QaReport.Cell(result)} |");
        return new SecurityReport(issues, [new QaReport.Section("Security checks", table.ToString())]);
    }

    /// <summary>Wraps a command so it runs as root or as the app (run-as); null if neither works.</summary>
    async Task<Func<string, string>?> StorageAccessAsync(string serial, string package, CancellationToken ct)
    {
        var id = await adb.ShellAsync(serial, "id", ct);
        if (id.Contains("uid=0", StringComparison.Ordinal))
            return cmd => cmd;
        var probe = await adb.ShellAsync(serial, $"run-as {AdbClient.ShellQuote(package)} id 2>&1", ct);
        if (probe.Contains("uid=", StringComparison.Ordinal))
            return cmd => $"run-as {AdbClient.ShellQuote(package)} sh -c {AdbClient.ShellQuote(cmd)}";
        return null;
    }

    static bool IsCleartext(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;
        return uri.Host is not ("localhost" or "127.0.0.1" or "10.0.2.2" or "[::1]" or "::1");
    }

    // Query strings often carry tokens: keep scheme, host and path.
    static string Redact(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u)
        ? u.GetLeftPart(UriPartial.Path) + (u.Query.Length > 1 ? "?…" : "")
        : url;

    // ---- pure parsers --------------------------------------------------------------------------------------

    /// <summary>Finds secrets and personal data in a text. Values are masked: first 4 and last 2 characters.</summary>
    public static IReadOnlyList<SecretMatch> FindSecrets(string text, string where)
    {
        var result = new List<SecretMatch>();
        void Add(string kind, string value) => result.Add(new SecretMatch(kind, Mask(value), where));

        foreach (Match m in JwtRegex().Matches(text)) Add("jwt", m.Value);
        foreach (Match m in BearerRegex().Matches(text)) Add("bearer", m.Value);
        foreach (Match m in GoogleKeyRegex().Matches(text)) Add("google-api-key", m.Value);
        foreach (Match m in AwsRegex().Matches(text)) Add("aws-key", m.Value);
        foreach (Match m in StripeRegex().Matches(text)) Add("stripe-key", m.Value);
        foreach (Match m in PrivateKeyRegex().Matches(text)) Add("private-key", m.Value);
        foreach (Match m in PasswordRegex().Matches(text))
        {
            var v = m.Groups["v"].Value;
            if (!IsPlaceholder(v)) Add("password", v);
        }
        foreach (Match m in PasswordXmlRegex().Matches(text))
        {
            var v = m.Groups["v"].Value;
            if (!IsPlaceholder(v)) Add("password", v);
        }
        foreach (Match m in EmailRegex().Matches(text)) Add("email", m.Value);
        foreach (Match m in CardRegex().Matches(text))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is < 13 or > 19 || digits.Distinct().Count() == 1 || !LuhnValid(digits)) continue;
            // A bare 13-digit number starting with 1 is a millisecond timestamp, not a card.
            if (digits.Length == 13 && digits[0] == '1' && m.Value.All(char.IsDigit)) continue;
            // One random number in ten passes Luhn (image cache keys, hashes): it must also start like a real card.
            if (!HasCardPrefix(digits)) continue;
            Add("card-number", digits);
        }
        foreach (Match m in PhoneRegex().Matches(text)) Add("phone", m.Value);
        return result;
    }

    static bool IsPlaceholder(string v) =>
        v.Trim('*', '•', 'x', 'X').Length == 0 || v.Equals("null", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("undefined", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Contains("REDACTED", StringComparison.OrdinalIgnoreCase);

    static string Mask(string value)
    {
        value = value.Trim();
        if (value.Length <= 8) return new string('*', value.Length);
        return value[..4] + "…" + value[^2..];
    }

    /// <summary>
    /// The issuer prefix and length of the major networks: Visa, Mastercard, Amex, Discover, JCB, Diners, UnionPay.
    /// </summary>
    public static bool HasCardPrefix(string digits)
    {
        int P(int n) => int.Parse(digits[..n], System.Globalization.CultureInfo.InvariantCulture);
        var len = digits.Length;
        return (digits[0] == '4' && len is 13 or 16 or 19)                                            // Visa
            || ((P(2) is >= 51 and <= 55 || P(4) is >= 2221 and <= 2720) && len == 16)                 // Mastercard
            || (P(2) is 34 or 37 && len == 15)                                                         // Amex
            || ((P(4) == 6011 || P(3) is >= 644 and <= 649 || P(2) == 65) && len is >= 16 and <= 19)   // Discover
            || (P(4) is >= 3528 and <= 3589 && len is >= 16 and <= 19)                                 // JCB
            || ((P(3) is >= 300 and <= 305 || P(2) is 36 or 38 or 39) && len is >= 14 and <= 19)       // Diners
            || (P(2) == 62 && len is >= 16 and <= 19);                                                 // UnionPay
    }

    public static bool LuhnValid(string digits)
    {
        var sum = 0;
        var alt = false;
        var n = 0;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var c = digits[i];
            if (c is ' ' or '-') continue;
            if (c is < '0' or > '9') return false;
            var d = c - '0';
            if (alt) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            alt = !alt;
            n++;
        }
        return n > 0 && sum % 10 == 0;
    }

    /// <summary>
    /// Whether the app's window sets FLAG_SECURE, from <c>dumpsys window windows</c> (the flag shows as "SECURE" in
    /// the <c>fl=</c> line of the window's mAttrs). Uses the focused window if it's the app's, else the first one.
    /// </summary>
    public static (bool Secure, bool Found) ParseFlagSecure(string dumpsysWindow, string package)
    {
        var focused = FocusRegex().Match(dumpsysWindow);
        var focusedId = focused.Success && focused.Groups["pkg"].Value == package ? focused.Groups["id"].Value : null;

        var windows = new List<(string Id, bool Secure)>();
        string? id = null;
        var secure = false;
        void Flush() { if (id is not null) windows.Add((id, secure)); }
        foreach (var raw in dumpsysWindow.Split('\n'))
        {
            var w = WindowRegex().Match(raw);
            if (w.Success)
            {
                Flush();
                id = w.Groups["pkg"].Value == package ? w.Groups["id"].Value : null;
                secure = false;
                continue;
            }
            if (id is null) continue;
            var fl = FlagsLineRegex().Match(raw);
            if (fl.Success && fl.Groups["f"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(t => t is "SECURE" or "FLAG_SECURE")) secure = true;
        }
        Flush();

        if (windows.Count == 0) return (false, false);
        var pick = focusedId is not null ? windows.FirstOrDefault(x => x.Id == focusedId) : default;
        if (pick.Id is null) pick = windows[0];
        return (pick.Secure, true);
    }

    /// <summary>Flags of the package from <c>dumpsys package</c>: the <c>pkgFlags=[ ... ]</c> / <c>flags=[ ... ]</c> lines.</summary>
    public static IReadOnlyList<string> ParsePackageFlags(string dumpsysPackage)
    {
        var set = new List<string>();
        foreach (Match m in PkgFlagsRegex().Matches(dumpsysPackage))
            foreach (var f in m.Groups["f"].Value.Split([' ', '|'], StringSplitOptions.RemoveEmptyEntries))
                if (!set.Contains(f)) set.Add(f);
        return set;
    }

    /// <summary>Whether a WebView devtools socket of one of the pids (or a generic chrome_devtools_remote) is open.</summary>
    public static bool HasWebViewDevtools(string procNetUnix, IReadOnlyCollection<int> pids)
    {
        foreach (var line in procNetUnix.Split('\n'))
        {
            if (line.Contains("chrome_devtools_remote", StringComparison.Ordinal)) return true;
            var m = DevtoolsRegex().Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pids.Contains(pid))
                return true;
        }
        return false;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")] private static partial Regex JwtRegex();
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/-]{20,}")] private static partial Regex BearerRegex();
    [GeneratedRegex(@"AIza[0-9A-Za-z_-]{35}")] private static partial Regex GoogleKeyRegex();
    [GeneratedRegex(@"AKIA[0-9A-Z]{16}")] private static partial Regex AwsRegex();
    [GeneratedRegex(@"sk_(?:live|test)_[0-9A-Za-z]{16,}")] private static partial Regex StripeRegex();
    [GeneratedRegex(@"-----BEGIN (?:RSA |EC )?PRIVATE KEY-----")] private static partial Regex PrivateKeyRegex();
    [GeneratedRegex(@"(?i)(?<![a-z])""?pass(?:word)?""?\s*[:=]\s*""?(?<v>[^""\s,}]{4,})")] private static partial Regex PasswordRegex();
    [GeneratedRegex(@"(?i)name=""[^""]*pass(?:word)?[^""]*"">(?<v>[^<]{4,})<")] private static partial Regex PasswordXmlRegex();
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")] private static partial Regex EmailRegex();
    // Not glued to letters: digits inside an identifier or hash ("key5982…a1") aren't a card number.
    [GeneratedRegex(@"(?<![\dA-Za-z_-])(?:\d[ -]?){12,18}\d(?![\dA-Za-z_])")] private static partial Regex CardRegex();
    [GeneratedRegex(@"(?<![\w+])\+\d{10,15}(?!\d)")] private static partial Regex PhoneRegex();

    // "Window #7 Window{5f516c5 u0 co.gifted.app/co.gifted.app.MainActivity}:"
    [GeneratedRegex(@"^\s*Window #\d+ Window\{(?<id>[0-9a-f]+) u\d+ (?<pkg>[^/\s}]+)(?:/[^}]*)?\}")] private static partial Regex WindowRegex();
    // "mCurrentFocus=Window{5f516c5 u0 co.gifted.app/co.gifted.app.MainActivity}"
    [GeneratedRegex(@"mCurrentFocus=Window\{(?<id>[0-9a-f]+) u\d+ (?<pkg>[^/\s}]+)")] private static partial Regex FocusRegex();
    // "      fl=LAYOUT_IN_SCREEN ... SECURE ..."
    [GeneratedRegex(@"^\s*fl=(?<f>.*)$")] private static partial Regex FlagsLineRegex();
    [GeneratedRegex(@"(?m)^\s*(?:pkgFlags|flags)=\[(?<f>[^\]]*)\]")] private static partial Regex PkgFlagsRegex();
    [GeneratedRegex(@"@webview_devtools_remote_(\d+)")] private static partial Regex DevtoolsRegex();
}
