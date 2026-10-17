# Nevermail.Aspire.Hosting

[.NET Aspire](https://aspire.dev) hosting integration for [Nevermail](https://github.com/mdi1984/nevermail): a local mock for **SendGrid**, **Brevo**, **Mailchimp Transactional (Mandrill)**, **Azure Communication Services Email**, **AhaSend** and **SMTP**. Every mail your app sends ends up in a web UI, and nothing leaves your machine.

Works with any Aspire 13.x.

```bash
dotnet add package Nevermail.Aspire.Hosting
```

## App host

The dashboard links to the inbox (**Inbox** on the `mail` resource).

Tell `AddNevermail` which provider your project uses, then hand its connection properties to the settings your project already has. The project binds them like any other configuration and never knows it's talking to Nevermail:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Api>("api");

if (builder.ExecutionContext.IsRunMode)
{
    // Locally: Nevermail
    var mail = builder.AddNevermail("mail", NevermailProvider.SendGrid);
    api.WithEnvironment("SendGrid__Host", mail.Resource.GetConnectionProperty("Endpoint"))
       .WithEnvironment("SendGrid__ApiKey", mail.Resource.GetConnectionProperty("ApiKey"))
       .WaitFor(mail);
}
else
{
    // Deployed: the real provider. Without SendGrid__Host the SDK uses https://api.sendgrid.com.
    api.WithEnvironment("SendGrid__ApiKey", builder.AddParameter("sendgrid-key", secret: true));
}

builder.Build().Run();
```

| `NevermailProvider` | Connection properties |
|---|---|
| `SendGrid` | `Endpoint` (`http://localhost:54321`), `ApiKey` |
| `Brevo` | `Endpoint` (`http://localhost:54321/v3`), `ApiKey` |
| `Mandrill` | `Endpoint` (`http://localhost:54321/api/1.3/`), `ApiKey` |
| `AzureCommunicationServices` | `Endpoint` (`http://localhost:54321/`), `AccessKey` |
| `AhaSend` | `Endpoint` (`http://localhost:54321`), `ApiKey`, `AccountId` (any UUID works) |
| `Smtp` | `Host`, `Port`, `Endpoint` (`smtp://localhost:54322`), and `Username`, `Password` with `WithSmtpCredentials(...)` |

`ApiKey` is `nevermail`, since any key is accepted, unless you require one with `WithSendGridApiKey(...)` (or the Brevo, Mandrill or AhaSend variant). Pass the property itself to `WithEnvironment`, not its `.ValueExpression`: that's the manifest placeholder (`{sendgrid-key.value}`), not the value. `WithReference(mail)` also works. It sets the connection string (`ConnectionStrings__mail`, the properties as `Key=Value;…`) and every property as `MAIL_ENDPOINT`, `MAIL_APIKEY` and so on.

The samples below show the run-mode `WithEnvironment` lines and the Api project's `Program.cs`, with an endpoint that sends a welcome mail. The settings' defaults are the real provider's addresses, used when deployed.

### SendGrid

[`SendGrid.Extensions.DependencyInjection`](https://www.nuget.org/packages/SendGrid.Extensions.DependencyInjection). `SendGridClientOptions` has `Host` and `ApiKey`, so the section binds straight onto it.

```csharp
// App host
api.WithEnvironment("SendGrid__Host", mail.Resource.GetConnectionProperty("Endpoint"))
   .WithEnvironment("SendGrid__ApiKey", mail.Resource.GetConnectionProperty("ApiKey"));
```

```csharp
using SendGrid;
using SendGrid.Extensions.DependencyInjection;
using SendGrid.Helpers.Mail;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSendGrid(options => builder.Configuration.GetSection("SendGrid").Bind(options));

var app = builder.Build();

app.MapPost("/welcome", async (string to, ISendGridClient sendGrid) =>
{
    var message = MailHelper.CreateSingleEmail(new EmailAddress("shop@example.com"), new EmailAddress(to), "Welcome", "Hi!", "<p>Hi!</p>");
    var response = await sendGrid.SendEmailAsync(message);
    return response.IsSuccessStatusCode ? Results.Ok() : Results.Problem(await response.Body.ReadAsStringAsync());
});

app.Run();
```

### Brevo

[`brevo_csharp`](https://www.nuget.org/packages/brevo_csharp)

```csharp
// App host
api.WithEnvironment("Brevo__BasePath", mail.Resource.GetConnectionProperty("Endpoint"))
   .WithEnvironment("Brevo__ApiKey", mail.Resource.GetConnectionProperty("ApiKey"));
```

```csharp
using brevo_csharp.Api;
using brevo_csharp.Model;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<BrevoSettings>().BindConfiguration("Brevo");
builder.Services.AddSingleton(services =>
{
    var settings = services.GetRequiredService<IOptions<BrevoSettings>>().Value;
    var config = new brevo_csharp.Client.Configuration { BasePath = settings.BasePath };
    config.ApiKey["api-key"] = settings.ApiKey;
    return new TransactionalEmailsApi(config);
});

var app = builder.Build();

app.MapPost("/welcome", async (string to, TransactionalEmailsApi brevo) =>
{
    var result = await brevo.SendTransacEmailAsync(new SendSmtpEmail(
        sender: new SendSmtpEmailSender(email: "shop@example.com"),
        to: [new SendSmtpEmailTo(to)],
        subject: "Welcome",
        htmlContent: "<p>Hi!</p>"));
    return Results.Ok(result.MessageId);
});

app.Run();

sealed class BrevoSettings
{
    public string BasePath { get; set; } = "https://api.brevo.com/v3";
    public string ApiKey { get; set; } = "";
}
```

### Mailchimp Transactional (Mandrill)

[`Mandrill.net`](https://www.nuget.org/packages/Mandrill.net), which uses the `HttpClient`'s base address as the API root.

```csharp
// App host
api.WithEnvironment("Mandrill__BaseAddress", mail.Resource.GetConnectionProperty("Endpoint"))
   .WithEnvironment("Mandrill__ApiKey", mail.Resource.GetConnectionProperty("ApiKey"));
```

```csharp
using Mandrill;
using Mandrill.Model;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<MandrillSettings>().BindConfiguration("Mandrill");
builder.Services.AddSingleton<IMandrillApi>(services =>
{
    var settings = services.GetRequiredService<IOptions<MandrillSettings>>().Value;
    return new MandrillApi(settings.ApiKey, new HttpClient { BaseAddress = settings.BaseAddress });
});

var app = builder.Build();

app.MapPost("/welcome", async (string to, IMandrillApi mandrill) =>
{
    var results = await mandrill.Messages.SendAsync(new MandrillMessage("shop@example.com", to, "Welcome", "<p>Hi!</p>"));
    return results.All(r => r.Status == MandrillSendMessageResponseStatus.Sent) ? Results.Ok() : Results.Problem(results[0].RejectReason);
});

app.Run();

sealed class MandrillSettings
{
    public Uri BaseAddress { get; set; } = new("https://mandrillapp.com/api/1.0/");
    public string ApiKey { get; set; } = "";
}
```

### Azure Communication Services

[`Azure.Communication.Email`](https://www.nuget.org/packages/Azure.Communication.Email) with [`Microsoft.Extensions.Azure`](https://www.nuget.org/packages/Microsoft.Extensions.Azure). The ACS SDK is configured with a connection string, and Nevermail's is a regular ACS one, so pass it as it is. Deployed, `builder.AddConnectionString("mail")` holds the one from the Azure portal (`endpoint=https://….communication.azure.com/;accesskey=…`).

```csharp
// App host
api.WithReference(mail);   // ConnectionStrings__mail
```

```csharp
using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Azure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAzureClients(clients => clients.AddEmailClient(builder.Configuration.GetConnectionString("mail")));

var app = builder.Build();

app.MapPost("/welcome", async (string to, EmailClient email) =>
{
    var operation = await email.SendAsync(WaitUntil.Completed, "shop@example.com", to, "Welcome", "<p>Hi!</p>");
    return Results.Ok(operation.Value.Status.ToString());
});

app.Run();
```

Use the connection string, not `DefaultAzureCredential`: the Azure SDK only sends Entra ID tokens over `https`, and Nevermail serves `http`.

### AhaSend

[API v2](https://ahasend.com/docs) over `HttpClient`, since there's no .NET SDK.

```csharp
// App host
api.WithEnvironment("AhaSend__BaseAddress", mail.Resource.GetConnectionProperty("Endpoint"))
   .WithEnvironment("AhaSend__ApiKey", mail.Resource.GetConnectionProperty("ApiKey"))
   .WithEnvironment("AhaSend__AccountId", mail.Resource.GetConnectionProperty("AccountId"));
```

```csharp
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AhaSendSettings>().BindConfiguration("AhaSend");
builder.Services.AddHttpClient("ahasend", (services, client) =>
{
    var settings = services.GetRequiredService<IOptions<AhaSendSettings>>().Value;
    client.BaseAddress = settings.BaseAddress;
    client.DefaultRequestHeaders.Authorization = new("Bearer", settings.ApiKey);
});

var app = builder.Build();

app.MapPost("/welcome", async (string to, IHttpClientFactory httpClients, IOptions<AhaSendSettings> settings) =>
{
    var response = await httpClients.CreateClient("ahasend").PostAsJsonAsync($"v2/accounts/{settings.Value.AccountId}/messages", new
    {
        from = new { email = "shop@example.com" },
        recipients = new[] { new { email = to } },
        subject = "Welcome",
        html_content = "<p>Hi!</p>",
    });
    return response.IsSuccessStatusCode ? Results.Ok() : Results.Problem(await response.Content.ReadAsStringAsync());
});

app.Run();

sealed class AhaSendSettings
{
    public Uri BaseAddress { get; set; } = new("https://api.ahasend.com");
    public string ApiKey { get; set; } = "";
    public Guid AccountId { get; set; }
}
```

### SMTP

[`MailKit`](https://www.nuget.org/packages/MailKit)

```csharp
// App host
api.WithEnvironment("Smtp__Host", mail.Resource.GetConnectionProperty("Host"))
   .WithEnvironment("Smtp__Port", mail.Resource.GetConnectionProperty("Port"));
```

```csharp
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<SmtpSettings>().BindConfiguration("Smtp");

var app = builder.Build();

app.MapPost("/welcome", async (string to, IOptions<SmtpSettings> options) =>
{
    var settings = options.Value;
    var message = new MimeMessage();
    message.From.Add(MailboxAddress.Parse("shop@example.com"));
    message.To.Add(MailboxAddress.Parse(to));
    message.Subject = "Welcome";
    message.Body = new TextPart("html") { Text = "<p>Hi!</p>" };

    using var smtp = new SmtpClient();
    // Auto: plain against Nevermail, which offers no STARTTLS, and TLS against a real server that does
    await smtp.ConnectAsync(settings.Host, settings.Port, SecureSocketOptions.Auto);
    if (settings.Username is not null)
    {
        await smtp.AuthenticateAsync(settings.Username, settings.Password);
    }
    await smtp.SendAsync(message);
    await smtp.DisconnectAsync(true);
    return Results.Ok();
});

app.Run();

sealed class SmtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
}
```

With `WithSmtpCredentials(...)`, also pass `Smtp__Username` and `Smtp__Password` from the `Username` and `Password` properties. With `System.Net.Mail.SmtpClient`, use the same host and port with `EnableSsl = false` locally.

### One instance for several providers

Without a provider, `AddNevermail("mail")` has the properties `Endpoint` (base address of all HTTP APIs), `SmtpHost`, `SmtpPort` and `AccessKey`, and the connection string `Endpoint=…;SmtpHost=…;SmtpPort=…;AccessKey=…`, which is also a valid ACS one. Add the provider paths yourself:

```csharp
var mail = builder.AddNevermail("mail");

builder.AddProject<Projects.Api>("api")
    .WithEnvironment("SendGrid__Host", mail.Resource.HttpEndpoint)
    .WithEnvironment("Brevo__BasePath", ReferenceExpression.Create($"{mail.Resource.HttpEndpoint}/v3"))
    .WithEnvironment("Smtp__Host", mail.Resource.SmtpEndpoint.Property(EndpointProperty.Host))
    .WithEnvironment("Smtp__Port", mail.Resource.SmtpEndpoint.Property(EndpointProperty.Port))
    .WaitFor(mail);
```

## Options

```csharp
var sendGridKey = builder.AddParameter("sendgrid-key", secret: true);

var mail = builder.AddNevermail("mail", NevermailProvider.SendGrid, httpPort: 5110, smtpPort: 1025) // fixed ports instead of random ones
    .WithDataVolume()                     // keep mails across app host restarts
    .WithLifetime(ContainerLifetime.Persistent) // keep the container running between app host runs
    .WithSendGridApiKey(sendGridKey)      // only accept this key; it becomes the ApiKey property
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
