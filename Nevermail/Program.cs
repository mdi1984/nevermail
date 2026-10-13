using Nevermail;
using Nevermail.Api;
using Nevermail.Providers.Brevo;
using Nevermail.Providers.Mandrill;
using Nevermail.Providers.SendGrid;
using Nevermail.Providers.Smtp;
using Nevermail.Storage;

// Legacy charsets (windows-1252, iso-8859-15, ...) still show up in SMTP mails.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
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

app.UseFileServer();

app.MapGet("/health", () => "ok");
app.MapMailApi();

if (IsEnabled("Providers:SendGrid"))
{
    app.MapSendGrid();
}
if (IsEnabled("Providers:Brevo"))
{
    app.MapBrevo();
}
if (IsEnabled("Providers:Mandrill"))
{
    app.MapMandrill();
}

app.Run();

bool IsEnabled(string section) => !string.Equals(app.Configuration[$"{section}:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

public partial class Program;
