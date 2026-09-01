using System.Text;
using System.Text.Json;

namespace RoslynConfigExporter;

internal static class ReportWriter
{
    public static async Task WriteAsync(AnalysisReport report, string output, string format, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var content = format switch
        {
            "csv" => ToCsv(report),
            "markdown" => ToMarkdown(report),
            _ => JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            })
        };
        await File.WriteAllTextAsync(output, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static string ToCsv(AnalysisReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("key,confidence,valueTypes,projects,usageCount,presentInConfiguration,configurationFiles");
        foreach (var key in report.Keys)
        {
            builder.AppendLine(string.Join(',',
                Csv(key.Key),
                Csv(key.Confidence.ToString()),
                Csv(string.Join(";", key.ValueTypes)),
                Csv(string.Join(";", key.Projects)),
                key.UsageCount,
                key.PresentInConfiguration.ToString().ToLowerInvariant(),
                Csv(string.Join(";", key.ConfigurationFiles))));
        }

        return builder.ToString();
    }

    private static string ToMarkdown(AnalysisReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Configuration usage report");
        builder.AppendLine();
        builder.AppendLine($"Target: `{EscapeMarkdown(report.Target)}`  ");
        builder.AppendLine($"Generated: `{report.GeneratedAtUtc:O}`  ");
        builder.AppendLine($"Projects: {report.Projects.Count}; keys: {report.Keys.Count}; usages: {report.Usages.Count}");
        builder.AppendLine();
        builder.AppendLine("| Key | Confidence | Type | Usages | In config |");
        builder.AppendLine("|---|---|---|---:|:---:|");
        foreach (var key in report.Keys)
        {
            builder.AppendLine($"| `{EscapeMarkdown(key.Key)}` | {key.Confidence} | {EscapeMarkdown(string.Join(", ", key.ValueTypes))} | {key.UsageCount} | {(key.PresentInConfiguration ? "yes" : "no")} |");
        }

        if (report.UnusedConfigurationEntries.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Configuration entries not matched by code");
            builder.AppendLine();
            foreach (var entry in report.UnusedConfigurationEntries)
            {
                builder.AppendLine($"- `{EscapeMarkdown(entry.Key)}` — `{EscapeMarkdown(entry.File)}`");
            }
        }

        if (report.Issues.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Analysis issues");
            builder.AppendLine();
            foreach (var issue in report.Issues)
            {
                builder.AppendLine($"- **{EscapeMarkdown(issue.Severity)}** {EscapeMarkdown(issue.Project)}: {EscapeMarkdown(issue.Message)}");
            }
        }

        return builder.ToString();
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal);
}
