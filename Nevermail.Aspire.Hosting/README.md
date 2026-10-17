# Nevermail.Aspire.Hosting

[.NET Aspire](https://aspire.dev) hosting integration for [Nevermail](https://github.com/mdi1984/nevermail): a local mock for **SendGrid**, **Brevo**, **Mailchimp Transactional (Mandrill)**, **Azure Communication Services Email**, **AhaSend** and **SMTP**. Every mail your app sends ends up in a web UI, and nothing leaves your machine.

Works with any Aspire 13.x.

```bash
dotnet add package Nevermail.Aspire.Hosting
```

## App host

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var mail = builder.AddNevermail("mail");

builder.AddProject<Projects.Api>("api")
    .WithReference(mail)   // ConnectionStrings__mail
    .WaitFor(mail);

builder.Build().Run();
```

The dashboard links to the inbox (**Inbox** on the `mail` resource). The connection string looks like this:

```text
Endpoint=http://localhost:54321;SmtpHost=localhost;SmtpPort=54322;AccessKey=bmV2ZXJtYWls
```

| Key | Meaning |
|---|---|
| `Endpoint` | Base address of all HTTP APIs: SendGrid `{Endpoint}`, Brevo `{Endpoint}/v3`, Mandrill `{Endpoint}/api/1.3/`, AhaSend `{Endpoint}` |
| `SmtpHost`, `SmtpPort` | Plain SMTP, no TLS |
| `AccessKey` | A dummy key, so the whole string is also a valid Azure Communication Services connection string |

### More options

```csharp
var sendGridKey = builder.AddParameter("sendgrid-key", secret: true);

var mail = builder.AddNevermail("mail", httpPort: 5110, smtpPort: 1025) // fixed ports instead of random ones
    .WithDataVolume()                     // keep mails across app host restarts
    .WithLifetime(ContainerLifetime.Persistent) // keep the container running between app host runs
    .WithSendGridApiKey(sendGridKey)      // only accept this key (any key is accepted otherwise)
    .WithFaults(rate: 0.1, statusCode: 503, delay: TimeSpan.FromMilliseconds(200)) // fail 10% of sends, slow down all
    .WithMaxMessages(500);                // keep at most 500 mails
```

| Method | Effect |
|---|---|
| `WithDataVolume()` / `WithDataBindMount(path)` | Persist mails |
| `WithSendGridApiKey(param)`, `WithBrevoApiKey(param)`, `WithMandrillApiKey(param)`, `WithAhaSendApiKey(param)` | Require a specific API key. Without one, any key is accepted |
| `WithSmtpCredentials(user, password)` | Require SMTP authentication |
| `WithFaults(rate, statusCode, delay)` | Fail or slow down sends on purpose |
| `WithMaxMessages(n)` | Keep at most `n` mails |
| `WithImageTag("…")` | Use another image version. Defaults to the version this package was released with |

### Nevermail locally, the real provider when deployed

Use Nevermail in run mode and a connection string parameter in publish mode. Your project reads `ConnectionStrings:mail` either way:

```csharp
IResourceBuilder<IResourceWithConnectionString> mail = builder.ExecutionContext.IsRunMode
    ? builder.AddNevermail("mail")
    : builder.AddConnectionString("mail");

builder.AddProject<Projects.Api>("api")
    .WithReference(mail);
```

This works especially well for Azure Communication Services: the deployed connection string (`endpoint=https://….communication.azure.com/;accesskey=…`) and Nevermail's are read by the same code.

### Passing single settings instead

If your project already has configuration for its email SDK, set it from the app host instead of parsing the connection string:

```csharp
builder.AddProject<Projects.Api>("api")
    .WithEnvironment("SendGrid__Host", mail.Resource.HttpEndpoint)   // -> configuration "SendGrid:Host"
    .WithEnvironment("Smtp__Host", mail.Resource.SmtpEndpoint.Property(EndpointProperty.Host))
    .WithEnvironment("Smtp__Port", mail.Resource.SmtpEndpoint.Property(EndpointProperty.Port))
    .WaitFor(mail);
```

## Using it in a project

Read the connection string once:

```csharp
using System.Data.Common;

var connectionString = builder.Configuration.GetConnectionString("mail")!;
var mail = new DbConnectionStringBuilder { ConnectionString = connectionString };
var endpoint = (string)mail["Endpoint"];
```

Then register the client you use. API keys can be anything unless you set one with `With…ApiKey`.

**SendGrid** (`SendGrid.Extensions.DependencyInjection`)

```csharp
builder.Services.AddSendGrid(options =>
{
    options.ApiKey = "dev";
    options.Host = endpoint;
});

// later: inject ISendGridClient
await sendGrid.SendEmailAsync(MailHelper.CreateSingleEmail(from, to, "Welcome", "Hi!", "<p>Hi!</p>"));
```

**Brevo** (`brevo_csharp`)

```csharp
builder.Services.AddSingleton(_ =>
{
    var config = new brevo_csharp.Client.Configuration { BasePath = $"{endpoint}/v3" };
    config.ApiKey["api-key"] = "dev";
    return new TransactionalEmailsApi(config);
});
```

**Mailchimp Transactional / Mandrill** (`Mandrill.net`)

```csharp
// Mandrill.net uses the HttpClient's base address as the API root.
builder.Services.AddSingleton<IMandrillApi>(_ =>
    new MandrillApi("dev", new HttpClient { BaseAddress = new Uri($"{endpoint}/api/1.3/") }));
```

**Azure Communication Services** (`Azure.Communication.Email`)

```csharp
// With Microsoft.Extensions.Azure:
builder.Services.AddAzureClients(clients => clients.AddEmailClient(connectionString));
// or without it:
builder.Services.AddSingleton(new EmailClient(connectionString));

// later: inject EmailClient
var operation = await email.SendAsync(WaitUntil.Completed, "shop@example.com", "alice@example.com", "Welcome", "<p>Hi!</p>");
```

Use a connection string, not `DefaultAzureCredential`: the Azure SDK only sends Entra ID tokens over `https`, and Nevermail serves `http`.

**AhaSend** (API v2 over `HttpClient`, there's no .NET SDK)

```csharp
// Any account UUID works against Nevermail.
builder.Services.AddHttpClient("ahasend", client =>
{
    client.BaseAddress = new Uri($"{endpoint}/");
    client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
});

// later: var http = httpClientFactory.CreateClient("ahasend");
await http.PostAsJsonAsync($"v2/accounts/{accountId}/messages", new
{
    from = new { email = "shop@example.com" },
    recipients = new[] { new { email = "alice@example.com" } },
    subject = "Welcome",
    html_content = "<p>Hi!</p>",
});
```

**SMTP** (MailKit)

```csharp
using var smtp = new SmtpClient();
await smtp.ConnectAsync((string)mail["SmtpHost"], int.Parse((string)mail["SmtpPort"]), SecureSocketOptions.None);
await smtp.SendAsync(message);
await smtp.DisconnectAsync(true);
```

With `System.Net.Mail.SmtpClient`, use the same host and port with `EnableSsl = false`.

## Integration tests

With [Aspire.Hosting.Testing](https://www.nuget.org/packages/Aspire.Hosting.Testing), start the app host and ask Nevermail's inspection API what was sent. `wait=10` blocks until the mail arrives (up to 10 seconds), so tests don't need to sleep:

```csharp
[Fact]
public async Task Registration_SendsWelcomeMail()
{
    var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>();
    await using var app = await builder.BuildAsync();
    await app.StartAsync();
    await app.ResourceNotifications.WaitForResourceHealthyAsync("api");

    var to = $"{Guid.NewGuid():N}@example.com";   // unique per test, so tests can run in parallel
    var api = app.CreateHttpClient("api");
    (await api.PostAsJsonAsync("/register", new { email = to })).EnsureSuccessStatusCode();

    var nevermail = app.CreateHttpClient("mail", "http");
    var mails = await nevermail.GetFromJsonAsync<JsonElement>($"/api/mails?to={to}&wait=10");

    var summary = Assert.Single(mails.EnumerateArray());
    Assert.Equal("Welcome!", summary.GetProperty("subject").GetString());

    // The full mail: HTML, text, headers, attachments, the raw request
    var id = summary.GetProperty("id").GetString();
    var mail = await nevermail.GetFromJsonAsync<JsonElement>($"/api/mails/{id}");
    Assert.Contains("Confirm your address", mail.GetProperty("message").GetProperty("html").GetString());
}
```

Further endpoints: `DELETE /api/mails?to=…` cleans up after a test, `GET /api/mails/{id}/eml` downloads the mail. See the [inspection API](https://github.com/mdi1984/nevermail#inspection-api).

### Testing failures

Send to `someone@fail-<code>.test` to make a single send fail, e.g. `x@fail-429.test` for a rate limit with `Retry-After`. Nothing is captured for failed sends. For Azure Communication Services, `someone@fail-dropped.test` is accepted and then fails with `EmailDropped` when the operation is polled. See [fault injection](https://github.com/mdi1984/nevermail#fault-injection).
