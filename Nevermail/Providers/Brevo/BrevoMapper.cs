using System.Text.Json;
using Nevermail.Mail;

namespace Nevermail.Providers.Brevo;

/// <summary>
/// Maps a validated Brevo request to delivered mails: one mail, or one per message version.
/// Versions override the global subject, content, recipients and params where they set them.
/// </summary>
public static class BrevoMapper
{
    public static List<StoredMail> Map(BrevoRequest req, string rawRequest, string requestId, DateTimeOffset receivedAt)
    {
        var attachments = (req.Attachment ?? []).Select(MapAttachment).ToList();
        var versions = req.MessageVersions is { Count: > 0 } v
            ? v
            : [new BrevoMessageVersion { To = req.To, Cc = req.Cc, Bcc = req.Bcc }];

        var mails = new List<StoredMail>();
        foreach (var version in versions)
        {
            var parameters = MergeParams(req.Params, version.Params);
            var templateId = version.TemplateId ?? req.TemplateId;
            var replyTo = version.ReplyTo ?? req.ReplyTo;

            var message = new MailMessage
            {
                From = req.Sender is { Email: { } email } sender ? new MailAddress(email.Trim(), NullIfEmpty(sender.Name)) : null,
                To = MapAddresses(version.To),
                Cc = MapAddresses(version.Cc ?? req.Cc),
                Bcc = MapAddresses(version.Bcc ?? req.Bcc),
                ReplyTo = replyTo is { Email: { } replyEmail } ? [new MailAddress(replyEmail.Trim(), NullIfEmpty(replyTo.Name))] : [],
                Subject = Render(version.Subject ?? req.Subject, parameters),
                Html = Render(version.HtmlContent ?? req.HtmlContent, parameters),
                Text = Render(version.TextContent ?? req.TextContent, parameters),
                Attachments = attachments,
                Tags = req.Tags ?? [],
                TemplateId = templateId?.ToString(),
                TemplateData = parameters,
                SendAt = req.ScheduledAt,
            };
            foreach (var (key, value) in req.Headers ?? [])
            {
                message.Headers[key] = ProviderHelpers.ToText(value);
            }

            mails.Add(new StoredMail
            {
                RequestId = requestId,
                ReceivedAt = receivedAt,
                Provider = BrevoEndpoints.ProviderKey,
                Message = message,
                RawRequest = rawRequest,
            });
        }
        return mails;
    }

    public static string? FileNameFromUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : null;

    private static MailAttachment MapAttachment(BrevoAttachment a) => new()
    {
        Filename = !string.IsNullOrWhiteSpace(a.Name) ? a.Name : FileNameFromUrl(a.Url),
        ContentType = MimeKit.MimeTypes.GetMimeType(a.Name ?? FileNameFromUrl(a.Url) ?? ""),
        Content = a.Content ?? "",
        Url = string.IsNullOrEmpty(a.Content) ? a.Url : null,
    };

    private static List<MailAddress> MapAddresses(List<BrevoAddress>? list) =>
        list?.Select(a => new MailAddress(a.Email!.Trim(), NullIfEmpty(a.Name))).ToList() ?? [];

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Global params, shallowly overridden by the version's params.</summary>
    private static JsonElement? MergeParams(JsonElement? global, JsonElement? version)
    {
        if (version is not { ValueKind: JsonValueKind.Object })
        {
            return global;
        }
        if (global is not { ValueKind: JsonValueKind.Object })
        {
            return version;
        }

        var properties = new Dictionary<string, JsonElement>();
        foreach (var source in new[] { global.Value, version.Value })
        {
            foreach (var property in source.EnumerateObject())
            {
                properties[property.Name] = property.Value;
            }
        }
        return ProviderHelpers.BuildObject(properties);
    }

    /// <summary>
    /// Renders the simplest form of Brevo's template language: <c>{{ params.name }}</c> and nested
    /// <c>{{ params.order.id }}</c>. Anything more complex (filters, tags, loops) is left untouched.
    /// </summary>
    private static string? Render(string? text, JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } root)
        {
            return text;
        }

        return ProviderHelpers.ReplacePlaceholders(text, "{{", "}}", expression =>
        {
            if (!expression.StartsWith("params.", StringComparison.Ordinal))
            {
                return null;
            }
            var current = root;
            foreach (var segment in expression["params.".Length..].Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }
            return current.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? null : ProviderHelpers.ToText(current);
        });
    }
}
