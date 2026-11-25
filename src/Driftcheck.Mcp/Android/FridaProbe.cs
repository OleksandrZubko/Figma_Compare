using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Driftcheck.Mcp.Devices;

namespace Driftcheck.Mcp.Android;

/// <summary>
/// Reads the runtime properties of the app's views (text size, typeface, colors, corner radius, stroke) with
/// Frida: tools/frida-probe/probe.py attaches to the app and runs an agent that walks the View tree. Optional:
/// without Frida (no Python module, no frida-server on the device, DRIFTCHECK_FRIDA=off) the comparison works
/// from the screenshot as before.
/// </summary>
public sealed class FridaProbe(Settings settings)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A view of the app, with its visible bounds in dp (as uiautomator reports them).</summary>
    public sealed record View(string? ResourceId, Rect Bounds, RuntimeProps Props);

    /// <param name="Error">Why there are no views; null if Frida is turned off.</param>
    public sealed record Result(IReadOnlyList<View>? Views, string? Error);

    public async Task<Result> RunAsync(string serial, string package, double pxToDp, CancellationToken ct = default)
    {
        if (!settings.FridaEnabled) return new Result(null, null);

        var script = Path.Combine(AppContext.BaseDirectory, "frida-probe", "probe.py");
        if (!File.Exists(script)) return new Result(null, $"the probe script is missing ({script})");

        var psi = new ProcessStartInfo(settings.PythonPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // No -I: pip installs frida in the user's site-packages, which isolated mode skips.
        foreach (var arg in new[] { script, "--serial", serial, "--package", package }) psi.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new Result(null, $"could not start {settings.PythonPath}: {e.Message}");
        }

        using (process)
        {
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout;
                if (string.IsNullOrWhiteSpace(output))
                    return new Result(null, $"the probe printed nothing ({(await stderr).Trim()})");
                return Parse(output, pxToDp);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new Result(null, $"the probe didn't finish in {Timeout.TotalSeconds:0} s");
            }
        }
    }

    /// <summary>Parses the agent's JSON: bounds and sizes in px, colors as #AARRGGBB.</summary>
    public static Result Parse(string json, double pxToDp)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            return new Result(null, $"the probe returned invalid JSON: {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error)) return new Result(null, error.GetString());

            var density = 1 / pxToDp;
            var fontScale = 1.0;
            if (root.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
            {
                density = Number(metrics, "density") ?? density;
                fontScale = Number(metrics, "fontScale") ?? 1;
            }

            var views = new List<View>();
            foreach (var v in root.GetProperty("views").EnumerateArray())
            {
                var b = v.GetProperty("bounds");
                double l = b[0].GetDouble(), t = b[1].GetDouble(), r = b[2].GetDouble(), btm = b[3].GetDouble();
                if (r <= l || btm <= t) continue;

                var text = v.TryGetProperty("text", out var tv) && tv.ValueKind == JsonValueKind.Object
                    ? ParseText(tv, density, fontScale) : null;
                var box = v.TryGetProperty("background", out var bv) && bv.ValueKind == JsonValueKind.Object
                    ? ParseBox(bv, density) : null;

                views.Add(new View(
                    String(v, "id"),
                    new Rect(l, t, r - l, btm - t).Scale(pxToDp),
                    new RuntimeProps(String(v, "cls") ?? "", Number(v, "alpha") ?? 1, text, box)));
            }
            return new Result(views, null);
        }
    }

    static RuntimeText? ParseText(JsonElement t, double density, double fontScale)
    {
        if (Number(t, "size") is not { } sizePx) return null;
        var letterSpacing = Number(t, "letterSpacingPx") is { } px && sizePx > 0 ? px / sizePx : Number(t, "letterSpacing");
        return new RuntimeText(
            String(t, "text"),
            Math.Round(sizePx / (density * fontScale), 1),
            Number(t, "weight"),
            String(t, "family"),
            t.TryGetProperty("italic", out var i) && i.ValueKind == JsonValueKind.True,
            Color(String(t, "color")),
            Number(t, "lineHeight") is { } lh ? Math.Round(lh / density, 1) : null,
            letterSpacing is { } ls && ls != 0 ? Math.Round(ls, 3) : null);
    }

    static RuntimeBox? ParseBox(JsonElement b, double density)
    {
        var color = Color(String(b, "color"));
        var radius = Number(b, "radius") is { } r ? Math.Round(r / density, 1) : (double?)null;
        var stroke = Number(b, "strokeWidth") is { } s && s > 0 ? Math.Round(s / density, 1) : (double?)null;
        var strokeColor = stroke is null ? null : Color(String(b, "strokeColor"));
        if (color is null && radius is null && stroke is null) return null;
        return new RuntimeBox(color, radius, stroke, strokeColor);
    }

    /// <summary>
    /// Adds to each uiautomator element the properties of the view with the same bounds. Several views can share
    /// them (a container and its only child): the text comes from the TextView, the box from the view that draws
    /// a background.
    /// </summary>
    public static IReadOnlyList<ScreenElement> Attach(IReadOnlyList<ScreenElement> elements, IReadOnlyList<View> views, double toleranceDp = 1.5)
    {
        return elements.Select(e =>
        {
            var same = views.Where(v => Close(v.Bounds, e.Bounds, toleranceDp)).ToList();
            if (same.Count == 0) return e;

            var withText = same.Where(v => v.Props.Text is not null).ToList();
            var textView = withText.FirstOrDefault(v => e.Text is not null &&
                                                        string.Equals(v.Props.Text!.Value?.Trim(), e.Text.Trim(), StringComparison.Ordinal))
                           ?? withText.FirstOrDefault();
            var boxView = same.LastOrDefault(v => v.Props.Box?.Background is not null)
                          ?? same.LastOrDefault(v => v.Props.Box is not null);
            var main = textView ?? boxView ?? same[^1];

            return e with { Runtime = main.Props with { Text = textView?.Props.Text, Box = boxView?.Props.Box } };
        }).ToList();
    }

    static bool Close(Rect a, Rect b, double tolerance) =>
        Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance &&
        Math.Abs(a.X + a.Width - (b.X + b.Width)) <= tolerance && Math.Abs(a.Y + a.Height - (b.Y + b.Height)) <= tolerance;

    /// <summary>#AARRGGBB → color; null if it's fully transparent.</summary>
    public static Rgb? Color(string? argb)
    {
        if (argb is not { Length: 9 } || argb[0] != '#' ||
            !uint.TryParse(argb.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return null;
        if (v >> 24 == 0) return null;
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

    static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
