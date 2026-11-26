using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using Driftcheck.Mcp.Qa;
using ModelContextProtocol.Server;

namespace Driftcheck.Mcp.Tools;

/// <summary>
/// QA audits beyond the design comparison: monitoring (crashes, network, analytics), content, design tokens,
/// resilience, performance and security. Android only (they drive the device over ADB and read the app with Frida).
/// </summary>
[McpServerToolType]
public sealed class QaTools(FigmaClient figma, DeviceRegistry devices, Settings settings)
{
    [McpServerTool(Name = "monitor_session"), Description(
        "Monitors the app on an Android device while it is used: records every HTTP call (OkHttp, which React Native's fetch uses) " +
        "and analytics event (native SDK calls, PostHog/Amplitude/Segment/Mixpanel uploads, Firebase via logcat) with Frida, and reads " +
        "logcat for crashes, ANRs, React Native errors and the app's errors. Runs the optional ADB actions (same format as compare_states) " +
        "or waits durationSec for a person to use the app. Reports failed, slow, duplicated and cleartext requests, analytics events " +
        "against an optional tracking plan, and numbers on screen that differ from what the API returned.")]
    public async Task<string> MonitorSession(
        [Description("App package. Optional: the app in the foreground.")] string? package = null,
        [Description("Device serial. Optional: the first device.")] string? deviceSerial = null,
        [Description("Seconds to record when no actions are given (a person uses the app meanwhile). Default 15.")] int durationSec = 15,
        [Description("Optional JSON array of ADB actions to run while recording: [{\"type\":\"tap_text\",\"value\":\"Pay\"},{\"type\":\"wait\",\"ms\":2000}]. Types: tap, tap_text, tap_id, input_text, keyevent, swipe, deeplink, wait, shell.")] string? actionsJson = null,
        [Description("true to restart the app first and record its launch (the first few hundred ms before Frida attaches are missed). Default false.")] bool restartApp = false,
        [Description("Optional tracking plan: [{\"event\":\"purchase\",\"params\":[\"value\",\"currency\"],\"min\":1,\"max\":1}].")] string? trackingPlanJson = null,
        [Description("true to compare numbers on the final screen with the numbers the API returned. Default true.")] bool checkDataVsUi = true,
        CancellationToken ct = default)
    {
        var (adb, serial) = await AndroidAsync(deviceSerial, ct);
        if (restartApp)
        {
            package ??= await ForegroundPackageAsync(adb, serial, ct);
            await adb.ShellAsync(serial, $"am force-stop {AdbClient.ShellQuote(package)}", ct);
            await adb.ShellAsync(serial, $"monkey -p {AdbClient.ShellQuote(package)} -c android.intent.category.LAUNCHER 1", ct);
            for (var i = 0; i < 50 && (await LogcatMonitor.PidsAsync(adb, serial, package, ct)).Count == 0; i++) await Task.Delay(100, ct);
            await Task.Delay(1500, ct); // Attaching while the runtime is still starting can hang Frida.
        }
        package ??= await ForegroundPackageAsync(adb, serial, ct);
        var actions = ParseActions(actionsJson);

        var notes = new List<string>();
        var logcat = await LogcatMonitor.StartAsync(adb, serial, ct);
        var pidsBefore = await LogcatMonitor.PidsAsync(adb, serial, package, ct);
        var firebaseProps = await EnableFirebaseLoggingAsync(adb, serial, ct);

        FridaRecorder? recorder = null;
        try
        {
            recorder = await FridaRecorder.StartAsync(settings, serial, package, ct);
        }
        catch (InvalidOperationException e)
        {
            notes.Add($"Network and analytics hooks are off ({e.Message}); only logcat was monitored.");
        }

        IReadOnlyList<HttpCall> calls = [];
        IReadOnlyList<AnalyticsEvent> hooked = [];
        ScreenCapture? finalScreen = null;
        try
        {
            if (actions.Count > 0)
            {
                await DeviceActionExecutor.ExecuteAsync(adb, serial, actions, ct);
                await Task.Delay(2000, ct); // Let the requests the last action started finish.
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(durationSec, 1, 600)), ct);
            }
        }
        finally
        {
            if (recorder is not null)
            {
                var (c, ev, recorderNotes) = await recorder.StopAsync();
                calls = c;
                hooked = ev;
                notes.AddRange(recorderNotes.Where(n => !n.StartsWith("detached: application-requested", StringComparison.Ordinal)));
                await recorder.DisposeAsync();
            }
            await RestoreFirebaseLoggingAsync(adb, serial, firebaseProps);
        }

        var entries = await logcat.ReadAsync(ct);
        var pids = pidsBefore.Concat(await LogcatMonitor.PidsAsync(adb, serial, package, ct)).ToHashSet();
        var findings = LogcatMonitor.Analyze(entries, package, pids);

        var events = SessionAnalyzer.MergeEvents(hooked, SessionAnalyzer.EventsFromNetwork(calls), SessionAnalyzer.EventsFromLogcat(entries));
        var issues = new List<QaIssue>();
        issues.AddRange(LogcatMonitor.ToIssues(findings));
        issues.AddRange(SessionAnalyzer.AnalyzeNetwork(calls));
        issues.AddRange(SessionAnalyzer.CheckEvents(events, trackingPlanJson));

        if (checkDataVsUi && calls.Count > 0)
        {
            try
            {
                finalScreen = await adb.CaptureAsync(serial, ct);
                var texts = finalScreen.Elements
                    .Select(e => (Text: e.Runtime?.Text?.Value ?? e.Text ?? e.ContentDescription, Bounds: (Rect?)e.Bounds))
                    .Where(t => !string.IsNullOrWhiteSpace(t.Text)).Select(t => (t.Text!, t.Bounds));
                issues.AddRange(SessionAnalyzer.CompareDataWithUi(texts, calls));
            }
            catch (InvalidOperationException e)
            {
                notes.Add($"Data vs UI not checked: {e.Message}");
            }
        }

        var sections = new List<QaReport.Section>
        {
            new($"Network ({calls.Count} calls)", SessionAnalyzer.NetworkTable(calls)),
            new($"Analytics ({events.Count} events)", SessionAnalyzer.EventsTable(events)),
            new($"Log ({entries.Count} lines read, {findings.Count} findings)", LogTable(findings)),
        };
        if (recorder is not null) notes.Insert(0, $"Frida hooks: {recorder.Hooks}");

        var (markdown, dir) = QaReport.Write(settings.OutputDirectory, "monitor", $"Session monitor — {package}", issues, sections, notes);
        SaveJson(Path.Combine(dir, "network.json"), calls);
        SaveJson(Path.Combine(dir, "analytics.json"), events);
        return markdown;
    }

    [McpServerTool(Name = "audit_content"), Description(
        "Checks every text on the current screen for content bugs: leaked placeholders (null, undefined, NaN, {name}, %s, lorem ipsum), raw " +
        "translation keys (home.title), float artifacts (0.30000000000000004), whitespace problems, untranslated text for the given locale, " +
        "and dates, month names and numbers formatted against the locale. Works on Android and iOS.")]
    public async Task<string> AuditContent(
        [Description("Expected locale of the UI (BCP-47, e.g. uk-UA, de-DE). Optional: without it the untranslated and format checks are skipped.")] string? locale = null,
        [Description("Comma-separated words never flagged (brand and product names). Optional.")] string? allowlist = null,
        [Description("Device serial or UDID. Optional.")] string? deviceSerial = null,
        [Description("true to scroll through the whole screen first (Android). Default false.")] bool scroll = false,
        CancellationToken ct = default)
    {
        var capture = await CaptureAsync(deviceSerial, scroll, ct);
        var allow = allowlist?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var issues = ContentAuditor.Audit(capture, locale, allow);
        var texts = capture.Elements.Count(e => !string.IsNullOrWhiteSpace(e.Text ?? e.ContentDescription));
        return QaReport.Write(settings.OutputDirectory, "content", "Content audit", issues,
            notes: [$"{texts} texts checked{(locale is null ? "" : $" for {locale}")}. Screenshot: {capture.ScreenshotPath}"]).Markdown;
    }

    [McpServerTool(Name = "audit_design_tokens"), Description(
        "Design token lint: checks that the running app only uses the design system's values — text and background colors, borders, icon " +
        "colors, font sizes, families and weights, corner radii and spacing — without needing the Figma frame of this screen. The palette " +
        "comes from one or more Figma frames (a design system page or other screens) and/or a tokens JSON (Tokens Studio, W3C or a flat map). " +
        "Needs Frida on Android to read the app's values.")]
    public async Task<string> AuditDesignTokens(
        [Description("Comma-separated Figma frame URLs whose colors, fonts, radii and spacing make the palette. Optional if tokensJson is given.")] string? figmaUrls = null,
        [Description("Optional tokens JSON: {\"primary\":\"#262B36\",\"radius-m\":12} or nested {\"value\":...,\"type\":...} / {\"$value\":...,\"$type\":...}.")] string? tokensJson = null,
        [Description("Device serial. Optional.")] string? deviceSerial = null,
        [Description("Color tolerance (RGB distance). Default 6.")] double colorTolerance = 6,
        [Description("Spacing grid used when the palette has no spacing tokens. Default 4.")] int spacingGrid = 4,
        [Description("true to scroll through the whole screen first (Android). Default false.")] bool scroll = false,
        [Description("true to download the Figma frames again instead of the cache.")] bool refresh = false,
        CancellationToken ct = default)
    {
        var urls = figmaUrls?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (urls.Length == 0 && string.IsNullOrWhiteSpace(tokensJson))
            throw new ArgumentException("Give figmaUrls (frames that make the palette) and/or tokensJson.");

        var frames = new List<DesignFrame>();
        foreach (var url in urls) frames.Add(await figma.GetFrameAsync(url, refresh, ct));
        var palette = DesignTokenAuditor.BuildPalette(frames, tokensJson);

        var capture = await CaptureAsync(deviceSerial, scroll, ct);
        var notes = new List<string>();
        if (capture.RuntimeCount == 0)
            notes.Add($"The app's values weren't read (Frida: {capture.RuntimeError ?? "not available"}); nothing could be checked.");
        var issues = DesignTokenAuditor.Audit(capture, palette, colorTolerance, spacingGrid);
        return QaReport.Write(settings.OutputDirectory, "tokens", "Design token audit", issues,
            [new QaReport.Section("Design system palette", DesignTokenAuditor.Describe(palette))], notes).Markdown;
    }

    [McpServerTool(Name = "test_resilience"), Description(
        "Puts the app on an Android device through adverse conditions and checks it copes: offline-launch (killed, network off, " +
        "relaunched: no crash, blank screen, endless loader or forced sign-out), offline-action (network cut on the open screen, then a " +
        "pull-to-refresh or the given actions: an error must be shown), network-recovery (network off then on: the app must recover by " +
        "itself), slow-network (emulator), rotation, process-death (killed in the background and restored), permissions (runtime " +
        "permissions revoked), back, notification and incoming-call (emulator). Captures the screen before and after each, reads logcat " +
        "for crashes, and restores every setting afterwards.")]
    public async Task<string> TestResilience(
        [Description("Comma-separated scenarios, or \"all\": offline-launch, offline-action, network-recovery, slow-network, rotation, process-death, permissions, back, notification, incoming-call. Default all.")] string? scenarios = null,
        [Description("App package. Optional: the app in the foreground.")] string? package = null,
        [Description("Device serial. Optional.")] string? deviceSerial = null,
        [Description("Optional JSON array of ADB actions for offline-action, run once the network is cut (e.g. tap the button that loads data): [{\"type\":\"tap_text\",\"value\":\"Shop\"}]. Default: a pull-to-refresh.")] string? offlineActionsJson = null,
        CancellationToken ct = default)
    {
        var (adb, serial) = await AndroidAsync(deviceSerial, ct);
        package ??= await ForegroundPackageAsync(adb, serial, ct);
        var list = string.IsNullOrWhiteSpace(scenarios) || scenarios.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
            ? []
            : scenarios.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var runner = new ResilienceRunner(adb, settings) { OfflineActions = ParseActions(offlineActionsJson) };
        var results = await runner.RunAsync(serial, package, list, ct);

        var sb = new StringBuilder();
        sb.AppendLine("| Scenario | Result | Issues | Before | After | Notes |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Scenario} | {(r.Passed ? "✅ passed" : "❌ failed")} | {r.Issues.Count} | {r.BeforeScreenshot} | {r.AfterScreenshot} | {QaReport.Cell(r.Notes)} |");

        var issues = results.SelectMany(r => r.Issues.Select(i => i with { Category = $"{r.Scenario}: {i.Category}" })).ToList();
        return QaReport.Write(settings.OutputDirectory, "resilience", $"Resilience — {package}", issues,
            [new QaReport.Section("Scenarios", sb.ToString())]).Markdown;
    }

    [McpServerTool(Name = "measure_performance"), Description(
        "Measures the app's performance on an Android device: cold and warm start time (am start -W, median of several runs), jank while " +
        "scrolling (dumpsys gfxinfo: janky frames, frame-time percentiles), memory (dumpsys meminfo) and overdraw (debug overdraw " +
        "colors on the current screen). Restores the app and every debug setting afterwards. Restarts the app when measuring startup.")]
    public async Task<string> MeasurePerformance(
        [Description("App package. Optional: the app in the foreground.")] string? package = null,
        [Description("Device serial. Optional.")] string? deviceSerial = null,
        [Description("Runs per start type (median is reported). Default 3.")] int startRuns = 3,
        [Description("Measure cold/warm start (restarts the app). Default true.")] bool measureStartup = true,
        [Description("Measure jank while scrolling the current screen. Default true.")] bool measureJank = true,
        [Description("Measure overdraw on the current screen. Default true.")] bool measureOverdraw = true,
        CancellationToken ct = default)
    {
        var (adb, serial) = await AndroidAsync(deviceSerial, ct);
        package ??= await ForegroundPackageAsync(adb, serial, ct);
        var report = await new PerformanceProbe(adb, settings).MeasureAsync(serial, package, startRuns, measureStartup, measureJank, measureOverdraw, ct);
        return QaReport.Write(settings.OutputDirectory, "performance", $"Performance — {package}", report.Issues, report.Sections).Markdown;
    }

    [McpServerTool(Name = "audit_security"), Description(
        "Defensive security and privacy check of your own Android app: secrets and personal data in logcat, FLAG_SECURE on sensitive " +
        "screens, tokens stored in plaintext (SharedPreferences, React Native AsyncStorage; needs a rooted emulator or a debuggable build), " +
        "debuggable/backup flags, WebView remote debugging and cleartext HTTP (from an optional short network recording).")]
    public async Task<string> AuditSecurity(
        [Description("App package. Optional: the app in the foreground.")] string? package = null,
        [Description("Device serial. Optional.")] string? deviceSerial = null,
        [Description("true if the current screen shows sensitive data (payment, personal info), so it must block screenshots. Default false: only screens with a password field are expected to.")] bool screenIsSensitive = false,
        [Description("Seconds to record network traffic for the cleartext check (0 to skip). Default 0.")] int recordNetworkSec = 0,
        CancellationToken ct = default)
    {
        var (adb, serial) = await AndroidAsync(deviceSerial, ct);
        package ??= await ForegroundPackageAsync(adb, serial, ct);

        List<string>? urls = null;
        var notes = new List<string>();
        if (recordNetworkSec > 0)
        {
            try
            {
                await using var recorder = await FridaRecorder.StartAsync(settings, serial, package, ct);
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(recordNetworkSec, 1, 300)), ct);
                var (calls, _, _) = await recorder.StopAsync();
                urls = calls.Select(c => c.Url).OfType<string>().ToList();
                notes.Add($"{urls.Count} HTTP calls recorded for the cleartext check.");
            }
            catch (InvalidOperationException e)
            {
                notes.Add($"Network not recorded: {e.Message}");
            }
        }

        var report = await new SecurityAuditor(adb).AuditAsync(serial, package, screenIsSensitive, urls, ct);
        return QaReport.Write(settings.OutputDirectory, "security", $"Security & privacy — {package}", report.Issues, report.Sections, notes).Markdown;
    }

    // ---------- helpers ----------

    async Task<(AdbClient Adb, string Serial)> AndroidAsync(string? deviceSerial, CancellationToken ct)
    {
        var (client, device) = await devices.ResolveAsync(deviceSerial, ct);
        if (client is not AdbClient adb)
            throw new InvalidOperationException("This audit drives the device over ADB: it works on Android devices and emulators only.");
        return (adb, device.Serial);
    }

    async Task<ScreenCapture> CaptureAsync(string? deviceSerial, bool scroll, CancellationToken ct)
    {
        var (client, device) = await devices.ResolveAsync(deviceSerial, ct);
        if (!scroll) return await client.CaptureAsync(device.Serial, ct);
        if (client is AdbClient adb) return await adb.CaptureScrolledAsync(device.Serial, 8, ct);
        throw new InvalidOperationException("Scroll capture works on Android devices only.");
    }

    static async Task<string> ForegroundPackageAsync(AdbClient adb, string serial, CancellationToken ct)
    {
        var activity = ResilienceRunner.ParseResumedActivity(await adb.ShellAsync(serial, "dumpsys activity activities", ct));
        var package = activity?.Split('/')[0];
        return string.IsNullOrWhiteSpace(package)
            ? throw new InvalidOperationException("No app in the foreground: open the app or pass package.")
            : package;
    }

    static IReadOnlyList<DeviceAction> ParseActions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("actionsJson must be a JSON array.");
        return DeviceAction.ParseArray(doc.RootElement);
    }

    /// <summary>Firebase Analytics logs its events only with log.tag.FA at VERBOSE; returns the previous values.</summary>
    static async Task<Dictionary<string, string>> EnableFirebaseLoggingAsync(AdbClient adb, string serial, CancellationToken ct)
    {
        var previous = new Dictionary<string, string>();
        foreach (var tag in new[] { "log.tag.FA", "log.tag.FA-SVC" })
        {
            try
            {
                previous[tag] = (await adb.ShellAsync(serial, $"getprop {tag}", ct)).Trim();
                await adb.ShellAsync(serial, $"setprop {tag} VERBOSE", ct);
            }
            catch (InvalidOperationException) { }
        }
        return previous;
    }

    static async Task RestoreFirebaseLoggingAsync(AdbClient adb, string serial, Dictionary<string, string> previous)
    {
        foreach (var (tag, value) in previous)
        {
            // An empty value can't be set back with setprop; INFO is Android's default for an unset log tag.
            try { await adb.ShellAsync(serial, $"setprop {tag} {(string.IsNullOrEmpty(value) ? "INFO" : AdbClient.ShellQuote(value))}"); }
            catch (InvalidOperationException) { }
        }
    }

    static string LogTable(IReadOnlyList<LogFinding> findings)
    {
        if (findings.Count == 0) return "No crashes, ANRs or errors from the app.";
        var sb = new StringBuilder();
        sb.AppendLine("| Kind | Count | Summary | First lines |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var f in findings.OrderBy(f => f.Kind is "crash" or "native-crash" or "anr" ? 0 : f.Kind == "js-error" ? 1 : 2))
            sb.AppendLine($"| {f.Kind} | {f.Count} | {QaReport.Cell(f.Summary)} | {QaReport.Cell(string.Join(" ⏎ ", f.Lines.Take(4)))} |");
        return sb.ToString();
    }

    static void SaveJson<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
