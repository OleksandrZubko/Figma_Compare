using System.Net;
using System.Text;
using Driftcheck.Mcp.Devices;
using SkiaSharp;

namespace Driftcheck.Mcp.Comparison;

/// <summary>
/// Generates an annotated screenshot (<c>a11y_annotated.png</c>), a Markdown report, and a self-contained
/// interactive HTML report (<c>a11y_report.html</c>) for <c>audit_accessibility</c>.
/// </summary>
public static class A11yReport
{
    public sealed record Output(
        int Score,
        int Errors,
        int Warnings,
        string Markdown,
        string HtmlPath,
        string AnnotatedPngPath);

    public static Output Write(
        ScreenCapture capture,
        SKBitmap screenshot,
        IReadOnlyList<A11yIssue> issues,
        string outputDirectory,
        IReadOnlyList<A11yIssue>? largeFontIssues = null)
    {
        var dir = Path.Combine(outputDirectory, $"a11y_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dir);

        var annotatedPath = Path.Combine(dir, "a11y_annotated.png");
        WriteAnnotatedPng(screenshot, capture.PixelsPerUnit, issues, annotatedPath);

        var score = A11yAuditor.ComputeScore(capture.Elements.Count, issues);
        var errors = issues.Count(i => i.Severity == A11ySeverity.Error);
        var warnings = issues.Count(i => i.Severity == A11ySeverity.Warning);

        var htmlPath = Path.Combine(dir, "a11y_report.html");
        var md = BuildMarkdown(capture, score, errors, warnings, issues, largeFontIssues, htmlPath, annotatedPath);
        File.WriteAllText(Path.Combine(dir, "a11y_report.md"), md);
        File.WriteAllText(htmlPath, BuildHtml(capture, score, errors, warnings, issues, largeFontIssues, File.ReadAllBytes(annotatedPath)));

        return new Output(score, errors, warnings, md, htmlPath, annotatedPath);
    }

    static void WriteAnnotatedPng(SKBitmap screenshot, double pxPerUnit, IReadOnlyList<A11yIssue> issues, string path)
    {
        using var annotated = screenshot.Copy();
        using var canvas = new SKCanvas(annotated);
        using var errStroke = new SKPaint { Color = new SKColor(220, 38, 38), Style = SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = true };
        using var warnStroke = new SKPaint { Color = new SKColor(234, 88, 12), Style = SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = true };
        using var errFill = new SKPaint { Color = new SKColor(220, 38, 38), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var warnFill = new SKPaint { Color = new SKColor(234, 88, 12), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var font = new SKFont { Size = 22 };

        for (var i = 0; i < issues.Count; i++)
        {
            var issue = issues[i];
            var r = issue.Bounds.Scale(pxPerUnit);
            var rect = SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
            var isErr = issue.Severity == A11ySeverity.Error;
            canvas.DrawRect(rect, isErr ? errStroke : warnStroke);

            var badge = (i + 1).ToString();
            var bx = Math.Clamp((float)r.X, 2, Math.Max(2, annotated.Width - 34));
            var by = Math.Clamp((float)r.Y - 26, 2, Math.Max(2, annotated.Height - 28));
            canvas.DrawRoundRect(SKRect.Create(bx, by, 30, 26), 6, 6, isErr ? errFill : warnFill);
            canvas.DrawText(badge, bx + 8, by + 20, SKTextAlign.Left, font, textPaint);
        }

        using var data = annotated.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    static string BuildMarkdown(
        ScreenCapture capture, int score, int errors, int warnings,
        IReadOnlyList<A11yIssue> issues, IReadOnlyList<A11yIssue>? largeFontIssues,
        string htmlPath, string annotatedPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Accessibility Audit (Google ATF + WCAG 2.1 AA)");
        sb.AppendLine();
        sb.AppendLine($"**A11y Score: {score} / 100** · Errors: **{errors}** · Warnings: **{warnings}** · Audited elements: {capture.Elements.Count}");
        sb.AppendLine($"- Device: {capture.Serial} ({capture.WidthUnits:0.#}×{capture.HeightUnits:0.#} {capture.Unit})");
        if (capture.App is { } app) sb.AppendLine($"- App: {app}");
        if (capture.SafeArea is { } sa) sb.AppendLine($"- Safe Area insets: top={sa.Top:0.#} {capture.Unit}, bottom={sa.Bottom:0.#} {capture.Unit}");
        if (capture.KeyboardHeightUnits is > 0 and var kb) sb.AppendLine($"- Visible keyboard height: {kb:0.#} {capture.Unit}");
        sb.AppendLine($"- Interactive report: {htmlPath}");
        sb.AppendLine($"- Annotated screenshot: {annotatedPath}");
        sb.AppendLine();

        if (issues.Count == 0)
        {
            sb.AppendLine("No accessibility issues found on this screen.");
        }
        else
        {
            sb.AppendLine("| # | Severity | Rule | WCAG | Element | Issue | Fix |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            for (var i = 0; i < issues.Count; i++)
            {
                var it = issues[i];
                var sev = it.Severity == A11ySeverity.Error ? "ERROR" : "WARN";
                sb.AppendLine($"| {i + 1} | {sev} | {Esc(it.RuleId)} | {Esc(it.Wcag)} | {Esc(it.Element)} | {Esc(it.Detail)} | {Esc(it.Fix)} |");
            }
        }

        if (largeFontIssues is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"## Large Font Check (`font_scale = 1.3`) — {largeFontIssues.Count} issue(s)");
            if (largeFontIssues.Count == 0)
            {
                sb.AppendLine("No new accessibility or text-truncation issues appeared at `font_scale = 1.3`.");
            }
            else
            {
                foreach (var lf in largeFontIssues)
                    sb.AppendLine($"- **{lf.RuleId}** (`{lf.Element}`): {lf.Detail}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    static string BuildHtml(
        ScreenCapture capture, int score, int errors, int warnings,
        IReadOnlyList<A11yIssue> issues, IReadOnlyList<A11yIssue>? largeFontIssues,
        byte[] annotatedPng)
    {
        var scoreClass = score >= 90 ? "ok" : score >= 70 ? "warn" : "bad";
        var imgData = "data:image/png;base64," + Convert.ToBase64String(annotatedPng);

        var rows = new StringBuilder();
        for (var i = 0; i < issues.Count; i++)
        {
            var it = issues[i];
            var sevClass = it.Severity == A11ySeverity.Error ? "err" : "wrn";
            var sevLabel = it.Severity == A11ySeverity.Error ? "ERROR" : "WARN";
            rows.AppendLine($"""
                <tr data-sev="{sevClass}" data-cat="{H(it.Category)}">
                  <td><strong>#{i + 1}</strong></td>
                  <td><span class="pill {sevClass}">{sevLabel}</span></td>
                  <td><code>{H(it.RuleId)}</code><div class="sub">{H(it.Wcag)}</div></td>
                  <td><strong>{H(it.Element)}</strong></td>
                  <td>{H(it.Detail)}<div class="fix">{H(it.Fix)}</div></td>
                </tr>
                """);
        }

        var lfSection = "";
        if (largeFontIssues is not null)
        {
            var lfItems = largeFontIssues.Count == 0
                ? "<p>No new issues appeared at <code>font_scale = 1.3</code>.</p>"
                : "<ul>" + string.Join("", largeFontIssues.Select(i => $"<li><strong>{H(i.RuleId)}</strong> ({H(i.Element)}): {H(i.Detail)}</li>")) + "</ul>";
            lfSection = $"<h2>Large Font Check (font_scale = 1.3)</h2>{lfItems}";
        }

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>DriftCheck Accessibility Audit</title>
              <style>
                body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; margin: 24px; color: #1f2328; background: #f6f8fa; }
                .header { display: flex; align-items: center; gap: 16px; margin-bottom: 20px; flex-wrap: wrap; }
                .score { font-size: 22px; font-weight: 700; padding: 6px 14px; border-radius: 10px; }
                .score.ok { background: #dcfce7; color: #166534; }
                .score.warn { background: #fef3c7; color: #92400e; }
                .score.bad { background: #fee2e2; color: #991b1b; }
                .layout { display: grid; grid-template-columns: 360px 1fr; gap: 24px; align-items: start; }
                @media (max-width: 960px) { .layout { grid-template-columns: 1fr; } }
                .shot { background: #fff; padding: 12px; border-radius: 10px; box-shadow: 0 1px 3px rgba(0,0,0,0.08); }
                .shot img { width: 100%; height: auto; border-radius: 6px; display: block; }
                .panel { background: #fff; padding: 16px; border-radius: 10px; box-shadow: 0 1px 3px rgba(0,0,0,0.08); }
                .filters { display: flex; gap: 8px; margin-bottom: 12px; }
                .filters button { border: 1px solid #d1d5db; background: #fff; padding: 5px 12px; border-radius: 6px; cursor: pointer; font-size: 13px; }
                .filters button.active { background: #1f2328; color: #fff; border-color: #1f2328; }
                table { border-collapse: collapse; width: 100%; font-size: 13px; }
                th, td { text-align: left; padding: 10px; border-bottom: 1px solid #e5e7eb; vertical-align: top; }
                th { background: #f3f4f6; font-weight: 600; }
                .pill { display: inline-block; padding: 2px 7px; border-radius: 10px; font-weight: 700; font-size: 11px; }
                .pill.err { background: #fee2e2; color: #b91c1c; }
                .pill.wrn { background: #ffedd5; color: #c2410c; }
                .sub { color: #6b7280; font-size: 11px; margin-top: 2px; }
                .fix { color: #047857; font-size: 12px; margin-top: 4px; }
              </style>
            </head>
            <body>
              <div class="header">
                <span class="score {{scoreClass}}">{{score}} / 100</span>
                <div>
                  <h1 style="margin:0;font-size:20px;">Accessibility Audit — {{H(capture.App?.Id ?? capture.Serial)}}</h1>
                  <div class="sub">{{H(capture.Serial)}} · {{errors}} errors, {{warnings}} warnings · {{capture.Elements.Count}} elements</div>
                </div>
              </div>
              <div class="layout">
                <div class="shot"><img src="{{imgData}}" alt="Annotated screenshot"></div>
                <div class="panel">
                  <div class="filters">
                    <button class="active" onclick="filter('all', this)">All ({{issues.Count}})</button>
                    <button onclick="filter('err', this)">Errors ({{errors}})</button>
                    <button onclick="filter('wrn', this)">Warnings ({{warnings}})</button>
                  </div>
                  <table>
                    <thead><tr><th>#</th><th>Severity</th><th>Rule</th><th>Element</th><th>Issue &amp; Fix</th></tr></thead>
                    <tbody>{{rows}}</tbody>
                  </table>
                  {{lfSection}}
                </div>
              </div>
              <script>
                function filter(sev, btn) {
                  document.querySelectorAll('.filters button').forEach(b => b.classList.remove('active'));
                  btn.classList.add('active');
                  document.querySelectorAll('tbody tr').forEach(tr => {
                    tr.style.display = (sev === 'all' || tr.dataset.sev === sev) ? '' : 'none';
                  });
                }
              </script>
            </body>
            </html>
            """;
    }

    static string H(string s) => WebUtility.HtmlEncode(s);
    static string Esc(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
