using System.Text.Json;
using Driftcheck.Mcp.Devices;

namespace Driftcheck.Mcp.Android;

/// <summary>
/// A UI interaction step executed on an Android device to reach a specific screen state (e.g. focused field,
/// validation error, empty list, loading, disabled button) before capturing and comparing.
/// </summary>
public sealed record DeviceAction(
    string Type,
    string? Value = null,
    double? X = null,
    double? Y = null,
    double? X2 = null,
    double? Y2 = null,
    int? DurationMs = null)
{
    public static IReadOnlyList<DeviceAction> ParseArray(JsonElement array)
    {
        var result = new List<DeviceAction>();
        foreach (var item in array.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(type)) continue;
            result.Add(new DeviceAction(
                type.Trim().ToLowerInvariant(),
                item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null,
                TryNum(item, "x"),
                TryNum(item, "y"),
                TryNum(item, "x2"),
                TryNum(item, "y2"),
                TryNum(item, "ms") is { } ms ? (int)ms : TryNum(item, "durationMs") is { } d ? (int)d : null));
        }
        return result;
    }

    static double? TryNum(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
}

/// <summary>Executes <see cref="DeviceAction"/> steps on an Android device via ADB.</summary>
public static class DeviceActionExecutor
{
    public static async Task ExecuteAsync(
        AdbClient adb,
        string serial,
        IReadOnlyList<DeviceAction> actions,
        CancellationToken ct = default)
    {
        if (actions.Count == 0) return;

        ScreenCapture? lastCapture = null;
        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case "tap":
                {
                    if (action.X is null || action.Y is null)
                        throw new ArgumentException("tap action requires 'x' and 'y' (in dp).");
                    lastCapture ??= await adb.CaptureAsync(serial, ct);
                    var pxX = (int)Math.Round(action.X.Value * lastCapture.PixelsPerUnit);
                    var pxY = (int)Math.Round(action.Y.Value * lastCapture.PixelsPerUnit);
                    await adb.TapAsync(serial, pxX, pxY, ct);
                    lastCapture = null;
                    break;
                }
                case "tap_text":
                {
                    if (string.IsNullOrWhiteSpace(action.Value))
                        throw new ArgumentException("tap_text action requires 'value'.");
                    lastCapture = await adb.CaptureAsync(serial, ct);
                    var el = lastCapture.Elements.FirstOrDefault(e =>
                        string.Equals(e.Text?.Trim(), action.Value.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.ContentDescription?.Trim(), action.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? lastCapture.Elements.FirstOrDefault(e =>
                            (e.Text?.Contains(action.Value, StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (e.ContentDescription?.Contains(action.Value, StringComparison.OrdinalIgnoreCase) ?? false))
                        ?? throw new InvalidOperationException($"Could not find element with text '{action.Value}' to tap.");
                    await TapCenterAsync(adb, serial, el, lastCapture.PixelsPerUnit, ct);
                    lastCapture = null;
                    break;
                }
                case "tap_id":
                {
                    if (string.IsNullOrWhiteSpace(action.Value))
                        throw new ArgumentException("tap_id action requires 'value'.");
                    lastCapture = await adb.CaptureAsync(serial, ct);
                    var el = lastCapture.Elements.FirstOrDefault(e =>
                        string.Equals(e.Id, action.Value, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Could not find element with AutomationId '{action.Value}' to tap.");
                    await TapCenterAsync(adb, serial, el, lastCapture.PixelsPerUnit, ct);
                    lastCapture = null;
                    break;
                }
                case "input_text":
                {
                    if (action.Value is null)
                        throw new ArgumentException("input_text action requires 'value'.");
                    await adb.InputTextAsync(serial, action.Value, ct);
                    lastCapture = null;
                    break;
                }
                case "keyevent":
                {
                    if (string.IsNullOrWhiteSpace(action.Value))
                        throw new ArgumentException("keyevent action requires 'value' (e.g. 'KEYCODE_BACK').");
                    await adb.KeyEventAsync(serial, action.Value, ct);
                    lastCapture = null;
                    break;
                }
                case "swipe":
                {
                    if (action.X is null || action.Y is null || action.X2 is null || action.Y2 is null)
                        throw new ArgumentException("swipe action requires 'x', 'y', 'x2', 'y2' (in dp).");
                    lastCapture ??= await adb.CaptureAsync(serial, ct);
                    var s = lastCapture.PixelsPerUnit;
                    await adb.SwipeAsync(serial,
                        (int)Math.Round(action.X.Value * s), (int)Math.Round(action.Y.Value * s),
                        (int)Math.Round(action.X2.Value * s), (int)Math.Round(action.Y2.Value * s),
                        action.DurationMs ?? 400, ct);
                    lastCapture = null;
                    break;
                }
                case "deeplink":
                {
                    if (string.IsNullOrWhiteSpace(action.Value))
                        throw new ArgumentException("deeplink action requires 'value'.");
                    await adb.OpenDeeplinkAsync(serial, action.Value, ct);
                    await Task.Delay(action.DurationMs ?? 1200, ct);
                    lastCapture = null;
                    break;
                }
                case "wait":
                {
                    await Task.Delay(action.DurationMs ?? 1000, ct);
                    break;
                }
                case "shell":
                {
                    if (string.IsNullOrWhiteSpace(action.Value))
                        throw new ArgumentException("shell action requires 'value'.");
                    await adb.ShellAsync(serial, action.Value, ct);
                    lastCapture = null;
                    break;
                }
                default:
                    throw new ArgumentException(
                        $"Unknown action type '{action.Type}'. Use tap, tap_text, tap_id, input_text, keyevent, swipe, deeplink, wait or shell.");
            }

            if (action.Type is not "wait" and not "deeplink")
                await Task.Delay(400, ct);
        }
    }

    static Task TapCenterAsync(AdbClient adb, string serial, ScreenElement el, double pxPerUnit, CancellationToken ct)
    {
        var cx = (int)Math.Round((el.Bounds.X + el.Bounds.Width / 2) * pxPerUnit);
        var cy = (int)Math.Round((el.Bounds.Y + el.Bounds.Height / 2) * pxPerUnit);
        return adb.TapAsync(serial, cx, cy, ct);
    }
}
