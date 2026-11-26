using System.Diagnostics;
using System.Text.Json;

namespace Driftcheck.Mcp.Qa;

/// <summary>An HTTP call made by the app through OkHttp, as the recorder saw it.</summary>
/// <param name="StartedMs">Device time in ms since the epoch.</param>
/// <param name="Status">HTTP status; null when the call failed before a response.</param>
/// <param name="Error">Exception of a failed call (timeout, DNS, TLS, no network).</param>
public sealed record HttpCall(
    long StartedMs, string? Method, string? Url, int? Status, long? Ms, string? Error,
    string? RequestType, long? RequestSize, string? RequestBody,
    string? ResponseType, long? ResponseSize, string? ResponseBody);

/// <summary>An event sent to an analytics SDK (from a native SDK call, the network or logcat).</summary>
/// <param name="Source">hook, network or logcat.</param>
public sealed record AnalyticsEvent(string Sdk, string Name, string? Params, long AtMs, string Source);

/// <summary>
/// Records the app's HTTP calls and analytics events with Frida (tools/frida-probe/record.py) between
/// <see cref="StartAsync"/> and <see cref="StopAsync"/>.
/// </summary>
public sealed class FridaRecorder : IAsyncDisposable
{
    readonly Process process;
    readonly List<HttpCall> calls = [];
    readonly List<AnalyticsEvent> events = [];
    readonly List<string> notes = [];
    readonly Task reader;
    readonly TaskCompletionSource<string?> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The hooks that were installed ("okhttp3.internal.connection.RealCall", analytics SDKs).</summary>
    public string Hooks { get; private set; } = "";

    FridaRecorder(Process process)
    {
        this.process = process;
        reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Attaches to the running app and waits until the hooks are in place. Throws InvalidOperationException with
    /// the reason (no frida module, no frida-server, app not running) if it can't.
    /// </summary>
    public static async Task<FridaRecorder> StartAsync(Settings settings, string serial, string package, CancellationToken ct = default)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "frida-probe", "record.py");
        if (!File.Exists(script)) throw new InvalidOperationException($"The Frida recorder is missing ({script}).");

        var psi = new ProcessStartInfo(settings.PythonPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { script, "--serial", serial, "--package", package }) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {settings.PythonPath}.");
        var recorder = new FridaRecorder(process);

        // Attaching can hang while the app is still starting; give it a bounded time.
        var finished = await Task.WhenAny(recorder.ready.Task, Task.Delay(TimeSpan.FromSeconds(25), ct));
        var error = finished == recorder.ready.Task ? await recorder.ready.Task : "the recorder didn't attach in 25 s";
        if (error is not null)
        {
            await recorder.DisposeAsync();
            throw new InvalidOperationException($"Frida recorder: {error}");
        }
        return recorder;
    }

    /// <summary>Removes the hooks and returns what was recorded.</summary>
    public async Task<(IReadOnlyList<HttpCall> Calls, IReadOnlyList<AnalyticsEvent> Events, IReadOnlyList<string> Notes)> StopAsync()
    {
        try
        {
            await process.StandardInput.WriteLineAsync("stop");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException) { }

        await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(10)));
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        lock (calls) return (calls.ToList(), events.ToList(), notes.ToList());
    }

    async Task ReadLoopAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc) Handle(doc.RootElement);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            var stderr = "";
            try { stderr = (await process.StandardError.ReadToEndAsync()).Trim(); } catch (Exception) { }
            ready.TrySetResult(string.IsNullOrEmpty(stderr) ? "the recorder exited" : $"the recorder exited: {Last(stderr)}");
        }
    }

    void Handle(JsonElement e)
    {
        switch (Str(e, "type"))
        {
            case "ready":
                Hooks = e.TryGetProperty("hooks", out var hooks) ? hooks.GetRawText() : "";
                ready.TrySetResult(null);
                break;
            case "error":
                ready.TrySetResult(Str(e, "error") ?? "unknown error");
                break;
            case "http":
                lock (calls)
                    calls.Add(new HttpCall(Long(e, "started") ?? 0, Str(e, "method"), Str(e, "url"), (int?)Long(e, "status"),
                        Long(e, "ms"), Str(e, "error"), Str(e, "requestType"), Long(e, "requestSize"), Str(e, "requestBody"),
                        Str(e, "responseType"), Long(e, "responseSize"), Str(e, "responseBody")));
                break;
            case "analytics":
                lock (calls)
                    events.Add(new AnalyticsEvent(Str(e, "sdk") ?? "?", Str(e, "name") ?? "?", Str(e, "params"), Long(e, "at") ?? 0, "hook"));
                break;
            case "agent-error":
                lock (calls) notes.Add($"agent error: {Str(e, "error")}");
                break;
            case "detached":
                lock (calls) notes.Add($"detached: {Str(e, "reason")}");
                break;
        }
    }

    static string Last(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? s : lines[^1];
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? (long)p.GetDouble() : null;

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        await Task.WhenAny(reader, Task.Delay(2000));
        process.Dispose();
    }
}
