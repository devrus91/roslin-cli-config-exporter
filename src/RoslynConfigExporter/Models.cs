using System.Text.Json.Serialization;

namespace RoslynConfigExporter;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageKind
{
    Indexer,
    GetValue,
    GetSection,
    GetRequiredSection,
    GetConnectionString,
    Bind,
    Get,
    Configure,
    BindConfiguration,
    OptionsProperty,
    WrapperCall
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Confidence
{
    Exact,
    Inferred,
    Pattern,
    Dynamic
}

public sealed record SourceLocation(string File, int Line, int Column);

public sealed record ConfigurationUsage(
    string Project,
    string Key,
    UsageKind Kind,
    Confidence Confidence,
    string? ValueType,
    string? OptionsType,
    SourceLocation Location,
    string Expression,
    string? Note = null);

public sealed record OptionsBinding(
    string Project,
    string OptionsType,
    string Section,
    Confidence Confidence,
    string Api,
    SourceLocation Location);

public sealed record OptionsConsumer(
    string Project,
    string OptionsType,
    string Interface,
    string? Name,
    SourceLocation Location);

public sealed record ProjectIssue(string Project, string Severity, string Message);

public sealed record ConfigFileEntry(string Key, string File);

public sealed record KeySummary(
    string Key,
    Confidence Confidence,
    IReadOnlyList<string> ValueTypes,
    IReadOnlyList<string> Projects,
    int UsageCount,
    bool PresentInConfiguration,
    IReadOnlyList<string> ConfigurationFiles);

public sealed record AnalysisReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string Target,
    IReadOnlyList<string> Projects,
    IReadOnlyList<KeySummary> Keys,
    IReadOnlyList<ConfigurationUsage> Usages,
    IReadOnlyList<OptionsBinding> OptionsBindings,
    IReadOnlyList<OptionsConsumer> OptionsConsumers,
    IReadOnlyList<ConfigFileEntry> ConfigurationEntries,
    IReadOnlyList<ConfigFileEntry> UnusedConfigurationEntries,
    IReadOnlyList<ProjectIssue> Issues);

internal readonly record struct EvaluatedKey(string Text, Confidence Confidence)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

public sealed record AnalysisResult(
    List<ConfigurationUsage> Usages,
    List<OptionsBinding> OptionsBindings,
    List<OptionsConsumer> OptionsConsumers,
    List<ProjectIssue> Issues);
