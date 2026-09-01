using System.Text.RegularExpressions;

namespace RoslynConfigExporter;

internal static class ReportBuilder
{
    public static AnalysisReport Build(
        string target,
        IReadOnlyList<string> projects,
        AnalysisResult result,
        IReadOnlyList<ConfigFileEntry> configEntries,
        IEnumerable<ProjectIssue>? workspaceIssues = null)
    {
        var usages = result.Usages
            .DistinctBy(u => (
                u.Project,
                u.Key.ToUpperInvariant(),
                u.Kind,
                u.Location.File.ToUpperInvariant(),
                u.Location.Line,
                u.Location.Column,
                u.ValueType))
            .OrderBy(u => u.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Location.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Location.Line)
            .ToArray();

        var summaries = usages
            .GroupBy(u => u.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var files = configEntries
                    .Where(entry => Matches(group.Key, entry.Key))
                    .Select(entry => entry.File)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new KeySummary(
                    group.Key,
                    group.Max(u => u.Confidence),
                    group.Select(u => u.ValueType).OfType<string>().Distinct().Order().ToArray(),
                    group.Select(u => u.Project).Distinct().Order().ToArray(),
                    group.Count(),
                    files.Length > 0,
                    files);
            })
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var unused = configEntries
            .Where(entry => !summaries.Any(summary => Matches(summary.Key, entry.Key)))
            .ToArray();

        return new(
            "1.0",
            DateTimeOffset.UtcNow,
            Path.GetFullPath(target),
            projects.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            summaries,
            usages,
            result.OptionsBindings.Distinct().OrderBy(b => b.OptionsType).ToArray(),
            result.OptionsConsumers.Distinct().OrderBy(c => c.OptionsType).ToArray(),
            configEntries,
            unused,
            result.Issues.Concat(workspaceIssues ?? []).ToArray());
    }

    public static bool Matches(string usagePattern, string configKey)
    {
        if (string.Equals(usagePattern, configKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!usagePattern.Contains('*') && !usagePattern.Contains('{'))
        {
            return configKey.StartsWith(usagePattern + ":", StringComparison.OrdinalIgnoreCase);
        }

        var regex = new System.Text.StringBuilder("^");
        for (var i = 0; i < usagePattern.Length; i++)
        {
            if (usagePattern[i] == '*')
            {
                regex.Append("[^:]+");
                continue;
            }

            if (usagePattern[i] == '{')
            {
                var end = usagePattern.IndexOf('}', i + 1);
                if (end >= 0)
                {
                    regex.Append("[^:]+");
                    i = end;
                    continue;
                }
            }

            regex.Append(Regex.Escape(usagePattern[i].ToString()));
        }

        regex.Append("(?:$|:)");
        return Regex.IsMatch(configKey, regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
