using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Driftcheck.Mcp.Qa;

/// <summary>
/// Turns what a monitored session recorded (HTTP calls, analytics events, logcat) into issues: failed, slow,
/// duplicated and cleartext requests; analytics events against a tracking plan; values shown on screen that
/// differ from what the API returned.
/// </summary>
public static partial class SessionAnalyzer
{
    const long SlowMs = 3000, SlowInfoMs = 1500, DuplicateWindowMs = 2000;

    // ---------- Network ----------

    public static IReadOnlyList<QaIssue> AnalyzeNetwork(IReadOnlyList<HttpCall> calls)
    {
        var issues = new List<QaIssue>();
        foreach (var c in calls)
        {
            var url = ShortUrl(c.Url);
            if (c.Error is not null)
                issues.Add(new QaIssue("network", QaSeverity.Error, "Request failed", $"{c.Method} {url}: {c.Error}", url,
                    Fix: "Handle the failure in the UI (error state, retry) and check the endpoint"));
            else if (c.Status >= 500)
                issues.Add(new QaIssue("network", QaSeverity.Error, $"Server error {c.Status}", $"{c.Method} {url}", url));
            else if (c.Status >= 400)
                issues.Add(new QaIssue("network", QaSeverity.Warning, $"Client error {c.Status}", $"{c.Method} {url}", url));

            if (c.Ms >= SlowMs)
                issues.Add(new QaIssue("network", QaSeverity.Warning, $"Slow request ({c.Ms} ms)", $"{c.Method} {url}", url,
                    Fix: "Show progress feedback; check caching, payload size and the backend"));
            else if (c.Ms >= SlowInfoMs)
                issues.Add(new QaIssue("network", QaSeverity.Info, $"Request took {c.Ms} ms", $"{c.Method} {url}", url));

            if (c.Url is { } raw && raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLocal(raw))
                issues.Add(new QaIssue("network", QaSeverity.Error, "Cleartext HTTP request", url, url,
                    Fix: "Use HTTPS; cleartext traffic can be read and changed on the network"));

            if (c.ResponseSize > 1_000_000)
                issues.Add(new QaIssue("network", QaSeverity.Info, $"Large response ({c.ResponseSize / 1024} KB)", url, url));
        }

        // The same request again within 2 s: a double tap, a re-render fetching twice, a missing cache.
        foreach (var group in calls.Where(c => c.Url is not null).GroupBy(c => (c.Method, c.Url)))
        {
            var ordered = group.OrderBy(c => c.StartedMs).ToList();
            var burst = 1;
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].StartedMs - ordered[i - 1].StartedMs <= DuplicateWindowMs) burst++;
            }
            if (burst > 1)
                issues.Add(new QaIssue("network", QaSeverity.Warning, $"Duplicate request ×{burst}",
                    $"{group.Key.Method} {ShortUrl(group.Key.Url)} sent {burst} times within {DuplicateWindowMs / 1000} s", ShortUrl(group.Key.Url),
                    Fix: "Deduplicate the call (debounce the action, cache the result, avoid fetching on every render)"));
        }
        return issues;
    }

    public static string NetworkTable(IReadOnlyList<HttpCall> calls)
    {
        if (calls.Count == 0) return "No HTTP calls recorded.";
        var sb = new StringBuilder();
        sb.AppendLine("| # | Method | URL | Status | Time | Response |");
        sb.AppendLine("|---|---|---|---|---|---|");
        var n = 0;
        foreach (var c in calls.OrderBy(c => c.StartedMs))
        {
            n++;
            var status = c.Error is not null ? "failed" : c.Status?.ToString(CultureInfo.InvariantCulture) ?? "";
            var size = c.ResponseSize is { } s && s >= 0 ? $"{s / 1024.0:0.#} KB {c.ResponseType?.Split(';')[0]}" : c.ResponseType?.Split(';')[0] ?? "";
            sb.AppendLine($"| {n} | {c.Method} | {QaReport.Cell(ShortUrl(c.Url))} | {status} | {c.Ms} ms | {QaReport.Cell(size)} |");
        }
        return sb.ToString();
    }

    /// <summary>The URL without its query string (it can carry tokens or personal data), at most 120 characters.</summary>
    public static string ShortUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var q = url.IndexOf('?');
        var clean = q >= 0 ? url[..q] + "?…" : url;
        return clean.Length > 120 ? clean[..120] + "…" : clean;
    }

    static bool IsLocal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host is "10.0.2.2" or "10.0.3.2");

    // ---------- Analytics ----------

    /// <summary>
    /// Analytics events found in the request bodies sent to known analytics endpoints (PostHog, Amplitude, Segment,
    /// Mixpanel, RudderStack) — the way JavaScript SDKs in React Native send them, which no native hook sees.
    /// </summary>
    public static IReadOnlyList<AnalyticsEvent> EventsFromNetwork(IReadOnlyList<HttpCall> calls)
    {
        var result = new List<AnalyticsEvent>();
        foreach (var c in calls)
        {
            if (c.RequestBody is not { Length: > 0 } body || c.Url is null) continue;
            if (SdkForUrl(c.Url) is not { } sdk) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException) { continue; }
            using (doc)
            {
                foreach (var (name, props) in FindEvents(doc.RootElement))
                    result.Add(new AnalyticsEvent(sdk, name, props, c.StartedMs, "network"));
            }
        }
        return result;
    }

    static string? SdkForUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var host = u.Host;
        if (host.Contains("posthog", StringComparison.OrdinalIgnoreCase)) return "posthog";
        if (host.Contains("amplitude", StringComparison.OrdinalIgnoreCase)) return "amplitude";
        if (host.Contains("segment.io", StringComparison.OrdinalIgnoreCase) || host.Contains("segment.com", StringComparison.OrdinalIgnoreCase)) return "segment";
        if (host.Contains("mixpanel", StringComparison.OrdinalIgnoreCase)) return "mixpanel";
        if (host.Contains("rudder", StringComparison.OrdinalIgnoreCase)) return "rudderstack";
        return null;
    }

    /// <summary>Every object with an "event" or "event_type" name in the body: a batch, a single event, an array.</summary>
    static IEnumerable<(string Name, string? Props)> FindEvents(JsonElement e, int depth = 0)
    {
        if (depth > 6) yield break;
        if (e.ValueKind == JsonValueKind.Object)
        {
            var name = e.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString()
                : e.TryGetProperty("event_type", out var et) && et.ValueKind == JsonValueKind.String ? et.GetString() : null;
            if (name is not null)
            {
                var props = e.TryGetProperty("properties", out var p) ? p.GetRawText()
                    : e.TryGetProperty("event_properties", out var ep) ? ep.GetRawText() : null;
                yield return (name, props is { Length: > 600 } ? props[..600] + "…" : props);
                yield break;
            }
            foreach (var prop in e.EnumerateObject())
                if (prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    foreach (var x in FindEvents(prop.Value, depth + 1)) yield return x;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                foreach (var x in FindEvents(item, depth + 1)) yield return x;
        }
    }

    /// <summary>Firebase Analytics events from logcat (tag FA / FA-SVC with log.tag.FA set to VERBOSE).</summary>
    public static IReadOnlyList<AnalyticsEvent> EventsFromLogcat(IReadOnlyList<LogEntry> entries) =>
        entries.Where(e => e.Tag is "FA" or "FA-SVC")
            .Select(e => (Entry: e, Match: FirebaseEventRegex().Match(e.Message)))
            .Where(x => x.Match.Success)
            .Select(x => new AnalyticsEvent("firebase", x.Match.Groups[1].Value, x.Match.Groups[2].Success ? x.Match.Groups[2].Value : null,
                (long)(x.Entry.Epoch * 1000), "logcat"))
            .ToList();

    /// <summary>
    /// One list of events: SDK hooks first; events of the same SDK seen again on the network (the batch upload) or
    /// in logcat are the same events, so they're only used for SDKs the hooks didn't see.
    /// </summary>
    public static IReadOnlyList<AnalyticsEvent> MergeEvents(params IReadOnlyList<AnalyticsEvent>[] sources)
    {
        var result = new List<AnalyticsEvent>();
        foreach (var source in sources)
        {
            var seenSdks = result.Select(e => e.Sdk).ToHashSet();
            result.AddRange(source.Where(e => !seenSdks.Contains(e.Sdk)));
        }
        return result.OrderBy(e => e.AtMs).ToList();
    }

    /// <summary>System events that SDKs send by themselves (session replay, autocapture), left out of plan checks.</summary>
    public static bool IsSystemEvent(string name) =>
        name.StartsWith('$') || name.StartsWith("_", StringComparison.Ordinal) || name is "session_start" or "first_open" or "user_engagement" or "screen_view" or "app_open";

    /// <summary>
    /// Checks the events against a tracking plan: <c>[{"event":"purchase","params":["value","currency"],"min":1,"max":1}]</c>.
    /// Missing events are errors, missing parameters and too many sends warnings, events not in the plan info.
    /// Without a plan it only reports duplicates (the same event with the same parameters within 1 s).
    /// </summary>
    public static IReadOnlyList<QaIssue> CheckEvents(IReadOnlyList<AnalyticsEvent> events, string? trackingPlanJson)
    {
        var issues = new List<QaIssue>();
        var userEvents = events.Where(e => !IsSystemEvent(e.Name)).ToList();

        foreach (var group in userEvents.GroupBy(e => (e.Sdk, e.Name, e.Params)))
        {
            var ordered = group.OrderBy(e => e.AtMs).ToList();
            var dupes = ordered.Zip(ordered.Skip(1)).Count(p => p.Second.AtMs - p.First.AtMs <= 1000);
            if (dupes > 0)
                issues.Add(new QaIssue("analytics", QaSeverity.Warning, $"Event sent twice: {group.Key.Name}",
                    $"{group.Key.Sdk}: {dupes + 1} identical events within 1 s", group.Key.Name,
                    Fix: "Track the event once per user action"));
        }

        if (string.IsNullOrWhiteSpace(trackingPlanJson)) return issues;

        using var doc = JsonDocument.Parse(trackingPlanJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("trackingPlanJson must be a JSON array of {\"event\":..., \"params\":[...]}.");

        var planned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("event", out var ev) || ev.GetString() is not { Length: > 0 } name) continue;
            planned.Add(name);
            var fired = events.Where(e => e.Name == name).ToList();
            var min = item.TryGetProperty("min", out var mi) && mi.ValueKind == JsonValueKind.Number ? mi.GetInt32() : 1;
            int? max = item.TryGetProperty("max", out var ma) && ma.ValueKind == JsonValueKind.Number ? ma.GetInt32() : null;

            if (fired.Count < min)
            {
                issues.Add(new QaIssue("analytics", QaSeverity.Error, $"Event not sent: {name}",
                    fired.Count == 0 ? "Not seen in the session" : $"Sent {fired.Count} time(s), expected at least {min}", name,
                    Fix: "Track the event on the action the tracking plan describes"));
                continue;
            }
            if (max is { } m && fired.Count > m)
                issues.Add(new QaIssue("analytics", QaSeverity.Warning, $"Event sent too often: {name}",
                    $"Sent {fired.Count} times, expected at most {m}", name));

            if (item.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in ps.EnumerateArray().Select(x => x.GetString()).OfType<string>())
                {
                    var missingIn = fired.Count(e => e.Params is null || !ParamPresent(e.Params, p));
                    if (missingIn > 0)
                        issues.Add(new QaIssue("analytics", QaSeverity.Warning, $"Parameter missing: {name}.{p}",
                            $"Missing in {missingIn} of {fired.Count} event(s)", name));
                }
            }
        }

        foreach (var name in userEvents.Select(e => e.Name).Distinct().Where(n => !planned.Contains(n)))
            issues.Add(new QaIssue("analytics", QaSeverity.Info, $"Event not in the tracking plan: {name}", "", name));
        return issues;
    }

    /// <summary>A parameter name in a JSON object, a Bundle[{k=v}] or a Map {k=v} text.</summary>
    static bool ParamPresent(string paramsText, string name) =>
        Regex.IsMatch(paramsText, $@"(^|[{{,\s""]){Regex.Escape(name)}(""\s*:|=)");

    public static string EventsTable(IReadOnlyList<AnalyticsEvent> events)
    {
        if (events.Count == 0) return "No analytics events recorded.";
        var sb = new StringBuilder();
        sb.AppendLine("| SDK | Event | Count | Source | Parameters (first) |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var g in events.GroupBy(e => (e.Sdk, e.Name)).OrderBy(g => g.Min(e => e.AtMs)))
            sb.AppendLine($"| {g.Key.Sdk} | {QaReport.Cell(g.Key.Name)} | {g.Count()} | {g.First().Source} | {QaReport.Cell(g.First().Params)} |");
        return sb.ToString();
    }

    // ---------- Data vs UI ----------

    /// <summary>
    /// Numbers shown on screen that are close to, but not the same as, a number the API returned in this session:
    /// rounding or formatting bugs (1146.91 shown as 1146.9, a value divided twice). A screen number with an exact
    /// match (or a cents/units match ×100) is fine; one with no near value at all is not reported (it may come
    /// from elsewhere). Only numbers with 3+ significant digits are checked, to avoid matching counters and years.
    /// </summary>
    public static IReadOnlyList<QaIssue> CompareDataWithUi(IEnumerable<(string Text, Rect? Bounds)> screenTexts, IReadOnlyList<HttpCall> calls)
    {
        var apiNumbers = new HashSet<decimal>();
        foreach (var c in calls)
        {
            if (c.ResponseBody is not { Length: > 0 } body || c.ResponseType is null || !c.ResponseType.Contains("json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var doc = JsonDocument.Parse(body);
                CollectNumbers(doc.RootElement, apiNumbers, 0);
            }
            catch (JsonException) { }
        }
        if (apiNumbers.Count == 0) return [];

        var issues = new List<QaIssue>();
        foreach (var (text, bounds) in screenTexts)
        {
            foreach (Match m in ScreenNumberRegex().Matches(text))
            {
                if (ParseScreenNumber(m.Value) is not { } shown) continue;
                if (SignificantDigits(shown) < 3 || (shown is >= 1900 and <= 2100 && shown == Math.Floor(shown))) continue;
                if (apiNumbers.Contains(shown) || apiNumbers.Contains(shown * 100) || apiNumbers.Contains(shown / 100)) continue;

                var near = apiNumbers.Where(a => a != 0 && Math.Abs(a - shown) / Math.Abs(a) <= 0.01m && Math.Abs(a - shown) > 0.0001m)
                    .OrderBy(a => Math.Abs(a - shown)).FirstOrDefault();
                if (near == 0) continue;
                issues.Add(new QaIssue("data", QaSeverity.Warning, "Value on screen differs from the API",
                    $"Shown \"{m.Value}\" ({shown.ToString(CultureInfo.InvariantCulture)}), the API returned {near.ToString(CultureInfo.InvariantCulture)}",
                    text.Length > 40 ? text[..40] + "…" : text, bounds,
                    "Check rounding and formatting of the value (decimal places, currency conversion)"));
            }
        }
        return issues;
    }

    static void CollectNumbers(JsonElement e, HashSet<decimal> into, int depth)
    {
        if (depth > 12 || into.Count > 20000) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Number when e.TryGetDecimal(out var d):
                into.Add(d);
                break;
            case JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s):
                into.Add(s);
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) CollectNumbers(p.Value, into, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray()) CollectNumbers(i, into, depth + 1);
                break;
        }
    }

    /// <summary>"1,146.91", "1.146,91", "1 146,91", "$1146.91" → 1146.91. Null when the separators are ambiguous.</summary>
    public static decimal? ParseScreenNumber(string s)
    {
        var t = s.Replace(" ", " ").Replace(" ", " ").Replace(" ", "");
        var lastDot = t.LastIndexOf('.');
        var lastComma = t.LastIndexOf(',');
        string normalized;
        if (lastDot >= 0 && lastComma >= 0)
            normalized = lastDot > lastComma ? t.Replace(",", "") : t.Replace(".", "").Replace(',', '.');
        else if (lastComma >= 0)
            normalized = t.Length - lastComma - 1 == 3 && t.Count(c => c == ',') >= 1 && !t.EndsWith(",", StringComparison.Ordinal) && t.IndexOf(',') == lastComma && lastComma <= 3
                ? t.Replace(",", "")          // 1,146 = thousands
                : t.Replace(',', '.');        // 12,50 = decimal
        else
            normalized = t;
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    static int SignificantDigits(decimal d) =>
        d.ToString(CultureInfo.InvariantCulture).TrimStart('-', '0', '.').Replace(".", "").TrimEnd('0').Length;

    [GeneratedRegex(@"(?<![\w.,])\d{1,3}(?:[ ,.  ]\d{3})*(?:[.,]\d{1,4})?(?![\w])|(?<![\w.,])\d+(?:[.,]\d{1,4})?(?![\w])")]
    private static partial Regex ScreenNumberRegex();

    [GeneratedRegex(@"Logging event[^:]*:.*?name=([\w$]+)(?:,\s*params=(Bundle\[.*\]))?")]
    private static partial Regex FirebaseEventRegex();
}
