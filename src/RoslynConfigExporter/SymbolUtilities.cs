using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynConfigExporter;

internal static class SymbolUtilities
{
    public const string IConfiguration = "Microsoft.Extensions.Configuration.IConfiguration";
    public const string IConfigurationSection = "Microsoft.Extensions.Configuration.IConfigurationSection";

    public static bool IsOrImplements(this ITypeSymbol? type, string metadataName)
    {
        if (type is null)
        {
            return false;
        }

        if (type.ToDisplayString() == metadataName || type.OriginalDefinition.ToDisplayString() == metadataName)
        {
            return true;
        }

        return type.AllInterfaces.Any(i =>
            i.ToDisplayString() == metadataName || i.OriginalDefinition.ToDisplayString() == metadataName);
    }

    public static bool IsConfiguration(this ITypeSymbol? type) => type.IsOrImplements(IConfiguration);

    public static bool IsConfigurationSection(this ITypeSymbol? type) => type.IsOrImplements(IConfigurationSection);

    public static SourceLocation LocationOf(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return new(
            Path.GetFullPath(span.Path),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1);
    }

    public static string DisplayName(this ITypeSymbol? type) =>
        type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? "unknown";

    public static IMethodSymbol? MethodSymbol(this SemanticModel model, InvocationExpressionSyntax invocation)
    {
        var info = model.GetSymbolInfo(invocation);
        return info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }
}
