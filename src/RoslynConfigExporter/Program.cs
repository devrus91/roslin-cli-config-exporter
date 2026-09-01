namespace RoslynConfigExporter;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CliOptions.Help);
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            Console.Error.WriteLine($"Loading {options.Target}...");
            var (workspace, solution, workspaceIssues) = await WorkspaceLoader.LoadAsync(options.Target, cancellation.Token);
            using (workspace)
            {
                Console.Error.WriteLine($"Analyzing {solution.Projects.Count()} project(s)...");
                var rules = ExternalWrapperRuleLoader.Load(options.RuleFiles);
                var analyzer = new ConfigurationUsageAnalyzer(options.IncludeGenerated, rules);
                var result = await analyzer.AnalyzeAsync(solution, cancellation.Token);
                var configEntries = ConfigurationFileReader.Read(options.ConfigFiles);
                var report = ReportBuilder.Build(
                    options.Target,
                    solution.Projects.Select(p => p.Name).ToArray(),
                    result,
                    configEntries,
                    workspaceIssues);

                await ReportWriter.WriteAsync(report, options.Output, options.Format, cancellation.Token);

                var dynamicCount = report.Keys.Count(k => k.Confidence == Confidence.Dynamic);
                var missingCount = options.ConfigFiles.Count == 0
                    ? 0
                    : report.Keys.Count(k => k.Confidence <= Confidence.Inferred && !k.PresentInConfiguration);
                Console.Error.WriteLine(
                    $"Exported {report.Keys.Count} keys ({dynamicCount} dynamic), {report.Usages.Count} usages to {options.Output}");
                if (options.ConfigFiles.Count > 0)
                {
                    Console.Error.WriteLine($"Configuration comparison: {missingCount} missing, {report.UnusedConfigurationEntries.Count} unmatched entries.");
                }

                if (options.FailOnDynamic && dynamicCount > 0)
                {
                    return 2;
                }

                if (options.FailOnMissing && missingCount > 0)
                {
                    return 3;
                }

                return 0;
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Analysis cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }
}
