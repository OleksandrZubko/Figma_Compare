using Driftcheck.Mcp;
using Driftcheck.Mcp.Qa;

namespace Driftcheck.Tests;

public class SessionAnalyzerTests
{
    static HttpCall Call(string url, int? status = 200, long ms = 120, long at = 0, string? error = null,
        string? requestBody = null, string? responseBody = null, string responseType = "application/json") =>
        new(at, "GET", url, status, ms, error, requestBody is null ? null : "application/json", requestBody?.Length, requestBody,
            responseType, responseBody?.Length, responseBody);

    [Fact]
    public void ReportsFailedSlowDuplicateAndCleartextRequests()
    {
        var issues = SessionAnalyzer.AnalyzeNetwork(
        [
            Call("https://api.app.com/balance", 500),
            Call("https://api.app.com/profile", 404),
            Call("https://api.app.com/offers", null, error: "java.net.SocketTimeoutException: timeout"),
            Call("https://api.app.com/rewards", ms: 4200),
            Call("https://api.app.com/feed?page=1", at: 1000),
            Call("https://api.app.com/feed?page=1", at: 1800),
            Call("http://cdn.app.com/logo.png"),
            Call("http://10.0.2.2:8081/index.bundle"),
        ]);

        Assert.Contains(issues, i => i is { Title: "Server error 500", Severity: QaSeverity.Error });
        Assert.Contains(issues, i => i is { Title: "Client error 404", Severity: QaSeverity.Warning });
        Assert.Contains(issues, i => i is { Title: "Request failed", Severity: QaSeverity.Error } && i.Detail.Contains("timeout"));
        Assert.Contains(issues, i => i.Title == "Slow request (4200 ms)");
        Assert.Contains(issues, i => i.Title == "Duplicate request ×2" && i.Detail.Contains("?…") && !i.Detail.Contains("page=1"));
        Assert.Single(issues, i => i.Title == "Cleartext HTTP request"); // The Metro bundler on the host is fine.
    }

    [Fact]
    public void FindsAnalyticsEventsInPostHogAndAmplitudeUploads()
    {
        var events = SessionAnalyzer.EventsFromNetwork(
        [
            Call("https://us.i.posthog.com/batch/", requestBody: """{"api_key":"phc_x","batch":[{"event":"purchase","properties":{"value":12.5}},{"event":"$screen","properties":{}}]}"""),
            Call("https://api2.amplitude.com/2/httpapi", requestBody: """{"events":[{"event_type":"sign_up","event_properties":{"method":"email"}}]}"""),
            Call("https://api.app.com/orders", requestBody: """{"event":"not analytics"}"""),
        ]);

        Assert.Equal(["purchase", "$screen", "sign_up"], events.Select(e => e.Name));
        Assert.Equal("posthog", events[0].Sdk);
        Assert.Contains("12.5", events[0].Params);
    }

    [Fact]
    public void ReadsFirebaseEventsFromLogcat()
    {
        var entries = LogcatMonitor.Parse(
            "  1795688435.684  1234  1250 V FA-SVC  : Logging event: origin=app,name=add_to_cart,params=Bundle[{value=9.99, currency=USD}]\n" +
            "  1795688435.700  1234  1250 V FA      : Something else\n");

        var e = Assert.Single(SessionAnalyzer.EventsFromLogcat(entries));
        Assert.Equal(("firebase", "add_to_cart"), (e.Sdk, e.Name));
        Assert.Contains("currency=USD", e.Params);
    }

    [Fact]
    public void ChecksEventsAgainstTheTrackingPlan()
    {
        var events = new List<AnalyticsEvent>
        {
            new("posthog", "purchase", """{"value":12.5}""", 1000, "hook"),
            new("posthog", "purchase", """{"value":12.5}""", 1400, "hook"),
            new("posthog", "promo_viewed", null, 2000, "hook"),
            new("posthog", "$snapshot", null, 2100, "hook"),
        };
        const string plan = """[{"event":"purchase","params":["value","currency"],"max":1},{"event":"checkout_started"}]""";

        var issues = SessionAnalyzer.CheckEvents(events, plan);

        Assert.Contains(issues, i => i is { Title: "Event not sent: checkout_started", Severity: QaSeverity.Error });
        Assert.Contains(issues, i => i.Title == "Parameter missing: purchase.currency");
        Assert.DoesNotContain(issues, i => i.Title == "Parameter missing: purchase.value");
        Assert.Contains(issues, i => i.Title == "Event sent too often: purchase");
        Assert.Contains(issues, i => i.Title == "Event sent twice: purchase");
        Assert.Contains(issues, i => i is { Title: "Event not in the tracking plan: promo_viewed", Severity: QaSeverity.Info });
        Assert.DoesNotContain(issues, i => i.Title.Contains("$snapshot"));
    }

    [Fact]
    public void FlagsScreenValuesThatAreCloseButNotEqualToTheApi()
    {
        var calls = new List<HttpCall>
        {
            Call("https://api.app.com/balance", responseBody: """{"balance":1146.91,"reward":"2055.43","year":2028,"items":[{"price":19.99}]}"""),
        };
        var texts = new List<(string, Rect?)>
        {
            ("$1146.9", null),          // rounded: reported
            ("$2,055.43", null),        // same value: fine
            ("Oct 24, 2028", null),     // a year: ignored
            ("19.99", null),            // same: fine
            ("Total 500.00", null),     // nothing close in the API: not reported
        };

        var issue = Assert.Single(SessionAnalyzer.CompareDataWithUi(texts, calls));
        Assert.Contains("1146.91", issue.Detail);
    }

    [Theory]
    [InlineData("1,146.91", 1146.91)]
    [InlineData("1.146,91", 1146.91)]
    [InlineData("1 146,91", 1146.91)]
    [InlineData("12,50", 12.50)]
    [InlineData("1,146", 1146)]
    public void ParsesScreenNumbersInAnyFormat(string text, double expected) =>
        Assert.Equal((decimal)expected, SessionAnalyzer.ParseScreenNumber(text));

    [Fact]
    public void JoinsAMessageLoggedOverSeveralLines()
    {
        const string log = """
              1795688400.300  4321  4400 W ReactNativeJS: '[loadCompanies] failed:', { statusCode: 429,
              1795688400.300  4321  4400 W ReactNativeJS:   message: 'Query limit, try later',
              1795688400.301  4321  4400 W ReactNativeJS:   domain: 'gifted.fyi' }
              1795688401.500  4321  4400 W ReactNativeJS: another warning
            """;
        var findings = LogcatMonitor.Analyze(LogcatMonitor.Parse(log), "co.gifted.app", [4321]);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Summary.Contains("statusCode: 429") && f.Summary.Contains("Query limit") && f.Summary.Contains("gifted.fyi"));
    }

    [Fact]
    public void FindsCrashesAnrsAndReactNativeErrorsOfTheApp()
    {
        const string log = """
              1795688400.100  4321  4321 E AndroidRuntime: FATAL EXCEPTION: main
              1795688400.101  4321  4321 E AndroidRuntime: Process: co.gifted.app, PID: 4321
              1795688400.102  4321  4321 E AndroidRuntime: java.lang.NullPointerException: Attempt to invoke virtual method
              1795688400.200   900   950 E ActivityManager: ANR in co.gifted.app (co.gifted.app/.MainActivity)
              1795688400.300  4321  4400 E ReactNativeJS: TypeError: undefined is not an object (evaluating 'a.b')
              1795688400.301  4321  4400 W ReactNativeJS: VirtualizedList: You have a large list
              1795688400.400  4321  4400 E unknown:ReactNative: Exception in native call
              1795688400.500  5555  5555 E OtherApp: not ours
              1795688400.600  4321  4401 E OkHttp: failed to connect
              1795688400.601  4321  4401 E OkHttp: failed to connect
            """;
        var findings = LogcatMonitor.Analyze(LogcatMonitor.Parse(log), "co.gifted.app", [4321]);

        Assert.Contains(findings, f => f is { Kind: "crash" } && f.Summary.Contains("NullPointerException"));
        Assert.Contains(findings, f => f.Kind == "anr");
        Assert.Contains(findings, f => f.Kind == "js-error" && f.Summary.Contains("TypeError"));
        Assert.Contains(findings, f => f.Kind == "js-error" && f.Summary.StartsWith("unknown:ReactNative"));
        Assert.Contains(findings, f => f.Kind == "js-warning");
        Assert.Contains(findings, f => f is { Kind: "error", Count: 2 } && f.Summary.StartsWith("OkHttp"));
        Assert.DoesNotContain(findings, f => f.Summary.Contains("OtherApp"));
    }
}
