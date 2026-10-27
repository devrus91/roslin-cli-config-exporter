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

    [Fact]
    public async Task InspectsCompiledExternalAssemblyWithoutInvokingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dll = Path.Combine(directory, "ExternalSettings.dll");
        try
        {
            var compilation = CSharpCompilation.Create("ExternalSettings",
                [CSharpSyntaxTree.ParseText("""
                    using Microsoft.Extensions.Configuration;
                    using Serilog;
                    public class SectionReader
                    {
                        private readonly IConfiguration section;
                        public SectionReader(IConfiguration section) { this.section = section; }
                        public void Read() { _ = Child(section)["Host"]; }
                        private static IConfiguration Child(IConfiguration c) => c.GetSection("Child");
                    }
                    public class ExternalOptions {
                        /// <summary>External XML description.</summary>
                        /// <example>123</example>
                        public int Number { get; set; }
                        /// <summary>External collection.</summary>
                        /// <example>external-example</example>
                        public string[] Items { get; set; }
                    }
                    public static class ExternalSettings
                    {
                        public static void Logging(IConfiguration config) { new Serilog.LoggerConfiguration().ReadFrom.Configuration(config); }
                        public static void Register(IConfigurationBuilder builder, string prefix) { builder.AddEnvironmentVariables(prefix); }
                        private static readonly string Prefix = "LIB_";
                        public static void Fixed(IConfigurationBuilder builder) { builder.AddEnvironmentVariables(Prefix); }
                        public static void Nested(IConfiguration config) => new SectionReader(config.GetSection("Nested")).Read();
                        static ExternalSettings() => throw new System.Exception("Must never execute");
                        public static void Read(IConfiguration config, string key)
                        {
                            _ = config["External:Url"];
                            _ = config.GetValue<int>("External:Retries", 3);
                            var section = config.GetSection("Nested");
                            _ = section.GetValue<bool>("Enabled");
                            _ = config[key];
                        }
                    }
                    """, cancellationToken: TestContext.Current.CancellationToken)], References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            var emitted = compilation.Emit(dll, xmlDocPath: Path.ChangeExtension(dll, ".xml"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            using var workspace = new AdhocWorkspace();
            var project = workspace.AddProject("Consumer", LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReferences(References().Append(MetadataReference.CreateFromFile(dll)))
                .AddDocument("App.cs", "using Microsoft.Extensions.Configuration; public class App { public void Read(IConfiguration c) { c.Get<ExternalOptions>(); ExternalSettings.Register(new ConfigurationBuilder(), \"APP_\"); ExternalSettings.Logging(c.GetSection(\"Wrapped\")); ExternalSettings.Read(c, \"Provided\"); ExternalSettings.Nested(c.GetSection(\"Parent\")); } }").Project;
            var result = await new ConfigurationUsageAnalyzer().AnalyzeAsync(project.Solution, TestContext.Current.CancellationToken);
            Assert.True(!result.Issues.Any(i => i.Message.Contains("ExternalSettings")), string.Join("\n", result.Issues));
            Assert.Contains(result.Usages, u => u.Key == "External:Url" && u.Location.File == dll);
            Assert.Contains(result.Usages, u => u.Key == "External:Retries");
            Assert.Contains(result.Usages, u => u.Key == "Nested:Enabled");
            Assert.Contains(result.Usages, u => u.Key == "{unknown-parent}:{dynamic}" && u.Confidence == Confidence.Dynamic);
            Assert.Contains(result.Usages, u => u.Key == "Parent:Nested:Child:Host");
            Assert.Contains(result.Usages, u => u.Key == "{unknown-parent}:Nested:Child:Host");
            Assert.Contains(result.Usages, u => u.Key == "Wrapped:Serilog:MinimumLevel");
            Assert.Contains(result.EnvironmentSources!, s => s.Prefix == "LIB_");
            Assert.Contains(result.EnvironmentSources!, s => s.Prefix == "APP_");
            Assert.Equal("External XML description.", Assert.Single(result.Usages, u => u.Key == "Number").Metadata!.Description);
            Assert.Equal("123", Assert.Single(result.Usages, u => u.Key == "Number").Metadata!.Example);
            Assert.Equal("External collection.", Assert.Single(result.Usages, u => u.Key == "Items:*").Metadata!.Description);
            Assert.Equal("external-example", Assert.Single(result.Usages, u => u.Key == "Items:*").Metadata!.Example);
            Assert.DoesNotContain(result.Issues, i => i.Message.Contains("ExternalSettings"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MarkdownUsesRequiredAttributeIndependentlyOfNullableAndKeyword()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Metadata", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .AddMetadataReferences(References())
            .AddDocument("Input.cs", """
                using Microsoft.Extensions.Configuration;
                using System.ComponentModel;
                using System.ComponentModel.DataAnnotations;
                public class Settings
                {
                    [Required, Description("First|second"), DefaultValue("fallback")]
                    public string? Name { get; set; }
                    public required string KeywordOnly { get; set; }
                    /// <summary>XML source description.</summary>
                    /// <example>84</example>
                    public int Initialized { get; set; } = 42;
                }
                public class App { public void Run(IConfiguration c) => c.GetSection("Root").Get<Settings>(); }
                """).Project;
        var result = await new ConfigurationUsageAnalyzer().AnalyzeAsync(project.Solution, TestContext.Current.CancellationToken);
        var name = Assert.Single(result.Usages, u => u.Key == "Root:Name");
        Assert.True(name.Metadata!.Required);
        Assert.True(name.Metadata.Nullable);
        Assert.False(Assert.Single(result.Usages, u => u.Key == "Root:KeywordOnly").Metadata!.Required);
        var initialized = Assert.Single(result.Usages, u => u.Key == "Root:Initialized");
        Assert.Equal("XML source description.", initialized.Metadata!.Description);
        Assert.Equal("42", initialized.Metadata.DefaultValue);
        Assert.Equal("84", initialized.Metadata.Example);
        var report = ReportBuilder.Build("app.csproj", ["Metadata"], result, []);
        var file = Path.GetTempFileName();
        try
        {
            await ReportWriter.WriteAsync(report, file, "markdown", TestContext.Current.CancellationToken);
            var markdown = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.Contains("Иерархия в спецификации", markdown);
            Assert.Contains("Требует переопределения<br>(required)", markdown);
            Assert.Contains("Нет (nullable) | Да | First\\|second", markdown);
            Assert.Contains("fallback", markdown);
            Assert.Contains("| Корень | Root |", markdown);
            Assert.Contains("env: Root__Name", markdown);
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task EnvironmentPrefixesAndUnknownHierarchyAreExplicitInMarkdown()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Env", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(References())
            .AddDocument("App.cs", """
                using Microsoft.Extensions.Configuration;
                public class App
                {
                    public void Read(string prefix)
                    {
                        var c = new ConfigurationBuilder().AddEnvironmentVariables(prefix: "APP_")
                            .AddEnvironmentVariables("SECOND__").AddEnvironmentVariables(prefix).Build();
                        _ = c["Mail:Smtp:Host"];
                    }
                }
                """).Project;
        var result = await new ConfigurationUsageAnalyzer().AnalyzeAsync(project.Solution, TestContext.Current.CancellationToken);
        Assert.Contains(result.EnvironmentSources!, s => s.Prefix == "APP_");
        Assert.Contains(result.EnvironmentSources!, s => s.Prefix == "SECOND__");
        Assert.Contains(result.EnvironmentSources!, s => s.Prefix is null);
        result.Usages.Add(new("Env", "{unknown-parent}:Child:Host", UsageKind.Indexer, Confidence.Dynamic,
            "string", null, new("external.dll", 0, 1), "IL"));
        var report = ReportBuilder.Build("App.csproj", ["Env"], result, []);
        var file = Path.GetTempFileName();
        try
        {
            await ReportWriter.WriteAsync(report, file, "markdown", TestContext.Current.CancellationToken);
            var markdown = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.Contains("env: APP_Mail__Smtp__Host", markdown);
            Assert.Contains("env: SECOND__Mail__Smtp__Host", markdown);
            Assert.Contains("env: неизвестно (динамический prefix)", markdown);
            Assert.Contains("Иерархия неизвестна (известная часть: Child)", markdown);
            Assert.Contains("env: неизвестно (иерархия неизвестна)", markdown);
            Assert.DoesNotContain("env: {unknown-parent}", markdown);
            Assert.False(ReportBuilder.Matches("{unknown-parent}:Host", "Mail:Host"));
        }
        finally { File.Delete(file); }
    }
    [Theory]
    [InlineData("new LoggerConfiguration().ReadFrom.Configuration(c)", "Serilog:WriteTo")]
    [InlineData("new LoggerConfiguration().ReadFrom.Configuration(c.GetSection(\"App\"))", "App:Serilog:WriteTo")]
    [InlineData("new LoggerConfiguration().ReadFrom.Configuration(c, new ConfigurationReaderOptions { SectionName = \"Custom\" })", "Custom:WriteTo")]
    [InlineData("new LoggerConfiguration().ReadFrom.Configuration(c.GetSection(\"Direct\"), new ConfigurationReaderOptions { SectionName = null })", "Direct:WriteTo")]
    [InlineData("new LoggerConfiguration().ReadFrom.Configuration(c, options)", "{unknown-parent}:WriteTo")]
    public async Task ResolvesSerilogReaderSections(string call, string expected)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Logging", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(References())
            .AddDocument("App.cs", $$"""
                using Microsoft.Extensions.Configuration;
                using Serilog;
                using Serilog.Settings.Configuration;
                public class App {
                    public void Run(IConfiguration c, ConfigurationReaderOptions options) { {{call}}; }
                }
                """).Project;
        var result = await new ConfigurationUsageAnalyzer().AnalyzeAsync(project.Solution, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(result.Issues, i => i.Severity == "error");
        Assert.Contains(result.Usages, u => u.Key == expected && u.Note == LoggingConfigurationAnalyzer.Note);
        if (!expected.StartsWith("Serilog:", StringComparison.Ordinal))
            Assert.DoesNotContain(result.Usages, u => u.Key == "Serilog:WriteTo");
    }

    [Fact]
    public async Task LoggingReportContainsFullPathsAndEnvironmentVariables()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Logging", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(References())
            .AddDocument("App.cs", """
                using Microsoft.Extensions.Configuration;
                using Microsoft.Extensions.Logging;
                using Microsoft.Extensions.Hosting;
                using Serilog;
                public class App {
                    public void Run(IConfiguration c, ILoggingBuilder logging) {
                        var host = Host.CreateApplicationBuilder();
                        host.Configuration.AddEnvironmentVariables("APP_");
                        new LoggerConfiguration().ReadFrom.Configuration(c);
                        logging.AddConfiguration(c.GetSection("CustomLogging"));
                    }
                }
                """).Project;
        var result = await new ConfigurationUsageAnalyzer().AnalyzeAsync(project.Solution, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(result.Issues, i => i.Severity == "error");
        Assert.Contains(result.Usages, u => u.Key == "Logging:LogLevel:Default");
        Assert.Contains(result.Usages, u => u.Key == "CustomLogging:LogLevel:Default");
        var level = Assert.Single(result.Usages, u => u.Key == "Serilog:MinimumLevel");
        Assert.Equal(string.Join(", ", Enum.GetNames<Serilog.Events.LogEventLevel>()), level.Metadata!.PossibleValues);
        Assert.Equal("Information", level.Metadata.DefaultValue);
        Assert.Contains("minimum level", level.Metadata.Description!);
        var microsoftLevel = Assert.Single(result.Usages, u => u.Key == "Logging:LogLevel:Default");
        Assert.Equal(string.Join(", ", Enum.GetNames<Microsoft.Extensions.Logging.LogLevel>()), microsoftLevel.Metadata!.PossibleValues);
        var report = ReportBuilder.Build("app.csproj", ["Logging"], result,
            [new("Serilog:WriteTo:0:Name", "appsettings.json"), new("Logging:Console:LogLevel:Default", "appsettings.json")]);
        Assert.Contains(report.Keys, k => k.Key == "Serilog:WriteTo:0:Name");
        Assert.Contains(report.Keys, k => k.Key == "Logging:Console:LogLevel:Default");
        var file = Path.GetTempFileName();
        try
        {
            await ReportWriter.WriteAsync(report, file, "markdown", TestContext.Current.CancellationToken);
            var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.Contains("env: APP_Serilog__MinimumLevel", text);
            Assert.Contains("env: APP_Serilog__WriteTo", text);
            Assert.Contains("env: APP_Serilog__WriteTo__0__Name", text);
            Assert.Contains("env: APP_Logging__Console__LogLevel__Default", text);
        }
        finally { File.Delete(file); }
    }
    [Theory]
    [InlineData("WebApplication.CreateBuilder();", "Kestrel")]
    [InlineData("new KestrelServerOptions().Configure(c.GetSection(\"Server\"));", "Server")]
    [InlineData("Host.CreateApplicationBuilder();", null)]
    public async Task KestrelConfigurationIsDetectedOnlyWithWebServerRegistration(string code, string? section)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("KestrelTest", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(References())
            .AddDocument("App.cs", $$"""
                using Microsoft.AspNetCore.Builder;
                using Microsoft.AspNetCore.Server.Kestrel.Core;
                using Microsoft.Extensions.Hosting;
                using Microsoft.Extensions.Configuration;
                public class App { public void Run(IConfiguration c) { {{code}} } }
                """).Project;
        var result = await new ConfigurationUsageAnalyzer().AnalyzeAsync(project.Solution, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(result.Issues, i => i.Severity == "error");
        var usages = result.Usages.Where(u => u.Note == KestrelConfigurationAnalyzer.Note).ToArray();
        if (section is null) { Assert.DoesNotContain(usages, u => !u.Key.Contains('{', StringComparison.Ordinal)); return; }
        Assert.Contains(usages, u => u.Key == section + ":Endpoints:*:Url");
        var protocols = Assert.Single(usages, u => u.Key == section + ":EndpointDefaults:Protocols" && u.Location.Line > 0);
        Assert.Equal(string.Join(", ", Enum.GetNames<Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols>()), protocols.Metadata!.PossibleValues);
        Assert.NotNull(protocols.Metadata.Description);
        Assert.NotNull(protocols.Metadata.DefaultValue);
        Assert.DoesNotContain(usages, u => u.Key.Contains(":Limits", StringComparison.Ordinal));
        var report = ReportBuilder.Build("app.csproj", ["KestrelTest"], result,
            [new(section + ":Endpoints:Http:Url", "appsettings.json")]);
        Assert.Contains(report.Keys, k => k.Key == section + ":Endpoints:Http:Url");
        var file = Path.GetTempFileName();
        try
        {
            await ReportWriter.WriteAsync(report, file, "markdown", TestContext.Current.CancellationToken);
            var markdown = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.Contains("env: " + section + "__Endpoints__Http__Url", markdown);
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task ReportOrdersSourceBeforeAssemblyAndOmitsUnresolvedGenericUsages()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Order", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(References())
            .AddDocument("App.cs", """
                using Microsoft.Extensions.Configuration;
                using Serilog;
                public class App {
                    static T Read<T>(IConfiguration c, string key) => c.GetValue<T>(key);
                    public void Run(IConfiguration c) {
                        _ = Read<int>(c, "Z:Count");
                        _ = c["Serilog:MinimumLevel"];
                        new LoggerConfiguration().ReadFrom.Configuration(c);
                    }
                }
                """).Project;
        var result = await new ConfigurationUsageAnalyzer().AnalyzeAsync(project.Solution, TestContext.Current.CancellationToken);
        var report = ReportBuilder.Build("app.csproj", ["Order"], result, []);
        Assert.DoesNotContain(report.Keys, k => k.ValueTypes.Contains("T"));
        Assert.Contains(report.Keys, k => k.Key == "Z:Count" && k.ValueTypes.Contains("int"));
        Assert.Equal(UsageOrigin.Source, Assert.Single(report.Keys, k => k.Key == "Serilog:MinimumLevel").Origin);
        Assert.Equal(report.Keys.OrderBy(k => k.Origin).ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase), report.Keys);
        var file = Path.GetTempFileName();
        try
        {
            await ReportWriter.WriteAsync(report, file, "markdown", TestContext.Current.CancellationToken);
            var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.True(text.IndexOf("## Найдено в исходниках", StringComparison.Ordinal) < text.IndexOf("## Найдено в сборках", StringComparison.Ordinal));
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public void RequirementFlagsUseTypesAndRequiredAttributeArguments()
    {
        var compilation = CSharpCompilation.Create("Requirements", [CSharpSyntaxTree.ParseText("""
            #nullable enable
            namespace Custom {
                public sealed class RequiredAttribute : System.Attribute {
                    public RequiredAttribute(bool required = true) { }
                }
            }
            public class Settings {
                public int Number { get; set; }
                public int? OptionalNumber { get; set; }
                public string Text { get; set; } = "";
                public string? OptionalText { get; set; }
                [Custom.Required(false)] public int Disabled { get; set; }
                [Custom.Required] public int Enabled { get; set; }
                [System.ComponentModel.DataAnnotations.Required(AllowEmptyStrings = false)] public string? Annotated { get; set; }
            }
            """, cancellationToken: TestContext.Current.CancellationToken)], References());
        var settings = compilation.GetTypeByMetadataName("Settings")!;
        var metadata = OptionsTypeExpander.Expand(settings, "Root", compilation).ToDictionary(p => p.Key, p => p.Metadata!);
        Assert.False(metadata["Root:Number"].Nullable);
        Assert.True(metadata["Root:OptionalNumber"].Nullable);
        Assert.False(metadata["Root:Text"].Nullable);
        Assert.True(metadata["Root:OptionalText"].Nullable);
        Assert.False(metadata["Root:Disabled"].Required);
        Assert.True(metadata["Root:Enabled"].Required);
        Assert.True(metadata["Root:Annotated"].Required);
        Assert.False(metadata["Root:Number"].Required);
    }
    [Fact]
    public void CollectionElementsKeepTheirOwnNullabilityAndDoNotInheritContainerRequired()
    {
        var compilation = CSharpCompilation.Create("Collections", [CSharpSyntaxTree.ParseText("""
            #nullable enable
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;
            public enum Mode { First, Second }
            public class Settings {
                /// <summary>Allowed audiences.</summary>
                /// <example>internal</example>
                [Required] public string[] Audiences { get; set; } = [];
                public string?[] OptionalElements { get; set; } = [];
                public string[]? OptionalArray { get; set; }
                public List<int?> Numbers { get; set; } = [];
                /// <summary>Modes by name.</summary>
                /// <example>First</example>
                public Dictionary<string, Mode?> Modes { get; set; } = [];
                /// <summary>Collection description.</summary>
                /// <example>Collection example.</example>
                public Dictionary<string, Child> Children { get; set; } = [];
            }
            public class Child {
                /// <summary>Child name.</summary>
                /// <example>Alice</example>
                [Required] public string? Name { get; set; }
                public string? Other { get; set; }
            }
            """, cancellationToken: TestContext.Current.CancellationToken)], References());
        var items = OptionsTypeExpander.Expand(compilation.GetTypeByMetadataName("Settings")!, "Root", compilation).ToDictionary(i => i.Key);
        Assert.False(items["Root:Audiences:*"].Metadata!.Nullable);
        Assert.False(items["Root:Audiences:*"].Metadata!.Required);
        Assert.Equal("Allowed audiences.", items["Root:Audiences:*"].Metadata!.Description);
        Assert.Equal("internal", items["Root:Audiences:*"].Metadata!.Example);
        Assert.Equal("[]", items["Root:Audiences:*"].Metadata!.DefaultValue);
        Assert.Equal("[]", items["Root:Numbers:*"].Metadata!.DefaultValue);
        Assert.Equal("{}", items["Root:Modes:*"].Metadata!.DefaultValue);
        Assert.Null(items["Root:OptionalArray:*"].Metadata!.DefaultValue);
        Assert.Null(items["Root:Children:*:Name"].Metadata!.DefaultValue);
        Assert.Equal("Modes by name.", items["Root:Modes:*"].Metadata!.Description);
        Assert.Equal("First", items["Root:Modes:*"].Metadata!.Example);
        Assert.Equal("Child name.", items["Root:Children:*:Name"].Metadata!.Description);
        Assert.Equal("Alice", items["Root:Children:*:Name"].Metadata!.Example);
        Assert.Equal("Collection description.", items["Root:Children:*:Other"].Metadata!.Description);
        Assert.Equal("Collection example.", items["Root:Children:*:Other"].Metadata!.Example);
        Assert.True(items["Root:OptionalElements:*"].Metadata!.Nullable);
        Assert.False(items["Root:OptionalArray:*"].Metadata!.Nullable);
        Assert.True(items["Root:Numbers:*"].Metadata!.Nullable);
        Assert.True(items["Root:Modes:*"].Metadata!.Nullable);
        Assert.Equal("First, Second", items["Root:Modes:*"].Metadata!.PossibleValues);
        Assert.True(items["Root:Children:*:Name"].Metadata!.Nullable);
        Assert.True(items["Root:Children:*:Name"].Metadata!.Required);
    }
    [Fact]
    public void EmptyCollectionDefaultsUseResolvedBclOperations()
    {
        var compilation = CSharpCompilation.Create("EmptyDefaults", [CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            using System.ComponentModel;
            public static class FakeArray { public static int[] Empty<T>() => new[] { 7 }; }
            public class Settings {
                public int[] Array { get; set; } = System.Array.Empty<int>();
                public List<int> List { get; set; } = new List<int>();
                public List<int> TargetTyped { get; set; } = new();
                public Dictionary<string, int> Map { get; set; } = new Dictionary<string, int>();
                public Dictionary<string, int> TargetMap { get; set; } = new() { };
                public List<int> Nonempty { get; set; } = new() { 1 };
                public int[] Numbers { get; set; } = new[] { 1, 2 + 3 };
                public string?[] Strings { get; set; } = ["a\"b", null];
                public Dictionary<string, int> Pairs { get; set; } = new() { { "b", 2 } };
                public List<int> Dynamic { get; set; } = new() { System.Environment.TickCount };
                public Dictionary<string, int> NonemptyMap { get; set; } = new() { ["a"] = 1 };
                public List<int> Copy { get; set; } = new(new[] { 1 });
                public int[] Fake { get; set; } = FakeArray.Empty<int>();
                [DefaultValue("override")] public List<int> Attribute { get; set; } = new();
            }
            """, cancellationToken: TestContext.Current.CancellationToken)], References());
        var items = OptionsTypeExpander.Expand(compilation.GetTypeByMetadataName("Settings")!, "Root", compilation).ToDictionary(i => i.Key, i => i.Metadata!.DefaultValue);
        foreach (var name in new[] { "Array", "List", "TargetTyped" }) Assert.Equal("[]", items["Root:" + name + ":*"]);
        foreach (var name in new[] { "Map", "TargetMap" }) Assert.Equal("{}", items["Root:" + name + ":*"]);
        Assert.Equal("[1]", items["Root:Nonempty:*"]);
        Assert.Equal("[1,5]", items["Root:Numbers:*"]);
        Assert.Equal("{\"a\":1}", items["Root:NonemptyMap:*"]);
        Assert.Equal("{\"b\":2}", items["Root:Pairs:*"]);
        var strings = System.Text.Json.JsonSerializer.Deserialize<string?[]>(items["Root:Strings:*"]!);
        Assert.Equal(new string?[] { "a\"b", null }, strings);
        foreach (var name in new[] { "Dynamic", "Copy", "Fake" }) Assert.StartsWith("Выражение:", items["Root:" + name + ":*"]!);
        Assert.Equal("override", items["Root:Attribute:*"]);
    }
    [Fact]
    public void ComplexCollectionDefaultsPreserveNestedInitializers()
    {
        var compilation = CSharpCompilation.Create("ObjectDefaults", [CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            public class Item {
                public string Name { get; set; } = "class-default";
                public Item Child { get; set; }
                public int[] Values { get; set; }
            }
            public class Settings {
                public List<Item> Items { get; set; } = new() {
                    new() { Name = "first", Child = new() { Name = "nested", Values = [1, 2] } },
                    new() { Name = "second" }
                };
                public Dictionary<string, Item> Map { get; set; } = new() {
                    ["a"] = new() { Name = "mapped" }
                };
                public Item[] Array { get; set; } = [new() { Name = "array" }];
                public Item[] Dynamic { get; set; } = [new() { Name = System.Environment.MachineName }];
            }
            """, cancellationToken: TestContext.Current.CancellationToken)], References());
        var items = OptionsTypeExpander.Expand(compilation.GetTypeByMetadataName("Settings")!, "Root", compilation)
            .ToDictionary(i => i.Key, i => i.Metadata?.DefaultValue);
        Assert.Equal("[{\"Name\":\"first\",\"Child\":{\"Name\":\"nested\",\"Values\":[1,2]}},{\"Name\":\"second\"}]", items["Root:Items"]);
        Assert.Equal("{\"a\":{\"Name\":\"mapped\"}}", items["Root:Map"]);
        Assert.Equal("[{\"Name\":\"array\"}]", items["Root:Array"]);
        Assert.StartsWith("Выражение:", items["Root:Dynamic"]!);
        Assert.Equal("class-default", items["Root:Items:*:Name"]);
    }

    [Fact]
    public void ObjectDefaultsIncludeClassInitializersAndRespectOverrides()
    {
        var declarations = CSharpSyntaxTree.ParseText("""
            public class BaseItem { public int Limit { get; set; } = 5; }
            public class Child { public string Name { get; set; } = "child"; }
            public class Item : BaseItem {
                public string Name { get; set; } = "default";
                public int[] Values { get; set; } = System.Array.Empty<int>();
                public Child Child { get; set; } = new();
            }
            public class Dynamic { public string Name { get; set; } = System.Environment.MachineName; }
            public class Recursive { public Recursive Child { get; set; } = new(); }
            """, cancellationToken: TestContext.Current.CancellationToken);
        var settings = CSharpSyntaxTree.ParseText("""
            public class Settings {
                public Item[] Items { get; set; } = [new(), new() { Name = "override", Values = [9], Child = null }];
                public Dynamic[] Known { get; set; } = [new() { Name = "explicit" }];
                public Dynamic[] Unknown { get; set; } = [new()];
                public Recursive[] Recursive { get; set; } = [new()];
            }
            """, cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("ClassDefaults", [declarations, settings], References());
        var type = compilation.GetTypeByMetadataName("Settings")!;
        string Default(string name) => PropertyMetadataReader.DefaultValue((IPropertySymbol)type.GetMembers(name).Single(), compilation)!;
        using var json = System.Text.Json.JsonDocument.Parse(Default("Items"));
        var first = json.RootElement[0];
        Assert.Equal("default", first.GetProperty("Name").GetString());
        Assert.Equal(5, first.GetProperty("Limit").GetInt32());
        Assert.Equal(0, first.GetProperty("Values").GetArrayLength());
        Assert.Equal("child", first.GetProperty("Child").GetProperty("Name").GetString());
        var second = json.RootElement[1];
        Assert.Equal("override", second.GetProperty("Name").GetString());
        Assert.Equal(9, second.GetProperty("Values")[0].GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, second.GetProperty("Child").ValueKind);
        Assert.Equal("[{\"Name\":\"explicit\"}]", Default("Known"));
        Assert.StartsWith("Выражение:", Default("Unknown"));
        Assert.StartsWith("Выражение:", Default("Recursive"));
    }

    [Fact]
    public void NestedInitializersMergeWithTheExistingPropertyObject()
    {
        var compilation = CSharpCompilation.Create("NestedDefaults", [CSharpSyntaxTree.ParseText("""
            public class Leaf {
                public string Name { get; set; } = "leaf";
                public int Limit { get; set; } = 1;
            }
            public class Child {
                public string Name { get; set; } = "child";
                public int Limit { get; set; } = 10;
                public Leaf Leaf { get; set; } = new() { Limit = 20 };
            }
            public class Item {
                public Child Child { get; set; } = new() {
                    Name = "declared", Limit = 25, Leaf = new() { Limit = 30 }
                };
            }
            public class Missing { public Child Child { get; set; } }
            public class NullChild { public Child Child { get; set; } = null; }
            public class Settings {
                public Item[] Items { get; set; } = [
                    new() { Child = { Name = "override", Leaf = { Name = "nested" } } },
                    new(), new() { Child = new() { Name = "replacement" } }
                ];
                public Missing[] Missing { get; set; } = [new() { Child = { Name = "unknown" } }];
                public NullChild[] Null { get; set; } = [new() { Child = { Name = "invalid" } }];
            }
            """, cancellationToken: TestContext.Current.CancellationToken)], References());
        var type = compilation.GetTypeByMetadataName("Settings")!;
        string Default(string name) => PropertyMetadataReader.DefaultValue((IPropertySymbol)type.GetMembers(name).Single(), compilation)!;
        using var json = System.Text.Json.JsonDocument.Parse(Default("Items"));
        var child = json.RootElement[0].GetProperty("Child");
        Assert.Equal("override", child.GetProperty("Name").GetString());
        Assert.Equal(25, child.GetProperty("Limit").GetInt32());
        Assert.Equal("nested", child.GetProperty("Leaf").GetProperty("Name").GetString());
        Assert.Equal(30, child.GetProperty("Leaf").GetProperty("Limit").GetInt32());
        Assert.Equal("declared", json.RootElement[1].GetProperty("Child").GetProperty("Name").GetString());
        Assert.Equal(10, json.RootElement[2].GetProperty("Child").GetProperty("Limit").GetInt32());
        Assert.StartsWith("Выражение:", Default("Missing"));
        Assert.StartsWith("Выражение:", Default("Null"));
    }

    [Fact]
    public void NestedCollectionsAppendAndPreserveDictionarySemantics()
    {
        var compilation = CSharpCompilation.Create("NestedCollections", [CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            public class Item {
                public List<int> Items { get; set; } = [1, 2];
                public Dictionary<string, int> Map { get; set; } = new() { ["original"] = 1 };
            }
            public class Settings {
                public Item[] Values { get; set; } = [
                    new() { Items = { 3, 4 }, Map = { { "added", 2 } } },
                    new() { Map = { ["original"] = 7, ["added"] = 3 } },
                    new(), new() { Items = new() { 9 } }
                ];
                public Item[] Duplicate { get; set; } = [new() { Map = { { "original", 2 } } }];
                public Item[] Dynamic { get; set; } = [new() { Items = { System.Environment.TickCount } }];
            }
            """, cancellationToken: TestContext.Current.CancellationToken)], References());
        var type = compilation.GetTypeByMetadataName("Settings")!;
        string Default(string name) => PropertyMetadataReader.DefaultValue((IPropertySymbol)type.GetMembers(name).Single(), compilation)!;
        using var json = System.Text.Json.JsonDocument.Parse(Default("Values"));
        Assert.Equal("[1,2,3,4]", json.RootElement[0].GetProperty("Items").GetRawText());
        Assert.Equal("{\"original\":1,\"added\":2}", json.RootElement[0].GetProperty("Map").GetRawText());
        Assert.Equal("{\"original\":7,\"added\":3}", json.RootElement[1].GetProperty("Map").GetRawText());
        Assert.Equal("[1,2]", json.RootElement[2].GetProperty("Items").GetRawText());
        Assert.Equal("[9]", json.RootElement[3].GetProperty("Items").GetRawText());
        Assert.StartsWith("Выражение:", Default("Duplicate"));
        Assert.StartsWith("Выражение:", Default("Dynamic"));
    }

    [Fact]
    public void InterfaceCollectionsPreserveDefaultsAndNestedAdditions()
    {
        var compilation = CSharpCompilation.Create("InterfaceCollections", [CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            public class Item {
                public IList<int> Items { get; set; } = new List<int> { 1 };
                public ICollection<string> Tags { get; set; } = new List<string> { "initial" };
                public IDictionary<string, int> Map { get; set; } = new Dictionary<string, int> { ["original"] = 2 };
            }
            public class Settings {
                public IList<Item> Values { get; set; } = new List<Item> {
                    new() { Items = { 3 }, Tags = { "added" }, Map = { { "added", 4 } } },
                    new() { Map = { ["original"] = 5 } }, new(),
                    new() { Items = new List<int> { 9 } }
                };
                public IList<int> Empty { get; set; } = new List<int>();
                public IDictionary<string, int> EmptyMap { get; set; } = new Dictionary<string, int>();
                public Item[] Duplicate { get; set; } = [new() { Map = { { "original", 6 } } }];
            }
            """, cancellationToken: TestContext.Current.CancellationToken)], References());
        var type = compilation.GetTypeByMetadataName("Settings")!;
        string Default(string name) => PropertyMetadataReader.DefaultValue((IPropertySymbol)type.GetMembers(name).Single(), compilation)!;
        using var json = System.Text.Json.JsonDocument.Parse(Default("Values"));
        Assert.Equal("[1,3]", json.RootElement[0].GetProperty("Items").GetRawText());
        Assert.Equal("[\"initial\",\"added\"]", json.RootElement[0].GetProperty("Tags").GetRawText());
        Assert.Equal("{\"original\":2,\"added\":4}", json.RootElement[0].GetProperty("Map").GetRawText());
        Assert.Equal(5, json.RootElement[1].GetProperty("Map").GetProperty("original").GetInt32());
        Assert.Equal("[1]", json.RootElement[2].GetProperty("Items").GetRawText());
        Assert.Equal("[9]", json.RootElement[3].GetProperty("Items").GetRawText());
        Assert.Equal("[]", Default("Empty"));
        Assert.Equal("{}", Default("EmptyMap"));
        Assert.StartsWith("Выражение:", Default("Duplicate"));
        var rows = OptionsTypeExpander.Expand(type, "Root", compilation).ToDictionary(i => i.Key);
        Assert.Equal(Default("Values"), rows["Root:Values"].Metadata!.DefaultValue);
        Assert.Equal("[1]", rows["Root:Values:*:Items:*"].Metadata!.DefaultValue);
    }

    private static MetadataReference CreateReference(string path)
    {
        var xml = Path.ChangeExtension(path, ".xml");
        if (!File.Exists(xml)) xml = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(xml));
        return MetadataReference.CreateFromFile(path, documentation: File.Exists(xml) ? XmlDocumentationProvider.CreateFromFile(xml) : null);
    }

    private static IEnumerable<MetadataReference> References()
    {
        var platformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(CreateReference);
        var extensionAssemblies = new[]
        {
            typeof(IConfiguration).Assembly.Location,
            typeof(Serilog.ConfigurationLoggerConfigurationExtensions).Assembly.Location,
            typeof(Serilog.LoggerConfiguration).Assembly.Location,
            typeof(ConfigurationBinder).Assembly.Location
        }.Distinct().Select(CreateReference);
        return platformAssemblies.Concat(extensionAssemblies);
    }
}
