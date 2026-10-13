# Nevermail

*Your emails, never delivered.*

Nevermail is a tiny local stand-in for transactional email services. Point your app at it instead of the real provider, and every mail it "sends" ends up in a web UI where you can inspect it. Nothing ever leaves your machine. It's built for local dev and integration tests, e.g. as a container in a .NET Aspire app host.

It speaks the HTTP APIs of **SendGrid**, **Brevo** (formerly Sendinblue) and **Mailchimp Transactional** (Mandrill), and plain **SMTP**. The image is a NativeAOT binary on `scratch`, about 16 MB.

> Formerly **SendGrid Mock** (`mdi1984/sendgrid-mock`). That image is frozen; use `mdi1984/nevermail`.

## Features

- **Realistic APIs**: each provider validates requests the way the real service does and answers with its own response and error format. A request that would fail in production fails here too.
- **Delivery semantics**: SendGrid personalizations, Brevo message versions, and Mandrill's per-recipient sending each turn into the individual mails the provider would deliver, with merge tags filled in for the simple cases.
- **SMTP server** like Mailpit: any client, AUTH PLAIN/LOGIN, MIME parsing including attachments, inline images and legacy charsets.
- **Web UI** with live updates, search and provider filters. Each mail shows its HTML preview (desktop/tablet/mobile widths), plain text, HTML source, headers, metadata and template data, and the raw request, plus attachments, inline (`cid:`) images and `.eml` download. Light and dark themes, keyboard navigation (`j`/`k`, `/`, `Del`), and it works on phones. Self-contained: no CDNs, works offline.
- **Inspection API** for your own tests (see below)
- **Persistence**: in memory, or one JSON file per mail so mails survive restarts

## Getting started

```bash
docker run -d -p 5110:8080 -p 1025:1025 mdi1984/nevermail
```

The UI is at [http://localhost:5110](http://localhost:5110). Then point your app at Nevermail:

| Provider | Point the client at | Auth |
|---|---|---|
| SendGrid | `http://localhost:5110` | `Authorization: Bearer <key>` |
| Brevo | `http://localhost:5110/v3` | `api-key: <key>` header |
| Mailchimp Transactional | `http://localhost:5110/api/1.3/` (any `1.x` works, with or without `.json` suffix) | `key` in the request body |
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
| `Providers__<Name>__Enabled` | Set to `false` to disable a provider's endpoints (`SendGrid`, `Brevo`, `Mandrill`). | `true` |
| `Smtp__Enabled` | Set to `false` to disable the SMTP server. | `true` |
| `Smtp__Port` | SMTP port inside the container. | `1025` |
| `Smtp__Username`, `Smtp__Password` | If set, clients must authenticate with these credentials before sending. | unset |
| `Smtp__MaxMessageSize` | Maximum message size in bytes. | `26214400` (25 MB) |
| `Smtp__IdleTimeoutSeconds` | Idle connections are closed after this time. | `300` |
| `Storage__Path` | Directory for mail files. If unset, mails are kept in memory only. | unset |
| `Storage__MaxMessages` | Oldest mails are dropped beyond this count. `0` = unlimited. | `1000` |

## Inspection API

Useful for asserting on sent mails in integration tests.

| Endpoint | Description |
|---|---|
| `GET /api/mails` | Mail summaries, newest first. Query: `search`, `to`, `provider` (`sendgrid`, `brevo`, `mandrill`, `smtp`), `requestId` (e.g. SendGrid's `X-Message-Id`), `limit` |
| `GET /api/mails/{id}` | Full mail including headers, attachments and the raw request |
| `GET /api/mails/{id}/attachments/{index}` | Download an attachment |
| `GET /api/mails/{id}/eml` | The mail as an `.eml` file (SMTP mails exactly as received) |
| `GET /api/events` | Server-sent events (`added`, `deleted`, `cleared`) whenever mails change |
| `GET /api/info` | Enabled providers and the SMTP port |
| `DELETE /api/mails/{id}` | Delete one mail |
| `DELETE /api/mails` | Delete all mails |
| `GET /health` | Health check |

## Development

```bash
dotnet run --project Nevermail/Nevermail.csproj   # http://localhost:5110
dotnet test Nevermail.slnx
```

Reflection is disabled in the AOT build, so every serialized type must be registered in a source-generated `JsonSerializerContext`. Unit tests run on the JIT and can't catch a missing registration, so CI also runs `scripts/smoke-test.sh` against the built image:

```bash
docker build -t nevermail:dev .
sh scripts/smoke-test.sh nevermail:dev
```
