using Microsoft.CodeAnalysis;

namespace RoslynConfigExporter;

internal static class OptionsTypeExpander
{
    public static IEnumerable<(string Key, string Type)> Expand(INamedTypeSymbol optionsType, string section)
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        return ExpandCore(optionsType, KeyEvaluator.Normalize(section), visited, 0);
    }

    private static IEnumerable<(string Key, string Type)> ExpandCore(
        ITypeSymbol type,
        string prefix,
        HashSet<ITypeSymbol> path,
        int depth)
    {
        type = UnwrapNullable(type);
        if (depth > 16 || IsScalar(type))
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                yield return (prefix, type.DisplayName());
            }

            yield break;
        }

        if (TryGetDictionaryValue(type, out var dictionaryValue))
        {
            foreach (var item in ExpandCore(dictionaryValue, Join(prefix, "*"), path, depth + 1))
            {
                yield return item;
            }

            yield break;
        }

        if (TryGetElementType(type, out var elementType))
        {
            foreach (var item in ExpandCore(elementType, Join(prefix, "*"), path, depth + 1))
            {
                yield return item;
            }

            yield break;
        }

        if (!path.Add(type))
        {
            yield return (Join(prefix, "{recursive}"), type.DisplayName());
            yield break;
        }

        try
        {
            var properties = GetProperties(type).Where(p =>
                !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public &&
                (p.SetMethod?.DeclaredAccessibility == Accessibility.Public || p.GetMethod is not null));

            foreach (var property in properties)
            {
                var keyName = property.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() ==
                        "Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute")
                    ?.ConstructorArguments.FirstOrDefault().Value as string
                    ?? property.Name;

                foreach (var item in ExpandCore(property.Type, Join(prefix, keyName), path, depth + 1))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            path.Remove(type);
        }
    }

    private static IEnumerable<IPropertySymbol> GetProperties(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                yield return property;
            }
        }
    }

    private static bool TryGetElementType(ITypeSymbol type, out ITypeSymbol element)
    {
        if (type is IArrayTypeSymbol array)
        {
            element = array.ElementType;
            return true;
        }

        var enumerable = type.AllInterfaces
            .Concat(type is INamedTypeSymbol named ? [named] : [])
            .FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>");
        if (enumerable is { TypeArguments.Length: 1 })
        {
            element = enumerable.TypeArguments[0];
            return type.SpecialType != SpecialType.System_String;
        }

        element = type;
        return false;
    }

    private static bool TryGetDictionaryValue(ITypeSymbol type, out ITypeSymbol value)
    {
        var dictionary = type.AllInterfaces
            .Concat(type is INamedTypeSymbol named ? [named] : [])
            .FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() ==
                "System.Collections.Generic.IDictionary<TKey, TValue>");
        if (dictionary is { TypeArguments.Length: 2 })
        {
            value = dictionary.TypeArguments[1];
            return true;
        }

        value = type;
        return false;
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    private static bool IsScalar(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum || type.SpecialType != SpecialType.None)
        {
            return true;
        }

        var name = type.OriginalDefinition.ToDisplayString();
        return name is "System.DateTime" or "System.DateTimeOffset" or "System.TimeSpan" or "System.Guid" or
            "System.Uri" or "System.Version" or "System.Globalization.CultureInfo";
    }

    private static string Join(string prefix, string name) =>
        string.IsNullOrWhiteSpace(prefix) ? name : prefix + ":" + name;
}
