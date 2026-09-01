namespace RoslynConfigExporter;

internal sealed record CliOptions(
    string Target,
    string Output,
    string Format,
    IReadOnlyList<string> ConfigFiles,
    IReadOnlyList<string> RuleFiles,
    bool IncludeGenerated,
    bool FailOnDynamic,
    bool FailOnMissing,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
        {
            return new("", "", "json", [], [], false, false, false, true);
        }

        string? target = null;
        string? output = null;
        var format = "json";
        var configs = new List<string>();
        var rules = new List<string>();
        var includeGenerated = false;
        var failOnDynamic = false;
        var failOnMissing = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-o" or "--output":
                    output = NextValue(args, ref i, arg);
                    break;
                case "-f" or "--format":
                    format = NextValue(args, ref i, arg).ToLowerInvariant();
                    break;
                case "-c" or "--config":
                    configs.Add(NextValue(args, ref i, arg));
                    break;
                case "-r" or "--rules":
                    rules.Add(NextValue(args, ref i, arg));
                    break;
                case "--include-generated":
                    includeGenerated = true;
                    break;
                case "--fail-on-dynamic":
                    failOnDynamic = true;
                    break;
                case "--fail-on-missing":
                    failOnMissing = true;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (target is not null)
                    {
                        throw new ArgumentException($"Only one solution or project can be analyzed. Unexpected: {arg}");
                    }

                    target = arg;
                    break;
            }
        }

        if (target is null)
        {
            throw new ArgumentException("A .sln, .slnx, or .csproj path is required.");
        }

        if (format is not ("json" or "csv" or "markdown" or "md"))
        {
            throw new ArgumentException("Format must be json, csv, or markdown.");
        }

        var extension = format switch { "csv" => ".csv", "markdown" or "md" => ".md", _ => ".json" };
        output ??= Path.Combine(Environment.CurrentDirectory, "configuration-usage-report" + extension);

        return new(
            Path.GetFullPath(target),
            Path.GetFullPath(output),
            format == "md" ? "markdown" : format,
            configs.Select(Path.GetFullPath).ToArray(),
            rules.Select(Path.GetFullPath).ToArray(),
            includeGenerated,
            failOnDynamic,
            failOnMissing,
            false);
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[index];
    }

    public static string Help => """
        RoslynConfigExporter - export configuration keys used by a .NET solution.

        Usage:
          config-usage <solution.sln|solution.slnx|project.csproj> [options]

        Options:
          -o, --output <path>       Output file (default: configuration-usage-report.<format>)
          -f, --format <format>     json, csv, or markdown (default: json)
          -c, --config <path>       appsettings JSON file or directory; repeatable
          -r, --rules <path>        JSON rules for external wrapper methods; repeatable
              --include-generated   Include generated C# documents
              --fail-on-dynamic     Exit 2 when dynamic keys remain
              --fail-on-missing     Exit 3 when exact used keys are absent from supplied configs
          -h, --help                Show help

        Directory values passed to --config recursively include appsettings*.json.
        """;
}
