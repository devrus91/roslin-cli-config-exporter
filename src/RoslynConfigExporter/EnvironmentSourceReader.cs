using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynConfigExporter;

internal static class EnvironmentSourceReader
{
    public static IEnumerable<EnvironmentSource> Read(string project, SyntaxNode root, SemanticModel model)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetOperation(invocation) is not IInvocationOperation operation) continue;
            var method = operation.TargetMethod;
            if (method.Name == "AddEnvironmentVariables" && method.ContainingType.ToDisplayString() ==
                "Microsoft.Extensions.Configuration.EnvironmentVariablesExtensions")
            {
                var prefix = operation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "prefix");
                if (prefix is not null)
                    yield return new(project, prefix.Value.ConstantValue.HasValue
                        ? prefix.Value.ConstantValue.Value as string ?? "" : null);
                else if (operation.Arguments.Any(a => a.Parameter?.Type.TypeKind == TypeKind.Delegate))
                    yield return new(project, null);
                else yield return new(project, "");
            }
            if (method.ContainingType.ToDisplayString() is "Microsoft.AspNetCore.Builder.WebApplication" or "Microsoft.Extensions.Hosting.Host" &&
                method.Name is "CreateBuilder" or "CreateDefaultBuilder" or "CreateApplicationBuilder")
                yield return new(project, "");
        }
    }
}
