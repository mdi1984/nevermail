using Nevermail;
using Nevermail.Api;
using Nevermail.Providers;
using Nevermail.Providers.AhaSend;
using Nevermail.Providers.AzureEmail;
using Nevermail.Providers.Brevo;
using Nevermail.Providers.Mandrill;
using Nevermail.Providers.SendGrid;
using Nevermail.Providers.Smtp;
using Nevermail.Storage;

// Legacy charsets (windows-1252, iso-8859-15, ...) still show up in SMTP mails.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FaultInjector>();
builder.Services.AddSingleton<AzureEmailOperations>();
builder.Services.AddSingleton<AhaSendIdempotency>();
builder.Services.AddSingleton<IMailStore>(sp => MailStore.FromConfiguration(
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<MailStore>>()));

builder.Services.AddSingleton<SmtpServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SmtpServer>());

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, NevermailJsonContext.Default);
});

var app = builder.Build();

app.UseFileServer(new FileServerOptions
{
    // Revalidate on every load (cheap 304s via ETag) so a new image version never serves a stale UI.
    StaticFileOptions = { OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" },
});

app.MapGet("/health", () => "ok");

var providers = new List<string>();
if (IsEnabled("Providers:SendGrid"))
{
    app.MapSendGrid();
    providers.Add(SendGridEndpoints.ProviderKey);
}
if (IsEnabled("Providers:Brevo"))
{
    app.MapBrevo();
    providers.Add(BrevoEndpoints.ProviderKey);
}
if (IsEnabled("Providers:Mandrill"))
{
    app.MapMandrill();
    providers.Add(MandrillEndpoints.ProviderKey);
}
if (IsEnabled("Providers:Azure"))
{
    app.MapAzureEmail();
    providers.Add(AzureEmailEndpoints.ProviderKey);
}
if (IsEnabled("Providers:AhaSend"))
{
    app.MapAhaSend();
    providers.Add(AhaSendEndpoints.ProviderKey);
}

app.MapMailApi(providers);

app.Run();

bool IsEnabled(string section) => !string.Equals(app.Configuration[$"{section}:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

public partial class Program;
