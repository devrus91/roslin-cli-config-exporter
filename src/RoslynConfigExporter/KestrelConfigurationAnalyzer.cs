using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynConfigExporter;

internal static class KestrelConfigurationAnalyzer
{
    internal const string Api = "Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions";
    internal const string Note = "Inferred from Kestrel configuration API contract.";

    public static void Analyze(string project, InvocationExpressionSyntax invocation, SemanticModel model, List<ConfigurationUsage> usages)
    {
        if (model.GetOperation(invocation) is not IInvocationOperation operation) return;
        var method = operation.TargetMethod;
        var owner = method.ContainingType.ToDisplayString();
        if ((owner == "Microsoft.AspNetCore.Builder.WebApplication" && method.Name is "CreateBuilder" or "CreateSlimBuilder") ||
            (owner == "Microsoft.Extensions.Hosting.GenericHostBuilderExtensions" && method.Name == "ConfigureWebHostDefaults"))
            Add(project, "Kestrel", SymbolUtilities.LocationOf(invocation), invocation.ToString(), usages);
        else if (owner == Api && method.Name == "Configure")
        {
            var config = operation.Arguments.FirstOrDefault(a => a.Parameter?.Type.IsConfiguration() == true);
            if (config?.Value.Syntax is ExpressionSyntax expression)
                Add(project, new KeyEvaluator(model).ResolveConfigurationPrefix(expression).Text,
                    SymbolUtilities.LocationOf(invocation), invocation.ToString(), usages);
        }
    }

    internal static void Add(string project, string section, SourceLocation location, string expression, List<ConfigurationUsage> usages)
    {
        var paths = new List<string> { "", "Endpoints", "Endpoints:*:Url" };
        foreach (var parent in new[] { "EndpointDefaults", "Endpoints:*", "Endpoints:*:Sni:*" })
            foreach (var name in new[] { "Protocols", "SslProtocols:*", "ClientCertificateMode" })
                paths.Add(parent + ":" + name);
        foreach (var parent in new[] { "Certificates:*", "Endpoints:*:Certificate", "Endpoints:*:Sni:*:Certificate" })
            foreach (var name in new[] { "Path", "KeyPath", "Password", "Subject", "Store", "Location", "AllowInvalid" })
                paths.Add(parent + ":" + name);
        foreach (var path in paths)
        {
            var key = LoggingConfigurationAnalyzer.Join(section, path);
            if (key.Length == 0) continue;
            usages.Add(new(project, key, UsageKind.WrapperCall,
                key.Contains('{') ? Confidence.Dynamic : key.Contains('*') ? Confidence.Pattern : Confidence.Inferred,
                null, null, location, expression, Note, Origin: UsageOrigin.Assembly, FrameworkSetting: new("Kestrel", path)));
        }
    }
}
