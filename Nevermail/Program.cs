using Nevermail;
using Nevermail.Api;
using Nevermail.Providers.SendGrid;
using Nevermail.Storage;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMailStore>(sp => MailStore.FromConfiguration(
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<MailStore>>()));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, NevermailJsonContext.Default);
});

var app = builder.Build();

app.UseFileServer();

app.MapGet("/health", () => "ok");
app.MapMailApi();

if (app.Configuration["Providers:SendGrid:Enabled"] != "false")
{
    app.MapSendGrid();
}

app.Run();

public partial class Program;
