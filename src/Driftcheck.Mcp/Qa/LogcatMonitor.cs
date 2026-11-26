using System.Globalization;
using System.Text.RegularExpressions;
using Driftcheck.Mcp.Android;

namespace Driftcheck.Mcp.Qa;

/// <summary>A logcat line (read with <c>-v epoch</c>).</summary>
public sealed record LogEntry(double Epoch, int Pid, int Tid, char Level, string Tag, string Message);

/// <summary>What went wrong in the log: a crash, an ANR, a React Native error or warning, or an app error.</summary>
/// <param name="Kind">crash, native-crash, anr, js-error, js-warning or error.</param>
/// <param name="Count">How many times the same line was logged.</param>
public sealed record LogFinding(string Kind, string Summary, IReadOnlyList<string> Lines, int Count = 1);

/// <summary>
/// Reads the device log for the span of a test (from <see cref="StartAsync"/> on) and finds crashes, ANRs, React
/// Native redbox/console errors and the app's own errors.
/// </summary>
public sealed partial class LogcatMonitor
{
    readonly AdbClient adb;
    readonly string serial;
    readonly string startEpoch;

    LogcatMonitor(AdbClient adb, string serial, string startEpoch) => (this.adb, this.serial, this.startEpoch) = (adb, serial, startEpoch);

    /// <summary>Marks the start of the span with the device's clock (one second earlier, for margin).</summary>
    public static async Task<LogcatMonitor> StartAsync(AdbClient adb, string serial, CancellationToken ct = default)
    {
        var now = (await adb.ShellAsync(serial, "date +%s", ct)).Trim();
        var epoch = long.TryParse(now, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s - 1 : 0;
        return new LogcatMonitor(adb, serial, epoch.ToString(CultureInfo.InvariantCulture) + ".000");
    }

    /// <summary>Every line logged since the start (all buffers: main, system, crash).</summary>
    public async Task<IReadOnlyList<LogEntry>> ReadAsync(CancellationToken ct = default) =>
        Parse(await adb.ShellAsync(serial, $"logcat -d -b main,system,crash -v epoch -T {startEpoch}", ct));

    public static IReadOnlyList<LogEntry> Parse(string text)
    {
        var result = new List<LogEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var m = LineRegex().Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            result.Add(new LogEntry(
                double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                m.Groups[4].Value[0],
                m.Groups[5].Value.Trim(),
                m.Groups[6].Value));
        }
        return result;
    }

    /// <summary>
    /// Findings for the app: its processes are <paramref name="pids"/> (a crash restarts it with a new pid, so
    /// crash and ANR lines naming the package count too).
    /// </summary>
    public static IReadOnlyList<LogFinding> Analyze(IReadOnlyList<LogEntry> entries, string? package, IReadOnlyCollection<int> pids)
    {
        var findings = new List<LogFinding>();
        var appPids = new HashSet<int>(pids);

        // A crash names the process in its second line ("Process: com.app, PID: 1234"): that pid is the app's.
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Tag == "AndroidRuntime" && e.Message.StartsWith("FATAL EXCEPTION", StringComparison.Ordinal))
            {
                var lines = entries.Skip(i).TakeWhile(x => x.Tag == "AndroidRuntime" && x.Pid == e.Pid).Take(40).Select(x => x.Message).ToList();
                var process = lines.FirstOrDefault(l => l.StartsWith("Process:", StringComparison.Ordinal));
                if (package is not null && process is not null && !process.Contains(package, StringComparison.Ordinal)) continue;
                appPids.Add(e.Pid);
                var exception = lines.Skip(1).FirstOrDefault(l => !l.StartsWith("Process:", StringComparison.Ordinal)) ?? e.Message;
                findings.Add(new LogFinding("crash", exception.Trim(), lines));
            }
            else if (e.Message.Contains("Fatal signal", StringComparison.Ordinal) && (e.Tag is "libc" or "DEBUG" || appPids.Contains(e.Pid)))
            {
                if (package is not null && !e.Message.Contains(package, StringComparison.Ordinal) && !appPids.Contains(e.Pid)) continue;
                findings.Add(new LogFinding("native-crash", e.Message.Trim(), [e.Message]));
            }
            else if (e.Tag == "ActivityManager" && e.Message.StartsWith("ANR in", StringComparison.Ordinal) &&
                     (package is null || e.Message.Contains(package, StringComparison.Ordinal)))
            {
                var lines = entries.Skip(i).TakeWhile(x => x.Tag == "ActivityManager" && x.Pid == e.Pid).Take(20).Select(x => x.Message).ToList();
                findings.Add(new LogFinding("anr", e.Message.Trim(), lines));
            }
        }

        // React Native logs console.error / redbox under ReactNativeJS (and some versions under ReactNative).
        // The app's other errors: level E/F from its processes, grouped by tag and first line.
        var grouped = entries
            .Where(e => appPids.Contains(e.Pid) && e.Tag != "AndroidRuntime")
            .Select(e => (Entry: e, Kind: e.Tag is "ReactNativeJS" or "ReactNative" or "unknown:ReactNative"
                ? e.Level is 'E' or 'F' ? "js-error" : e.Level == 'W' ? "js-warning" : null
                : e.Level is 'E' or 'F' ? "error" : null))
            .Where(x => x.Kind is not null)
            .GroupBy(x => (x.Kind, x.Entry.Tag, First: FirstLine(x.Entry.Message)));
        foreach (var g in grouped)
            findings.Add(new LogFinding(g.Key.Kind!, $"{g.Key.Tag}: {g.Key.First}", g.Take(5).Select(x => x.Entry.Message).ToList(), g.Count()));

        return findings;
    }

    /// <summary>The issues for the report: crashes and ANRs are errors, JS errors too, the rest warnings.</summary>
    public static IEnumerable<QaIssue> ToIssues(IEnumerable<LogFinding> findings) => findings.Select(f => new QaIssue(
        f.Kind,
        f.Kind is "crash" or "native-crash" or "anr" or "js-error" ? QaSeverity.Error : QaSeverity.Warning,
        f.Kind switch
        {
            "crash" => "App crashed",
            "native-crash" => "Native crash",
            "anr" => "App not responding (ANR)",
            "js-error" => "JavaScript error",
            "js-warning" => "JavaScript warning",
            _ => "Error logged",
        } + (f.Count > 1 ? $" ×{f.Count}" : ""),
        f.Summary));

    static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] : line;
    }

    /// <summary>Pids of the package's processes (the app and its :remote services), or none if it isn't running.</summary>
    public static async Task<IReadOnlyList<int>> PidsAsync(AdbClient adb, string serial, string package, CancellationToken ct = default)
    {
        var output = await adb.ShellAsync(serial, $"pidof {AdbClient.ShellQuote(package)}", ct);
        return output.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : -1)
            .Where(p => p > 0).ToList();
    }

    // "1795688435.684 16722 16755 D CompatChangeReporter: message"
    // Tags can contain a colon ("unknown:ReactNative"): the tag ends at the first ": ".
    [GeneratedRegex(@"^\s*(\d+\.\d+)\s+(\d+)\s+(\d+)\s+([VDIWEFA])\s+(.+?)\s*: (.*)$")]
    private static partial Regex LineRegex();
}
