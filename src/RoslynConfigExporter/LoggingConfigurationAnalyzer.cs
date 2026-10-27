using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynConfigExporter;

// API-specific summaries: these describe consumed paths, not proof that values exist.
internal static class LoggingConfigurationAnalyzer
{
    internal const string SerilogApi = "Serilog.ConfigurationLoggerConfigurationExtensions";
    internal const string MicrosoftApi = "Microsoft.Extensions.Logging.LoggingBuilderExtensions";
    internal const string Note = "Inferred from logging configuration API contract.";

    public static void Analyze(string project, InvocationExpressionSyntax invocation, SemanticModel model,
        List<ConfigurationUsage> usages)
    {
        if (model.GetOperation(invocation) is not IInvocationOperation operation) return;
        var method = operation.TargetMethod;
        var owner = method.ContainingType.ToDisplayString();
        var evaluator = new KeyEvaluator(model);
        var config = operation.Arguments.FirstOrDefault(a => a.Parameter?.Type.IsConfiguration() == true);
        string Prefix() => config?.Value.Syntax is ExpressionSyntax expression
            ? evaluator.ResolveConfigurationPrefix(expression).Text : ExternalAssemblyAnalyzer.UnknownParent;
        if (owner == SerilogApi && method.Name is "Configuration" or "ConfigurationSection")
        {
            var section = method.Name == "ConfigurationSection" ? "" : "Serilog";
            var explicitSection = operation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "sectionName");
            var options = operation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "readerOptions");
            if (explicitSection is not null)
                section = Constant(explicitSection.Value);
            else if (options is not null && !options.IsImplicit && !(options.Value.ConstantValue is { HasValue: true, Value: null }))
            {
                var value = options.Value;
                if (value is ILocalReferenceOperation local && local.Local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax variable && variable.Initializer is not null &&
                    !invocation.SyntaxTree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                        .Any(a => a.Left is MemberAccessExpressionSyntax member && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(member.Expression).Symbol, local.Local)))
                    value = model.GetOperation(variable.Initializer.Value)!;
                if (value is IObjectCreationOperation creation)
                {
                    var assignment = creation.Initializer?.Initializers.OfType<ISimpleAssignmentOperation>()
                        .FirstOrDefault(a => a.Target is IPropertyReferenceOperation p && p.Property.Name == "SectionName");
                    section = assignment is null ? "Serilog" : Constant(assignment.Value);
                }
                else section = ExternalAssemblyAnalyzer.UnknownParent;
            }
            Add(project, Join(Prefix(), string.IsNullOrWhiteSpace(section) ? "" : section), true,
                SymbolUtilities.LocationOf(invocation), invocation.ToString(), usages);
        }
        else if (method.Name == "AddConfiguration" && owner == MicrosoftApi && config is not null)
            Add(project, Prefix(), false, SymbolUtilities.LocationOf(invocation), invocation.ToString(), usages);
        else if ((owner == "Microsoft.AspNetCore.Builder.WebApplication" && method.Name == "CreateBuilder") ||
                 (owner == "Microsoft.Extensions.Hosting.Host" && method.Name is "CreateDefaultBuilder" or "CreateApplicationBuilder"))
            Add(project, "Logging", false, SymbolUtilities.LocationOf(invocation), invocation.ToString(), usages);
    }

    private static string Constant(IOperation operation) => operation.ConstantValue.HasValue
        ? operation.ConstantValue.Value as string ?? "" : ExternalAssemblyAnalyzer.UnknownParent;

    internal static string Join(string prefix, string key) => string.IsNullOrEmpty(prefix) ? key : string.IsNullOrEmpty(key) ? prefix : prefix + ":" + key;

    internal static void Add(string project, string section, bool serilog, SourceLocation location,
        string expression, List<ConfigurationUsage> usages)
    {
        string[] paths = serilog
            ? ["", "MinimumLevel", "MinimumLevel:Default", "MinimumLevel:Override:*", "MinimumLevel:ControlledBy",
               "WriteTo", "WriteTo:*:Name", "WriteTo:*:Args:*", "AuditTo", "AuditTo:*:Name", "AuditTo:*:Args:*",
               "Using:*", "Enrich:*", "Properties:*", "Filter:*:Name", "Filter:*:Args:*", "Destructure:*:Name", "Destructure:*:Args:*", "LevelSwitches:*", "FilterSwitches:*"]
            : ["", "LogLevel", "LogLevel:Default", "LogLevel:*", "*:LogLevel:Default", "*:LogLevel:*"];
        foreach (var path in paths)
        {
            var key = Join(section, path);
            if (key.Length == 0) continue;
            usages.Add(new(project, key, UsageKind.WrapperCall,
                key.Contains('{') ? Confidence.Dynamic : key.Contains('*') ? Confidence.Pattern : Confidence.Inferred,
                null, null, location, expression, Note, Origin: UsageOrigin.Assembly,
                FrameworkSetting: new(serilog ? "Serilog" : "Logging", path)));
        }
    }
}
