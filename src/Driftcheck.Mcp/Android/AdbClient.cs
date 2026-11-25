using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Driftcheck.Mcp.Devices;

namespace Driftcheck.Mcp.Android;

public sealed partial class AdbClient(Settings settings) : IDeviceClient
{
    public DevicePlatform Platform => DevicePlatform.Android;

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        var output = await RunTextAsync(null, ["devices"], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length == 2 && p[1] == "device")
            .Select(p => new DeviceInfo(p[0], "Android"))
            .ToList();
    }

    public async Task<ScreenCapture> CaptureAsync(string serial, CancellationToken ct = default)
    {
        Directory.CreateDirectory(settings.OutputDirectory);
        var screenshotPath = Path.Combine(settings.OutputDirectory, $"device_{Sanitize(serial)}.png");
        var png = await RunBytesAsync(serial, ["exec-out", "screencap", "-p"], ct);
        await File.WriteAllBytesAsync(screenshotPath, png, ct);

        var density = await GetDensityAsync(serial, ct);
        var (w, h) = await GetSizeAsync(serial, ct);

        await RunTextAsync(serial, ["shell", "uiautomator", "dump", "/sdcard/window_dump.xml"], ct);
        var xml = await RunTextAsync(serial, ["exec-out", "cat", "/sdcard/window_dump.xml"], ct);

        var package = ParseForegroundPackage(xml);
        var app = package is not null ? await GetAppInfoAsync(serial, package, ct) : null;
        var elements = ParseHierarchy(xml, 160.0 / density);

        string? runtimeError = null;
        IReadOnlyList<RuntimeView>? runtimeViews = null;
        if (package is not null)
        {
            var probe = await frida.RunAsync(serial, package, 160.0 / density, ct);
            runtimeViews = probe.Views;
            if (runtimeViews is not null) elements = FridaProbe.Attach(elements, runtimeViews);
            runtimeError = probe.Error;
        }

        var (safeArea, keyboardHeight) = await TryGetSystemInsetsAsync(serial, 160.0 / density, h, ct);

        return new ScreenCapture(serial, screenshotPath, w, h, density / 160, elements,
            DevicePlatform.Android, app, runtimeError, runtimeViews, safeArea, keyboardHeight);
    }

    /// <summary>
    /// Waits until two consecutive screenshots are virtually identical (animations/transitions/spinners settled),
    /// then performs a full capture.
    /// </summary>
    public async Task<ScreenCapture> CaptureStableAsync(string serial, int maxWaitMs = 3500, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        byte[]? prevBytes = null;
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            var curBytes = await RunBytesAsync(serial, ["exec-out", "screencap", "-p"], ct);
            if (prevBytes is not null && ScreenshotsMatch(prevBytes, curBytes))
                break;
            prevBytes = curBytes;
            await Task.Delay(350, ct);
        }
        return await CaptureAsync(serial, ct);
    }

    public static bool ScreenshotsMatch(byte[] pngA, byte[] pngB)
    {
        if (pngA.AsSpan().SequenceEqual(pngB)) return true;
        using var a = SkiaSharp.SKBitmap.Decode(pngA);
        using var b = SkiaSharp.SKBitmap.Decode(pngB);
        if (a is null || b is null || a.Width != b.Width || a.Height != b.Height) return false;
        var pa = a.Pixels;
        var pb = b.Pixels;
        var step = Math.Max(1, pa.Length / 4096);
        long diff = 0;
        var count = 0;
        for (var i = 0; i < pa.Length; i += step)
        {
            diff += Math.Abs(pa[i].Red - pb[i].Red) + Math.Abs(pa[i].Green - pb[i].Green) + Math.Abs(pa[i].Blue - pb[i].Blue);
            count++;
        }
        return count > 0 && (double)diff / (count * 3) < 1.5;
    }

    /// <summary>
    /// Temporarily disables Android system animations (window, transition, animator scales = 0) and restores
    /// their previous values on dispose.
    /// </summary>
    public async Task<IAsyncDisposable> DisableAnimationsAsync(string serial, CancellationToken ct = default)
    {
        string[] keys = ["window_animation_scale", "transition_animation_scale", "animator_duration_scale"];
        var previous = new List<(string Key, string Value)>();
        foreach (var key in keys)
        {
            try
            {
                var cur = (await ShellAsync(serial, $"settings get global {key}", ct)).Trim();
                previous.Add((key, double.TryParse(cur, CultureInfo.InvariantCulture, out _) ? cur : "1.0"));
                await ShellAsync(serial, $"settings put global {key} 0", ct);
            }
            catch (InvalidOperationException) { }
        }
        return new AnimationRestoreScope(this, serial, previous);
    }

    sealed class AnimationRestoreScope(AdbClient adb, string serial, List<(string Key, string Value)> previous) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var (key, val) in previous)
            {
                try { await adb.ShellAsync(serial, $"settings put global {key} {val}"); } catch { }
            }
        }
    }

    async Task<(Insets? SafeArea, double? KeyboardHeightDp)> TryGetSystemInsetsAsync(string serial, double pxToDp, int screenHeightPx, CancellationToken ct)
    {
        try
        {
            var output = await ShellAsync(serial, "dumpsys window displays | grep -E 'ITYPE_STATUS_BAR|ITYPE_NAVIGATION_BAR|ITYPE_IME|statusBars|navigationBars|ime|mInputShown' | head -n 40", ct);
            return ParseSystemInsets(output, pxToDp, screenHeightPx);
        }
        catch (InvalidOperationException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Parses status bar, navigation bar, and visible IME (keyboard) heights from <c>dumpsys window</c> output.
    /// </summary>
    public static (Insets? SafeArea, double? KeyboardHeightDp) ParseSystemInsets(string dumpsysOutput, double pxToDp, int screenHeightPx)
    {
        double topPx = 0, bottomPx = 0;
        double? kbHeightPx = null;

        foreach (var line in dumpsysOutput.Split('\n'))
        {
            var isStatus = line.Contains("ITYPE_STATUS_BAR", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("type=statusBars", StringComparison.OrdinalIgnoreCase);
            var isNav = line.Contains("ITYPE_NAVIGATION_BAR", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("type=navigationBars", StringComparison.OrdinalIgnoreCase);
            var isIme = line.Contains("ITYPE_IME", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("type=ime", StringComparison.OrdinalIgnoreCase);

            if (!isStatus && !isNav && !isIme) continue;
            var visible = !line.Contains("visible=false", StringComparison.OrdinalIgnoreCase);
            if (!visible) continue;

            var m = FrameRectRegex().Match(line);
            if (!m.Success) continue;
            int y1 = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int y2 = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            var h = Math.Max(0, y2 - y1);
            if (h == 0) continue;

            if (isStatus && y1 <= 10) topPx = Math.Max(topPx, y2);
            else if (isNav && y2 >= screenHeightPx - 10) bottomPx = Math.Max(bottomPx, screenHeightPx - y1);
            else if (isIme && h > 50) kbHeightPx = Math.Max(kbHeightPx ?? 0, h);
        }

        Insets? safe = topPx > 0 || bottomPx > 0
            ? new Insets(0, Math.Round(topPx * pxToDp, 1), 0, Math.Round(bottomPx * pxToDp, 1))
            : null;
        double? kbDp = kbHeightPx is > 0 ? Math.Round(kbHeightPx.Value * pxToDp, 1) : null;
        return (safe, kbDp);
    }

    /// <summary>
    /// Captures the full scrollable screen by swiping down step by step via ADB and stitching the screenshots and
    /// accessibility/Frida elements into a single tall <see cref="ScreenCapture"/>.
    /// Automatically targets the active scrollable container (including bottom sheets) and detects sticky headers,
    /// sticky bottom bars, and floating sticky buttons (FABs) on each scroll step.
    /// </summary>
    public async Task<ScreenCapture> CaptureScrolledAsync(string serial, int maxScrolls = 8, CancellationToken ct = default)
    {
        maxScrolls = Math.Clamp(maxScrolls, 1, 20);
        var first = await CaptureAsync(serial, ct);
        var scrollablePx = await TryGetScrollableBoundsPxAsync(serial, ct);
        var frames = new List<ScrollStitcher.Frame> { new(CopyScreenshot(first, 0), 0) };

        var cumulativeOffsetPx = 0;

        using var prevBmpHolder = new ListDisposable();
        var prevBmp = SkiaSharp.SKBitmap.Decode(frames[0].Capture.ScreenshotPath)
            ?? throw new InvalidOperationException("Could not decode screenshot.");
        prevBmpHolder.Add(prevBmp);

        // Swipe strictly inside the scrollable container (critical for bottom sheets so we don't drag the sheet
        // itself or swipe on the dimmed backdrop, and don't start the swipe on a sticky CTA button).
        var swipeBox = scrollablePx ?? new Rect(0, 0, first.WidthPx, first.HeightPx);
        var cx = (int)(swipeBox.X + swipeBox.Width / 2);
        var startY = (int)(swipeBox.Y + swipeBox.Height * 0.78);
        var endY = (int)(swipeBox.Y + swipeBox.Height * 0.22);

        for (var i = 1; i <= maxScrolls; i++)
        {
            await SwipeAsync(serial, cx, startY, cx, endY, durationMs: 450, ct);
            await Task.Delay(600, ct);

            var next = CopyScreenshot(await CaptureAsync(serial, ct), i);
            var nextBmp = SkiaSharp.SKBitmap.Decode(next.ScreenshotPath)
                ?? throw new InvalidOperationException("Could not decode screenshot.");
            prevBmpHolder.Add(nextBmp);

            var (topFixed, bottomFixed) = ScrollStitcher.FindFixedBands(prevBmp, nextBmp);
            var stickyOverlays = ScrollStitcher.FindStickyOverlaysPx(frames[^1].Capture, prevBmp, next, nextBmp, topFixed, bottomFixed);
            var deltaPx = ScrollStitcher.EstimateScrollDeltaPx(frames[^1].Capture, prevBmp, next, nextBmp, topFixed, bottomFixed, stickyOverlays);
            if (deltaPx <= 2) break;

            // Back-fill frame 0's fixed bands / sticky overlays from the first transition.
            if (i == 1)
                frames[0] = frames[0] with { TopFixedPx = topFixed, BottomFixedPx = bottomFixed, StickyOverlaysPx = stickyOverlays };

            cumulativeOffsetPx += deltaPx;
            frames.Add(new ScrollStitcher.Frame(next, cumulativeOffsetPx, topFixed, bottomFixed, stickyOverlays));
            prevBmp = nextBmp;
        }

        // The screen stays scrolled to the bottom: swiping back past the top would trigger pull-to-refresh.

        if (frames.Count == 1) return first;

        var stitchedPath = Path.Combine(settings.OutputDirectory, $"device_{Sanitize(serial)}_scrolled.png");
        return ScrollStitcher.Stitch(frames, stitchedPath);
    }

    /// <summary>
    /// Reads the bounds (in px) of the primary scrollable container from the latest uiautomator dump
    /// (e.g. a ScrollView, RecyclerView, or scrollable bottom sheet). Returns null if none is marked scrollable.
    /// </summary>
    async Task<Rect?> TryGetScrollableBoundsPxAsync(string serial, CancellationToken ct)
    {
        try
        {
            var xml = await RunTextAsync(serial, ["exec-out", "cat", "/sdcard/window_dump.xml"], ct);
            return ParseScrollableBoundsPx(xml);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static Rect? ParseScrollableBoundsPx(string xml)
    {
        var start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = xml.IndexOf("<hierarchy", StringComparison.Ordinal);
        if (start < 0) return null;

        Rect? best = null;
        double bestArea = 0;
        foreach (var node in XDocument.Parse(xml[start..]).Descendants("node"))
        {
            if ((string?)node.Attribute("scrollable") != "true") continue;
            var bounds = BoundsRegex().Match((string?)node.Attribute("bounds") ?? "");
            if (!bounds.Success) continue;

            int x1 = int.Parse(bounds.Groups[1].Value), y1 = int.Parse(bounds.Groups[2].Value);
            int x2 = int.Parse(bounds.Groups[3].Value), y2 = int.Parse(bounds.Groups[4].Value);
            double w = x2 - x1, h = y2 - y1;
            if (w < 40 || h < 80) continue;
            var area = w * h;
            if (area > bestArea)
            {
                bestArea = area;
                best = new Rect(x1, y1, w, h);
            }
        }
        return best;
    }

    ScreenCapture CopyScreenshot(ScreenCapture capture, int index)
    {
        var copy = Path.Combine(settings.OutputDirectory, $"device_{Sanitize(capture.Serial)}_scroll_{index}.png");
        File.Copy(capture.ScreenshotPath, copy, overwrite: true);
        return capture with { ScreenshotPath = copy };
    }

    sealed class ListDisposable : IDisposable
    {
        readonly List<IDisposable> _items = [];
        public void Add(IDisposable d) => _items.Add(d);
        public void Dispose() { foreach (var d in _items) d.Dispose(); }
    }

    public Task SwipeAsync(string serial, int x1, int y1, int x2, int y2, int durationMs = 400, CancellationToken ct = default) =>
        RunTextAsync(serial, ["shell", "input", "swipe", x1.ToString(CultureInfo.InvariantCulture),
            y1.ToString(CultureInfo.InvariantCulture), x2.ToString(CultureInfo.InvariantCulture),
            y2.ToString(CultureInfo.InvariantCulture), durationMs.ToString(CultureInfo.InvariantCulture)], ct);

    public Task TapAsync(string serial, int x, int y, CancellationToken ct = default) =>
        RunTextAsync(serial, ["shell", "input", "tap", x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture)], ct);

    public Task InputTextAsync(string serial, string text, CancellationToken ct = default)
    {
        // `input text` takes spaces as %s. adb joins the arguments into one command line for the device shell,
        // so the text is single-quoted: otherwise &, ;, ( or quotes in it would break or inject commands.
        var escaped = text.Replace(" ", "%s");
        return RunTextAsync(serial, ["shell", "input", "text", ShellQuote(escaped)], ct);
    }

    public Task KeyEventAsync(string serial, string keycode, CancellationToken ct = default) =>
        RunTextAsync(serial, ["shell", "input", "keyevent", keycode], ct);

    // Quoted for the device shell: an unquoted & in the query string would cut the URI and background the command.
    public Task OpenDeeplinkAsync(string serial, string uri, CancellationToken ct = default) =>
        RunTextAsync(serial, ["shell", "am", "start", "-a", "android.intent.action.VIEW", "-d", ShellQuote(uri)], ct);

    /// <summary>Single-quotes a value for the device's shell (adb shell joins its arguments into one command line).</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public Task<string> ShellAsync(string serial, string command, CancellationToken ct = default) =>
        RunTextAsync(serial, ["shell", command], ct);

    readonly FridaProbe frida = new(settings);

    /// <summary>
    /// Paquete de la app en primer plano: el de los nodos del árbol de uiautomator que no son de la barra
    /// del sistema. Null si no hay ninguno.
    /// </summary>
    public static string? ParseForegroundPackage(string xml)
    {
        var start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = xml.IndexOf("<hierarchy", StringComparison.Ordinal);
        if (start < 0) return null;

        return XDocument.Parse(xml[start..]).Descendants("node")
            .Select(n => (string?)n.Attribute("package"))
            .Where(p => !string.IsNullOrEmpty(p) && p != "com.android.systemui")
            .GroupBy(p => p)
            .MaxBy(g => g.Count())?.Key;
    }

    /// <summary>
    /// Versión y fecha de instalación de la app. Es solo para el reporte: si falla (un dispositivo raro, una
    /// app del sistema), la captura sigue sin esos datos.
    /// </summary>
    async Task<AppInfo?> GetAppInfoAsync(string serial, string package, CancellationToken ct)
    {
        if (!PackageRegex().IsMatch(package)) return null;

        // Todo en un solo "adb shell" para no sumar tiempo a cada captura. La fecha sale de stat (segundos
        // desde 1970), no de lastUpdateTime, que viene en la zona horaria del dispositivo. En MAUI Debug,
        // Fast Deployment copia los ensamblados a files/.__override__ sin reinstalar: run-as los lee si la
        // app es depurable, y si no, falla sin escribir nada.
        var script =
            $"dumpsys package {package} | grep -E 'versionName=|versionCode='; " +
            $"echo apk $(stat -c %Y $(pm path {package} | head -1 | cut -d: -f2)); " +
            $"echo override $(run-as {package} sh -c 'stat -c %Y files/.__override__/* files/.__override__/*/*' 2>/dev/null | sort -n | tail -1)";
        try
        {
            return ParseAppInfo(package, await RunTextAsync(serial, ["shell", script], ct));
        }
        catch (InvalidOperationException)
        {
            return new AppInfo(package, null, null);
        }
    }

    public static AppInfo ParseAppInfo(string package, string output)
    {
        // Una app que no declara versión sale en dumpsys como "versionName=null" y "versionCode=0".
        var name = VersionNameRegex().Match(output) is { Success: true } n && n.Groups[1].Value != "null" ? n.Groups[1].Value : null;
        var code = VersionCodeRegex().Match(output) is { Success: true } c && c.Groups[1].Value != "0" ? c.Groups[1].Value : null;
        var version = (name, code) switch
        {
            (null, null) => null,
            (_, null) => name,
            (null, _) => $"({code})",
            _ => $"{name} ({code})",
        };

        var deployed = DeployTimeRegex().Matches(output)
            .Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty()
            .Max();

        return new AppInfo(package, version,
            deployed > 0 ? DateTimeOffset.FromUnixTimeSeconds(deployed).LocalDateTime : null);
    }

    /// <summary>
    /// MAUI publica el AutomationId como "{paquete de la app}:id/{AutomationId}". Los ids de otro paquete
    /// son de Android o de librerías ("android:id/content", "com.android.systemui:id/clock") y no
    /// representan nada del diseño: si se usaran, "Content" en Figma se emparejaría con la raíz de la ventana.
    /// </summary>
    public static string? ParseAutomationId(string? resourceId, string? package)
    {
        if (string.IsNullOrEmpty(resourceId)) return null;

        var idx = resourceId.IndexOf(":id/", StringComparison.Ordinal);
        if (idx < 0) return resourceId;

        var owner = resourceId[..idx];
        if (owner == "android" || owner.StartsWith("com.android.", StringComparison.Ordinal)) return null;
        if (!string.IsNullOrEmpty(package) && owner != package) return null;

        var id = resourceId[(idx + 4)..];
        return AppCompatDecorIds.Contains(id) ? null : id;
    }

    /// <summary>
    /// Ids del marco de ventana de AppCompat (layouts abc_screen_*). Los recursos de las librerías se
    /// fusionan en el paquete de la app, así que llegan como "{paquete}:id/action_bar_root".
    /// </summary>
    static readonly HashSet<string> AppCompatDecorIds =
    [
        "action_bar_root", "action_bar_container", "action_bar", "action_context_bar",
        "action_mode_bar_stub", "decor_content_parent", "split_action_bar",
    ];

    public static IReadOnlyList<ScreenElement> ParseHierarchy(string xml, double pxToDp)
    {
        var start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = xml.IndexOf("<hierarchy", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"uiautomator didn't return XML: {xml}");

        var doc = XDocument.Parse(xml[start..]);
        var result = new List<ScreenElement>();

        foreach (var node in doc.Descendants("node"))
        {
            var bounds = BoundsRegex().Match((string?)node.Attribute("bounds") ?? "");
            if (!bounds.Success) continue;

            int x1 = int.Parse(bounds.Groups[1].Value), y1 = int.Parse(bounds.Groups[2].Value);
            int x2 = int.Parse(bounds.Groups[3].Value), y2 = int.Parse(bounds.Groups[4].Value);
            if (x2 <= x1 || y2 <= y1) continue;

            var resourceId = (string?)node.Attribute("resource-id");
            var text = (string?)node.Attribute("text");
            var desc = (string?)node.Attribute("content-desc");

            var id = ParseAutomationId(resourceId, (string?)node.Attribute("package"));
            var clickable = (string?)node.Attribute("clickable") == "true";
            var focused = (string?)node.Attribute("focused") == "true";
            var focusable = (string?)node.Attribute("focusable") == "true";
            var checkable = (string?)node.Attribute("checkable") == "true";
            var enabled = (string?)node.Attribute("enabled") != "false";

            if (id is null && string.IsNullOrEmpty(text) && string.IsNullOrEmpty(desc) && !clickable)
                continue;

            result.Add(new ScreenElement(
                id,
                (string?)node.Attribute("class") ?? "",
                NullIfEmpty(text),
                NullIfEmpty(desc),
                new Rect(x1, y1, x2 - x1, y2 - y1).Scale(pxToDp),
                Clickable: clickable,
                Focused: focused,
                Focusable: focusable,
                Checkable: checkable,
                Enabled: enabled));
        }

        return result;
    }

    async Task<double> GetDensityAsync(string serial, CancellationToken ct)
    {
        // "Physical density: 420" y, si se cambió, "Override density: 480".
        var output = await RunTextAsync(serial, ["shell", "wm", "density"], ct);
        var matches = DensityRegex().Matches(output);
        if (matches.Count == 0) throw new InvalidOperationException($"Could not read the density: {output}");
        return double.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    async Task<(int, int)> GetSizeAsync(string serial, CancellationToken ct)
    {
        var output = await RunTextAsync(serial, ["shell", "wm", "size"], ct);
        var matches = SizeRegex().Matches(output);
        if (matches.Count == 0) throw new InvalidOperationException($"Could not read the size: {output}");
        var m = matches[^1];
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    async Task<string> RunTextAsync(string? serial, string[] args, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await RunBytesAsync(serial, args, ct));

    async Task<byte[]> RunBytesAsync(string? serial, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(settings.AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (serial is not null)
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not run {settings.AdbPath}");

        using var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(copy, stderr, process.WaitForExitAsync(ct));

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"adb {string.Join(' ', args)} failed ({process.ExitCode}): {await stderr}");

        return stdout.ToArray();
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    [GeneratedRegex(@"\[(\d+),(\d+)\]\[(\d+),(\d+)\]")]
    private static partial Regex BoundsRegex();

    [GeneratedRegex(@"density:\s*(\d+)")]
    private static partial Regex DensityRegex();

    [GeneratedRegex(@"size:\s*(\d+)x(\d+)")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackageRegex();

    [GeneratedRegex(@"versionName=(\S+)")]
    private static partial Regex VersionNameRegex();

    [GeneratedRegex(@"versionCode=(\d+)")]
    private static partial Regex VersionCodeRegex();

    [GeneratedRegex(@"^(?:apk|override) (\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex DeployTimeRegex();

    [GeneratedRegex(@"frame=\[(\d+),(\d+)\]\[(\d+),(\d+)\]")]
    private static partial Regex FrameRectRegex();
}
