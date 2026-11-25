using System.Globalization;
using System.Text.RegularExpressions;

namespace Driftcheck.Mcp.Android;

/// <summary>
/// Device configuration overrides for matrix testing (screen width/height, font scale, dark/light theme, locale).
/// Applied via ADB before capture and automatically restored afterwards.
/// </summary>
public sealed record DeviceConfig(
    int? WidthDp = null,
    int? HeightDp = null,
    int? Density = null,
    double? FontScale = null,
    string? Theme = null,
    string? Locale = null,
    bool? Rtl = null)
{
    public bool IsEmpty => WidthDp is null && HeightDp is null && Density is null &&
                           FontScale is null && string.IsNullOrWhiteSpace(Theme) &&
                           string.IsNullOrWhiteSpace(Locale) && Rtl is null;

    public string Summary()
    {
        var parts = new List<string>();
        if (WidthDp is { } w) parts.Add(HeightDp is { } h ? $"{w}×{h}dp" : $"{w}dp wide");
        if (Density is { } d) parts.Add($"{d}dpi");
        if (FontScale is { } fs) parts.Add($"font {fs.ToString("0.##", CultureInfo.InvariantCulture)}x");
        if (!string.IsNullOrWhiteSpace(Theme)) parts.Add(Theme.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(Locale)) parts.Add(Locale.Trim());
        if (Rtl == true) parts.Add("RTL");
        else if (Rtl == false) parts.Add("LTR");
        return parts.Count == 0 ? "default" : string.Join(", ", parts);
    }

    public static DeviceConfig FromPreset(string preset) => preset.Trim().ToLowerInvariant() switch
    {
        "default" => new DeviceConfig(),
        "narrow" or "narrow-phone" => new DeviceConfig(WidthDp: 360, HeightDp: 780),
        "phone" or "standard" => new DeviceConfig(WidthDp: 412, HeightDp: 915),
        "wide" or "wide-phone" => new DeviceConfig(WidthDp: 430, HeightDp: 932),
        "tablet" => new DeviceConfig(WidthDp: 800, HeightDp: 1280, Density: 240),
        "large-font" or "large_font" or "font-1.3" => new DeviceConfig(FontScale: 1.3),
        "xlarge-font" or "font-1.5" => new DeviceConfig(FontScale: 1.5),
        "dark" or "dark-theme" => new DeviceConfig(Theme: "dark"),
        "light" or "light-theme" => new DeviceConfig(Theme: "light"),
        "rtl" => new DeviceConfig(Rtl: true),
        _ => throw new ArgumentException(
            $"Unknown config preset '{preset}'. Use default, narrow, phone, wide, tablet, large-font, xlarge-font, dark, light or rtl."),
    };
}

/// <summary>
/// Applies <see cref="DeviceConfig"/> overrides to an Android device via ADB and restores the original settings on dispose.
/// </summary>
public static partial class DeviceConfigurator
{
    public static async Task<IAsyncDisposable> ApplyAsync(
        AdbClient adb,
        string serial,
        DeviceConfig config,
        string? appPackage = null,
        CancellationToken ct = default)
    {
        if (config.IsEmpty) return NoopDisposable.Instance;

        var restoreCommands = new List<string>();

        try
        {
            // A size or density the user had already overridden is put back as it was, not reset to the panel's.
            if (config.Density is { } density)
            {
                var previous = OverrideValue(await adb.ShellAsync(serial, "wm density", ct), "density");
                restoreCommands.Add(previous is null ? "wm density reset" : $"wm density {previous}");
                await adb.ShellAsync(serial, $"wm density {density}", ct);
            }

            if (config.WidthDp is { } widthDp)
            {
                var currentDensity = config.Density ?? await ReadDensityAsync(adb, serial, ct);
                var scale = currentDensity / 160.0;
                var wPx = (int)Math.Round(widthDp * scale);
                var hPx = config.HeightDp is { } heightDp
                    ? (int)Math.Round(heightDp * scale)
                    : (int)Math.Round(widthDp * 2.15 * scale);
                var previous = OverrideValue(await adb.ShellAsync(serial, "wm size", ct), "size");
                restoreCommands.Add(previous is null ? "wm size reset" : $"wm size {previous}");
                await adb.ShellAsync(serial, $"wm size {wPx}x{hPx}", ct);
            }

            if (config.FontScale is { } fontScale)
            {
                var prev = (await adb.ShellAsync(serial, "settings get system font_scale", ct)).Trim();
                var restoreVal = double.TryParse(prev, CultureInfo.InvariantCulture, out _) ? prev : "1.0";
                restoreCommands.Add($"settings put system font_scale {restoreVal}");
                await adb.ShellAsync(serial, $"settings put system font_scale {fontScale.ToString("0.##", CultureInfo.InvariantCulture)}", ct);
            }

            if (!string.IsNullOrWhiteSpace(config.Theme))
            {
                // "Night mode: yes", "no", "auto" or "custom": anything but yes/no goes back to auto.
                var prevMode = (await adb.ShellAsync(serial, "cmd uimode night", ct)).Trim();
                var prevArg = prevMode.EndsWith("yes", StringComparison.OrdinalIgnoreCase) ? "yes"
                    : prevMode.EndsWith("no", StringComparison.OrdinalIgnoreCase) ? "no" : "auto";
                restoreCommands.Add($"cmd uimode night {prevArg}");
                var target = config.Theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase) ? "yes" : "no";
                await adb.ShellAsync(serial, $"cmd uimode night {target}", ct);
            }

            if (!string.IsNullOrWhiteSpace(config.Locale))
            {
                var locale = config.Locale.Trim();
                if (!LocaleRegex().IsMatch(locale))
                    throw new ArgumentException($"Invalid locale tag: '{locale}' (expected e.g. 'de-DE', 'uk-UA', 'en-US').");

                if (!string.IsNullOrWhiteSpace(appPackage))
                {
                    // Android 13+ per-app locale (non-destructive, no root required).
                    try
                    {
                        await adb.ShellAsync(serial, $"cmd locale set-app-locales {appPackage} --locales {locale}", ct);
                        restoreCommands.Add($"cmd locale set-app-locales {appPackage} --locales \"\"");
                    }
                    catch (InvalidOperationException)
                    {
                        await SetSystemLocaleFallbackAsync(adb, serial, locale, restoreCommands, ct);
                    }
                }
                else
                {
                    await SetSystemLocaleFallbackAsync(adb, serial, locale, restoreCommands, ct);
                }
            }

            if (config.Rtl is { } rtl)
            {
                var prev = (await adb.ShellAsync(serial, "settings get global debug.force_rtl", ct)).Trim();
                var restoreVal = prev is "0" or "1" ? prev : "0";
                restoreCommands.Add($"settings put global debug.force_rtl {restoreVal}");
                await adb.ShellAsync(serial, $"settings put global debug.force_rtl {(rtl ? "1" : "0")}", ct);
            }

            // Let Android finish re-creating activities / re-laying out views.
            await Task.Delay(1200, ct);

            return new RestoreScope(adb, serial, restoreCommands);
        }
        catch
        {
            await RestoreAsync(adb, serial, restoreCommands);
            throw;
        }
    }

    static async Task SetSystemLocaleFallbackAsync(AdbClient adb, string serial, string locale, List<string> restoreCommands, CancellationToken ct)
    {
        var prev = (await adb.ShellAsync(serial, "settings get system system_locales", ct)).Trim();
        if (!string.IsNullOrEmpty(prev) && prev != "null")
            restoreCommands.Add($"settings put system system_locales {prev}");
        await adb.ShellAsync(serial, $"settings put system system_locales {locale}", ct);
    }

    /// <summary>The "Override size: 1080x2400" / "Override density: 480" value of wm, or null if there is none.</summary>
    public static string? OverrideValue(string wmOutput, string what)
    {
        var m = Regex.Match(wmOutput, $@"Override {what}:\s*(\S+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    static async Task<int> ReadDensityAsync(AdbClient adb, string serial, CancellationToken ct)
    {
        var output = await adb.ShellAsync(serial, "wm density", ct);
        var matches = DensityRegex().Matches(output);
        return matches.Count > 0 ? int.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture) : 420;
    }

    static async Task RestoreAsync(AdbClient adb, string serial, List<string> commands)
    {
        for (var i = commands.Count - 1; i >= 0; i--)
        {
            try { await adb.ShellAsync(serial, commands[i]); } catch { }
        }
    }

    sealed class RestoreScope(AdbClient adb, string serial, List<string> commands) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await RestoreAsync(adb, serial, commands);
            await Task.Delay(500);
        }
    }

    sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [GeneratedRegex(@"density:\s*(\d+)")]
    private static partial Regex DensityRegex();

    [GeneratedRegex(@"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LocaleRegex();
}
