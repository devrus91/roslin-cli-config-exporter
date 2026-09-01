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
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

public sealed class FeatureOptions
{
    public Dictionary<string, FeatureDefinition> Definitions { get; set; } = [];
}

public sealed class FeatureDefinition
{
    public bool Enabled { get; set; }
    public string[] Audiences { get; set; } = [];
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
