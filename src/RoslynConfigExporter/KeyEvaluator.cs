using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynConfigExporter;

internal sealed class KeyEvaluator(SemanticModel model)
{
    private readonly HashSet<ISymbol> _resolvingSymbols = new(SymbolEqualityComparer.Default);

    public EvaluatedKey Evaluate(ExpressionSyntax expression)
    {
        expression = Unwrap(expression);
        var constant = model.GetConstantValue(expression);
        if (constant.HasValue && constant.Value is string text)
        {
            return new(text, Confidence.Exact);
        }

        switch (expression)
        {
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                return Concatenate(Evaluate(binary.Left), Evaluate(binary.Right));

            case InterpolatedStringExpressionSyntax interpolated:
                return EvaluateInterpolated(interpolated);

            case ConditionalExpressionSyntax conditional:
                {
                    var whenTrue = Evaluate(conditional.WhenTrue);
                    var whenFalse = Evaluate(conditional.WhenFalse);
                    if (whenTrue == whenFalse)
                    {
                        return whenTrue;
                    }

                    return new($"{{{whenTrue.Text}|{whenFalse.Text}}}", Confidence.Dynamic);
                }

            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                {
                    var symbol = model.GetSymbolInfo(expression).Symbol;
                    var initializer = ResolveInitializer(symbol);
                    if (initializer is not null)
                    {
                        return Evaluate(initializer);
                    }

                    return new($"{{{symbol?.Name ?? Compact(expression)}}}", Confidence.Dynamic);
                }

            default:
                return new($"{{{Compact(expression)}}}", Confidence.Dynamic);
        }
    }

    public EvaluatedKey ResolveConfigurationPrefix(ExpressionSyntax expression)
    {
        expression = Unwrap(expression);

        if (expression is InvocationExpressionSyntax invocation)
        {
            var method = model.MethodSymbol(invocation);
            if (method?.Name is "GetSection" or "GetRequiredSection")
            {
                var (receiver, arguments) = InvocationParts(invocation, method);
                if (receiver is not null && arguments.Count > 0)
                {
                    return Join(ResolveConfigurationPrefix(receiver), Evaluate(arguments[0].Expression));
                }
            }
        }

        if (expression is IdentifierNameSyntax or MemberAccessExpressionSyntax)
        {
            var symbol = model.GetSymbolInfo(expression).Symbol;
            var initializer = ResolveInitializer(symbol);
            if (initializer is not null)
            {
                return ResolveConfigurationPrefix(initializer);
            }
        }

        var type = model.GetTypeInfo(expression).Type;
        if (type.IsConfigurationSection())
        {
            return new("{section}", Confidence.Dynamic);
        }

        return type.IsConfiguration()
            ? new("", Confidence.Exact)
            : new("{configuration}", Confidence.Dynamic);
    }

    public static EvaluatedKey Join(EvaluatedKey prefix, EvaluatedKey key)
    {
        if (prefix.IsEmpty)
        {
            return new(Normalize(key.Text), key.Confidence);
        }

        if (key.IsEmpty)
        {
            return new(Normalize(prefix.Text), prefix.Confidence);
        }

        return new(
            Normalize(prefix.Text) + ":" + Normalize(key.Text),
            (Confidence)Math.Max((int)prefix.Confidence, (int)key.Confidence));
    }

    private static EvaluatedKey Concatenate(EvaluatedKey left, EvaluatedKey right) => new(
        left.Text + right.Text,
        (Confidence)Math.Max((int)left.Confidence, (int)right.Confidence));

    private EvaluatedKey EvaluateInterpolated(InterpolatedStringExpressionSyntax expression)
    {
        var parts = new List<EvaluatedKey>();
        foreach (var content in expression.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    parts.Add(new(text.TextToken.ValueText, Confidence.Exact));
                    break;
                case InterpolationSyntax interpolation:
                    parts.Add(Evaluate(interpolation.Expression));
                    break;
            }
        }

        return parts.Aggregate(new EvaluatedKey("", Confidence.Exact), Concatenate);
    }

    private ExpressionSyntax? ResolveInitializer(ISymbol? symbol)
    {
        if (symbol is null || !_resolvingSymbols.Add(symbol))
        {
            return null;
        }

        try
        {
            var syntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (syntax is null || syntax.SyntaxTree != model.SyntaxTree)
            {
                return null;
            }

            return syntax switch
            {
                VariableDeclaratorSyntax variable => variable.Initializer?.Value,
                PropertyDeclarationSyntax property when property.ExpressionBody is not null => property.ExpressionBody.Expression,
                PropertyDeclarationSyntax property => property.AccessorList?.Accessors
                    .SelectMany(a => a.DescendantNodes().OfType<ReturnStatementSyntax>())
                    .Select(r => r.Expression)
                    .FirstOrDefault(e => e is not null),
                ParameterSyntax parameter when parameter.Default is not null => parameter.Default.Value,
                _ => null
            };
        }
        finally
        {
            _resolvingSymbols.Remove(symbol);
        }
    }

    private static (ExpressionSyntax? Receiver, IReadOnlyList<ArgumentSyntax> Arguments) InvocationParts(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        if (method.IsExtensionMethod && method.ReducedFrom is null && invocation.ArgumentList.Arguments.Count > 0)
        {
            return (invocation.ArgumentList.Arguments[0].Expression, invocation.ArgumentList.Arguments.Skip(1).ToArray());
        }

        if (invocation.Expression is MemberAccessExpressionSyntax member)
        {
            return (member.Expression, invocation.ArgumentList.Arguments.ToArray());
        }

        return (null, invocation.ArgumentList.Arguments.ToArray());
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static string Compact(SyntaxNode node) => string.Concat(node.ToString().Where(c => !char.IsWhiteSpace(c)));

    public static string Normalize(string key) => key.Trim().Trim(':').Replace("::", ":", StringComparison.Ordinal);
}
