# Nevermail.Aspire.Hosting

[.NET Aspire](https://aspire.dev) hosting integration for [Nevermail](https://github.com/mdi1984/nevermail): a local mock for **SendGrid**, **Brevo**, **Mailchimp Transactional (Mandrill)**, **Azure Communication Services Email** and **SMTP**. Every mail your app sends ends up in a web UI, and nothing leaves your machine.

```csharp
var mail = builder.AddNevermail("mail")
    .WithDataVolume();                   // optional: keep mails across restarts

builder.AddProject<Projects.Api>("api")
    .WithReference(mail)                 // ConnectionStrings__mail
    .WithEnvironment("SendGrid__Host", mail.Resource.HttpEndpoint)
    .WaitFor(mail);
```

The dashboard links to the inbox. The connection string looks like `Endpoint=http://localhost:54321;SmtpHost=localhost;SmtpPort=54322;AccessKey=bmV2ZXJtYWls`. `Endpoint` is the base address of all HTTP APIs:

| Provider | Base address |
|---|---|
| SendGrid | `{Endpoint}` |
| Brevo | `{Endpoint}/v3` |
| Mailchimp Transactional | `{Endpoint}/api/1.3/` |
| Azure Communication Services | The connection string itself: `new EmailClient(builder.Configuration.GetConnectionString("mail"))` |
| SMTP | `SmtpHost:SmtpPort`, no TLS |

## Options

| Method | Effect |
|---|---|
| `WithDataVolume()` / `WithDataBindMount(path)` | Persist mails |
| `WithSendGridApiKey(param)`, `WithBrevoApiKey(param)`, `WithMandrillApiKey(param)` | Require a specific API key. Without one, any key is accepted |
| `WithSmtpCredentials(user, password)` | Require SMTP authentication |
| `WithFaults(rate: 0.1, statusCode: 503, delay: TimeSpan.FromMilliseconds(200))` | Fail or slow down sends on purpose |
| `WithMaxMessages(n)` | Keep at most `n` mails |
| `WithImageTag("…")` | Use another image version. Defaults to the version this package was released with |

Single sends can be failed by addressing them to `someone@fail-<code>.test`, e.g. `x@fail-429.test`. For Azure Communication Services, `someone@fail-dropped.test` is accepted and then fails when polled.
