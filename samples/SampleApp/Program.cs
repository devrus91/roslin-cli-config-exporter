using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

var serviceUrl = builder.Configuration["Service:Url"];
var retries = builder.Configuration.GetValue<int>("Service:Retries");
var service = builder.Configuration.GetRequiredSection("Service");
var timeout = service.GetValue<TimeSpan>("Timeout");
var connectionString = builder.Configuration.GetConnectionString("Main");

builder.Services.Configure<MailOptions>(builder.Configuration.GetSection(MailOptions.SectionName));
builder.Services.AddOptions<FeatureOptions>().BindConfiguration("Features");
var cache = builder.Configuration.GetSection("Cache").Get<CacheOptions>();
var legacy = new LegacyOptions();
builder.Configuration.GetSection("Legacy").Bind(legacy);

var betaEnabled = ConfigReader.Read<bool>(builder.Configuration, "Flags:Beta");
var tenantId = args.FirstOrDefault() ?? "default";
var tenantUrl = builder.Configuration[$"Tenants:{tenantId}:Url"];

builder.Services.AddSingleton<Consumer>();
var app = builder.Build();
app.Logger.LogInformation("SampleApp configured Serilog from appsettings.json");
app.MapGet("/", () => new { serviceUrl, retries, timeout, connectionString, cache, legacy, betaEnabled, tenantUrl });
app.Run();

public static class ConfigReader
{
    public static T? Read<T>(IConfiguration configuration, string key) => configuration.GetValue<T>(key);
}

public sealed class MailOptions
{
    public const string SectionName = "Mail";

    [ConfigurationKeyName("from-address")]
    public string FromAddress { get; set; } = "";

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    /// <summary>SMTP server hostname.</summary>
    /// <example>smtp.example.test</example>
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

public sealed class FeatureOptions
{
    public Dictionary<string, FeatureDefinition> Definitions { get; set; } = new()
    {
        ["preview"] = new FeatureDefinition
        {
            Enabled = true,
            Audiences = ["internal"],
            AdditionalAudiences = { "beta" },
            Tags = { "preview" },
            Labels = { ["channel"] = "preview" },
            Child = { Name = "override" }
        }
    };
}

public sealed class FeatureDefinition
{
    public FeatureChild Child { get; set; } = new() { Name = "default", Limit = 25 };
    public bool Enabled { get; set; }
    /// <summary>Audiences allowed to use this feature.</summary>
    /// <example>internal</example>
    public string[] Audiences { get; set; } = Array.Empty<string>();
    /// <summary>Additional audience names.</summary>
    /// <example>partners</example>
    public IList<string> AdditionalAudiences { get; set; } = new List<string> { "partners", "internal" };
    /// <summary>Tags associated with the feature.</summary>
    public ICollection<string> Tags { get; set; } = new List<string> { "default" };
    /// <summary>Labels associated with the feature.</summary>
    /// <example>internal</example>
    public IDictionary<string, string> Labels { get; set; } = new Dictionary<string, string> { ["visibility"] = "internal" };
}

public sealed class FeatureChild
{
    public string Name { get; set; } = "child";
    public int Limit { get; set; } = 10;
}

public sealed class CacheOptions
{
    public int Size { get; set; }
    public TimeSpan Ttl { get; set; }
}

public sealed class LegacyOptions
{
    public Uri? Endpoint { get; set; }
}

public sealed class Consumer(
    IOptions<MailOptions> mail,
    IOptionsSnapshot<FeatureOptions> features,
    IOptionsMonitor<CacheOptions> cache)
{
    public MailOptions Mail => mail.Value;
    public FeatureOptions Features => features.Value;
    public CacheOptions Cache => cache.CurrentValue;
}
