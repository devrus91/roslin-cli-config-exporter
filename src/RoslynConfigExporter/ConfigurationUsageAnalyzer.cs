using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynConfigExporter;

public sealed class ConfigurationUsageAnalyzer(
    bool includeGenerated = false,
    IReadOnlyList<ExternalWrapperRule>? externalWrapperRules = null)
{
    private static readonly Regex GeneratedFilePattern = new(
        @"(\.g\.cs|\.generated\.cs|\.designer\.cs)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<AnalysisResult> AnalyzeAsync(Solution solution, CancellationToken cancellationToken = default)
    {
        var usages = new List<ConfigurationUsage>();
        var bindings = new List<OptionsBinding>();
        var consumers = new List<OptionsConsumer>();
        var issues = new List<ProjectIssue>();

        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                issues.Add(new(project.Name, "error", "Roslyn could not create a compilation."));
                continue;
            }

            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken)
                         .Where(d => d.Severity == DiagnosticSeverity.Error)
                         .Take(25))
            {
                issues.Add(new(project.Name, "error", diagnostic.ToString()));
            }

            var documents = project.Documents
                .Where(d => d.SupportsSyntaxTree && (includeGenerated || !IsGenerated(d.FilePath)))
                .ToArray();
            var wrappers = await DiscoverWrappersAsync(documents, cancellationToken).ConfigureAwait(false);

            foreach (var document in documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || model is null)
                {
                    continue;
                }

                AnalyzeDocument(
                    project.Name,
                    root,
                    model,
                    wrappers,
                    externalWrapperRules ?? [],
                    usages,
                    bindings,
                    consumers);
            }
        }

        return new(usages, bindings, consumers, issues);
    }

    private static void AnalyzeDocument(
        string project,
        SyntaxNode root,
        SemanticModel model,
        IReadOnlyDictionary<IMethodSymbol, IReadOnlyList<WrapperSummary>> wrappers,
        IReadOnlyList<ExternalWrapperRule> externalRules,
        List<ConfigurationUsage> usages,
        List<OptionsBinding> bindings,
        List<OptionsConsumer> consumers)
    {
        var evaluator = new KeyEvaluator(model);

        foreach (var elementAccess in root.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
        {
            var receiverType = model.GetTypeInfo(elementAccess.Expression).Type;
            if (!receiverType.IsConfiguration() || elementAccess.ArgumentList.Arguments.Count == 0)
            {
                continue;
            }

            var key = KeyEvaluator.Join(
                evaluator.ResolveConfigurationPrefix(elementAccess.Expression),
                evaluator.Evaluate(elementAccess.ArgumentList.Arguments[0].Expression));
            AddUsage(usages, project, key, UsageKind.Indexer, model.GetTypeInfo(elementAccess).Type, null, elementAccess);
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var method = model.MethodSymbol(invocation);
            if (method is null)
            {
                continue;
            }

            AnalyzeBuiltInInvocation(project, invocation, method, model, evaluator, usages, bindings);
            AnalyzeWrapperInvocation(project, invocation, method, evaluator, wrappers, usages);
            AnalyzeExternalWrapperInvocation(project, invocation, method, evaluator, externalRules, usages);
        }

        foreach (var genericName in root.DescendantNodes().OfType<GenericNameSyntax>())
        {
            if (genericName.TypeArgumentList.Arguments.Count != 1 ||
                model.GetSymbolInfo(genericName).Symbol is not INamedTypeSymbol named)
            {
                continue;
            }

            var definition = named.OriginalDefinition.ToDisplayString();
            if (definition is not ("Microsoft.Extensions.Options.IOptions<TOptions>" or
                "Microsoft.Extensions.Options.IOptionsSnapshot<TOptions>" or
                "Microsoft.Extensions.Options.IOptionsMonitor<TOptions>"))
            {
                continue;
            }

            var owner = genericName.FirstAncestorOrSelf<ParameterSyntax>() as SyntaxNode
                        ?? genericName.FirstAncestorOrSelf<PropertyDeclarationSyntax>() as SyntaxNode
                        ?? genericName.FirstAncestorOrSelf<VariableDeclarationSyntax>() as SyntaxNode
                        ?? genericName;
            var consumerName = owner switch
            {
                ParameterSyntax parameter => parameter.Identifier.ValueText,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                VariableDeclarationSyntax variable => variable.Variables.FirstOrDefault()?.Identifier.ValueText,
                _ => null
            };
            consumers.Add(new(
                project,
                named.TypeArguments[0].DisplayName(),
                named.Name,
                consumerName,
                SymbolUtilities.LocationOf(genericName)));
        }
    }

    private static void AnalyzeBuiltInInvocation(
        string project,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        KeyEvaluator evaluator,
        List<ConfigurationUsage> usages,
        List<OptionsBinding> bindings)
    {
        var (receiver, arguments) = InvocationParts(invocation, method);
        var receiverType = receiver is null ? null : model.GetTypeInfo(receiver).Type;
        var isConfigReceiver = receiverType.IsConfiguration();

        switch (method.Name)
        {
            case "GetSection" or "GetRequiredSection" when isConfigReceiver && arguments.Count > 0:
                {
                    var key = KeyEvaluator.Join(
                        evaluator.ResolveConfigurationPrefix(receiver!),
                        evaluator.Evaluate(arguments[0].Expression));
                    AddUsage(
                        usages,
                        project,
                        key,
                        method.Name == "GetSection" ? UsageKind.GetSection : UsageKind.GetRequiredSection,
                        method.ReturnType,
                        null,
                        invocation);
                    break;
                }

            case "GetValue" when isConfigReceiver && arguments.Count > 0:
                {
                    var key = KeyEvaluator.Join(
                        evaluator.ResolveConfigurationPrefix(receiver!),
                        evaluator.Evaluate(arguments[0].Expression));
                    var valueType = method.TypeArguments.FirstOrDefault() ?? method.ReturnType;
                    AddUsage(usages, project, key, UsageKind.GetValue, valueType, null, invocation);
                    break;
                }

            case "GetConnectionString" when isConfigReceiver && arguments.Count > 0:
                {
                    var key = KeyEvaluator.Join(
                        new("ConnectionStrings", Confidence.Exact),
                        evaluator.Evaluate(arguments[0].Expression));
                    AddUsage(usages, project, key, UsageKind.GetConnectionString, method.ReturnType, null, invocation);
                    break;
                }

            case "Get" when isConfigReceiver && method.TypeArguments.FirstOrDefault() is INamedTypeSymbol getType:
                {
                    var section = evaluator.ResolveConfigurationPrefix(receiver!);
                    AddBindingAndProperties(project, getType, section, UsageKind.Get, invocation, usages, bindings);
                    break;
                }

            case "Bind" when isConfigReceiver:
                AnalyzeConfigurationBind(project, invocation, arguments, receiver!, model, evaluator, usages, bindings);
                break;

            case "Configure":
                AnalyzeConfigure(project, invocation, method, arguments, model, evaluator, usages, bindings);
                break;

            case "BindConfiguration":
                AnalyzeBindConfiguration(project, invocation, method, arguments, model, evaluator, usages, bindings);
                break;

            case "Bind" when IsOptionsBuilder(receiverType):
                AnalyzeOptionsBuilderBind(project, invocation, method, arguments, model, evaluator, usages, bindings);
                break;
        }
    }

    private static void AnalyzeConfigurationBind(
        string project,
        InvocationExpressionSyntax invocation,
        IReadOnlyList<ArgumentSyntax> arguments,
        ExpressionSyntax receiver,
        SemanticModel model,
        KeyEvaluator evaluator,
        List<ConfigurationUsage> usages,
        List<OptionsBinding> bindings)
    {
        var section = evaluator.ResolveConfigurationPrefix(receiver);
        ExpressionSyntax? target = null;
        if (arguments.Count > 1 && model.GetTypeInfo(arguments[0].Expression).Type?.SpecialType == SpecialType.System_String)
        {
            section = KeyEvaluator.Join(section, evaluator.Evaluate(arguments[0].Expression));
            target = arguments[1].Expression;
        }
        else if (arguments.Count > 0)
        {
            target = arguments[0].Expression;
        }

        if (target is not null && model.GetTypeInfo(target).Type is INamedTypeSymbol optionsType)
        {
            AddBindingAndProperties(project, optionsType, section, UsageKind.Bind, invocation, usages, bindings);
        }
        else
        {
            AddUsage(usages, project, section, UsageKind.Bind, null, null, invocation, "Bound target type could not be resolved.");
        }
    }

    private static void AnalyzeConfigure(
        string project,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        IReadOnlyList<ArgumentSyntax> arguments,
        SemanticModel model,
        KeyEvaluator evaluator,
        List<ConfigurationUsage> usages,
        List<OptionsBinding> bindings)
    {
        if (method.TypeArguments.FirstOrDefault() is not INamedTypeSymbol optionsType)
        {
            return;
        }

        var configArgument = arguments.FirstOrDefault(a => model.GetTypeInfo(a.Expression).Type.IsConfiguration());
        if (configArgument is null)
        {
            return;
        }

        var section = evaluator.ResolveConfigurationPrefix(configArgument.Expression);
        AddBindingAndProperties(project, optionsType, section, UsageKind.Configure, invocation, usages, bindings);
    }

    private static void AnalyzeBindConfiguration(
        string project,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        IReadOnlyList<ArgumentSyntax> arguments,
        SemanticModel model,
        KeyEvaluator evaluator,
        List<ConfigurationUsage> usages,
        List<OptionsBinding> bindings)
    {
        var optionsType = method.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
        if (optionsType is null && invocation.Expression is MemberAccessExpressionSyntax member &&
            model.GetTypeInfo(member.Expression).Type is INamedTypeSymbol { TypeArguments.Length: 1 } builderType)
        {
            optionsType = builderType.TypeArguments[0] as INamedTypeSymbol;
        }

        if (optionsType is null || arguments.Count == 0)
        {
            return;
        }

        var section = evaluator.Evaluate(arguments[0].Expression);
        AddBindingAndProperties(project, optionsType, section, UsageKind.BindConfiguration, invocation, usages, bindings);
    }

    private static void AnalyzeOptionsBuilderBind(
        string project,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        IReadOnlyList<ArgumentSyntax> arguments,
        SemanticModel model,
        KeyEvaluator evaluator,
        List<ConfigurationUsage> usages,
        List<OptionsBinding> bindings)
    {
        var optionsType = method.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
        var configArgument = arguments.FirstOrDefault(a => model.GetTypeInfo(a.Expression).Type.IsConfiguration());
        if (optionsType is null || configArgument is null)
        {
            return;
        }

        AddBindingAndProperties(
            project,
            optionsType,
            evaluator.ResolveConfigurationPrefix(configArgument.Expression),
            UsageKind.Configure,
            invocation,
            usages,
            bindings);
    }

    private static void AddBindingAndProperties(
        string project,
        INamedTypeSymbol optionsType,
        EvaluatedKey section,
        UsageKind api,
        SyntaxNode location,
        List<ConfigurationUsage> usages,
        List<OptionsBinding> bindings)
    {
        section = new(KeyEvaluator.Normalize(section.Text), section.Confidence);
        var sourceLocation = SymbolUtilities.LocationOf(location);
        bindings.Add(new(
            project,
            optionsType.DisplayName(),
            section.Text,
            section.Confidence,
            api.ToString(),
            sourceLocation));

        AddUsage(usages, project, section, api, optionsType, optionsType, location);
        foreach (var (key, type) in OptionsTypeExpander.Expand(optionsType, section.Text))
        {
            var confidence = key.Contains('*', StringComparison.Ordinal)
                ? Confidence.Pattern
                : section.Confidence;
            usages.Add(new(
                project,
                key,
                UsageKind.OptionsProperty,
                confidence,
                type,
                optionsType.DisplayName(),
                sourceLocation,
                location.ToString(),
                "Inferred from configuration binding."));
        }
    }

    private static void AddUsage(
        List<ConfigurationUsage> usages,
        string project,
        EvaluatedKey key,
        UsageKind kind,
        ITypeSymbol? valueType,
        INamedTypeSymbol? optionsType,
        SyntaxNode node,
        string? note = null)
    {
        if (key.IsEmpty && kind is not (UsageKind.Bind or UsageKind.Get or UsageKind.Configure))
        {
            return;
        }

        usages.Add(new(
            project,
            KeyEvaluator.Normalize(key.Text),
            kind,
            key.Confidence,
            valueType?.DisplayName(),
            optionsType?.DisplayName(),
            SymbolUtilities.LocationOf(node),
            node.ToString(),
            note));
    }

    private static bool IsOptionsBuilder(ITypeSymbol? type) =>
        type is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() ==
        "Microsoft.Extensions.Options.OptionsBuilder<TOptions>";

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

    private static bool IsGenerated(string? path) => path is not null && GeneratedFilePattern.IsMatch(path);

    private static async Task<IReadOnlyDictionary<IMethodSymbol, IReadOnlyList<WrapperSummary>>> DiscoverWrappersAsync(
        IEnumerable<Document> documents,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<IMethodSymbol, IReadOnlyList<WrapperSummary>>(SymbolEqualityComparer.Default);
        foreach (var document in documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || model is null)
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol method)
                {
                    continue;
                }

                var summaries = new List<WrapperSummary>();
                foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var inner = model.MethodSymbol(invocation);
                    if (inner?.Name is not ("GetValue" or "GetSection" or "GetRequiredSection" or "GetConnectionString"))
                    {
                        continue;
                    }

                    var (receiver, arguments) = InvocationParts(invocation, inner);
                    if (arguments.Count == 0 || (receiver is not null && !model.GetTypeInfo(receiver).Type.IsConfiguration()))
                    {
                        continue;
                    }

                    var template = BuildTemplate(arguments[0].Expression, model, method);
                    if (!template.Contains("{{p", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var prefix = inner.Name == "GetConnectionString" ? "ConnectionStrings" : "";
                    if (receiver is not null && inner.Name != "GetConnectionString")
                    {
                        var resolvedPrefix = new KeyEvaluator(model).ResolveConfigurationPrefix(receiver);
                        if (!resolvedPrefix.IsEmpty && resolvedPrefix.Text is not ("{section}" or "{configuration}"))
                        {
                            prefix = resolvedPrefix.Text;
                        }
                    }

                    summaries.Add(new(
                        string.IsNullOrEmpty(prefix) ? template : prefix + ":" + template,
                        inner.Name switch
                        {
                            "GetSection" => UsageKind.GetSection,
                            "GetRequiredSection" => UsageKind.GetRequiredSection,
                            "GetConnectionString" => UsageKind.GetConnectionString,
                            _ => UsageKind.GetValue
                        },
                        inner.TypeArguments.FirstOrDefault()?.DisplayName() ?? inner.ReturnType.DisplayName()));
                }

                if (summaries.Count > 0)
                {
                    result[method] = summaries;
                }
            }
        }

        return result;
    }

    private static string BuildTemplate(ExpressionSyntax expression, SemanticModel model, IMethodSymbol owner)
    {
        var constant = model.GetConstantValue(expression);
        if (constant.HasValue && constant.Value is string text)
        {
            return text;
        }

        if (expression is BinaryExpressionSyntax binary)
        {
            return BuildTemplate(binary.Left, model, owner) + BuildTemplate(binary.Right, model, owner);
        }

        if (expression is InterpolatedStringExpressionSyntax interpolated)
        {
            return string.Concat(interpolated.Contents.Select(content => content switch
            {
                InterpolatedStringTextSyntax text => text.TextToken.ValueText,
                InterpolationSyntax interpolation => BuildTemplate(interpolation.Expression, model, owner),
                _ => "{dynamic}"
            }));
        }

        var symbol = model.GetSymbolInfo(expression).Symbol;
        if (symbol is IParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, owner))
        {
            return $"{{{{p{parameter.Ordinal}}}}}";
        }

        return "{dynamic}";
    }

    private static void AnalyzeWrapperInvocation(
        string project,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        KeyEvaluator evaluator,
        IReadOnlyDictionary<IMethodSymbol, IReadOnlyList<WrapperSummary>> wrappers,
        List<ConfigurationUsage> usages)
    {
        if (!wrappers.TryGetValue(method.OriginalDefinition, out var summaries) &&
            !wrappers.TryGetValue(method, out summaries))
        {
            return;
        }

        foreach (var summary in summaries)
        {
            var text = summary.Template;
            var confidence = Confidence.Inferred;
            for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
            {
                var placeholder = $"{{{{p{i}}}}}";
                if (!text.Contains(placeholder, StringComparison.Ordinal))
                {
                    continue;
                }

                var value = evaluator.Evaluate(invocation.ArgumentList.Arguments[i].Expression);
                text = text.Replace(placeholder, value.Text, StringComparison.Ordinal);
                confidence = (Confidence)Math.Max((int)confidence, (int)value.Confidence);
            }

            if (text.Contains("{{p", StringComparison.Ordinal) || text.Contains("{dynamic}", StringComparison.Ordinal))
            {
                confidence = Confidence.Dynamic;
            }

            usages.Add(new(
                project,
                KeyEvaluator.Normalize(text),
                UsageKind.WrapperCall,
                confidence,
                method.TypeArguments.FirstOrDefault()?.DisplayName() ?? summary.ValueType,
                null,
                SymbolUtilities.LocationOf(invocation),
                invocation.ToString(),
                $"Inferred through source wrapper ({summary.InnerKind})."));
        }
    }

    private static void AnalyzeExternalWrapperInvocation(
        string project,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        KeyEvaluator evaluator,
        IReadOnlyList<ExternalWrapperRule> rules,
        List<ConfigurationUsage> usages)
    {
        foreach (var rule in rules.Where(rule => rule.Matches(method)))
        {
            EvaluatedKey key;
            var ruleConfidence = rule.Prefix?.IndexOfAny(['*', '{']) >= 0
                ? Confidence.Pattern
                : Confidence.Inferred;
            if (rule.KeyArgument == -1 && !string.IsNullOrWhiteSpace(rule.Prefix))
            {
                key = new(rule.Prefix!, ruleConfidence);
            }
            else if (rule.KeyArgument >= 0 && rule.KeyArgument < invocation.ArgumentList.Arguments.Count)
            {
                key = evaluator.Evaluate(invocation.ArgumentList.Arguments[rule.KeyArgument].Expression);
                if (!string.IsNullOrWhiteSpace(rule.Prefix))
                {
                    key = KeyEvaluator.Join(new(rule.Prefix!, ruleConfidence), key);
                }
            }
            else
            {
                continue;
            }

            var valueType = rule.ValueTypeArgument is int typeIndex &&
                            typeIndex >= 0 && typeIndex < method.TypeArguments.Length
                ? method.TypeArguments[typeIndex].DisplayName()
                : method.ReturnType.DisplayName();
            usages.Add(new(
                project,
                KeyEvaluator.Normalize(key.Text),
                rule.Kind,
                (Confidence)Math.Max((int)Confidence.Inferred, (int)key.Confidence),
                valueType,
                null,
                SymbolUtilities.LocationOf(invocation),
                invocation.ToString(),
                $"Matched external wrapper rule: {rule.Method}."));
        }
    }

    private sealed record WrapperSummary(string Template, UsageKind InnerKind, string ValueType);
}
