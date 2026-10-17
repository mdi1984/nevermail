# Nevermail.Aspire.Hosting

[.NET Aspire](https://aspire.dev) hosting integration for [Nevermail](https://github.com/mdi1984/nevermail): a local mock for **SendGrid**, **Brevo**, **Mailchimp Transactional (Mandrill)**, **Azure Communication Services Email**, **AhaSend** and **SMTP**. Every mail your app sends ends up in a web UI, and nothing leaves your machine.

Works with any Aspire 13.x.

```bash
dotnet add package Nevermail.Aspire.Hosting
```

## App host

The dashboard links to the inbox (**Inbox** on the `mail` resource).

Tell `AddNevermail` which provider your project uses. The connection string then has the shape that provider's client needs. Deployed, the same name holds the real provider's connection string, so your project code doesn't change:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<IResourceWithConnectionString> mail = builder.ExecutionContext.IsRunMode
    ? builder.AddNevermail("mail", NevermailProvider.SendGrid)   // locally: Nevermail
    : builder.AddConnectionString("mail");                       // deployed: the real provider, as a secret

builder.AddProject<Projects.Api>("api")
    .WithReference(mail)   // ConnectionStrings__mail
    .WaitFor(mail);

builder.Build().Run();
```

| `NevermailProvider` | Locally (Nevermail) | Deployed (real provider) |
|---|---|---|
| `SendGrid` | `Endpoint=http://localhost:54321;ApiKey=nevermail` | `Endpoint=https://api.sendgrid.com;ApiKey=SG.…` |
| `Brevo` | `Endpoint=http://localhost:54321/v3;ApiKey=nevermail` | `Endpoint=https://api.brevo.com/v3;ApiKey=xkeysib-…` |
| `Mandrill` | `Endpoint=http://localhost:54321/api/1.3/;ApiKey=nevermail` | `Endpoint=https://mandrillapp.com/api/1.0/;ApiKey=…` |
| `AzureCommunicationServices` | `endpoint=http://localhost:54321/;accesskey=bmV2ZXJtYWls` | `endpoint=https://….communication.azure.com/;accesskey=…` from the Azure portal |
| `AhaSend` | `Endpoint=http://localhost:54321;ApiKey=nevermail;AccountId=00000000-0000-0000-0000-000000000000` | `Endpoint=https://api.ahasend.com;ApiKey=…;AccountId=<your account ID>` |
| `Smtp` | `Endpoint=smtp://localhost:54322` | `Endpoint=smtp://smtp.example.com:587;Username=…;Password=…` |

Nevermail accepts any API key. If you require one with `WithSendGridApiKey(...)` (or the Brevo, Mandrill or AhaSend variant), that key goes into the connection string instead of `nevermail`. `WithSmtpCredentials(...)` adds `Username` and `Password` to the SMTP one.

Each sample below is the Api project's `Program.cs`, with an endpoint that sends a welcome mail.

### SendGrid

[`SendGrid.Extensions.DependencyInjection`](https://www.nuget.org/packages/SendGrid.Extensions.DependencyInjection)

```csharp
using System.Data.Common;
using SendGrid;
using SendGrid.Extensions.DependencyInjection;
using SendGrid.Helpers.Mail;

var builder = WebApplication.CreateBuilder(args);
var mail = new DbConnectionStringBuilder { ConnectionString = builder.Configuration.GetConnectionString("mail") };

builder.Services.AddSendGrid(options =>
{
    options.Host = (string)mail["Endpoint"];
    options.ApiKey = (string)mail["ApiKey"];
});

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
using System.Data.Common;
using brevo_csharp.Api;
using brevo_csharp.Model;

var builder = WebApplication.CreateBuilder(args);
var mail = new DbConnectionStringBuilder { ConnectionString = builder.Configuration.GetConnectionString("mail") };

builder.Services.AddSingleton(_ =>
{
    var config = new brevo_csharp.Client.Configuration { BasePath = (string)mail["Endpoint"] };
    config.ApiKey["api-key"] = (string)mail["ApiKey"];
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
```

### Mailchimp Transactional (Mandrill)

[`Mandrill.net`](https://www.nuget.org/packages/Mandrill.net), which uses the `HttpClient`'s base address as the API root.

```csharp
using System.Data.Common;
using Mandrill;
using Mandrill.Model;

var builder = WebApplication.CreateBuilder(args);
var mail = new DbConnectionStringBuilder { ConnectionString = builder.Configuration.GetConnectionString("mail") };

builder.Services.AddSingleton<IMandrillApi>(_ => new MandrillApi(
    (string)mail["ApiKey"],
    new HttpClient { BaseAddress = new Uri((string)mail["Endpoint"]) }));

var app = builder.Build();

app.MapPost("/welcome", async (string to, IMandrillApi mandrill) =>
{
    var results = await mandrill.Messages.SendAsync(new MandrillMessage("shop@example.com", to, "Welcome", "<p>Hi!</p>"));
    return results.All(r => r.Status == MandrillSendMessageResponseStatus.Sent) ? Results.Ok() : Results.Problem(results[0].RejectReason);
});

app.Run();
```

### Azure Communication Services

[`Azure.Communication.Email`](https://www.nuget.org/packages/Azure.Communication.Email) with [`Microsoft.Extensions.Azure`](https://www.nuget.org/packages/Microsoft.Extensions.Azure). The connection string is a regular ACS one, so the SDK reads it as it is:

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
using System.Data.Common;

var builder = WebApplication.CreateBuilder(args);
var mail = new DbConnectionStringBuilder { ConnectionString = builder.Configuration.GetConnectionString("mail") };
var accountId = (string)mail["AccountId"];

builder.Services.AddHttpClient("ahasend", client =>
{
    client.BaseAddress = new Uri((string)mail["Endpoint"]);
    client.DefaultRequestHeaders.Authorization = new("Bearer", (string)mail["ApiKey"]);
});

var app = builder.Build();

app.MapPost("/welcome", async (string to, IHttpClientFactory httpClients) =>
{
    var response = await httpClients.CreateClient("ahasend").PostAsJsonAsync($"v2/accounts/{accountId}/messages", new
    {
        from = new { email = "shop@example.com" },
        recipients = new[] { new { email = to } },
        subject = "Welcome",
        html_content = "<p>Hi!</p>",
    });
    return response.IsSuccessStatusCode ? Results.Ok() : Results.Problem(await response.Content.ReadAsStringAsync());
});

app.Run();
```

### SMTP

[`MailKit`](https://www.nuget.org/packages/MailKit)

```csharp
using System.Data.Common;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

var builder = WebApplication.CreateBuilder(args);
var mail = new DbConnectionStringBuilder { ConnectionString = builder.Configuration.GetConnectionString("mail") };
var server = new Uri((string)mail["Endpoint"]);

var app = builder.Build();

app.MapPost("/welcome", async (string to) =>
{
    var message = new MimeMessage();
    message.From.Add(MailboxAddress.Parse("shop@example.com"));
    message.To.Add(MailboxAddress.Parse(to));
    message.Subject = "Welcome";
    message.Body = new TextPart("html") { Text = "<p>Hi!</p>" };

    using var smtp = new SmtpClient();
    // Auto: plain against Nevermail, which offers no STARTTLS, and TLS against a real server that does
    await smtp.ConnectAsync(server.Host, server.Port, SecureSocketOptions.Auto);
    if (mail.TryGetValue("Username", out var userName))
    {
        await smtp.AuthenticateAsync((string)userName, (string)mail["Password"]);
    }
    await smtp.SendAsync(message);
    await smtp.DisconnectAsync(true);
    return Results.Ok();
});

app.Run();
```

With `System.Net.Mail.SmtpClient`, use the same host and port with `EnableSsl = false` locally.

### One instance for several providers

Without a provider, `AddNevermail("mail")` gives one connection string with every address in it:

```text
Endpoint=http://localhost:54321;SmtpHost=localhost;SmtpPort=54322;AccessKey=bmV2ZXJtYWls
```

`Endpoint` is the base address of all HTTP APIs (SendGrid `{Endpoint}`, Brevo `{Endpoint}/v3`, Mandrill `{Endpoint}/api/1.3/`, AhaSend `{Endpoint}`), and the dummy `AccessKey` makes it a valid ACS connection string too. To hand a project single settings instead, use the endpoints directly:

```csharp
var mail = builder.AddNevermail("mail");

builder.AddProject<Projects.Api>("api")
    .WithEnvironment("SendGrid__Host", mail.Resource.HttpEndpoint)
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
    .WithSendGridApiKey(sendGridKey)      // only accept this key, and put it into the connection string
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
