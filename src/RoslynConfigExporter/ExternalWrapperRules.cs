using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace RoslynConfigExporter;

public sealed record ExternalWrapperRule(
    string Method,
    int KeyArgument,
    string? Prefix = null,
    int? ValueTypeArgument = null,
    UsageKind Kind = UsageKind.WrapperCall)
{
    public bool Matches(IMethodSymbol symbol)
    {
        var fullName = symbol.ContainingType.ToDisplayString() + "." + symbol.Name;
        var regex = "^" + Regex.Escape(Method).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(fullName, regex, RegexOptions.CultureInvariant);
    }
}

internal sealed record ExternalWrapperRuleFile(IReadOnlyList<ExternalWrapperRule> Methods);

internal static class ExternalWrapperRuleLoader
{
    public static IReadOnlyList<ExternalWrapperRule> Load(IEnumerable<string> files)
    {
        var result = new List<ExternalWrapperRule>();
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException("External wrapper rules file was not found.", file);
            }

            var rules = JsonSerializer.Deserialize<ExternalWrapperRuleFile>(File.ReadAllText(file), options)
                        ?? throw new InvalidDataException($"Invalid rules file: {file}");
            result.AddRange(rules.Methods);
        }

        return result;
    }
}
