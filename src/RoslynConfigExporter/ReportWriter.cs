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
        foreach (var group in report.Keys.GroupBy(k => k.Origin).OrderBy(g => g.Key))
        {
            builder.AppendLine(group.Key == UsageOrigin.Source ? "## Найдено в исходниках" : "## Найдено в сборках");
            builder.AppendLine();
            builder.AppendLine("| Иерархия в спецификации | Параметр | Тип значения | Обязательный<br>(nullable) | Требует переопределения<br>(required) | Описание | Возможные значения | Значение по умолчанию | Пример значения | Источник<br>(env/ехс/...) |");
            builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var key in group)
            {
                var metadata = report.Usages.Where(u => string.Equals(u.Key, key.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(u => u.Metadata).OfType<ParameterMetadata>().ToArray();
                string Values(Func<ParameterMetadata, string?> select) => string.Join("; ", metadata.Select(select).OfType<string>().Distinct());
                var split = key.Key.LastIndexOf(':');
                var cells = new[]
                {
                key.Key.Contains(ExternalAssemblyAnalyzer.UnknownParent, StringComparison.Ordinal) ? "Иерархия неизвестна" + (split > ExternalAssemblyAnalyzer.UnknownParent.Length ? " (известная часть: " + key.Key[(ExternalAssemblyAnalyzer.UnknownParent.Length + 1)..split] + ")" : "") : split < 0 ? "Корень" : key.Key[..split], split < 0 ? key.Key : key.Key[(split + 1)..],
                string.Join(", ", key.ValueTypes),
                Values(m => m.Nullable is null ? null : m.Nullable.Value ? "Нет (nullable)" : "Да (non-nullable)"),
                Values(m => m.Required is null ? null : m.Required.Value ? "Да" : "Нет"),
                Values(m => m.Description), Values(m => m.PossibleValues), Values(m => m.DefaultValue),
                Values(m => m.Example), EnvironmentVariable(key, report)
            };
                builder.AppendLine("| " + string.Join(" | ", cells.Select(c => EscapeMarkdown(string.IsNullOrEmpty(c) ? "Неизвестно" : c))) + " |");
            }
            builder.AppendLine();
        }
        builder.AppendLine("Nullable описывает тип, required — наличие [Required]. Источник показывает стандартное имя переменной среды (__ вместо :). Учитываются prefix провайдеров AddEnvironmentVariables, найденных в проекте; при нескольких провайдерах перечислены варианты. Наличие переменных не проверяется. Если регистрация не обнаружена, показано имя для стандартного провайдера без prefix. Пример значения берётся из XML-тега example.");
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

    private static string EnvironmentVariable(KeySummary key, AnalysisReport report)
    {
        if (key.Key.Contains(ExternalAssemblyAnalyzer.UnknownParent, StringComparison.Ordinal))
            return "env: неизвестно (иерархия неизвестна)";
        var prefixes = key.Projects.SelectMany(project =>
        {
            var sources = (report.EnvironmentSources ?? []).Where(s => s.Project == project).ToArray();
            return sources.Length == 0 ? new string?[] { "" } : sources.Select(s => s.Prefix);
        }).Distinct().ToArray();
        var suffix = key.Key.Replace(":", "__", StringComparison.Ordinal);
        return string.Join("; ", prefixes.Select(prefix => prefix is null
            ? "env: неизвестно (динамический prefix)"
            : "env: " + prefix.Replace(":", "__", StringComparison.Ordinal) + suffix +
                (key.Key.IndexOfAny(['*', '{']) >= 0 ? " (шаблон)" : "")));
    }
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string EscapeMarkdown(string value) => value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal);
}
