using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Devices;

namespace Driftcheck.Mcp.Qa;

public sealed record ResilienceResult(
    string Scenario, bool Passed, IReadOnlyList<QaIssue> Issues, string? BeforeScreenshot, string? AfterScreenshot, string Notes);

/// <summary>
/// Puts the app through the situations a phone throws at it (no network, rotation, process death, revoked
/// permissions, back, a notification, a call) and reports crashes, lost state and a screen that left the app.
/// Every change to the device is undone afterwards, even when the capture fails.
/// </summary>
public sealed partial class ResilienceRunner(AdbClient adb, Settings settings)
{
    public static IReadOnlyList<string> AllScenarios { get; } =
    [
        // network-recovery first: it needs a healthy screen to start from, and the other two can leave an error state.
        "network-recovery", "offline-launch", "offline-action", "slow-network", "rotation", "process-death",
        "permissions", "back", "notification", "incoming-call",
    ];

    /// <summary>Old names still accepted: "offline" was the network cut on an open screen.</summary>
    static readonly Dictionary<string, string> Aliases = new() { ["offline"] = "offline-action" };

    /// <summary>
    /// What offline-action does once the network is cut (tap the button that loads data, open a tab…). Without it,
    /// a pull-to-refresh on the current screen.
    /// </summary>
    public IReadOnlyList<DeviceAction>? OfflineActions { get; init; }

    const string Category = "resilience";
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(2500);

    public async Task<IReadOnlyList<ResilienceResult>> RunAsync(
        string serial, string package, IReadOnlyList<string> scenarios, CancellationToken ct = default)
    {
        var names = NormalizeScenarios(scenarios);
        var dir = Path.Combine(settings.OutputDirectory, "qa", $"resilience-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);

        var results = new List<ResilienceResult>();
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunScenarioAsync(serial, package, name, dir, ct));
        }
        return results;
    }

    // One scenario: before capture, log monitor, apply, settle, after capture, restore (always), evaluate.
    sealed class Run
    {
        public required string Serial { get; init; }
        public required string Package { get; init; }
        public required string Name { get; init; }
        public required string Dir { get; init; }
        public required ScreenCapture Before { get; init; }
        public required string BeforePath { get; init; }
        public required byte[] BeforeBytes { get; init; }
        public required IReadOnlyList<int> PidsBefore { get; init; }
        public ScreenCapture? After { get; set; }
        public string? AfterPath { get; set; }
        public byte[]? AfterBytes { get; set; }
        public List<QaIssue> Issues { get; } = [];
        public List<string> Notes { get; } = [];
        /// <summary>Severity of "App left the foreground"; null when leaving is expected (it's reported separately).</summary>
        public QaSeverity? LeaveSeverity { get; set; } = QaSeverity.Error;
        public bool Skipped { get; set; }
    }

    async Task<ResilienceResult> RunScenarioAsync(string serial, string package, string name, string dir, CancellationToken ct)
    {
        string? beforePath = null;
        try
        {
            var before = await adb.CaptureAsync(serial, ct);
            beforePath = Path.Combine(dir, $"{name}-before.png");
            File.Copy(before.ScreenshotPath, beforePath, overwrite: true);
            var pids = await SafeAsync(() => LogcatMonitor.PidsAsync(adb, serial, package, ct)) ?? [];
            var run = new Run
            {
                Serial = serial, Package = package, Name = name, Dir = dir, Before = before,
                BeforePath = beforePath, BeforeBytes = await File.ReadAllBytesAsync(beforePath, ct), PidsBefore = pids,
            };
            var monitor = await LogcatMonitor.StartAsync(adb, serial, ct);

            try
            {
                await ExecuteAsync(run, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                run.Issues.Add(new QaIssue(Category, QaSeverity.Error, $"Scenario '{name}' failed to run", ex.Message));
            }

            // Crashes during the scenario, whichever pids the app has now.
            try
            {
                var entries = await monitor.ReadAsync(ct);
                var now = await SafeAsync(() => LogcatMonitor.PidsAsync(adb, serial, package, ct)) ?? [];
                var all = run.PidsBefore.Concat(now).Distinct().ToList();
                // Only what breaks the app counts here: the app's ordinary error and warning lines repeat on every
                // relaunch and belong in monitor_session, not in each scenario.
                run.Issues.AddRange(LogcatMonitor.ToIssues(LogcatMonitor.Analyze(entries, package, all)
                    .Where(f => f.Kind is "crash" or "native-crash" or "anr" or "js-error")));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Notes.Add($"Log could not be read: {ex.Message}");
            }

            if (!run.Skipped && run.After is { } after)
            {
                var fg = after.App?.Id;
                if (fg != package && run.LeaveSeverity is { } severity)
                    run.Issues.Add(new QaIssue(Category, severity, "App left the foreground",
                        $"Foreground after '{name}': {fg ?? "unknown"}", Fix: "The app should stay on screen or come back by itself."));
            }

            // Leave the device on the app for the next scenario.
            if (run.After is { } a && a.App?.Id != package)
                await SafeAsync(() => LaunchAsync(serial, package, 1500, ct));

            return new ResilienceResult(name, run.Issues.All(i => i.Severity != QaSeverity.Error), run.Issues,
                run.BeforePath, run.AfterPath, string.Join(" ", run.Notes));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var issue = new QaIssue(Category, QaSeverity.Error, $"Scenario '{name}' failed to start", ex.Message);
            return new ResilienceResult(name, false, [issue], beforePath, null, ex.Message);
        }
    }

    Task ExecuteAsync(Run run, CancellationToken ct) => run.Name switch
    {
        "offline-launch" => OfflineLaunchAsync(run, ct),
        "offline-action" => OfflineActionAsync(run, ct),
        "network-recovery" => NetworkRecoveryAsync(run, ct),
        "slow-network" => SlowNetworkAsync(run, ct),
        "rotation" => RotationAsync(run, ct),
        "process-death" => ProcessDeathAsync(run, ct),
        "permissions" => PermissionsAsync(run, ct),
        "back" => BackAsync(run, ct),
        "notification" => NotificationAsync(run, ct),
        "incoming-call" => IncomingCallAsync(run, ct),
        _ => throw new ArgumentException(UnknownScenarioMessage(run.Name)),
    };

    // --- scenarios ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The app started from scratch with no network: it must not crash, must not sit on a blank screen or an
    /// endless loader, must not log the user out, and should show cached data or a clear offline state.
    /// </summary>
    async Task OfflineLaunchAsync(Run run, CancellationToken ct)
    {
        await adb.ShellAsync(run.Serial, $"am force-stop {AdbClient.ShellQuote(run.Package)}", ct);
        var network = await NetworkOffAsync(run.Serial, ct);
        byte[] at3s;
        try
        {
            await LaunchAsync(run.Serial, run.Package, 3000, ct);
            at3s = await SaveScreenshotAsync(run, "after-3s", ct);
            await Task.Delay(5000, ct);
            await CaptureAfterAsync(run, ct);
        }
        finally
        {
            await network.RestoreAsync();
        }

        var after = run.After!;
        var afterBytes = run.AfterBytes!;
        AddVisibleTexts(run, "Screen after an offline launch");

        if (IsBlank(afterBytes))
            run.Issues.Add(new QaIssue(Category, QaSeverity.Error, "Blank screen after an offline launch",
                "8 s after launch the screen is a single color: nothing was rendered.",
                Fix: "Render cached content or an offline/error state instead of waiting for the network."));
        else if (!AdbClient.ScreenshotsMatch(at3s, afterBytes) && !HasOfflineMessage(after))
            run.Issues.Add(new QaIssue(Category, QaSeverity.Warning, "Still loading 8 s after an offline launch",
                "The screen kept changing between 3 s and 8 s and shows no offline message: an endless loader.",
                Fix: "Time out the request and show an offline/error state with a retry."));

        if (LooksLikeLogin(after) && !LooksLikeLogin(run.Before))
            run.Issues.Add(new QaIssue(Category, QaSeverity.Error, "Logged out by an offline launch",
                "The app showed a sign-in screen; it was signed in before.",
                Fix: "Don't sign the user out when the token refresh fails for lack of network; retry when it comes back."));

        AddContentIssues(run, after);
    }

    /// <summary>
    /// The network drops while the app is open, then the user acts (pull-to-refresh or the given actions): the app
    /// must tell them something went wrong, not stay silent or crash.
    /// </summary>
    async Task OfflineActionAsync(Run run, CancellationToken ct)
    {
        var network = await NetworkOffAsync(run.Serial, ct);
        try
        {
            if (OfflineActions is { Count: > 0 } actions)
                await DeviceActionExecutor.ExecuteAsync(adb, run.Serial, actions, ct);
            else
                await PullToRefreshAsync(run.Serial, ct);
            await Task.Delay(3000, ct);
            await CaptureAfterAsync(run, ct);
        }
        finally
        {
            await network.RestoreAsync();
        }

        AddVisibleTexts(run, "Screen after acting offline");
        if (HasOfflineMessage(run.After!))
            run.Notes.Add("An offline/error message is shown.");
        else if (AdbClient.ScreenshotsMatch(run.BeforeBytes, run.AfterBytes!))
            run.Issues.Add(new QaIssue(Category, QaSeverity.Warning, "No feedback when acting offline",
                (OfflineActions is { Count: > 0 } ? "The actions" : "A pull-to-refresh") + " without network left the screen unchanged.",
                Fix: "Show an offline banner, a toast or an error state with a retry. (A toast isn't in the view tree: check the after screenshot.)"));
        else
            run.Notes.Add("The screen changed but no offline/error text was found; check the after screenshot.");
        AddContentIssues(run, run.After!);
    }

    /// <summary>
    /// Network off for a while, then back on: without any action from the user, the app should drop its offline
    /// state and show the same content as before.
    /// </summary>
    async Task NetworkRecoveryAsync(Run run, CancellationToken ct)
    {
        // Already showing an error with the network on: an earlier failure the app never recovered from.
        if (HasOfflineMessage(run.Before))
        {
            await CaptureAfterAsync(run, ct);
            run.Issues.Add(new QaIssue(Category, QaSeverity.Error, "Doesn't recover by itself",
                "The screen already showed an offline/error message with the network on: an earlier failure was never retried.",
                Fix: "Listen for connectivity changes and retry, or hide the offline state when a request succeeds."));
            return;
        }

        var network = await NetworkOffAsync(run.Serial, ct);
        ScreenCapture offline;
        try
        {
            await Task.Delay(4000, ct);
            await SaveScreenshotAsync(run, "offline", ct);
            offline = await adb.CaptureAsync(run.Serial, ct);
        }
        finally
        {
            await network.RestoreAsync();
        }

        await Task.Delay(8000, ct); // Reconnecting, then the app's retry.
        await CaptureAfterAsync(run, ct);
        var after = run.After!;

        if (HasOfflineMessage(after))
            run.Issues.Add(new QaIssue(Category, QaSeverity.Error, "Still offline after the network came back",
                "8 s after reconnecting the offline/error message is still on screen.",
                Fix: "Listen for connectivity changes and retry, or hide the offline state when a request succeeds."));

        var missing = MissingTexts(VisibleTexts(run.Before), VisibleTexts(after));
        if (TextOverlap(VisibleTexts(run.Before), VisibleTexts(after)) < 0.5 && after.App?.Id == run.Package)
            run.Issues.Add(new QaIssue(Category, QaSeverity.Warning, "Content didn't come back after reconnecting",
                "Missing compared to before going offline: " + Join(missing),
                Fix: "Reload the data when the connection is restored."));

        if (HasOfflineMessage(offline)) run.Notes.Add("While offline the app showed an offline/error message.");
    }

    async Task SlowNetworkAsync(Run run, CancellationToken ct)
    {
        if (!run.Serial.StartsWith("emulator-", StringComparison.Ordinal))
        {
            run.Skipped = true;
            run.Issues.Add(new QaIssue(Category, QaSeverity.Info, "Slow network skipped", "Only emulators can throttle the network (adb emu network)."));
            return;
        }
        try
        {
            await EmuAsync(run.Serial, ["network", "speed", "gsm"], ct);
            await EmuAsync(run.Serial, ["network", "delay", "gprs"], ct);
            await Task.Delay(Settle, ct);
            await CaptureAfterAsync(run, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(8000 - Settle.TotalMilliseconds), ct);
            var later = await adb.CaptureAsync(run.Serial, ct);
            var laterPath = Path.Combine(run.Dir, $"{run.Name}-after-8s.png");
            File.Copy(later.ScreenshotPath, laterPath, overwrite: true);
            if (!AdbClient.ScreenshotsMatch(run.AfterBytes!, await File.ReadAllBytesAsync(laterPath, ct)))
                run.Issues.Add(new QaIssue(Category, QaSeverity.Info, "Screen still changing after 8 s",
                    "Content or a loader was still moving on a slow connection.", Fix: "Check that a loading state is shown and that there's a timeout."));
        }
        finally
        {
            await SafeAsync(() => EmuAsync(run.Serial, ["network", "speed", "full"], CancellationToken.None));
            await SafeAsync(() => EmuAsync(run.Serial, ["network", "delay", "none"], CancellationToken.None));
        }
        EvaluateOfflineLike(run, "slow network");
    }

    async Task RotationAsync(Run run, CancellationToken ct)
    {
        var accel = ParseSettingInt(await adb.ShellAsync(run.Serial, "settings get system accelerometer_rotation", ct));
        var user = ParseSettingInt(await adb.ShellAsync(run.Serial, "settings get system user_rotation", ct));
        var activityBefore = ParseResumedActivity(await adb.ShellAsync(run.Serial, "dumpsys activity activities", ct));
        string? activityAfter = null;
        try
        {
            await adb.ShellAsync(run.Serial, "settings put system accelerometer_rotation 0", ct);
            await adb.ShellAsync(run.Serial, "settings put system user_rotation 1", ct);
            await Task.Delay(Settle, ct);
            await CaptureAfterAsync(run, ct);
            activityAfter = ParseResumedActivity(await adb.ShellAsync(run.Serial, "dumpsys activity activities", ct));
        }
        finally
        {
            await SafeAsync(() => adb.ShellAsync(run.Serial, $"settings put system user_rotation {user ?? 0}", CancellationToken.None));
            await SafeAsync(() => adb.ShellAsync(run.Serial, $"settings put system accelerometer_rotation {accel ?? 1}", CancellationToken.None));
        }

        var (w, h) = PngSize(run.AfterBytes!) ?? (run.After!.WidthPx, run.After.HeightPx);
        if (w > h)
        {
            run.Notes.Add("Rotated to landscape.");
            var beforeTexts = VisibleTexts(run.Before);
            var lost = MissingTexts(beforeTexts, VisibleTexts(run.After!));
            var beforeCount = beforeTexts.Distinct().Count();
            if (lost.Count > 0 && beforeCount > 0 && (activityBefore is null || activityBefore == activityAfter))
            {
                var ratio = (double)lost.Count / beforeCount;
                run.Issues.Add(new QaIssue(Category, ratio > 0.5 ? QaSeverity.Error : QaSeverity.Warning,
                    "Texts missing after rotation", $"{lost.Count} of {beforeCount} texts gone: {Join(lost)}",
                    Fix: "Check the landscape layout and that state survives the configuration change."));
            }
        }
        else
        {
            run.Issues.Add(new QaIssue(Category, QaSeverity.Info, "App is locked to portrait",
                "The screen stayed in portrait with rotation forced to landscape."));
        }
    }

    async Task ProcessDeathAsync(Run run, CancellationToken ct)
    {
        try
        {
            await adb.KeyEventAsync(run.Serial, "KEYCODE_HOME", ct);
            await Task.Delay(1000, ct);
            await adb.ShellAsync(run.Serial, $"am kill {AdbClient.ShellQuote(run.Package)}", ct);
            await Task.Delay(500, ct);
            var pidsNow = await LogcatMonitor.PidsAsync(adb, run.Serial, run.Package, ct);
            if (run.PidsBefore.Count > 0 && pidsNow.Intersect(run.PidsBefore).Any())
                run.Issues.Add(new QaIssue(Category, QaSeverity.Info, "am kill did not kill the app",
                    $"pid {string.Join(',', pidsNow)} still running (a foreground service keeps it alive?)."));
        }
        finally
        {
            // Bring the task back even when the kill failed half-way.
            await SafeAsync(() => LaunchAsync(run.Serial, run.Package, 3000, CancellationToken.None));
        }
        await CaptureAfterAsync(run, ct);

        var before = VisibleTexts(run.Before);
        var after = VisibleTexts(run.After!);
        var overlap = TextOverlap(before, after);
        if (overlap < 0.5)
        {
            var lost = MissingTexts(before, after);
            run.Issues.Add(new QaIssue(Category, QaSeverity.Error, "State lost after process death",
                $"Only {overlap:P0} of the texts are the same. Gone: {Join(lost)}",
                Fix: "Persist screen state (navigation, form input) and restore it on restart."));
        }
    }

    async Task PermissionsAsync(Run run, CancellationToken ct)
    {
        var granted = ParseGrantedPermissions(await adb.ShellAsync(run.Serial, $"dumpsys package {AdbClient.ShellQuote(run.Package)}", ct));
        if (granted.Count == 0)
        {
            run.Skipped = true;
            run.Issues.Add(new QaIssue(Category, QaSeverity.Warning, "No granted runtime permissions to revoke",
                "Grant the permissions (camera, location...) first, then run this scenario again."));
            return;
        }

        var revoked = new List<string>();
        try
        {
            foreach (var perm in granted)
            {
                await adb.ShellAsync(run.Serial, $"pm revoke {AdbClient.ShellQuote(run.Package)} {AdbClient.ShellQuote(perm)}", ct);
                revoked.Add(perm);
            }
            await LaunchAsync(run.Serial, run.Package, 3000, ct);
            await CaptureAfterAsync(run, ct);
        }
        finally
        {
            foreach (var perm in revoked)
                await SafeAsync(() => adb.ShellAsync(run.Serial, $"pm grant {AdbClient.ShellQuote(run.Package)} {AdbClient.ShellQuote(perm)}", CancellationToken.None));
            // Revoking kills the app; leave it running again.
            if (revoked.Count > 0) await SafeAsync(() => LaunchAsync(run.Serial, run.Package, 1500, CancellationToken.None));
        }
        run.Issues.Add(new QaIssue(Category, QaSeverity.Info, "Permissions revoked and restored",
            string.Join(", ", revoked.Select(ShortPermission))));
    }

    async Task BackAsync(Run run, CancellationToken ct)
    {
        run.LeaveSeverity = null;
        await adb.KeyEventAsync(run.Serial, "KEYCODE_BACK", ct);
        await Task.Delay(Settle, ct);
        await CaptureAfterAsync(run, ct);
        if (run.After!.App?.Id != run.Package)
        {
            run.Issues.Add(new QaIssue(Category, QaSeverity.Info, "Back left the app",
                $"Foreground is now {run.After.App?.Id ?? "the launcher"}. Expected on a root screen."));
            await SafeAsync(() => LaunchAsync(run.Serial, run.Package, 1500, CancellationToken.None));
        }
    }

    async Task NotificationAsync(Run run, CancellationToken ct)
    {
        try
        {
            await adb.ShellAsync(run.Serial, "cmd notification post -S bigtext -t \"Driftcheck\" driftcheck_test \"Test notification\"", ct);
            await Task.Delay(Settle, ct);
            await CaptureAfterAsync(run, ct);
        }
        finally
        {
            await SafeAsync(() => adb.ShellAsync(run.Serial, "cmd notification cancel driftcheck_test", CancellationToken.None));
        }
    }

    async Task IncomingCallAsync(Run run, CancellationToken ct)
    {
        if (!run.Serial.StartsWith("emulator-", StringComparison.Ordinal))
        {
            run.Skipped = true;
            run.Issues.Add(new QaIssue(Category, QaSeverity.Info, "Incoming call skipped", "Only emulators can simulate a call (adb emu gsm)."));
            return;
        }
        run.LeaveSeverity = QaSeverity.Warning;
        try
        {
            await EmuAsync(run.Serial, ["gsm", "call", "5551234"], ct);
            await Task.Delay(3000, ct);
            await EmuAsync(run.Serial, ["gsm", "cancel", "5551234"], ct);
            await Task.Delay(2000, ct);
            await CaptureAfterAsync(run, ct);
        }
        finally
        {
            await SafeAsync(() => EmuAsync(run.Serial, ["gsm", "cancel", "5551234"], CancellationToken.None));
        }
    }

    // --- shared steps ------------------------------------------------------------------------------------------

    async Task CaptureAfterAsync(Run run, CancellationToken ct)
    {
        var after = await adb.CaptureAsync(run.Serial, ct);
        var path = Path.Combine(run.Dir, $"{run.Name}-after.png");
        File.Copy(after.ScreenshotPath, path, overwrite: true);
        run.After = after;
        run.AfterPath = path;
        run.AfterBytes = await File.ReadAllBytesAsync(path, ct);
    }

    /// <summary>Turns Wi-Fi and mobile data off; RestoreAsync turns back on what was on.</summary>
    async Task<NetworkState> NetworkOffAsync(string serial, CancellationToken ct)
    {
        var wasWifi = ParseSettingInt(await adb.ShellAsync(serial, "settings get global wifi_on", ct));
        var wasData = ParseSettingInt(await adb.ShellAsync(serial, "settings get global mobile_data", ct));
        var state = new NetworkState(adb, serial, wasWifi is not 0, wasData is not 0);
        try
        {
            await adb.ShellAsync(serial, "svc wifi disable", ct);
            await adb.ShellAsync(serial, "svc data disable", ct);
            await Task.Delay(1500, ct); // Let connectivity notice before the app is used.
        }
        catch
        {
            await state.RestoreAsync();
            throw;
        }
        return state;
    }

    sealed class NetworkState(AdbClient adb, string serial, bool wifi, bool data)
    {
        public async Task RestoreAsync()
        {
            if (wifi) await SafeAsync(() => adb.ShellAsync(serial, "svc wifi enable", CancellationToken.None));
            if (data) await SafeAsync(() => adb.ShellAsync(serial, "svc data enable", CancellationToken.None));
        }
    }

    async Task PullToRefreshAsync(string serial, CancellationToken ct)
    {
        var size = await adb.ShellAsync(serial, "wm size", ct);
        var m = Regex.Matches(size, @"(\d+)x(\d+)");
        var (w, h) = m.Count > 0 ? (int.Parse(m[^1].Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m[^1].Groups[2].Value, CultureInfo.InvariantCulture)) : (1080, 2400);
        await adb.SwipeAsync(serial, w / 2, (int)(h * 0.3), w / 2, (int)(h * 0.75), 500, ct);
    }

    async Task<byte[]> SaveScreenshotAsync(Run run, string suffix, CancellationToken ct)
    {
        var bytes = await adb.ScreencapAsync(run.Serial, ct);
        await File.WriteAllBytesAsync(Path.Combine(run.Dir, $"{run.Name}-{suffix}.png"), bytes, ct);
        return bytes;
    }

    void AddVisibleTexts(Run run, string title)
    {
        var texts = VisibleTexts(run.After!).Distinct().ToList();
        run.Issues.Add(new QaIssue(Category, QaSeverity.Info, title, texts.Count == 0 ? "No visible texts." : "Visible texts: " + Join(texts)));
    }

    /// <summary>Leaked placeholders and raw translation keys on the offline screen (error states are where they hide).</summary>
    static void AddContentIssues(Run run, ScreenCapture capture)
    {
        foreach (var issue in ContentAuditor.Audit(capture).Where(i => i.Category is "placeholder" or "i18n-key"))
            run.Issues.Add(issue with { Category = $"{Category}/{issue.Category}" });
    }

    /// <summary>A visible text that says the network or the request failed (English and Ukrainian/Russian).</summary>
    public static bool HasOfflineMessage(ScreenCapture capture) =>
        VisibleTexts(capture).Any(t => OfflineTextRegex().IsMatch(t));

    /// <summary>The screen asks to sign in: a password field, or sign-in words on a button or title.</summary>
    public static bool LooksLikeLogin(ScreenCapture capture) =>
        VisibleTexts(capture).Count(t => LoginTextRegex().IsMatch(t)) >= 2;

    /// <summary>
    /// A screen of one color (a blank or splash-colored screen): over 97 % of the sampled pixels, status and
    /// navigation bars left out, are the same color.
    /// </summary>
    public static bool IsBlank(byte[] png)
    {
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        if (bitmap is null) return false;
        var counts = new Dictionary<uint, int>();
        var total = 0;
        int top = (int)(bitmap.Height * 0.06), bottom = (int)(bitmap.Height * 0.92);
        for (var y = top; y < bottom; y += 8)
        for (var x = 0; x < bitmap.Width; x += 8)
        {
            var c = bitmap.GetPixel(x, y);
            var key = (uint)((c.Red >> 3) << 10 | (c.Green >> 3) << 5 | (c.Blue >> 3));
            counts[key] = counts.GetValueOrDefault(key) + 1;
            total++;
        }
        return total > 0 && counts.Values.Max() > total * 0.97;
    }

    void EvaluateOfflineLike(Run run, string label)
    {
        if (run.After is null || run.AfterBytes is null) return;
        var texts = VisibleTexts(run.After).Distinct().ToList();
        run.Issues.Add(new QaIssue(Category, QaSeverity.Info, $"Screen while {label}",
            texts.Count == 0 ? "No visible texts." : "Visible texts: " + Join(texts)));
        if (AdbClient.ScreenshotsMatch(run.BeforeBytes, run.AfterBytes))
            run.Issues.Add(new QaIssue(Category, QaSeverity.Warning, $"No feedback shown while {label}",
                "The screen is identical to the one before.", Fix: "Show an offline/loading/error state so the user knows what is happening."));
    }

    async Task LaunchAsync(string serial, string package, int waitMs, CancellationToken ct)
    {
        await adb.ShellAsync(serial, $"monkey -p {AdbClient.ShellQuote(package)} -c android.intent.category.LAUNCHER 1", ct);
        await Task.Delay(waitMs, ct);
    }

    /// <summary>`adb -s serial emu ...`: the emulator console, which is a host-side command, not a device shell one.</summary>
    async Task EmuAsync(string serial, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(settings.AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(serial);
        psi.ArgumentList.Add("emu");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not run {settings.AdbPath}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        _ = await stdout;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"adb emu {string.Join(' ', args)} failed ({process.ExitCode}): {await stderr}");
    }

    static async Task<T?> SafeAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception) { return default; }
    }

    static async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception) { /* restoring is best effort: the other restores must still run */ }
    }

    // --- pure helpers (unit-tested) ----------------------------------------------------------------------------

    /// <summary>Scenario names, trimmed and lower-cased; "all" or an empty list means every scenario.</summary>
    public static IReadOnlyList<string> NormalizeScenarios(IReadOnlyList<string>? scenarios)
    {
        if (scenarios is null || scenarios.Count == 0) return AllScenarios;
        var names = scenarios.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        if (names.Contains("all")) return AllScenarios;
        names = names.Select(n => Aliases.GetValueOrDefault(n, n)).Distinct().ToList();
        foreach (var n in names)
            if (!AllScenarios.Contains(n)) throw new ArgumentException(UnknownScenarioMessage(n));
        return names;
    }

    public static string UnknownScenarioMessage(string name) =>
        $"Unknown resilience scenario '{name}'. Valid scenarios: {string.Join(", ", AllScenarios)}.";

    /// <summary>The value of `settings get`: an integer, or null for "null" / empty / anything else.</summary>
    public static int? ParseSettingInt(string? output) =>
        int.TryParse(output?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Runtime permissions with granted=true from the "runtime permissions:" blocks of `dumpsys package`.</summary>
    public static IReadOnlyList<string> ParseGrantedPermissions(string dumpsys)
    {
        var result = new List<string>();
        var inSection = false;
        foreach (var raw in dumpsys.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim() == "runtime permissions:") { inSection = true; continue; }
            if (!inSection) continue;
            var m = PermissionRegex().Match(line);
            if (!m.Success) { inSection = false; continue; }
            if (m.Groups[2].Value == "true" && !result.Contains(m.Groups[1].Value)) result.Add(m.Groups[1].Value);
        }
        return result;
    }

    /// <summary>Component of the resumed activity in `dumpsys activity activities` ("co.app/.MainActivity").</summary>
    public static string? ParseResumedActivity(string dumpsys)
    {
        var m = ResumedRegex().Match(dumpsys);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Jaccard similarity of two sets of texts (trimmed, empty ones ignored). Two empty sets are the same screen.</summary>
    public static double TextOverlap(IEnumerable<string> before, IEnumerable<string> after)
    {
        var a = NormalizeTexts(before);
        var b = NormalizeTexts(after);
        if (a.Count == 0 && b.Count == 0) return 1;
        return (double)a.Intersect(b).Count() / a.Union(b).Count();
    }

    /// <summary>Texts in <paramref name="before"/> that are not in <paramref name="after"/>.</summary>
    public static IReadOnlyList<string> MissingTexts(IEnumerable<string> before, IEnumerable<string> after)
    {
        var b = NormalizeTexts(after);
        return NormalizeTexts(before).Where(t => !b.Contains(t)).ToList();
    }

    /// <summary>Width and height from a PNG header, or null if it isn't a PNG.</summary>
    public static (int Width, int Height)? PngSize(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return null;
        int Be(int o) => (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
        return (Be(16), Be(20));
    }

    /// <summary>The user-visible texts of a capture: element text, or the content description when there is none.</summary>
    public static IReadOnlyList<string> VisibleTexts(ScreenCapture capture) =>
        capture.Elements
            .Select(e => !string.IsNullOrWhiteSpace(e.Text) ? e.Text! : e.ContentDescription ?? "")
            .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    static HashSet<string> NormalizeTexts(IEnumerable<string> texts) =>
        texts.Select(t => t?.Trim() ?? "").Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);

    static string Join(IEnumerable<string> texts)
    {
        var list = texts.ToList();
        var shown = string.Join(" | ", list.Take(8).Select(t => t.Length > 40 ? t[..40] + "…" : t));
        return list.Count > 8 ? $"{shown} (+{list.Count - 8} more)" : shown;
    }

    static string ShortPermission(string permission) => permission.StartsWith("android.permission.", StringComparison.Ordinal)
        ? permission["android.permission.".Length..] : permission;

    // "        android.permission.CAMERA: granted=false, flags=[ USER_SENSITIVE_WHEN_GRANTED]"
    [GeneratedRegex(@"(?i)\b(offline|no (internet|connection|network)|network (error|request failed|unavailable)|connection (lost|failed|error)|check your (internet|connection)|couldn.?t (connect|load)|failed to (load|connect)|try again)\b|немає (інтернет|з.єднан|мереж)|нет (интернет|соединени|сети)|помилка (мережі|з.єднання)|спробуйте ще|офлайн")]
    private static partial Regex OfflineTextRegex();

    [GeneratedRegex(@"(?i)\b(log ?in|sign ?in|sign ?up|password|forgot|create account)\b|увійти|вхід|пароль|зареєструватися|войти|вход")]
    private static partial Regex LoginTextRegex();

    [GeneratedRegex(@"^\s+([\w.]+): granted=(true|false)")]
    private static partial Regex PermissionRegex();

    // "topResumedActivity=ActivityRecord{43bdc4c u0 co.gifted.app/.MainActivity t673}"
    [GeneratedRegex(@"topResumedActivity=ActivityRecord\{\S+ u\d+ (\S+)")]
    private static partial Regex ResumedRegex();
}
