using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Driftcheck.Mcp.Android;
using SkiaSharp;

namespace Driftcheck.Mcp.Qa;

public sealed record StartupResult(string Kind, IReadOnlyList<int> TotalTimesMs, int MedianMs);
public sealed record JankResult(int TotalFrames, int JankyFrames, double JankyPercent, int P50Ms, int P90Ms, int P95Ms, int P99Ms,
    int SlowUiThread, int SlowBitmapUploads, int SlowDrawCommands, int MissedVsync, int HighInputLatency);
public sealed record MemoryResult(double TotalPssMb, double JavaHeapMb, double NativeHeapMb, double GraphicsMb, int Views, int Activities);
public sealed record OverdrawResult(double Ratio1x, double Ratio2x, double Ratio3x, double Ratio4xPlus, string ScreenshotPath);
public sealed record StartupParse(string? LaunchState, int? TotalTime, int? WaitTime);
public sealed record PerformanceReport(StartupResult? Cold, StartupResult? Warm, JankResult? Jank, MemoryResult? Memory,
    OverdrawResult? Overdraw, IReadOnlyList<QaIssue> Issues, IReadOnlyList<QaReport.Section> Sections);

/// <summary>
/// Startup time, jank, memory and overdraw of an Android app, measured with am start -W, dumpsys gfxinfo / meminfo
/// and the HWUI overdraw debug overlay. Every measurement is independent; whatever it changes on the device is restored.
/// </summary>
public sealed class PerformanceProbe(AdbClient adb, Settings settings)
{
    const string OverdrawProp = "debug.hwui.overdraw";
    const string PokeApps = "service call activity 1599295570";

    public async Task<PerformanceReport> MeasureAsync(string serial, string package, int startRuns = 3,
        bool measureStartup = true, bool measureJank = true, bool measureOverdraw = true, CancellationToken ct = default)
    {
        var issues = new List<QaIssue>();
        var sections = new List<QaReport.Section>();
        StartupResult? cold = null, warm = null;
        JankResult? jank = null;
        MemoryResult? memory = null;
        OverdrawResult? overdraw = null;
        startRuns = Math.Clamp(startRuns, 1, 10);
        var dir = Path.Combine(settings.OutputDirectory, "qa", $"perf-{DateTime.Now:yyyyMMdd-HHmmss}");

        if (measureStartup)
        {
            try
            {
                var component = ParseLauncherActivity(await adb.ShellAsync(serial,
                    $"cmd package resolve-activity --brief {AdbClient.ShellQuote(package)}", ct))
                    ?? throw new InvalidOperationException("no launcher activity found");
                var launch = $"am start -W -n {AdbClient.ShellQuote(component)}";
                var coldTimes = new List<int>();
                var warmTimes = new List<int>();
                try
                {
                    for (var i = 0; i < startRuns; i++)
                    {
                        await adb.ShellAsync(serial, $"am force-stop {AdbClient.ShellQuote(package)}", ct);
                        await Task.Delay(1000, ct);
                        if (ParseAmStart(await adb.ShellAsync(serial, launch, ct)) is { TotalTime: { } t }) coldTimes.Add(t);
                    }
                    for (var i = 0; i < startRuns; i++)
                    {
                        await adb.KeyEventAsync(serial, "KEYCODE_HOME", ct);
                        await Task.Delay(1000, ct);
                        if (ParseAmStart(await adb.ShellAsync(serial, launch, ct)) is { TotalTime: { } t }) warmTimes.Add(t);
                    }
                }
                finally
                {
                    // Leave the app in the foreground whatever happened.
                    try { await adb.ShellAsync(serial, launch, CancellationToken.None); } catch { /* best effort */ }
                }
                if (coldTimes.Count > 0) cold = new StartupResult("cold", coldTimes, Median(coldTimes));
                if (warmTimes.Count > 0) warm = new StartupResult("warm", warmTimes, Median(warmTimes));
                if (cold is null && warm is null) throw new InvalidOperationException("am start -W reported no TotalTime");

                if (cold is { MedianMs: > 5000 })
                    issues.Add(new QaIssue("startup", QaSeverity.Error, $"Cold start {cold.MedianMs} ms (limit 5000 ms)",
                        $"runs: {string.Join(", ", cold.TotalTimesMs)} ms", Fix: "Android vitals flags cold starts above 5 s as excessive."));
                else if (cold is { MedianMs: > 2000 })
                    issues.Add(new QaIssue("startup", QaSeverity.Warning, $"Cold start {cold.MedianMs} ms (limit 2000 ms)",
                        $"runs: {string.Join(", ", cold.TotalTimesMs)} ms", Fix: "Defer work out of Application.onCreate / the first render."));
                if (warm is { MedianMs: > 1000 })
                    issues.Add(new QaIssue("startup", QaSeverity.Warning, $"Warm start {warm.MedianMs} ms (limit 1000 ms)",
                        $"runs: {string.Join(", ", warm.TotalTimesMs)} ms"));

                var sb = new StringBuilder("| Kind | Runs (ms) | Median (ms) |\n|---|---|---|\n");
                foreach (var s in new[] { cold, warm }.OfType<StartupResult>())
                    sb.AppendLine($"| {s.Kind} | {string.Join(", ", s.TotalTimesMs)} | {s.MedianMs} |");
                sections.Add(new QaReport.Section("Startup", sb.ToString()));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { issues.Add(Unmeasured("startup", ex)); }
        }

        if (measureJank)
        {
            try
            {
                var (w, h) = ParseWmSize(await adb.ShellAsync(serial, "wm size", ct));
                await adb.ShellAsync(serial, $"dumpsys gfxinfo {AdbClient.ShellQuote(package)} reset", ct);
                int x = w / 2, hi = (int)(h * 0.7), lo = (int)(h * 0.3);
                for (var i = 0; i < 3; i++)
                {
                    await adb.SwipeAsync(serial, x, hi, x, lo, 300, ct);
                    await Task.Delay(400, ct);
                }
                for (var i = 0; i < 3; i++)
                {
                    await adb.SwipeAsync(serial, x, lo, x, hi, 300, ct);
                    await Task.Delay(400, ct);
                }
                jank = ParseGfxinfo(await adb.ShellAsync(serial, $"dumpsys gfxinfo {AdbClient.ShellQuote(package)}", ct))
                    ?? throw new InvalidOperationException("gfxinfo has no frame statistics (is the app in the foreground?)");

                if (jank.JankyPercent > 25)
                    issues.Add(new QaIssue("jank", QaSeverity.Error, $"{QaReport.F(jank.JankyPercent)}% janky frames while scrolling",
                        $"{jank.JankyFrames} of {jank.TotalFrames} frames; p90 {jank.P90Ms} ms"));
                else if (jank.JankyPercent > 10)
                    issues.Add(new QaIssue("jank", QaSeverity.Warning, $"{QaReport.F(jank.JankyPercent)}% janky frames while scrolling",
                        $"{jank.JankyFrames} of {jank.TotalFrames} frames; p90 {jank.P90Ms} ms"));
                if (jank.P90Ms > 16)
                    issues.Add(new QaIssue("jank", QaSeverity.Info, $"90th percentile frame time {jank.P90Ms} ms (budget 16 ms)",
                        $"p50 {jank.P50Ms} / p95 {jank.P95Ms} / p99 {jank.P99Ms} ms"));

                sections.Add(new QaReport.Section("Jank (6 swipes)",
                    "| Metric | Value |\n|---|---|\n" +
                    $"| Total frames | {jank.TotalFrames} |\n| Janky frames | {jank.JankyFrames} ({QaReport.F(jank.JankyPercent)}%) |\n" +
                    $"| p50 / p90 / p95 / p99 | {jank.P50Ms} / {jank.P90Ms} / {jank.P95Ms} / {jank.P99Ms} ms |\n" +
                    $"| Slow UI thread | {jank.SlowUiThread} |\n| Slow bitmap uploads | {jank.SlowBitmapUploads} |\n" +
                    $"| Slow draw commands | {jank.SlowDrawCommands} |\n| Missed Vsync | {jank.MissedVsync} |\n" +
                    $"| High input latency | {jank.HighInputLatency} |\n"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { issues.Add(Unmeasured("jank", ex)); }
        }

        try
        {
            var output = await adb.ShellAsync(serial, $"dumpsys meminfo {AdbClient.ShellQuote(package)}", ct);
            // The full dump asks the app for its object counts and times out when the app is busy; --local reads
            // only the process's memory (no Views/Activities counts, which then stay 0).
            var parsed = ParseMeminfo(output)
                ?? ParseMeminfo(await adb.ShellAsync(serial, $"dumpsys meminfo --local {AdbClient.ShellQuote(package)}", ct));
            memory = parsed ?? throw new InvalidOperationException("meminfo has no App Summary (app not running?)");
            if (memory.Activities > 1)
                issues.Add(new QaIssue("memory", QaSeverity.Info, $"{memory.Activities} activities alive: possible leaked/duplicated activity",
                    $"Views: {memory.Views}"));
            if (memory.TotalPssMb > 500)
                issues.Add(new QaIssue("memory", QaSeverity.Warning, $"Total PSS {QaReport.F(memory.TotalPssMb)} MB (limit 500 MB)",
                    $"java {QaReport.F(memory.JavaHeapMb)} / native {QaReport.F(memory.NativeHeapMb)} / graphics {QaReport.F(memory.GraphicsMb)} MB"));
            sections.Add(new QaReport.Section("Memory",
                "| Metric | Value |\n|---|---|\n" +
                $"| Total PSS | {QaReport.F(memory.TotalPssMb)} MB |\n| Java heap | {QaReport.F(memory.JavaHeapMb)} MB |\n" +
                $"| Native heap | {QaReport.F(memory.NativeHeapMb)} MB |\n| Graphics | {QaReport.F(memory.GraphicsMb)} MB |\n" +
                $"| Views | {memory.Views} |\n| Activities | {memory.Activities} |\n"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { issues.Add(Unmeasured("memory", ex)); }

        if (measureOverdraw)
        {
            try
            {
                overdraw = await MeasureOverdrawAsync(serial, dir, ct);
                if (overdraw.Ratio4xPlus > 0.10)
                    issues.Add(new QaIssue("overdraw", QaSeverity.Warning, $"{QaReport.F(overdraw.Ratio4xPlus * 100)}% of the screen is overdrawn 4x or more",
                        $"screenshot: {overdraw.ScreenshotPath}", Fix: "Remove stacked opaque backgrounds and clip or flatten overlapping layers."));
                sections.Add(new QaReport.Section("Overdraw",
                    "| Layers | Share of screen |\n|---|---|\n" +
                    $"| 1x (blue) | {QaReport.F(overdraw.Ratio1x * 100)}% |\n| 2x (green) | {QaReport.F(overdraw.Ratio2x * 100)}% |\n" +
                    $"| 3x (pink) | {QaReport.F(overdraw.Ratio3x * 100)}% |\n| 4x+ (red) | {QaReport.F(overdraw.Ratio4xPlus * 100)}% |\n\n" +
                    $"Screenshot: {overdraw.ScreenshotPath}\n"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { issues.Add(Unmeasured("overdraw", ex)); }
        }

        return new PerformanceReport(cold, warm, jank, memory, overdraw, issues, sections);
    }

    async Task<OverdrawResult> MeasureOverdrawAsync(string serial, string dir, CancellationToken ct)
    {
        var previous = (await adb.ShellAsync(serial, $"getprop {OverdrawProp}", ct)).Trim();
        byte[] overdrawPng, normalPng;
        try
        {
            await adb.ShellAsync(serial, $"setprop {OverdrawProp} show", ct);
            await adb.ShellAsync(serial, PokeApps, ct);
            await Task.Delay(1500, ct);
            // The capture reuses one file name, so read the bytes before the next capture overwrites them.
            overdrawPng = await File.ReadAllBytesAsync((await adb.CaptureAsync(serial, ct)).ScreenshotPath, ct);
        }
        finally
        {
            await RestoreOverdrawAsync(serial, previous);
        }
        await Task.Delay(1500, ct);
        normalPng = await File.ReadAllBytesAsync((await adb.CaptureAsync(serial, ct)).ScreenshotPath, ct);

        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "overdraw.png");
        await File.WriteAllBytesAsync(path, overdrawPng, ct);
        using var a = SKBitmap.Decode(overdrawPng) ?? throw new InvalidOperationException("could not decode the overdraw screenshot");
        using var b = SKBitmap.Decode(normalPng) ?? throw new InvalidOperationException("could not decode the normal screenshot");
        return ClassifyOverdraw(a, b, path);
    }

    async Task RestoreOverdrawAsync(string serial, string previous)
    {
        try
        {
            var value = previous.Length == 0 ? "false" : AdbClient.ShellQuote(previous);
            await adb.ShellAsync(serial, $"setprop {OverdrawProp} {value}", CancellationToken.None);
            await adb.ShellAsync(serial, PokeApps, CancellationToken.None);
        }
        catch { /* best effort */ }
    }

    static QaIssue Unmeasured(string what, Exception ex) =>
        new("performance", QaSeverity.Info, $"could not measure {what}: {ex.Message}");

    static int Median(List<int> values)
    {
        var s = values.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (int)Math.Round((s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0);
    }

    static (int W, int H) ParseWmSize(string output)
    {
        // "Physical size: 1080x2400" (and "Override size: ..." when changed, which is the effective one).
        var matches = Regex.Matches(output, @"(\d+)x(\d+)");
        if (matches.Count == 0) throw new InvalidOperationException("could not read the screen size");
        var m = matches[^1];
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    // ---- parsers ----

    public static StartupParse? ParseAmStart(string output)
    {
        string? state = null;
        int? total = null, wait = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("LaunchState:", StringComparison.Ordinal)) state = line["LaunchState:".Length..].Trim();
            else if (line.StartsWith("TotalTime:", StringComparison.Ordinal)) total = ToInt(line["TotalTime:".Length..]);
            else if (line.StartsWith("WaitTime:", StringComparison.Ordinal)) wait = ToInt(line["WaitTime:".Length..]);
        }
        return state is null && total is null && wait is null ? null : new StartupParse(state, total, wait);
    }

    public static JankResult? ParseGfxinfo(string output)
    {
        var total = Match(output, @"Total frames rendered:\s*(\d+)");
        if (total is null) return null;
        var janky = Regex.Match(output, @"^\s*Janky frames:\s*(\d+)\s*\(([\d.]+)%\)", RegexOptions.Multiline);
        var jankyFrames = janky.Success ? ToInt(janky.Groups[1].Value) ?? 0 : 0;
        var percent = janky.Success && double.TryParse(janky.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
            ? p : total > 0 ? jankyFrames * 100.0 / total.Value : 0;
        int Pct(int n) => Match(output, $@"^\s*{n}th percentile:\s*(\d+)ms", RegexOptions.Multiline) ?? 0;
        int Count(string label) => Match(output, $@"Number {label}:\s*(\d+)") ?? 0;
        return new JankResult(total.Value, jankyFrames, percent, Pct(50), Pct(90), Pct(95), Pct(99),
            Count("Slow UI thread"), Count("Slow bitmap uploads"), Count("Slow issue draw commands"),
            Count("Missed Vsync"), Count("High input latency"));
    }

    public static MemoryResult? ParseMeminfo(string output)
    {
        var total = Match(output, @"TOTAL PSS:\s*(\d+)") ?? Match(output, @"^\s*TOTAL\s+(\d+)", RegexOptions.Multiline);
        if (total is null) return null;
        static double Mb(int? kb) => (kb ?? 0) / 1024.0;
        return new MemoryResult(Mb(total),
            Mb(Match(output, @"Java Heap:\s*(\d+)")),
            Mb(Match(output, @"Native Heap:\s*(\d+)")),
            Mb(Match(output, @"Graphics:\s*(\d+)")),
            Match(output, @"\bViews:\s*(\d+)") ?? 0,
            Match(output, @"\bActivities:\s*(\d+)") ?? 0);
    }

    /// <summary>Last non-empty line of `cmd package resolve-activity --brief`, e.g. "co.gifted.app/.MainActivity".</summary>
    public static string? ParseLauncherActivity(string resolveActivityOutput)
    {
        var last = resolveActivityOutput.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return last is not null && Regex.IsMatch(last, @"^[\w.]+/[\w.$]+$") ? last : null;
    }

    static int? Match(string s, string pattern, RegexOptions options = RegexOptions.None)
    {
        var m = Regex.Match(s, pattern, options);
        return m.Success ? ToInt(m.Groups[1].Value) : null;
    }

    static int? ToInt(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    // ---- overdraw ----

    /// <summary>
    /// Classifies each pixel of the screenshot taken with "Show overdraw areas" against the normal screenshot.
    /// The overlay blends a translucent color over the content (1x blue, 2x green, 3x pink, 4x+ red), so the
    /// tint is the channel that drops least; the two other channels fall by the tint's alpha. 3x and 4x+ share
    /// the red hue and are told apart by how strong the blend is. Unchanged pixels (0x) are ignored.
    /// </summary>
    public static OverdrawResult ClassifyOverdraw(SKBitmap withOverdraw, SKBitmap normal, string path)
    {
        var w = Math.Min(withOverdraw.Width, normal.Width);
        var h = Math.Min(withOverdraw.Height, normal.Height);
        if (w == 0 || h == 0) return new OverdrawResult(0, 0, 0, 0, path);
        const int minDrop = 12;
        long c1 = 0, c2 = 0, c3 = 0, c4 = 0;

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = withOverdraw.GetPixel(x, y);
                var n = normal.GetPixel(x, y);
                int[] drop = [n.Red - o.Red, n.Green - o.Green, n.Blue - o.Blue];
                int[] baseVal = [n.Red, n.Green, n.Blue];
                var tint = Array.IndexOf(drop, drop.Min());
                var others = new[] { 0, 1, 2 }.Where(i => i != tint).ToArray();
                // Both non-tint channels must fall noticeably, and clearly more than the tint channel.
                if (others.Any(i => drop[i] < minDrop || drop[i] - drop[tint] < minDrop / 2)) continue;

                switch (tint)
                {
                    case 2: c1++; break;
                    case 1: c2++; break;
                    default:
                        // Alpha of the red blend from the channels that fell, where the content is bright enough to tell.
                        var usable = others.Where(i => baseVal[i] >= 40).ToArray();
                        var alpha = usable.Length > 0
                            ? usable.Average(i => (double)drop[i] / baseVal[i])
                            : double.MaxValue;
                        if (alpha >= 0.34) c4++; else c3++;
                        break;
                }
            }

        double area = (double)w * h;
        return new OverdrawResult(c1 / area, c2 / area, c3 / area, c4 / area, path);
    }
}
