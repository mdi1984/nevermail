# Nevermail

*Your emails, never delivered.*

**A fake email provider for local development and tests.** Point your app at Nevermail instead of SendGrid, Brevo, Mailchimp Transactional, Azure Communication Services, AhaSend or an SMTP server. Your code sends mail exactly as it would in production; Nevermail accepts it, shows it in a web inbox, and never delivers anything.

![Nevermail inbox showing captured mails from SendGrid, Azure, SMTP, Mandrill and Brevo](docs/screenshot.png)

```bash
docker run -d -p 5110:8080 -p 1025:1025 mdi1984/nevermail
```

Open [http://localhost:5110](http://localhost:5110) and send your first mail.

- **Drop-in APIs** for SendGrid, Brevo, Mailchimp Transactional (Mandrill), Azure Communication Services Email, AhaSend, and SMTP. Requests are validated like the real service, so what fails in production fails here too.
- **Web inbox** with live updates, search, HTML preview at desktop/tablet/mobile widths, headers, attachments and `.eml` download.
- **Built for tests**: an [inspection API](#inspection-api) that waits for a mail instead of sleeping, and [fault injection](#fault-injection) for rate limits, outages and bounces.
- **Tiny**: a ~16 MB NativeAOT image for `linux/amd64` and `linux/arm64`, with a [.NET Aspire integration](#net-aspire).

> Formerly **SendGrid Mock** (`mdi1984/sendgrid-mock`). That image is frozen; use `mdi1984/nevermail`.

## Connecting your app

Point your existing client at Nevermail. Any API key works unless you [configure one](#configuration).

| Provider | Point the client at | Auth |
|---|---|---|
| SendGrid | `http://localhost:5110` | `Authorization: Bearer <key>` |
| Brevo | `http://localhost:5110/v3` | `api-key: <key>` header |
| Mailchimp Transactional | `http://localhost:5110/api/1.3/` (any `1.x` works, with or without `.json` suffix) | `key` in the request body |
| Azure Communication Services | connection string `endpoint=http://localhost:5110/;accesskey=bmV2ZXJtYWls` | access key (any base64 value) |
| AhaSend | `http://localhost:5110` instead of `https://api.ahasend.com` (API v2, any account UUID) | `Authorization: Bearer <key>` |
| SMTP | `localhost:1025`, no TLS | optional |

```csharp
// SendGrid
var sendGrid = new SendGridClient(new SendGridClientOptions { ApiKey = "anything", Host = "http://localhost:5110" });

// Brevo (brevo_csharp)
var config = new brevo_csharp.Client.Configuration { BasePath = "http://localhost:5110/v3" };
config.ApiKey["api-key"] = "anything";
var brevo = new TransactionalEmailsApi(config);

// Mandrill.net uses the HttpClient's base address as API root
var mandrill = new MandrillApi("anything", new HttpClient { BaseAddress = new Uri("http://localhost:5110/api/1.3/") });

// Azure Communication Services (Azure.Communication.Email)
var azure = new EmailClient("endpoint=http://localhost:5110/;accesskey=bmV2ZXJtYWls");

// SMTP (MailKit, System.Net.Mail, ...)
await smtp.ConnectAsync("localhost", 1025, SecureSocketOptions.None);
```

Or with curl:

```bash
curl -i http://localhost:5110/v3/mail/send   -H 'Authorization: Bearer anything'   -H 'Content-Type: application/json'   -d '{
    "personalizations": [{ "to": [{ "email": "recipient@example.com" }] }],
    "from": { "email": "sender@example.com" },
    "subject": "Hello, World!",
    "content": [{ "type": "text/plain", "value": "This is a test!" }]
  }'
```

### .NET Aspire

```bash
dotnet add package Nevermail.Aspire.Hosting
```

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

#### SendGrid

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

#### Brevo

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

#### Mailchimp Transactional (Mandrill)

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

#### Azure Communication Services

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

#### AhaSend

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

#### SMTP

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

#### One instance for several providers

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

The dashboard links to the inbox. The [package README](Nevermail.Aspire.Hosting/README.md) has options like `WithDataVolume()` and `WithFaults(...)`, and integration tests with `Aspire.Hosting.Testing`. The package works with any Aspire 13.x.

### Persistence

```bash
docker run -d -p 5110:8080 -p 1025:1025   -e Storage__Path=/app/data   -v nevermail_data:/app/data   mdi1984/nevermail
```

## Providers

### SendGrid

`POST /v3/mail/send`, plus `GET /v3/scopes`, `POST /v3/mail/batch` and `GET /v3/mail/batch/{id}`.

- All validation errors are reported at once in SendGrid's format: `{"errors":[{"message","field","help"}]}`.
- One mail per personalization, with that personalization's subject, headers, `from` and substitutions applied.
- Dynamic templates: `template_id` and `dynamic_template_data` are stored and shown. Templates themselves aren't rendered.
- `mail_settings.sandbox_mode` answers `200` like SendGrid. The mail is still captured, flagged as sandbox.
- The response's `X-Message-Id` is the `requestId` of the captured mails.

### Brevo (Sendinblue)

`POST /v3/smtp/email`.

- Errors look like Brevo's: `{"code":"missing_parameter","message":"..."}`, first error only.
- Checks the documented limits: name lengths, recipients per version and request, and the attachment extension allow-list.
- `messageVersions` become one mail each. Versions override the global subject, content, recipients and params.
- `{{ params.x }}` placeholders in subject and content are filled in. Filters, loops and template IDs aren't rendered.
- URL attachments are recorded but never downloaded.
- Returns `201` with `messageId` (or `messageIds` for versions), and `202` for `scheduledAt` in the future. Each message ID contains the captured mail's ID: `<mailId@nevermail.local>`.

### Mailchimp Transactional (Mandrill)

`messages/send`, `messages/send-template`, `users/ping` and `users/ping2` under `/api/{1.x}/`.

- Errors use HTTP 500 with `{"status":"error","code":-1,"name":"Invalid_Key",...}`, like Mandrill.
- Every recipient gets their own mail, unless `preserve_recipients` is set. Per-recipient `merge_vars` and `recipient_metadata` are applied.
- Merge tags: `*|NAME|*` (mailchimp) and `{{name}}` / `{{{name}}}` (handlebars, HTML-escaped in HTML content). Block helpers aren't rendered.
- Invalid recipients are reported as `"status":"invalid"` and aren't delivered, `send_at` in the future reports `"scheduled"`, and `images` become inline attachments.
- `send-template` accepts any template name and stores it with the template content and merge vars.

### Azure Communication Services Email

`POST /emails:send` and `GET /emails/operations/{id}`, for any `api-version` (`2023-03-31`, `2025-09-01`, previews and future versions).

- Sending starts a long-running operation like on Azure: `202` with an `Operation-Location` header. Nevermail completes it right away, so the SDK's `WaitUntil.Completed` returns after the first poll. The operation ID is the client's `Operation-Id` (the SDK always sends one) and the captured mail's `requestId`. Retries with the same `Operation-Id` don't send the mail twice.
- Errors look like Azure's: `{"error":{"code":"BadRequest","message":"Request body validation error. See property 'attachments[0].contentType'"}}` plus an `x-ms-error-code` header, first error only.
- Checks the documented limits: at most 50 recipients, 10 MB per request, and the attachment MIME type allow-list. The sender must be a bare address; whether its domain would be verified on Azure isn't checked.
- Requests must be signed with an access key (`HMAC-SHA256`) or carry a bearer token, but signatures aren't verified, so any base64 access key works. Microsoft Entra ID credentials (`DefaultAzureCredential`, managed identity) can't be used locally: the Azure SDK refuses bearer tokens over plain `http`. Use a connection string in development.
- Azure's SMTP relay (`smtp.azurecomm.net`) needs STARTTLS, which Nevermail doesn't support. Point SMTP clients at Nevermail's SMTP server without TLS instead.

### AhaSend

`POST /v2/accounts/{account_id}/messages` (API v2). Any account UUID works.

- Errors look like AhaSend's: `{"message":"..."}`, first error only. AhaSend's own wording isn't stable, so neither is Nevermail's.
- Checks the documented limits: 1 to 100 recipients, `text_content` or `html_content`, header names, `reply_to` vs. a `reply-to` header, attachment fields, `sandbox_result`, `retention` and the `schedule` windows (first attempt within 7 days, expiry within 8).
- Every recipient gets their own mail, like on AhaSend. `{{ name }}`, `{{ order.id }}` and `{{ name|safe }}` are filled in from the global `substitutions`, overridden by the recipient's own. Values are HTML-escaped in `html_content` unless marked `|safe`. Other Jinja filters, tags and loops aren't rendered.
- Attachments with a `content_id` in angle brackets (`<logo>`) are inline images. Plain-text attachments (`"base64": false`) are stored as-is.
- `sandbox: true` mails are captured and flagged as sandbox; `sandbox_result` is recorded but doesn't change the response.
- Returns `202` with one `message` per recipient (`queued`, or `scheduled` for a future `schedule.first_attempt`). Each message ID contains the captured mail's ID: `<mailId@nevermail.local>`.
- `Idempotency-Key` works like on AhaSend: a repeated request replays the stored response with `Idempotent-Replayed: true` and doesn't send again. A key reused with a different body gets `422`, and `5xx` outcomes aren't stored. Keys are kept in memory for 24 hours.
- API v1 (`/v1/email/send`), conversation messages (CC/BCC) and stored templates aren't supported. AhaSend's SMTP relay needs STARTTLS; use Nevermail's SMTP server without TLS instead.

### SMTP

Port `1025`. Supports `SIZE`, `8BITMIME`, `SMTPUTF8`, `PIPELINING` and `AUTH PLAIN`/`LOGIN`. STARTTLS isn't supported.

- Recipients in the envelope but not in the `To`/`Cc` headers show up as Bcc.
- The envelope (`MAIL FROM`, `RCPT TO`, `HELO`, user) is kept as metadata, and the raw message is kept too.
- Without `Smtp__Username`, any credentials (or none) are accepted.
- If the port is already taken (e.g. by Mailpit), Nevermail logs an error and keeps serving the HTTP APIs without SMTP.

## Configuration

| Variable | Description | Default |
|---|---|---|
| `Providers__SendGrid__ApiKey` | If set, requests must use this key. If unset, any key is accepted, but a missing `Authorization` header is always rejected, like SendGrid does. | unset |
| `Providers__Brevo__ApiKey` | Same for Brevo's `api-key` header. | unset |
| `Providers__Mandrill__ApiKey` | Same for Mandrill's `key` body field. | unset |
| `Providers__AhaSend__ApiKey` | Same for AhaSend's `Authorization: Bearer` header. | unset |
| `Providers__<Name>__Enabled` | Set to `false` to disable a provider's endpoints (`SendGrid`, `Brevo`, `Mandrill`, `Azure`, `AhaSend`). | `true` |
| `Smtp__Enabled` | Set to `false` to disable the SMTP server. | `true` |
| `Smtp__Port` | SMTP port inside the container. | `1025` |
| `Smtp__Username`, `Smtp__Password` | If set, clients must authenticate with these credentials before sending. | unset |
| `Smtp__MaxMessageSize` | Maximum message size in bytes. | `26214400` (25 MB) |
| `Smtp__IdleTimeoutSeconds` | Idle connections are closed after this time. | `300` |
| `Storage__Path` | Directory for mail files. If unset, mails are kept in memory only. | unset |
| `Storage__MaxMessages` | Oldest mails are dropped beyond this count. `0` = unlimited. | `1000` |
| `Faults__Rate` | Share of sends (0 to 1) that fail on purpose, see [Fault injection](#fault-injection). | `0` |
| `Faults__StatusCode` | HTTP status for those failures. SMTP answers `451`. | `503` |
| `Faults__DelayMs` | Delay added to every send, like a slow provider. | `0` |

## Inspection API

Useful for asserting on sent mails in integration tests.

| Endpoint | Description |
|---|---|
| `GET /api/mails` | Mail summaries, newest first. Query: `search`, `to`, `provider` (`sendgrid`, `brevo`, `mandrill`, `azure`, `ahasend`, `smtp`), `requestId` (e.g. SendGrid's `X-Message-Id`), `limit`. Add `wait=<seconds>` (max 60) to block until at least `count` (default 1) mails match |
| `GET /api/mails/{id}` | Full mail including headers, attachments and the raw request |
| `GET /api/mails/{id}/attachments/{index}` | Download an attachment |
| `GET /api/mails/{id}/eml` | The mail as an `.eml` file (SMTP mails exactly as received) |
| `GET /api/events` | Server-sent events (`added`, `deleted`, `cleared`) whenever mails change |
| `GET /api/info` | Enabled providers and the SMTP port |
| `DELETE /api/mails/{id}` | Delete one mail |
| `DELETE /api/mails` | Delete all mails, or only those matching `search`, `to`, `provider`, `requestId` |
| `GET /health` | Health check |

In an integration test, wait for the mail instead of sleeping:

```csharp
await app.RegisterUserAsync("alice@example.com");

var mails = await http.GetFromJsonAsync<JsonElement>("http://localhost:5110/api/mails?to=alice@example.com&wait=10");
var id = mails[0].GetProperty("id").GetString();
var mail = await http.GetFromJsonAsync<JsonElement>($"http://localhost:5110/api/mails/{id}");
Assert.Contains("Confirm your address", mail.GetProperty("message").GetProperty("subject").GetString());
```

Tests running in parallel can clean up after themselves with `DELETE /api/mails?to=alice@example.com`.

## Fault injection

Send to an address at `fail-<code>.test` to make that send fail:

| Recipient | Result |
|---|---|
| `x@fail-429.test` | HTTP 429 with `Retry-After`, in the provider's error format |
| `x@fail-500.test`, `x@fail-503.test`, ... | That HTTP status |
| `x@fail-dropped.test` (Azure) | Accepted with `202`, then the operation fails with `EmailDropped` when polled |
| `x@fail-550.test` (SMTP) | `RCPT TO` is rejected with `550`; other recipients of the mail still get it |
| `x@fail-450.test` (SMTP) | Temporary rejection, `450` |

Nothing is captured for failed API sends. For random failures across all sends, set `Faults__Rate` (e.g. `0.2`) and optionally `Faults__StatusCode`. `Faults__DelayMs` simulates a slow provider.

## Development

```bash
dotnet run --project Nevermail/Nevermail.csproj   # http://localhost:5110
dotnet test Nevermail.slnx
```

Releases are cut by pushing a `vX.Y.Z` tag. CI then publishes the multi-arch image (`mdi1984/nevermail:vX.Y.Z`) and, once the image exists, `Nevermail.Aspire.Hosting` `X.Y.Z` with that image tag as its default. NuGet publishing uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no API key is stored. It needs a policy on nuget.org for this repository and `docker-publish.yml`, plus the nuget.org profile name in the `NUGET_USER` secret.

Reflection is disabled in the AOT build, so every serialized type must be registered in a source-generated `JsonSerializerContext`. Unit tests run on the JIT and can't catch a missing registration, so CI also runs `scripts/smoke-test.sh` against the built image:

```bash
docker build -t nevermail:dev .
sh scripts/smoke-test.sh nevermail:dev
```

## License

[MIT](LICENSE)
