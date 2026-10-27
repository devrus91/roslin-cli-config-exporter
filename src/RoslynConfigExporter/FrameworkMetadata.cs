using Microsoft.CodeAnalysis;

namespace RoslynConfigExporter;

// Maps configuration syntax to the actual API symbols of the referenced library version.
// Descriptions and enum names are never copied into this table.
internal static class FrameworkMetadata
{
    private sealed record Spec(string Type, ISymbol? Symbol = null, bool DefaultApplies = false, string? Enum = null);

    public static void Enrich(string project, Compilation compilation, List<ConfigurationUsage> usages)
    {
        var cache = new Dictionary<FrameworkSetting, (string Type, ParameterMetadata Metadata)>();
        for (var i = 0; i < usages.Count; i++)
        {
            var usage = usages[i];
            if (usage.Project != project || usage.FrameworkSetting is not { } setting) continue;
            if (!cache.TryGetValue(setting, out var metadata))
            {
                var spec = Resolve(setting, compilation);
                var enumType = spec.Enum is null ? null : compilation.GetTypeByMetadataName(spec.Enum);
                var possible = enumType?.TypeKind == TypeKind.Enum
                    ? string.Join(", ", enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => f.Name)) : null;
                var description = spec.Symbol is null ? null : PropertyMetadataReader.Description(spec.Symbol, compilation);
                string? defaultValue = null;
                if (spec.DefaultApplies && spec.Symbol is { } symbol)
                {
                    defaultValue = symbol switch
                    {
                        IPropertySymbol property => PropertyMetadataReader.DefaultValue(property, compilation),
                        IParameterSymbol { HasExplicitDefaultValue: true } parameter => PropertyMetadataReader.FormatConstant(parameter.ExplicitDefaultValue, parameter.Type),
                        _ => null
                    };
                    defaultValue ??= PropertyMetadataReader.DocumentedDefault(symbol, compilation);
                }
                var type = spec.Symbol switch { IPropertySymbol p => p.Type, IParameterSymbol p => p.Type, ITypeSymbol t => t, _ => enumType };
                metadata = (spec.Type, new(RequirementMetadata.IsNullable(type), RequirementMetadata.RequiresOverride(spec.Symbol), description, possible, defaultValue, spec.Symbol is null ? null : PropertyMetadataReader.Example(spec.Symbol, compilation)));
                cache[setting] = metadata;
            }
            usages[i] = usage with { ValueType = metadata.Type, Metadata = metadata.Metadata };
        }
    }

    private static Spec Resolve(FrameworkSetting setting, Compilation compilation)
    {
        ISymbol? Property(string type, string member) => compilation.GetTypeByMetadataName(type)?.GetMembers(member).OfType<IPropertySymbol>().FirstOrDefault();
        ISymbol? Parameter(string type, string method, string name) => compilation.GetTypeByMetadataName(type)?.GetMembers(method).OfType<IMethodSymbol>()
            .SelectMany(m => m.Parameters).FirstOrDefault(p => p.Name == name);
        var path = setting.Path;
        var leaf = path.Split(':').Last();
        if (setting.Family == "Logging")
        {
            const string level = "Microsoft.Extensions.Logging.LogLevel";
            if (path.Contains("LogLevel:", StringComparison.Ordinal))
                return new(level, Property("Microsoft.Extensions.Logging.LoggerFilterOptions", "MinLevel"), Enum: level);
            return new("section", path.Length == 0 ? compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.LoggerFilterOptions")
                : Property("Microsoft.Extensions.Logging.LoggerFilterOptions", "Rules"));
        }
        if (setting.Family == "Serilog")
        {
            const string level = "Serilog.Events.LogEventLevel";
            if (path is "MinimumLevel" or "MinimumLevel:Default")
                return new(path == "MinimumLevel" ? level + " | section" : level,
                    Property("Serilog.LoggerConfiguration", "MinimumLevel"), true, level);
            if (path == "MinimumLevel:Override:*")
                return new(level, Parameter("Serilog.Configuration.LoggerMinimumLevelConfiguration", "Override", "minimumLevel"), Enum: level);
            if (path == "LevelSwitches:*")
                return new(level, Property("Serilog.Core.LoggingLevelSwitch", "MinimumLevel"), Enum: level);
            if (path == "MinimumLevel:ControlledBy")
                return new("string", Parameter("Serilog.Configuration.LoggerMinimumLevelConfiguration", "ControlledBy", "levelSwitch"));
            if (path == "Properties:*")
                return new("object", Parameter("Serilog.Configuration.LoggerEnrichmentConfiguration", "WithProperty", "value"));
            if (path.Contains(":Args:", StringComparison.Ordinal)) return new("object");
            if (path == "Using:*") return new("string");
            if (path == "FilterSwitches:*") return new("string");
            var member = path.Split(':')[0];
            return new(path.Length == 0 || !path.Contains(':') ? "section" : "string",
                path.Length == 0 ? compilation.GetTypeByMetadataName("Serilog.LoggerConfiguration") : Property("Serilog.LoggerConfiguration", member));
        }
        const string https = "Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions";
        if (leaf == "Protocols")
            return new("Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols",
                Property("Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions", "Protocols"), true,
                "Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols");
        if (path.EndsWith("SslProtocols:*", StringComparison.Ordinal))
            return new("System.Security.Authentication.SslProtocols", Property(https, "SslProtocols"), true, "System.Security.Authentication.SslProtocols");
        if (leaf == "ClientCertificateMode")
            return new("Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode", Property(https, leaf), true,
                "Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode");
        if (leaf == "Url")
            return new("string", Property("Microsoft.AspNetCore.Server.Kestrel.Core.Internal.EndpointConfig", "Url"));
        if (leaf is "Path" or "Password" or "Subject" or "Store" or "Location" or "AllowInvalid")
        {
            var parameter = leaf switch
            {
                "Path" => "fileName",
                "Password" => "password",
                "Subject" => "subject",
                "Store" => "storeName",
                "Location" => "storeLocation",
                _ => "allowInvalid"
            };
            var type = leaf switch { "AllowInvalid" => "bool", "Location" => "System.Security.Cryptography.X509Certificates.StoreLocation", _ => "string" };
            return new(type, Parameter("Microsoft.AspNetCore.Hosting.ListenOptionsHttpsExtensions", "UseHttps", parameter),
                Enum: leaf == "Location" ? type : null);
        }
        if (leaf == "KeyPath") return new("string");
        return new("section", compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions"));
    }
}
