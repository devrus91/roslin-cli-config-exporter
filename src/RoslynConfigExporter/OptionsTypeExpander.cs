using Microsoft.CodeAnalysis;

namespace RoslynConfigExporter;

internal static class OptionsTypeExpander
{
    public static IEnumerable<(string Key, string Type, ParameterMetadata? Metadata)> Expand(INamedTypeSymbol optionsType, string section, Compilation compilation)
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        return ExpandCore(optionsType, KeyEvaluator.Normalize(section), visited, 0, compilation);
    }

    private static IEnumerable<(string Key, string Type, ParameterMetadata? Metadata)> ExpandCore(
        ITypeSymbol type,
        string prefix,
        HashSet<ITypeSymbol> path,
        int depth, Compilation compilation)
    {
        var declaredType = type;
        type = UnwrapNullable(type);
        if (depth > 16 || IsScalar(type))
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                yield return (prefix, declaredType.DisplayName(), IsScalar(type)
                    ? new ParameterMetadata(RequirementMetadata.IsNullable(declaredType), false, null,
                        type.TypeKind == TypeKind.Enum
                            ? string.Join(", ", type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => f.Name)) : null,
                        null)
                    : null);
            }

            yield break;
        }

        if (TryGetDictionaryValue(type, out var dictionaryValue))
        {
            foreach (var item in ExpandCore(dictionaryValue, Join(prefix, "*"), path, depth + 1, compilation))
            {
                yield return item;
            }

            yield break;
        }

        if (TryGetElementType(type, out var elementType))
        {
            foreach (var item in ExpandCore(elementType, Join(prefix, "*"), path, depth + 1, compilation))
            {
                yield return item;
            }

            yield break;
        }

        if (!path.Add(type))
        {
            yield return (Join(prefix, "{recursive}"), type.DisplayName(), null);
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

                var collectionType = UnwrapNullable(property.Type);
                if ((TryGetDictionaryValue(collectionType, out var memberType) || TryGetElementType(collectionType, out memberType)) &&
                    !IsScalar(UnwrapNullable(memberType)) && PropertyMetadataReader.DefaultValue(property, compilation) is { } collectionDefault)
                {
                    // Keep the whole collection default on its own row, not on each element's scalar properties.
                    yield return (Join(prefix, keyName), property.Type.DisplayName(), new ParameterMetadata(
                        RequirementMetadata.IsNullable(property.Type), RequirementMetadata.RequiresOverride(property),
                        PropertyMetadataReader.Description(property, compilation), null, collectionDefault,
                        PropertyMetadataReader.Example(property, compilation)));
                }

                foreach (var item in ExpandCore(property.Type, Join(prefix, keyName), path, depth + 1, compilation))
                {
                    var scalar = UnwrapNullable(property.Type);
                    string? Attribute(string name) => property.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == name)
                        ?.ConstructorArguments.FirstOrDefault().Value?.ToString();
                    var nullable = RequirementMetadata.IsNullable(property.Type);
                    if (!IsScalar(scalar))
                    {
                        var metadata = item.Metadata;
                        if (metadata is not null && (TryGetDictionaryValue(scalar, out _) || TryGetElementType(scalar, out _)))
                        {
                            metadata = metadata with
                            {
                                Description = metadata.Description ?? PropertyMetadataReader.Description(property, compilation),
                                Example = metadata.Example ?? PropertyMetadataReader.Example(property, compilation),
                                // The wildcard row represents this collection. Do not apply its
                                // initializer to individual properties of complex element objects.
                                DefaultValue = metadata.DefaultValue ?? (item.Key == Join(Join(prefix, keyName), "*")
                                    ? PropertyMetadataReader.DefaultValue(property, compilation) : null)
                            };
                        }
                        yield return (item.Key, item.Type, metadata);
                        continue;
                    }
                    yield return (item.Key, property.Type.DisplayName(), new ParameterMetadata(nullable, RequirementMetadata.RequiresOverride(property),
                            Attribute("System.ComponentModel.DescriptionAttribute") ?? PropertyMetadataReader.Description(property, compilation),
                            scalar.TypeKind == TypeKind.Enum ? string.Join(", ", scalar.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => f.Name)) : null,
                            PropertyMetadataReader.DefaultValue(property, compilation), PropertyMetadataReader.Example(property, compilation)));
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
