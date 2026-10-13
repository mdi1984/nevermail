using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nevermail.Mail;
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
    };

    public NevermailFactory(Dictionary<string, string?>? config = null)
    {
        foreach (var (key, value) in config ?? [])
        {
            _config[key] = value;
        }
    }

    public IMailStore Store => Services.GetRequiredService<IMailStore>();

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
