using System.Globalization;
using System.Text;

namespace Driftcheck.Mcp.Qa;

public enum QaSeverity { Error, Warning, Info }

/// <summary>
/// A finding of the QA audits that aren't a design comparison: content, design tokens, resilience, performance,
/// monitoring (crashes, network, analytics) and security.
/// </summary>
/// <param name="Category">Short group shown in the report ("placeholder", "network", "jank"…).</param>
/// <param name="Title">One line: what is wrong.</param>
/// <param name="Detail">The evidence: values, the text found, the log line.</param>
/// <param name="Element">The element or resource it's about, if any.</param>
/// <param name="Bounds">Where on the screen, in dp, if it's about something visible.</param>
/// <param name="Fix">How to fix it, if there's a useful suggestion.</param>
public sealed record QaIssue(
    string Category,
    QaSeverity Severity,
    string Title,
    string Detail = "",
    string? Element = null,
    Rect? Bounds = null,
    string? Fix = null);

/// <summary>Markdown report shared by the QA audit tools, saved next to the other outputs of a run.</summary>
public static class QaReport
{
    /// <summary>A titled block of the report: a table or free text.</summary>
    public sealed record Section(string Title, string Markdown);

    /// <summary>
    /// Writes report.md (and issues.json) to a new folder in the output directory and returns the markdown.
    /// The issues table goes first, ordered by severity; <paramref name="sections"/> follow (measurements, logs).
    /// </summary>
    public static (string Markdown, string Directory) Write(
        string outputDirectory, string kind, string title, IReadOnlyList<QaIssue> issues,
        IReadOnlyList<Section>? sections = null, IReadOnlyList<string>? notes = null)
    {
        var dir = Path.Combine(outputDirectory, "qa", $"{DateTime.Now:yyyyMMdd-HHmmss}-{kind}");
        System.IO.Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();

        var errors = issues.Count(i => i.Severity == QaSeverity.Error);
        var warnings = issues.Count(i => i.Severity == QaSeverity.Warning);
        var infos = issues.Count - errors - warnings;
        sb.AppendLine(issues.Count == 0
            ? "**No issues found.**"
            : $"**{errors} error(s), {warnings} warning(s), {infos} info**");
        sb.AppendLine();

        foreach (var note in notes ?? [])
        {
            sb.AppendLine($"> {note}");
            sb.AppendLine();
        }

        if (issues.Count > 0)
        {
            sb.AppendLine("| Severity | Category | Issue | Element | Detail | Fix |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var i in issues.OrderBy(i => i.Severity).ThenBy(i => i.Category, StringComparer.Ordinal))
                sb.AppendLine($"| {Icon(i.Severity)} | {Cell(i.Category)} | {Cell(i.Title)} | {Cell(i.Element)} | {Cell(i.Detail)} | {Cell(i.Fix)} |");
            sb.AppendLine();
        }

        foreach (var section in sections ?? [])
        {
            sb.AppendLine($"## {section.Title}");
            sb.AppendLine();
            sb.AppendLine(section.Markdown.TrimEnd());
            sb.AppendLine();
        }

        var markdown = sb.ToString();
        File.WriteAllText(Path.Combine(dir, "report.md"), markdown);
        File.WriteAllText(Path.Combine(dir, "issues.json"), System.Text.Json.JsonSerializer.Serialize(issues,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        return (markdown + $"\nReport: {Path.Combine(dir, "report.md")}\n", dir);
    }

    static string Icon(QaSeverity s) => s switch
    {
        QaSeverity.Error => "🔴 error",
        QaSeverity.Warning => "🟠 warning",
        _ => "🔵 info",
    };

    /// <summary>A value for a markdown table cell: no pipes or line breaks, at most 200 characters.</summary>
    public static string Cell(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ⏎ ");
        return clean.Length > 200 ? clean[..200] + "…" : clean;
    }

    public static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
