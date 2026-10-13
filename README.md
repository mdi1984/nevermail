# Nevermail

*Your emails, never delivered.*

Nevermail is a tiny local stand-in for transactional email APIs. Point your app at it instead of the real provider, and every mail it "sends" ends up in a web UI where you can inspect it. Nothing ever leaves your machine. It's built for local dev and integration tests, e.g. as a container in a .NET Aspire app host.

The image is a NativeAOT binary on `scratch`, about 14 MB.

> Formerly **SendGrid Mock** (`mdi1984/sendgrid-mock`). That image is frozen; use `mdi1984/nevermail`.

## Features

- **SendGrid v3 Mail Send API** (`POST /v3/mail/send`) with SendGrid's validation rules and error format. Invalid requests fail here the way they would in production.
  - One mail per personalization, with per-personalization subject, headers, `from`, and substitutions applied
  - Dynamic templates: `template_id` and `dynamic_template_data` are stored and shown
  - `mail_settings.sandbox_mode` answers `200` like SendGrid (the mail is still captured, flagged as sandbox)
  - `GET /v3/scopes`, `POST /v3/mail/batch`, `GET /v3/mail/batch/{id}`
- **Web UI** with the HTML, plain text and raw request of each mail, plus attachments and inline (`cid:`) images
- **Inspection API** for your own tests (see below)
- **Persistence**: in memory, or one JSON file per mail so mails survive restarts

Planned: Brevo (Sendinblue), Mailchimp Transactional (Mandrill), and an SMTP server.

## Getting started

```bash
docker run -d -p 5110:8080 mdi1984/nevermail
```

Open the UI at [http://localhost:5110](http://localhost:5110) and point your SendGrid client at `http://localhost:5110`:

```csharp
var client = new SendGridClient(new SendGridClientOptions
{
    ApiKey = "anything",
    Host = "http://localhost:5110",
});
```

Or with curl:

```bash
curl -i http://localhost:5110/v3/mail/send \
  -H 'Authorization: Bearer anything' \
  -H 'Content-Type: application/json' \
  -d '{
    "personalizations": [{ "to": [{ "email": "recipient@example.com" }] }],
    "from": { "email": "sender@example.com" },
    "subject": "Hello, World!",
    "content": [{ "type": "text/plain", "value": "This is a test!" }]
  }'
```

### Persistence

```bash
docker run -d -p 5110:8080 \
  -e Storage__Path=/app/data \
  -v nevermail_data:/app/data \
  mdi1984/nevermail
```

## Configuration

| Variable | Description | Default |
|---|---|---|
| `Providers__SendGrid__ApiKey` | If set, requests must send exactly this key (`Authorization: Bearer <key>`). If unset, any key is accepted. A missing `Authorization` header is always rejected, like SendGrid does. | unset |
| `Providers__SendGrid__Enabled` | Set to `false` to disable the SendGrid endpoints. | `true` |
| `Storage__Path` | Directory for mail files. If unset, mails are kept in memory only. | unset |
| `Storage__MaxMessages` | Oldest mails are dropped beyond this count. `0` = unlimited. | `1000` |

## Inspection API

Useful for asserting on sent mails in integration tests.

| Endpoint | Description |
|---|---|
| `GET /api/mails` | Mail summaries, newest first. Query: `search`, `to`, `provider`, `requestId` (the `X-Message-Id` returned by SendGrid), `limit` |
| `GET /api/mails/{id}` | Full mail including headers, attachments and the raw request |
| `GET /api/mails/{id}/attachments/{index}` | Download an attachment |
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
