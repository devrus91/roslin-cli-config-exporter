using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RoslynConfigExporter.Tests;

public sealed class AnalyzerTests
{
    [Fact]
    public async Task FindsDirectDynamicOptionsAndWrapperKeys()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;

            public sealed class Settings
            {
                [ConfigurationKeyName("renamed")]
                public int Value { get; set; }
                public Nested Child { get; set; } = new();
            }

            public sealed class Nested { public bool Enabled { get; set; } }

            public static class Reader
            {
                public static T? Read<T>(IConfiguration config, string key) => config.GetValue<T>(key);
            }

            public static class ExternalReader
            {
                public static T? Fetch<T>(string key) => default;
            }

            public static class App
            {
                public static void Run(IConfiguration config, string tenant)
                {
                    _ = config["Service:Url"];
                    _ = config.GetSection("Service").GetValue<int>("Retries");
                    _ = config[$"Tenants:{tenant}:Url"];
                    _ = config.GetSection("Root").Get<Settings>();
                    _ = Reader.Read<bool>(config, "Flags:Beta");
                    _ = ConfigurationBinder.GetValue<int>(config, "Static:Number");
                    _ = ExternalReader.Fetch<long>("External:Number");
                }
            }
            """;

        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("TestProject", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Latest));
        project = project.AddMetadataReferences(References());
        project = project.AddDocument("Input.cs", source).Project;

        var rules = new[]
        {
            new ExternalWrapperRule("ExternalReader.Fetch", 0, ValueTypeArgument: 0)
        };
        var result = await new ConfigurationUsageAnalyzer(externalWrapperRules: rules).AnalyzeAsync(
            project.Solution,
            TestContext.Current.CancellationToken);
        var keys = result.Usages.Select(u => u.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Service:Url", keys);
        Assert.Contains("Service:Retries", keys);
        Assert.Contains("Tenants:{tenant}:Url", keys);
        Assert.Contains("Root:renamed", keys);
        Assert.Contains("Root:Child:Enabled", keys);
        Assert.Contains("Flags:Beta", keys);
        Assert.Contains("Static:Number", keys);
        Assert.Contains("External:Number", keys);
    }

    [Theory]
    [InlineData("Features:Definitions:*:Enabled", "Features:Definitions:Beta:Enabled")]
    [InlineData("Tenants:{tenant}:Url", "Tenants:acme:Url")]
    [InlineData("Mail", "Mail:Smtp:Host")]
    public void PatternMatchingMatchesConfigurationKeys(string pattern, string key)
    {
        Assert.True(ReportBuilder.Matches(pattern, key));
    }

    private static IEnumerable<MetadataReference> References()
    {
        var platformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        var extensionAssemblies = new[]
        {
            typeof(IConfiguration).Assembly.Location,
            typeof(ConfigurationBinder).Assembly.Location
        }.Distinct().Select(path => MetadataReference.CreateFromFile(path));
        return platformAssemblies.Concat(extensionAssemblies);
    }
}
