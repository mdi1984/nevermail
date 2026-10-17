using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Nevermail.Mail;
using Nevermail.Providers.Smtp;
using Nevermail.Storage;

namespace Nevermail.Tests;

/// <summary>A fresh Nevermail instance with in-memory storage. Create one per test to keep tests isolated.</summary>
public sealed class NevermailFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test_api_key";

    private readonly Dictionary<string, string?> _config = new()
    {
        ["Storage:Path"] = "",
        ["Providers:SendGrid:ApiKey"] = ApiKey,
        ["Providers:Brevo:ApiKey"] = ApiKey,
        ["Providers:Mandrill:ApiKey"] = ApiKey,
        ["Providers:AhaSend:ApiKey"] = ApiKey,
        // Random free port, so parallel test hosts don't collide.
        ["Smtp:Port"] = "0",
    };

    public NevermailFactory(Dictionary<string, string?>? config = null)
    {
        foreach (var (key, value) in config ?? [])
        {
            _config[key] = value;
        }
    }

    public IMailStore Store => Services.GetRequiredService<IMailStore>();

    public int SmtpPort => Services.GetRequiredService<SmtpServer>().Port ?? throw new InvalidOperationException("SMTP is disabled");

    /// <summary>
    /// Starts a real Kestrel server (for clients that can't use the in-memory test server) and returns its base address.
    /// </summary>
    public Uri StartKestrel()
    {
        UseKestrel(0);
        StartServer();
        var address = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address);
    }

    public async Task<List<MailSummary>> ListMailsAsync(string query = "")
    {
        using var client = CreateClient();
        return (await client.GetFromJsonAsync<List<MailSummary>>($"/api/mails{query}"))!;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_config));
    }
}
