using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynConfigExporter;

internal static class PropertyMetadataReader
{
    public static string? Description(ISymbol symbol, Compilation compilation)
    {
        var attribute = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute");
        if (attribute is not null) return attribute.ConstructorArguments.FirstOrDefault().Value as string;
        var doc = Documentation(symbol is IParameterSymbol p ? p.ContainingSymbol : symbol, compilation);
        var element = symbol is IParameterSymbol parameter
            ? doc?.Elements("param").FirstOrDefault(e => (string?)e.Attribute("name") == parameter.Name)
            : doc?.Element("summary");
        return element is null ? null : PlainText(element);
    }

    internal static string? Example(ISymbol symbol, Compilation compilation)
    {
        var doc = Documentation(symbol is IParameterSymbol parameter ? parameter.ContainingSymbol : symbol, compilation);
        var example = doc?.Element("example");
        return example is null ? null : PlainText(example);
    }

    internal static XElement? Documentation(ISymbol symbol, Compilation compilation)
    {
        try
        {
            var xml = symbol.GetDocumentationCommentXml();
            if (!string.IsNullOrWhiteSpace(xml)) return XElement.Parse(xml);
            var reference = compilation.References.OfType<PortableExecutableReference>().FirstOrDefault(r =>
                SymbolEqualityComparer.Default.Equals(compilation.GetAssemblyOrModuleSymbol(r), symbol.ContainingAssembly));
            var file = Path.ChangeExtension(reference?.FilePath, ".xml");
            if (file is null || !File.Exists(file)) return null;
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            return XDocument.Load(reader).Descendants("member")
                .FirstOrDefault(m => (string?)m.Attribute("name") == symbol.GetDocumentationCommentId());
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string PlainText(XElement element)
    {
        var copy = new XElement(element);
        foreach (var see in copy.Descendants().Where(e => e.Name == "see" || e.Name == "paramref").ToArray())
            see.ReplaceWith((string?)see.Attribute("langword") ?? (string?)see.Attribute("name") ??
                ReferenceName((string?)see.Attribute("cref")) ?? see.Value);
        return string.Join(" ", copy.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? ReferenceName(string? cref) => cref is { Length: > 2 } && cref[1] == ':' ? cref[2..] : cref;

    // Only explicit XML default statements; do not infer CLR defaults or values from appsettings.
    internal static string? DocumentedDefault(ISymbol symbol, Compilation compilation)
    {
        var doc = Documentation(symbol, compilation);
        if (doc is null) return null;
        foreach (var element in doc.Elements().Where(e => e.Name == "summary" || e.Name == "remarks" || e.Name == "value"))
        {
            foreach (var see in element.Descendants("see"))
            {
                var before = string.Concat(see.PreviousNode is XText text ? new[] { text.Value } : Array.Empty<string>());
                var cref = (string?)see.Attribute("cref");
                if (cref?.StartsWith("F:", StringComparison.Ordinal) == true &&
                    (before.Contains("Defaults to", StringComparison.OrdinalIgnoreCase) || before.Contains("not specified", StringComparison.OrdinalIgnoreCase)))
                    return cref[(cref.LastIndexOf('.') + 1)..];
            }
            var value = PlainText(element);
            if (value.StartsWith("Defaults to ", StringComparison.OrdinalIgnoreCase))
                return "XML: " + value[12..].TrimEnd('.');
        }
        return null;
    }

    public static string? DefaultValue(IPropertySymbol property, Compilation compilation)
    {
        var attribute = property.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DefaultValueAttribute");
        if (attribute is not null && attribute.ConstructorArguments.Length > 0)
            return FormatConstant(attribute.ConstructorArguments.Last().Value, property.Type);
        var syntax = property.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>().FirstOrDefault();
        if (syntax?.Initializer?.Value is not { } expression) return null;
        var model = compilation.GetSemanticModel(expression.SyntaxTree);
        if (TryCollectionValue(expression, model, out var collectionValue))
            return JsonSerializer.Serialize(collectionValue);
        if (expression is CollectionExpressionSyntax { Elements.Count: 0 })
            return property.Type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IDictionary<TKey, TValue>") ? "{}" : "[]";
        var operation = model.GetOperation(expression);
        if (operation is IInvocationOperation { Arguments.Length: 0 } invocation &&
            invocation.TargetMethod.Name == "Empty" && invocation.TargetMethod.IsStatic &&
            invocation.TargetMethod.ContainingType.SpecialType == SpecialType.System_Array)
            return "[]";
        if (operation is IObjectCreationOperation { Arguments.Length: 0 } creation &&
            (creation.Initializer is null || creation.Initializer.Initializers.Length == 0) &&
            creation.Type is INamedTypeSymbol collection && !collection.Locations.Any(l => l.IsInSource))
        {
            if (SymbolEqualityComparer.Default.Equals(collection.OriginalDefinition, compilation.GetTypeByMetadataName("System.Collections.Generic.List`1"))) return "[]";
            if (SymbolEqualityComparer.Default.Equals(collection.OriginalDefinition, compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2"))) return "{}";
        }
        var constant = model.GetConstantValue(expression);
        if (constant.HasValue) return FormatConstant(constant.Value, property.Type);
        return "Выражение: " + expression.ToString();
    }

    internal static string FormatConstant(object? value, ITypeSymbol type)
    {
        if (value is null) return "null";
        if (value is string { Length: 0 }) return "\"\"";
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) type = nullable.TypeArguments[0];
        if (type.TypeKind == TypeKind.Enum)
        {
            var member = type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
            if (member is not null) return member.Name;
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
    }

    private static bool TryCollectionValue(ExpressionSyntax expression, SemanticModel model, out object? value, int depth = 0)
    {
        value = null;
        if (depth > 32) return false;
        var typeInfo = model.GetTypeInfo(expression);
        var type = typeInfo.Type ?? typeInfo.ConvertedType;
        var name = (type as INamedTypeSymbol)?.OriginalDefinition;
        var dictionary = SymbolEqualityComparer.Default.Equals(name, model.Compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2"));
        var list = SymbolEqualityComparer.Default.Equals(name, model.Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1"));
        if (type is not IArrayTypeSymbol && !list && !dictionary) return false;

        IEnumerable<ExpressionSyntax>? elements = null;
        switch (expression)
        {
            case CollectionExpressionSyntax collection when collection.Elements.All(e => e is ExpressionElementSyntax):
                elements = collection.Elements.Cast<ExpressionElementSyntax>().Select(e => e.Expression);
                break;
            case ArrayCreationExpressionSyntax { Initializer: { } initializer }:
                elements = initializer.Expressions;
                break;
            case ImplicitArrayCreationExpressionSyntax array:
                elements = array.Initializer.Expressions;
                break;
            case BaseObjectCreationExpressionSyntax creation when
                model.GetOperation(creation) is IObjectCreationOperation { Arguments.Length: 0 }:
                elements = creation.Initializer?.Expressions ?? Enumerable.Empty<ExpressionSyntax>();
                break;
        }
        if (elements is null) return false;
        return TryCollectionElements(elements, dictionary, model, out value, depth);
    }

    private static bool TryCollectionElements(IEnumerable<ExpressionSyntax> elements, bool dictionary,
        SemanticModel model, out object? value, int depth, object? existing = null)
    {
        value = null;
        if (depth > 32) return false;
        var items = existing is List<object?> list ? new List<object?>(list) : new List<object?>();
        var entries = existing is Dictionary<string, object?> map
            ? new Dictionary<string, object?>(map) : new Dictionary<string, object?>();
        foreach (var element in elements)
        {
            if (!dictionary)
            {
                if (!TryValue(element, model, out var item, depth + 1)) return false;
                items.Add(item);
                continue;
            }
            ExpressionSyntax key, entryValue;
            if (element is InitializerExpressionSyntax { Expressions.Count: 2 } pair)
            {
                key = pair.Expressions[0];
                entryValue = pair.Expressions[1];
            }
            else if (element is AssignmentExpressionSyntax { Left: ImplicitElementAccessSyntax access } assignment && access.ArgumentList.Arguments.Count == 1)
            {
                key = access.ArgumentList.Arguments[0].Expression;
                entryValue = assignment.Right;
            }
            else return false;
            if (!TryValue(key, model, out var keyValue, depth + 1) || keyValue is null ||
                !TryValue(entryValue, model, out var mapped, depth + 1)) return false;
            var keyText = Convert.ToString(keyValue, CultureInfo.InvariantCulture)!;
            if (entries.ContainsKey(keyText) && element is InitializerExpressionSyntax) return false;
            entries[keyText] = mapped;
        }
        value = dictionary ? entries : items;
        return true;
    }

    private static bool TryValue(ExpressionSyntax expression, SemanticModel model, out object? value, int depth = 0)
    {
        value = null;
        if (depth > 32) return false;
        var constant = model.GetConstantValue(expression);
        if (!constant.HasValue)
        {
            if (TryCollectionValue(expression, model, out value, depth + 1)) return true;
            if (model.GetOperation(expression) is IInvocationOperation { Arguments.Length: 0 } invocation &&
                invocation.TargetMethod.Name == "Empty" && invocation.TargetMethod.IsStatic &&
                invocation.TargetMethod.ContainingType.SpecialType == SpecialType.System_Array)
            {
                value = Array.Empty<object>();
                return true;
            }
            if (expression is BaseObjectCreationExpressionSyntax creation &&
                model.GetOperation(expression) is IObjectCreationOperation { Arguments.Length: 0, Type: INamedTypeSymbol objectType })
                return TryObjectInitializer(objectType, creation.Initializer, model, out value, depth + 1);
            return false;
        }
        value = constant.Value;
        if (value is not null && model.GetTypeInfo(expression).Type is { TypeKind: TypeKind.Enum } type)
            value = FormatConstant(value, type);
        return true;
    }

    // Read declared defaults and explicit assignments without executing constructors or getters.
    private static bool TryObjectInitializer(INamedTypeSymbol type, InitializerExpressionSyntax? initializer,
        SemanticModel model, out object? value, int depth, Dictionary<string, object?>? existing = null)
    {
        value = null;
        if (depth > 32 || !type.Locations.Any(l => l.IsInSource)) return false;
        var properties = existing is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(existing);
        var assigned = new HashSet<string>();
        foreach (var expression in initializer?.Expressions ?? Enumerable.Empty<ExpressionSyntax>())
        {
            if (expression is not AssignmentExpressionSyntax assignment ||
                model.GetSymbolInfo(assignment.Left).Symbol is not IPropertySymbol { IsStatic: false } property)
                return false;
            object? memberValue;
            var key = PropertyKey(property);
            if (assignment.Right is InitializerExpressionSyntax nested)
            {
                if (!properties.TryGetValue(key, out var initialValue))
                {
                    var declaredInitializer = PropertyInitializer(property);
                    if (declaredInitializer is null || !TryValue(declaredInitializer,
                        model.Compilation.GetSemanticModel(declaredInitializer.SyntaxTree), out initialValue, depth + 1)) return false;
                }
                var definition = (property.Type as INamedTypeSymbol)?.OriginalDefinition;
                bool IsType(string name) => SymbolEqualityComparer.Default.Equals(definition,
                    model.Compilation.GetTypeByMetadataName(name));
                var isList = IsType("System.Collections.Generic.List`1") ||
                    IsType("System.Collections.Generic.IList`1") || IsType("System.Collections.Generic.ICollection`1");
                var isDictionary = IsType("System.Collections.Generic.Dictionary`2") ||
                    IsType("System.Collections.Generic.IDictionary`2");
                if (isList || isDictionary)
                {
                    if ((isList && initialValue is not List<object?>) ||
                        (isDictionary && initialValue is not Dictionary<string, object?>) ||
                        !TryCollectionElements(nested.Expressions, isDictionary, model, out memberValue, depth + 1, initialValue)) return false;
                }
                else if (property.Type is not INamedTypeSymbol nestedType ||
                         initialValue is not Dictionary<string, object?> initialProperties ||
                         !TryObjectInitializer(nestedType, nested, model, out memberValue, depth + 1, initialProperties)) return false;
            }
            else if (!TryValue(assignment.Right, model, out memberValue, depth + 1)) return false;
            properties[key] = memberValue;
            assigned.Add(property.Name);
        }
        for (var current = existing is null ? type : null; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>().Where(p =>
                !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public))
            {
                if (!assigned.Add(property.Name)) continue;
                var expression = PropertyInitializer(property);
                if (expression is null) continue;
                var propertyModel = model.Compilation.GetSemanticModel(expression.SyntaxTree);
                if (!TryValue(expression, propertyModel, out var memberValue, depth + 1)) return false;
                properties[PropertyKey(property)] = memberValue;
            }
        }
        value = properties;
        return true;
    }

    private static ExpressionSyntax? PropertyInitializer(IPropertySymbol property) =>
        property.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault()?.Initializer?.Value;

    private static string PropertyKey(IPropertySymbol property) => property.GetAttributes()
        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute")
        ?.ConstructorArguments.FirstOrDefault().Value as string ?? property.Name;
}
