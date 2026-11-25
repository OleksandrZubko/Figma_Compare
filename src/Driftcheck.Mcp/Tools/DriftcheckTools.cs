using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using ModelContextProtocol.Server;

namespace Driftcheck.Mcp.Tools;

[McpServerToolType]
public sealed class DriftcheckTools(FigmaClient figma, DeviceRegistry devices, ScreenComparer comparer, Settings settings)
{
    /// <summary>
    /// Si la captura parece una pantalla de carga se vuelve a tomar: el splash de .NET dura unos segundos
    /// después de abrir la app, más con el emulador recién arrancado.
    /// </summary>
    const int MaxCaptureAttempts = 3;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2.5);

    [McpServerTool(Name = "import_design"), Description(
        "Imports a .driftcheck.json file exported with the Driftcheck plugin in Figma, to compare without the Figma API " +
        "(no token or no quota). Exports in the Downloads folder are picked up on their own; this is for the ones that " +
        "ended up in another folder. Then compare with the frame link, as usual.")]
    public string ImportDesign(
        [Description("Path of the .driftcheck.json file")] string path)
    {
        var export = new DesignExports(settings).Import(path);
        var file = export.FileKey is null ? "" : $" from file {export.FileKey}";
        return $"Imported \"{export.NodeName}\" (node-id={export.NodeId.Replace(':', '-')}){file}, exported on " +
               $"{export.ExportedAtUtc:yyyy-MM-dd HH:mm} UTC. Use get_design_specs or compare_screen with that " +
               "frame's link (Copy link to selection in Figma).";
    }

    [McpServerTool(Name = "list_devices"), Description(
        "Lists the Android devices and emulators connected through adb and, on macOS, the booted iOS simulators.")]
    public async Task<string> ListDevices(CancellationToken ct)
    {
        var listing = await devices.ListAsync(ct);
        var sb = new StringBuilder();
        if (listing.Devices.Count == 0)
            sb.AppendLine("No devices connected. Start an Android emulator or an iOS simulator, or connect a phone with USB debugging.");
        foreach (var (_, d) in listing.Devices)
            sb.AppendLine($"{d.Serial} — {d.Description}");
        foreach (var error in listing.Errors)
            sb.AppendLine($"(Could not query {error})");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_design_specs"), Description(
        "Returns the measurements of the Figma frame: position, size, colors, texts and typography of every named layer. " +
        "Use it BEFORE writing a screen's UI code. If deviceWidth is passed, values are scaled to that width in dp or pt.")]
    public async Task<string> GetDesignSpecs(
        [Description("Figma frame URL (Copy link to selection), must include ?node-id=")] string figmaUrl,
        [Description("Logical width of the target device in dp or pt (e.g. 412 for Pixel 7, 402 for iPhone 17). Optional.")] double? deviceWidth = null,
        [Description("true to download the Figma frame again instead of using the local cache. Use it only if the design changed: each download uses Figma API quota.")] bool refresh = false,
        CancellationToken ct = default)
    {
        var frame = await figma.GetFrameAsync(figmaUrl, refresh, ct);
        var scale = deviceWidth is > 0 ? deviceWidth.Value / frame.Bounds.Width : 1;

        var sb = new StringBuilder();
        sb.AppendLine($"# {frame.Name} ({frame.Bounds.Width:0.#}×{frame.Bounds.Height:0.#}, scale {scale:0.###})");
        sb.AppendLine();
        if (frame.CachedAt is { } cachedAt)
            sb.AppendLine($"Design taken from the local cache (downloaded on {cachedAt:yyyy-MM-dd HH:mm}). Use refresh=true if it changed in Figma.");
        sb.AppendLine("compare_screen matches texts by visible text and finds non-text layers (buttons, fields, lines, " +
                      "icons, images) by how they look; it also matches by layer name = AutomationId.");
        sb.AppendLine();
        sb.AppendLine("| Layer | Type | x | y | width | height | background | radius | text | font |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (var e in frame.Elements)
        {
            var b = e.Bounds.Scale(scale);
            var font = e.FontSize is { } fs
                ? $"{e.FontFamily} {F(fs * scale)} w{e.FontWeight:0}"
                : "";
            sb.AppendLine($"| {e.Name} | {e.Type} | {F(b.X)} | {F(b.Y)} | {F(b.Width)} | {F(b.Height)} | {e.Fill?.Hex} | {(e.CornerRadius is { } r ? F(r * scale) : "")} | {Escape(e.Text)} | {font} |");
        }

        if (frame.Shapes is { Count: > 0 } shapes)
        {
            sb.AppendLine();
            sb.AppendLine("## Visible non-text layers");
            sb.AppendLine();
            sb.AppendLine("| Layer | Kind | x | y | width | height | background | border | radius |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var e in shapes)
            {
                var b = e.Bounds.Scale(scale);
                var kind = e.Shape switch { ShapeKind.Icon => e.MultiColor ? "multicolor icon" : "icon", ShapeKind.Image => "image", _ => "shape" };
                var colorCol = e.IconColors is { Count: > 1 } ic ? string.Join(", ", ic.Select(c => c.Hex)) : e.Fill?.Hex;
                sb.AppendLine($"| {Escape(e.Name)} | {kind} | {F(b.X)} | {F(b.Y)} | {F(b.Width)} | {F(b.Height)} | {colorCol} | {e.Stroke?.Hex} | {(e.CornerRadius is { } r ? F(r * scale) : "")} |");
            }
        }

        if (frame.Layouts is { Count: > 0 } layouts)
        {
            sb.AppendLine();
            sb.AppendLine("## Auto Layout spacing");
            sb.AppendLine();
            sb.AppendLine("| Container | Direction | Padding (L, T, R, B) | Gap |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var l in layouts)
            {
                var p = l.Padding.Scale(scale);
                var gap = l.SpaceBetween ? "space-between" : F(l.ItemSpacing * scale);
                sb.AppendLine($"| {Escape(l.Name)} | {(l.IsHorizontal ? "horizontal" : "vertical")} | {p} | {gap} |");
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "capture_screen"), Description(
        "Takes a screenshot of the device (Android or iOS Simulator) and lists the visible elements with their AutomationId " +
        "(resource-id on Android, accessibilityIdentifier on iOS), text and bounds in dp or pt. Set scroll=true on Android to " +
        "scroll through the full screen and stitch it into one tall screenshot. On iOS it takes a few more seconds: " +
        "it reads the accessibility tree with a UI test, which is built the first time.")]
    public async Task<string> CaptureScreen(
        [Description("Device serial or UDID (from list_devices). Optional: uses the first one.")] string? deviceSerial = null,
        [Description("true to scroll down through the entire screen via ADB and stitch screenshots/elements (Android). Default false.")] bool scroll = false,
        [Description("Maximum number of scroll swipes when scroll=true. Default 8.")] int maxScrolls = 8,
        CancellationToken ct = default)
    {
        var capture = await CaptureInternalAsync(deviceSerial, scroll, maxScrolls, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Screenshot: {capture.ScreenshotPath}");
        sb.AppendLine($"Device: {capture.Serial}, {capture.WidthPx}×{capture.HeightPx} px, {capture.PixelsPerUnit:0.##} px per {capture.Unit} ({capture.WidthUnits:0.#} {capture.Unit} wide)");
        if (capture.App is { } app) sb.AppendLine($"App: {app}");
        sb.AppendLine(RuntimeLine(capture));
        sb.AppendLine();
        sb.AppendLine("| AutomationId | Class | Text | x | y | width | height | Font | Text color | Background | Radius | Stroke |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var e in capture.Elements)
        {
            var t = e.Runtime?.Text;
            var box = e.Runtime?.Box;
            var font = t is null ? "" : $"{F(t.SizeSp)} sp {RuntimeFonts.Weight(t)?.ToString(CultureInfo.InvariantCulture)} {t.Family}".Trim();
            var stroke = box?.StrokeWidth is { } sw ? $"{F(sw)} {box.StrokeColor?.Hex}".Trim() : "";
            sb.AppendLine($"| {e.Id} | {e.ClassName.Split('.').Last()} | {Escape(e.Text ?? e.ContentDescription)} | {F(e.Bounds.X)} | {F(e.Bounds.Y)} | {F(e.Bounds.Width)} | {F(e.Bounds.Height)} " +
                          $"| {Escape(font)} | {t?.Color?.Hex} | {box?.Background?.Hex} | {(box?.CornerRadius is { } r ? F(r) : "")} | {stroke} |");
        }

        // Icons rarely have an id or a description, so uiautomator doesn't list them; Frida does.
        var icons = (capture.RuntimeViews ?? []).Where(v => v.Props.Icon is not null).ToList();
        if (icons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Icons (Frida, {capture.Unit}):");
            sb.AppendLine();
            sb.AppendLine("| Class | x | y | width | height | Colors |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var v in icons)
            {
                var icon = v.Props.Icon!;
                var colors = string.Join(", ", icon.Colors.Select(c => c.Hex)) + (icon.Gradient ? " + gradient" : "");
                sb.AppendLine($"| {v.Props.ClassName.Split('.').Last()} | {F(v.Bounds.X)} | {F(v.Bounds.Y)} | {F(v.Bounds.Width)} | {F(v.Bounds.Height)} | {colors} |");
            }
        }

        return sb.ToString();
    }

    /// <summary>Whether the runtime properties (Frida) were read, so the user knows what the comparison is based on.</summary>
    static string RuntimeLine(ScreenCapture capture) => capture switch
    {
        { RuntimeCount: > 0 } => $"Runtime properties (Frida): read for {capture.RuntimeCount} of {capture.Elements.Count} elements.",
        { RuntimeError: { } error } => $"Runtime properties (Frida): not read ({error}). Fonts are judged from the screenshot only.",
        _ => "Runtime properties (Frida): off.",
    };

    [McpServerTool(Name = "compare_screen"), Description(
        "Compares the app's current screen on the device (Android or iOS Simulator) with a Figma frame. " +
        "Matches Figma layers to app elements by visible text, name = AutomationId or position, and reports differences in position, size, " +
        "text and background color. Non-text layers (buttons, fields, lines, icons, images) are found in the screenshot by " +
        "how they look, comparing box, fill, corner radius, border and content. Reports fidelity (% of elements as in Figma, and how much it changed since the previous comparison) " +
        "and how to fix each difference with the framework's properties. Generates report.html (interactive report to " +
        "show the user), side_by_side.png and diff.png (in diff.png: green = matches, orange = differs, " +
        "dotted blue = where Figma says it should be). Open the images for a visual review. " +
        "If the screenshot doesn't look like the design (a loading screen), it waits and captures again up to 3 times.")]
    public async Task<string> CompareScreen(
        [Description("Figma frame URL (Copy link to selection), must include ?node-id=")] string figmaUrl,
        [Description("Device serial or UDID. Optional: uses the first one.")] string? deviceSerial = null,
        [Description("Position/size tolerance in dp or pt. Default 2.")] double tolerance = 2,
        [Description("Color tolerance (RGB distance 0–441). Default 24.")] double colorTolerance = 24,
        [Description("true to download the Figma frame again instead of using the local cache. Use it only if the design changed: each download uses Figma API quota.")] bool refresh = false,
        [Description("App framework, to write the suggestions with its properties: maui, android (XML views), compose, uikit, swiftui or flutter. Optional.")] string? framework = null,
        [Description("App project folder. Optional: if it has files newer than the installed app, the report warns that the app needs to be rebuilt and reinstalled.")] string? projectPath = null,
        [Description("true to scroll down through the full screen via ADB and stitch it to match a tall Figma frame (Android). Default false.")] bool scroll = false,
        [Description("Maximum number of scroll swipes when scroll=true. Default 8.")] int maxScrolls = 8,
        [Description("true to ignore text string differences (useful when testing another locale against a base Figma frame while still checking overflow/truncation/layout). Default false.")] bool ignoreTextContent = false,
        [Description("Comma-separated AutomationIds, layer names, or /regex/ patterns for dynamic content (prices, dates, avatars) whose text/pixels should be masked while still checking layout, font, color and overflow. Optional.")] string? dynamicFields = null,
        [Description("true to run accessibility, keyboard occlusion and safe-area checks (WCAG contrast, 48dp/44pt tap targets, missing labels). Default true.")] bool checkA11y = true,
        [Description("true to disable Android system animations and wait for the screen to settle before capturing. Default false.")] bool stabilize = false,
        CancellationToken ct = default)
    {
        var fw = FixSuggester.ParseFramework(framework);
        if (projectPath is not null && !Directory.Exists(projectPath))
            throw new ArgumentException($"Project folder not found: {projectPath}");
        var mask = DynamicMask.Parse(dynamicFields);

        var (client, device) = await devices.ResolveAsync(deviceSerial, ct);
        await using var animScope = stabilize && client is AdbClient adbAnim
            ? await adbAnim.DisableAnimationsAsync(device.Serial, ct)
            : null;

        var frameTask = figma.GetFrameAsync(figmaUrl, refresh, ct);
        var pngTask = figma.RenderPngAsync(figmaUrl, 2, refresh, ct);
        var captureTask = CaptureWithClientAsync(client, device.Serial, scroll, maxScrolls, stabilize, ct);
        await Task.WhenAll(frameTask, pngTask, captureTask);
        var (design, designPng, capture) = (await frameTask, await pngTask, await captureTask);

        ComparisonResult result;
        var retries = 0;
        while (true)
        {
            List<string> warnings = [];
            if (projectPath is not null && ProjectFreshness.Check(projectPath, capture.App) is { } stale) warnings.Add(stale);
            if (capture.Platform == DevicePlatform.Android && capture.RuntimeError is { } runtimeError)
                warnings.Add($"Text size, weight and family weren't read from the app (Frida: {runtimeError}), only estimated " +
                             "from the screenshot. Set DRIFTCHECK_FRIDA=off to hide this.");

            result = comparer.Compare(design, designPng, capture, tolerance, colorTolerance, fw, warnings, ignoreTextContent, mask, checkA11y);
            if (!result.LooksLikeLoadingScreen || retries == MaxCaptureAttempts - 1) break;

            // Se borra para que la próxima comparación no la tome como la anterior.
            ScreenComparer.Discard(result);
            await Task.Delay(RetryDelay, ct);
            capture = await CaptureWithClientAsync(client, device.Serial, scroll, maxScrolls, stabilize, ct);
            retries++;
        }

        var retryNote = retries switch
        {
            0 => "",
            1 => "(The first screenshot didn't look like the design, so Driftcheck waited and captured again.)\n\n",
            _ => $"(The first {retries} screenshots didn't look like the design, so Driftcheck waited and captured again.)\n\n",
        };
        var runtimeNote = capture.RuntimeCount > 0 ? RuntimeLine(capture) + "\n\n" : "";
        return retryNote + runtimeNote + result.Report +
               $"\n\nreport.html: {result.HtmlReportPath}\nside_by_side: {result.SideBySidePath}\ndiff: {result.DiffPath}";
    }

    [McpServerTool(Name = "compare_states"), Description(
        "Compares multiple UI states of a screen (e.g. default, empty, error, loading, disabled, focused) against their " +
        "corresponding Figma variants in a single run. Each state can optionally run ADB actions (tap, tap_text, tap_id, " +
        "input_text, keyevent, swipe, deeplink, wait, shell) before capturing. Generates an individual report per state and " +
        "a combined summary.html.")]
    public async Task<string> CompareStates(
        [Description("JSON array of states: [{\"name\":\"focused\",\"figmaUrl\":\"https://...\",\"actions\":[{\"type\":\"tap_text\",\"value\":\"Email\"}],\"scroll\":false,\"ignoreTextContent\":false}]")] string statesJson,
        [Description("Device serial or UDID. Optional: uses the first one.")] string? deviceSerial = null,
        [Description("Position/size tolerance in dp or pt. Default 2.")] double tolerance = 2,
        [Description("Color tolerance (RGB distance 0–441). Default 24.")] double colorTolerance = 24,
        [Description("true to download Figma frames again instead of using local cache.")] bool refresh = false,
        [Description("App framework: maui, android, compose, uikit, swiftui or flutter. Optional.")] string? framework = null,
        [Description("App project folder. Optional.")] string? projectPath = null,
        [Description("Comma-separated AutomationIds, layer names, or /regex/ patterns for dynamic content to mask. Optional.")] string? dynamicFields = null,
        [Description("true to run accessibility, keyboard occlusion and safe-area checks. Default true.")] bool checkA11y = true,
        [Description("true to disable Android system animations and wait for stable frames before capturing. Default false.")] bool stabilize = false,
        CancellationToken ct = default)
    {
        var fw = FixSuggester.ParseFramework(framework);
        if (projectPath is not null && !Directory.Exists(projectPath))
            throw new ArgumentException($"Project folder not found: {projectPath}");
        var mask = DynamicMask.Parse(dynamicFields);

        var states = ParseStates(statesJson);
        if (states.Count == 0)
            throw new ArgumentException("statesJson must contain at least one state with 'name' and 'figmaUrl'.");

        var (client, device) = await devices.ResolveAsync(deviceSerial, ct);
        await using var animScope = stabilize && client is AdbClient adbAnim
            ? await adbAnim.DisableAnimationsAsync(device.Serial, ct)
            : null;
        var entries = new List<BatchReport.Entry>();

        foreach (var state in states)
        {
            try
            {
                if (state.Actions.Count > 0)
                {
                    if (client is not AdbClient adb)
                        throw new InvalidOperationException("State setup actions are supported on Android devices (via ADB).");
                    await DeviceActionExecutor.ExecuteAsync(adb, device.Serial, state.Actions, ct);
                }

                var frameTask = figma.GetFrameAsync(state.FigmaUrl, refresh, ct);
                var pngTask = figma.RenderPngAsync(state.FigmaUrl, 2, refresh, ct);
                var captureTask = CaptureWithClientAsync(client, device.Serial, state.Scroll, state.MaxScrolls, stabilize, ct);
                await Task.WhenAll(frameTask, pngTask, captureTask);
                var (design, designPng, capture) = (await frameTask, await pngTask, await captureTask);

                List<string> warnings = [];
                if (projectPath is not null && ProjectFreshness.Check(projectPath, capture.App) is { } stale) warnings.Add(stale);
                var result = comparer.Compare(design, designPng, capture, tolerance, colorTolerance, fw, warnings, state.IgnoreTextContent, mask, checkA11y);
                entries.Add(new BatchReport.Entry(state.Name, device.Serial, state.Scroll ? "scrolled" : "viewport", result));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                entries.Add(new BatchReport.Entry(state.Name, device.Serial, state.Scroll ? "scrolled" : "viewport", null, ex.Message));
            }
        }

        var (markdown, _) = BatchReport.Write("UI States Comparison", settings.OutputDirectory, entries);
        return markdown;
    }

    [McpServerTool(Name = "compare_matrix"), Description(
        "Compares a screen across multiple devices and/or device configurations (narrow/wide/tablet screen widths, " +
        "font_scale 1.3, dark theme, RTL layout, other locales) in a single run. Android settings are applied via ADB and automatically " +
        "restored after each variant. Reports fidelity, diffs, and text overflows/truncations/overlaps in a combined summary.html.")]
    public async Task<string> CompareMatrix(
        [Description("Default Figma frame URL (variants may override with their own figmaUrl, e.g. for dark mode or tablet).")] string figmaUrl,
        [Description("Optional JSON array of variants: [{\"name\":\"narrow\",\"widthDp\":360,\"heightDp\":780},{\"name\":\"large-font\",\"fontScale\":1.3},{\"name\":\"dark\",\"theme\":\"dark\",\"figmaUrl\":\"...\"},{\"name\":\"rtl\",\"rtl\":true},{\"name\":\"german\",\"locale\":\"de-DE\"}]. Presets supported via \"preset\": default|narrow|phone|wide|tablet|large-font|dark|light|rtl. If omitted, runs default + narrow + large-font + dark.")] string? variantsJson = null,
        [Description("Comma-separated device serials, or \"all\" for every connected device. Optional: uses the first device.")] string? deviceSerials = null,
        [Description("Position/size tolerance in dp or pt. Default 2.")] double tolerance = 2,
        [Description("Color tolerance (RGB distance 0–441). Default 24.")] double colorTolerance = 24,
        [Description("true to download Figma frames again instead of using local cache.")] bool refresh = false,
        [Description("App framework: maui, android, compose, uikit, swiftui or flutter. Optional.")] string? framework = null,
        [Description("App project folder. Optional.")] string? projectPath = null,
        [Description("Comma-separated AutomationIds, layer names, or /regex/ patterns for dynamic content to mask. Optional.")] string? dynamicFields = null,
        [Description("true to run accessibility, keyboard occlusion and safe-area checks. Default true.")] bool checkA11y = true,
        [Description("true to disable Android system animations and wait for stable frames before capturing. Default false.")] bool stabilize = false,
        CancellationToken ct = default)
    {
        var fw = FixSuggester.ParseFramework(framework);
        if (projectPath is not null && !Directory.Exists(projectPath))
            throw new ArgumentException($"Project folder not found: {projectPath}");
        var mask = DynamicMask.Parse(dynamicFields);

        var variants = ParseVariants(variantsJson, figmaUrl);
        var targets = await ResolveTargetDevicesAsync(deviceSerials, ct);
        var entries = new List<BatchReport.Entry>();

        foreach (var (client, device) in targets)
        {
            await using var animScope = stabilize && client is AdbClient adbAnim
                ? await adbAnim.DisableAnimationsAsync(device.Serial, ct)
                : null;

            string? appPackage = null;
            if (client is AdbClient && variants.Any(v => !string.IsNullOrWhiteSpace(v.Config.Locale)))
            {
                try { appPackage = (await client.CaptureAsync(device.Serial, ct)).App?.Id; }
                catch { }
            }

            foreach (var variant in variants)
            {
                try
                {
                    if (!variant.Config.IsEmpty && client is not AdbClient)
                    {
                        entries.Add(new BatchReport.Entry(variant.Name, device.Serial, variant.Config.Summary(), null,
                            "Runtime device configuration overrides are only supported on Android (ADB)."));
                        continue;
                    }

                    await using var scope = client is AdbClient adb
                        ? await DeviceConfigurator.ApplyAsync(adb, device.Serial, variant.Config, appPackage, ct)
                        : null;

                    var url = variant.FigmaUrl ?? figmaUrl;
                    var frameTask = figma.GetFrameAsync(url, refresh, ct);
                    var pngTask = figma.RenderPngAsync(url, 2, refresh, ct);
                    var captureTask = CaptureWithClientAsync(client, device.Serial, variant.Scroll, variant.MaxScrolls, stabilize, ct);
                    await Task.WhenAll(frameTask, pngTask, captureTask);
                    var (design, designPng, capture) = (await frameTask, await pngTask, await captureTask);

                    // When testing another locale against the base Figma frame, ignore text-string diffs
                    // so we focus on overflow, truncation, overlap and layout breakage.
                    var ignoreText = variant.IgnoreTextContent ?? (!string.IsNullOrWhiteSpace(variant.Config.Locale) && variant.FigmaUrl is null);
                    List<string> warnings = [];
                    if (projectPath is not null && ProjectFreshness.Check(projectPath, capture.App) is { } stale) warnings.Add(stale);
                    var result = comparer.Compare(design, designPng, capture, tolerance, colorTolerance, fw, warnings, ignoreText, mask, checkA11y);
                    entries.Add(new BatchReport.Entry(variant.Name, device.Serial, variant.Config.Summary(), result));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entries.Add(new BatchReport.Entry(variant.Name, device.Serial, variant.Config.Summary(), null, ex.Message));
                }
            }
        }

        var (markdown, _) = BatchReport.Write("Multi-Device & Config Matrix", settings.OutputDirectory, entries);
        return markdown;
    }

    [McpServerTool(Name = "save_baseline"), Description(
        "Saves the current app screen (screenshot + element tree + Frida runtime properties) as a named local baseline " +
        "for visual regression testing without Figma. Use compare_baseline later to detect UI regressions between builds.")]
    public async Task<string> SaveBaseline(
        [Description("Unique baseline name (e.g. 'checkout-screen', 'home-dark').")] string name,
        [Description("Device serial or UDID. Optional: uses the first one.")] string? deviceSerial = null,
        [Description("true to capture the full scrolled screen (Android). Default false.")] bool scroll = false,
        [Description("Maximum scroll swipes when scroll=true. Default 8.")] int maxScrolls = 8,
        [Description("true to disable Android system animations and wait for a stable frame before saving. Default false.")] bool stabilize = false,
        CancellationToken ct = default)
    {
        var (client, device) = await devices.ResolveAsync(deviceSerial, ct);
        await using var animScope = stabilize && client is AdbClient adbAnim
            ? await adbAnim.DisableAnimationsAsync(device.Serial, ct)
            : null;

        var capture = await CaptureWithClientAsync(client, device.Serial, scroll, maxScrolls, stabilize, ct);
        var dir = new BaselineStore(settings).Save(name, capture);
        return $"Saved baseline \"{name}\" ({capture.WidthPx}×{capture.HeightPx} px, {capture.Elements.Count} elements, " +
               $"Frida runtime: {capture.RuntimeCount}) to {dir}. Run compare_baseline with name=\"{name}\" to check for regressions.";
    }

    [McpServerTool(Name = "compare_baseline"), Description(
        "Compares the current app screen against a previously saved local baseline (without Figma) to catch visual, " +
        "layout, typography, overflow and accessibility regressions between builds. Generates report.html, side_by_side.png and diff.png.")]
    public async Task<string> CompareBaseline(
        [Description("Name of the baseline saved with save_baseline.")] string name,
        [Description("Device serial or UDID. Optional: uses the first one.")] string? deviceSerial = null,
        [Description("Position/size tolerance in dp or pt. Default 2.")] double tolerance = 2,
        [Description("Color tolerance (RGB distance 0–441). Default 24.")] double colorTolerance = 24,
        [Description("App framework for fix suggestions: maui, android, compose, uikit, swiftui or flutter. Optional.")] string? framework = null,
        [Description("true to capture the full scrolled screen (Android). Default false.")] bool scroll = false,
        [Description("Maximum scroll swipes when scroll=true. Default 8.")] int maxScrolls = 8,
        [Description("Comma-separated AutomationIds, layer names, or /regex/ patterns for dynamic content to mask. Optional.")] string? dynamicFields = null,
        [Description("true to run accessibility, keyboard occlusion and safe-area checks. Default true.")] bool checkA11y = true,
        [Description("true to disable Android system animations and wait for a stable frame before capturing. Default false.")] bool stabilize = false,
        CancellationToken ct = default)
    {
        var fw = FixSuggester.ParseFramework(framework);
        var mask = DynamicMask.Parse(dynamicFields);
        var baseline = new BaselineStore(settings).Load(name);
        var design = BaselineStore.ToDesignFrame(baseline);

        var (client, device) = await devices.ResolveAsync(deviceSerial, ct);
        await using var animScope = stabilize && client is AdbClient adbAnim
            ? await adbAnim.DisableAnimationsAsync(device.Serial, ct)
            : null;

        var capture = await CaptureWithClientAsync(client, device.Serial, scroll, maxScrolls, stabilize, ct);
        var result = comparer.Compare(design, baseline.Capture.ScreenshotPath, capture, tolerance, colorTolerance, fw,
            warnings: null, ignoreTextContent: false, dynamicMask: mask, checkA11y: checkA11y,
            designPxPerUnit: baseline.Capture.PixelsPerUnit);

        return result.Report +
               $"\n\nreport.html: {result.HtmlReportPath}\nside_by_side: {result.SideBySidePath}\ndiff: {result.DiffPath}";
    }

    [McpServerTool(Name = "audit_accessibility"), Description(
        "Runs a full Google ATF + WCAG 2.1 AA accessibility audit on the current screen (without needing Figma): " +
        "checks touch target sizes (48dp/44pt), text & icon contrast, missing/duplicate/redundant screen-reader labels, " +
        "editable contentDescription, small/truncated text, keyboard occlusion, and safe-area overlap. Optionally tests " +
        "font_scale=1.3 as well. Generates an interactive a11y_report.html and a11y_annotated.png.")]
    public async Task<string> AuditAccessibility(
        [Description("Device serial or UDID. Optional: uses the first one.")] string? deviceSerial = null,
        [Description("true to scroll down through the full screen via ADB before auditing (Android). Default false.")] bool scroll = false,
        [Description("Maximum scroll swipes when scroll=true. Default 8.")] int maxScrolls = 8,
        [Description("true to disable Android system animations and wait for a stable frame. Default false.")] bool stabilize = false,
        [Description("true to also run a second audit at font_scale=1.3 (Android) to catch Dynamic Type / truncation regressions. Default false.")] bool testLargeFont = false,
        CancellationToken ct = default)
    {
        var (client, device) = await devices.ResolveAsync(deviceSerial, ct);
        await using var animScope = stabilize && client is AdbClient adbAnim
            ? await adbAnim.DisableAnimationsAsync(device.Serial, ct)
            : null;

        var capture = await CaptureWithClientAsync(client, device.Serial, scroll, maxScrolls, stabilize, ct);
        using var bmp = SkiaSharp.SKBitmap.Decode(capture.ScreenshotPath)
            ?? throw new InvalidOperationException("Could not decode device screenshot.");
        var issues = A11yAuditor.Audit(capture, bmp);

        IReadOnlyList<A11yIssue>? largeFontIssues = null;
        if (testLargeFont && client is AdbClient adb)
        {
            await using var lfScope = await DeviceConfigurator.ApplyAsync(adb, device.Serial, new DeviceConfig(FontScale: 1.3), null, ct);
            var lfCapture = await CaptureWithClientAsync(client, device.Serial, scroll, maxScrolls, stabilize, ct);
            using var lfBmp = SkiaSharp.SKBitmap.Decode(lfCapture.ScreenshotPath)
                ?? throw new InvalidOperationException("Could not decode large-font screenshot.");
            var allLf = A11yAuditor.Audit(lfCapture, lfBmp);
            var baseKeys = issues.Select(i => (i.RuleId, i.Element)).ToHashSet();
            largeFontIssues = allLf.Where(i => !baseKeys.Contains((i.RuleId, i.Element))).ToList();
        }

        var output = A11yReport.Write(capture, bmp, issues, settings.OutputDirectory, largeFontIssues);
        return output.Markdown;
    }

    async Task<ScreenCapture> CaptureInternalAsync(string? deviceSerial, bool scroll, int maxScrolls, CancellationToken ct)
    {
        var (client, device) = await devices.ResolveAsync(deviceSerial, ct);
        return await CaptureWithClientAsync(client, device.Serial, scroll, maxScrolls, stabilize: false, ct);
    }

    static Task<ScreenCapture> CaptureWithClientAsync(IDeviceClient client, string serial, bool scroll, int maxScrolls, bool stabilize, CancellationToken ct)
    {
        if (scroll)
        {
            if (client is AdbClient adb) return adb.CaptureScrolledAsync(serial, maxScrolls, ct);
            throw new InvalidOperationException("Full-screen scroll capture is currently supported on Android devices (via ADB).");
        }
        if (stabilize && client is AdbClient adbStable)
            return adbStable.CaptureStableAsync(serial, ct: ct);
        return client.CaptureAsync(serial, ct);
    }

    async Task<IReadOnlyList<(IDeviceClient Client, DeviceInfo Device)>> ResolveTargetDevicesAsync(string? deviceSerials, CancellationToken ct)
    {
        var listing = await devices.ListAsync(ct);
        if (listing.Devices.Count == 0)
            throw new InvalidOperationException("No devices connected. Start an Android emulator or an iOS simulator, or connect a phone.");

        if (string.IsNullOrWhiteSpace(deviceSerials))
            return [listing.Devices[0]];

        if (string.Equals(deviceSerials.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            return listing.Devices;

        var wanted = deviceSerials.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<(IDeviceClient, DeviceInfo)>();
        foreach (var s in wanted)
        {
            var found = listing.Devices.FirstOrDefault(d => string.Equals(d.Device.Serial, s, StringComparison.OrdinalIgnoreCase));
            if (found.Client is null)
                throw new InvalidOperationException($"Device {s} not found. Use list_devices to see the available ones.");
            result.Add(found);
        }
        return result;
    }

    public sealed record StateSpec(
        string Name, string FigmaUrl, IReadOnlyList<DeviceAction> Actions, bool Scroll, int MaxScrolls, bool IgnoreTextContent);

    public sealed record MatrixVariant(
        string Name, string? FigmaUrl, DeviceConfig Config, bool Scroll, int MaxScrolls, bool? IgnoreTextContent);

    public static IReadOnlyList<StateSpec> ParseStates(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("statesJson must be a JSON array.");

        var list = new List<StateSpec>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = item.TryGetProperty("figmaUrl", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Each state in statesJson must have 'name' and 'figmaUrl'.");

            var actions = item.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.Array
                ? DeviceAction.ParseArray(a)
                : [];
            var scroll = item.TryGetProperty("scroll", out var sc) && sc.ValueKind == JsonValueKind.True;
            var maxScrolls = item.TryGetProperty("maxScrolls", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt32() : 8;
            var ignoreText = item.TryGetProperty("ignoreTextContent", out var it) && it.ValueKind == JsonValueKind.True;
            list.Add(new StateSpec(name, url, actions, scroll, maxScrolls, ignoreText));
        }
        return list;
    }

    public static IReadOnlyList<MatrixVariant> ParseVariants(string? json, string defaultFigmaUrl)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return
            [
                new("default", defaultFigmaUrl, new DeviceConfig(), false, 8, null),
                new("narrow-360dp", defaultFigmaUrl, DeviceConfig.FromPreset("narrow"), false, 8, null),
                new("large-font-1.3x", defaultFigmaUrl, DeviceConfig.FromPreset("large-font"), false, 8, null),
                new("dark", defaultFigmaUrl, DeviceConfig.FromPreset("dark"), false, 8, null),
            ];
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("variantsJson must be a JSON array.");

        var list = new List<MatrixVariant>();
        var index = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            index++;
            var baseCfg = item.TryGetProperty("preset", out var pr) && pr.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pr.GetString())
                ? DeviceConfig.FromPreset(pr.GetString()!)
                : new DeviceConfig();

            var cfg = baseCfg with
            {
                WidthDp = item.TryGetProperty("widthDp", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : baseCfg.WidthDp,
                HeightDp = item.TryGetProperty("heightDp", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : baseCfg.HeightDp,
                Density = item.TryGetProperty("density", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : baseCfg.Density,
                FontScale = item.TryGetProperty("fontScale", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetDouble() : baseCfg.FontScale,
                Theme = item.TryGetProperty("theme", out var th) && th.ValueKind == JsonValueKind.String ? th.GetString() : baseCfg.Theme,
                Locale = item.TryGetProperty("locale", out var loc) && loc.ValueKind == JsonValueKind.String ? loc.GetString() : baseCfg.Locale,
                Rtl = item.TryGetProperty("rtl", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : baseCfg.Rtl,
            };

            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()!
                : (cfg.IsEmpty ? $"variant-{index}" : cfg.Summary());
            var url = item.TryGetProperty("figmaUrl", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString())
                ? u.GetString()
                : null;
            var scroll = item.TryGetProperty("scroll", out var sc) && sc.ValueKind == JsonValueKind.True;
            var maxScrolls = item.TryGetProperty("maxScrolls", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt32() : 8;
            bool? ignoreText = item.TryGetProperty("ignoreTextContent", out var it) && it.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? it.GetBoolean()
                : null;

            list.Add(new MatrixVariant(name, url, cfg, scroll, maxScrolls, ignoreText));
        }

        if (list.Count == 0)
            throw new ArgumentException("variantsJson must contain at least one variant.");
        return list;
    }

    static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    static string Escape(string? s) =>
        s is null ? "" : s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ⏎ ");
}
