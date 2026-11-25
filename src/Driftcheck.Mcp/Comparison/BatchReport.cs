using System.Net;
using System.Text;

namespace Driftcheck.Mcp.Comparison;

/// <summary>
/// Writes a combined Markdown + HTML summary report for multi-state (<c>compare_states</c>) and
/// multi-device/configuration matrix (<c>compare_matrix</c>) runs.
/// </summary>
public static class BatchReport
{
    public sealed record Entry(
        string Label,
        string Device,
        string Config,
        ComparisonResult? Result,
        string? Error = null)
    {
        public int OverflowCount => Result?.Diffs.Count(d => d.Property is "text overflow" or "overflow" or "overlap") ?? 0;
        public int MissingCount => Result?.Elements.Count(e => e.Status == ElementStatus.Missing) ?? 0;
    }

    public static (string Markdown, string HtmlPath) Write(string title, string outputDirectory, IReadOnlyList<Entry> entries)
    {
        var batchDir = Path.Combine(outputDirectory, $"batch_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(batchDir);

        var md = BuildMarkdown(title, entries);
        var mdPath = Path.Combine(batchDir, "summary.md");
        var htmlPath = Path.Combine(batchDir, "summary.html");
        File.WriteAllText(mdPath, md);
        File.WriteAllText(htmlPath, BuildHtml(title, entries));

        return (md + $"\n\nSummary report: {htmlPath}", htmlPath);
    }

    public static string BuildMarkdown(string title, IReadOnlyList<Entry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();

        var succeeded = entries.Where(e => e.Result is not null).ToList();
        var failed = entries.Count - succeeded.Count;
        var avgFidelity = succeeded.Count > 0 ? (int)Math.Round(succeeded.Average(e => e.Result!.Fidelity.Percent)) : 0;
        var totalDiffs = succeeded.Sum(e => e.Result!.Diffs.Count);
        var totalOverflows = succeeded.Sum(e => e.OverflowCount);

        sb.AppendLine($"Runs: **{entries.Count}** ({succeeded.Count} compared, {failed} failed) · " +
                      $"Average fidelity: **{avgFidelity} %** · Total diffs: **{totalDiffs}**" +
                      (totalOverflows > 0 ? $" · Overflows/truncations: **{totalOverflows}**" : ""));
        sb.AppendLine();
        sb.AppendLine("| Case | Device | Config | Fidelity | Diffs | Overflows | Missing | Report |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var e in entries)
        {
            if (e.Result is { } r)
            {
                sb.AppendLine($"| {Esc(e.Label)} | {Esc(e.Device)} | {Esc(e.Config)} | {r.Fidelity.Percent} % | {r.Diffs.Count} | {e.OverflowCount} | {e.MissingCount} | {r.HtmlReportPath} |");
            }
            else
            {
                sb.AppendLine($"| {Esc(e.Label)} | {Esc(e.Device)} | {Esc(e.Config)} | — | — | — | — | Error: {Esc(e.Error ?? "unknown")} |");
            }
        }

        // Highlight key diffs per failing/degraded case.
        foreach (var e in succeeded.Where(x => x.Result!.Diffs.Count > 0 || x.MissingCount > 0))
        {
            var r = e.Result!;
            sb.AppendLine();
            sb.AppendLine($"## {e.Label} ({e.Device} · {e.Config}) — {r.Fidelity.Percent} %");
            foreach (var d in r.Diffs.Take(15))
                sb.AppendLine($"- **{d.Name}** `{d.Property}`: Figma `{d.Expected}`, App `{d.Actual}`");
            if (r.Diffs.Count > 15)
                sb.AppendLine($"- … and {r.Diffs.Count - 15} more diffs (see {r.HtmlReportPath})");
        }

        return sb.ToString().TrimEnd();
    }

    static string BuildHtml(string title, IReadOnlyList<Entry> entries)
    {
        var rows = new StringBuilder();
        foreach (var e in entries)
        {
            if (e.Result is { } r)
            {
                var badgeClass = r.Fidelity.Percent >= 90 ? "ok" : r.Fidelity.Percent >= 70 ? "warn" : "bad";
                var link = "file://" + Uri.EscapeDataString(r.HtmlReportPath).Replace("%2F", "/");
                rows.AppendLine($"""
                    <tr>
                      <td><strong>{H(e.Label)}</strong></td>
                      <td>{H(e.Device)}</td>
                      <td>{H(e.Config)}</td>
                      <td><span class="badge {badgeClass}">{r.Fidelity.Percent} %</span></td>
                      <td>{r.Diffs.Count}</td>
                      <td>{e.OverflowCount}</td>
                      <td>{e.MissingCount}</td>
                      <td><a href="{link}">Open report</a></td>
                    </tr>
                    """);
            }
            else
            {
                rows.AppendLine($"""
                    <tr class="err">
                      <td><strong>{H(e.Label)}</strong></td>
                      <td>{H(e.Device)}</td>
                      <td>{H(e.Config)}</td>
                      <td colspan="5">Error: {H(e.Error ?? "unknown")}</td>
                    </tr>
                    """);
            }
        }

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>{{H(title)}}</title>
              <style>
                body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; margin: 24px; color: #1f2328; background: #f6f8fa; }
                h1 { font-size: 20px; margin-bottom: 16px; }
                table { border-collapse: collapse; width: 100%; background: #fff; border-radius: 8px; overflow: hidden; box-shadow: 0 1px 3px rgba(0,0,0,0.08); }
                th, td { text-align: left; padding: 10px 14px; border-bottom: 1px solid #e5e7eb; font-size: 14px; }
                th { background: #f3f4f6; font-weight: 600; }
                .badge { display: inline-block; padding: 2px 8px; border-radius: 12px; font-weight: 600; font-size: 12px; }
                .ok { background: #dcfce7; color: #166534; }
                .warn { background: #fef3c7; color: #92400e; }
                .bad { background: #fee2e2; color: #991b1b; }
                .err td { background: #fef2f2; color: #991b1b; }
                a { color: #2563eb; text-decoration: none; }
                a:hover { text-decoration: underline; }
              </style>
            </head>
            <body>
              <h1>{{H(title)}}</h1>
              <table>
                <thead>
                  <tr><th>Case</th><th>Device</th><th>Config</th><th>Fidelity</th><th>Diffs</th><th>Overflows</th><th>Missing</th><th>Report</th></tr>
                </thead>
                <tbody>
                  {{rows}}
                </tbody>
              </table>
            </body>
            </html>
            """;
    }

    static string H(string s) => WebUtility.HtmlEncode(s);
    static string Esc(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
