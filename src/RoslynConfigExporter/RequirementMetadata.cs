using Microsoft.CodeAnalysis;

namespace RoslynConfigExporter;

internal static class RequirementMetadata
{
    public static bool? IsNullable(ITypeSymbol? type) => type is null ? null :
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } ||
        type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated;

    public static bool RequiresOverride(ISymbol? symbol)
    {
        var attributes = symbol?.GetAttributes().Where(a => a.AttributeClass?.Name is "RequiredAttribute" or "Required").ToArray() ?? [];
        return attributes.Any(attribute =>
            !attribute.ConstructorArguments.Any(a => a.Value is false) &&
            !attribute.NamedArguments.Any(a => a.Key is "Required" or "IsRequired" or "Value" or "Enabled" && a.Value.Value is false));
    }
}
