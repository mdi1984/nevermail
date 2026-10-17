using System.Net;
using System.Text;
using System.Text.Json;
using Nevermail.Mail;

namespace Nevermail.Providers.AhaSend;

/// <summary>
/// Maps a validated AhaSend request to delivered mails: AhaSend sends every recipient a separate message,
/// with the global substitutions shallowly overridden by the recipient's own.
/// </summary>
public static class AhaSendMapper
{
    public static List<StoredMail> Map(AhaSendRequest req, string rawRequest, string requestId, DateTimeOffset receivedAt)
    {
        var attachments = (req.Attachments ?? []).Select(MapAttachment).ToList();

        var mails = new List<StoredMail>();
        foreach (var recipient in req.Recipients!)
        {
            var substitutions = MergeSubstitutions(req.Substitutions, recipient.Substitutions);
            var message = new MailMessage
            {
                From = new MailAddress(req.From!.Email!.Trim(), NullIfEmpty(req.From.Name)),
                To = [new MailAddress(recipient.Email!.Trim(), NullIfEmpty(recipient.Name))],
                ReplyTo = req.ReplyTo is { Email: { } replyEmail } ? [new MailAddress(replyEmail.Trim(), NullIfEmpty(req.ReplyTo.Name))] : [],
                Subject = Render(req.Subject, substitutions, html: false),
                Html = Render(req.HtmlContent, substitutions, html: true),
                Text = Render(req.TextContent, substitutions, html: false),
                Attachments = attachments,
                Tags = req.Tags ?? [],
                TemplateData = substitutions,
                SendAt = req.Schedule?.FirstAttempt,
            };
            foreach (var (key, value) in req.Headers ?? [])
            {
                // AhaSend generates its own Message-ID and ignores the one in the request.
                if (!key.Equals("message-id", StringComparison.OrdinalIgnoreCase))
                {
                    message.Headers[key] = Render(value, substitutions, html: false)!;
                }
            }
            if (req.SandboxResult is { } sandboxResult)
            {
                message.Metadata["sandbox_result"] = sandboxResult;
            }

            mails.Add(new StoredMail
            {
                RequestId = requestId,
                ReceivedAt = receivedAt,
                Provider = AhaSendEndpoints.ProviderKey,
                Sandbox = req.Sandbox == true,
                Message = message,
                RawRequest = rawRequest,
            });
        }
        return mails;
    }

    /// <summary>
    /// A content id in angle brackets ("&lt;logo@example.com&gt;") makes the attachment an inline image;
    /// without brackets, AhaSend delivers it as a regular attachment.
    /// </summary>
    private static MailAttachment MapAttachment(AhaSendAttachment a)
    {
        var contentId = a.ContentId?.Trim();
        var inline = contentId is { Length: > 2 } && contentId.StartsWith('<') && contentId.EndsWith('>');
        return new MailAttachment
        {
            Filename = a.FileName,
            ContentType = a.ContentType,
            ContentId = inline ? contentId![1..^1] : null,
            Inline = inline,
            Content = a.Base64 == true ? a.Data! : Convert.ToBase64String(Encoding.UTF8.GetBytes(a.Data!)),
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Global substitutions, shallowly overridden by the recipient's. Null if neither has any.</summary>
    private static JsonElement? MergeSubstitutions(JsonElement? global, JsonElement? recipient)
    {
        var hasGlobal = global is { ValueKind: JsonValueKind.Object };
        var hasRecipient = recipient is { ValueKind: JsonValueKind.Object };
        if (!hasRecipient)
        {
            return hasGlobal ? global : null;
        }
        if (!hasGlobal)
        {
            return recipient;
        }

        var properties = new Dictionary<string, JsonElement>();
        foreach (var source in new[] { global!.Value, recipient!.Value })
        {
            foreach (var property in source.EnumerateObject())
            {
                properties[property.Name] = property.Value;
            }
        }
        return ProviderHelpers.BuildObject(properties);
    }

    /// <summary>
    /// Renders the simplest form of AhaSend's Jinja2 templates: <c>{{ name }}</c>, nested <c>{{ order.id }}</c> and
    /// <c>{{ name|safe }}</c>. Values are HTML-escaped in HTML content unless marked <c>|safe</c>, like Jinja's autoescape.
    /// Anything more complex (other filters, tags, loops) is left untouched.
    /// </summary>
    private static string? Render(string? text, JsonElement? substitutions, bool html)
    {
        if (substitutions is not { ValueKind: JsonValueKind.Object } root)
        {
            return text;
        }

        return ProviderHelpers.ReplacePlaceholders(text, "{{", "}}", expression =>
        {
            var safe = false;
            var pipe = expression.IndexOf('|');
            if (pipe >= 0)
            {
                if (expression[(pipe + 1)..].Trim() != "safe")
                {
                    return null;
                }
                safe = true;
                expression = expression[..pipe].Trim();
            }

            var current = root;
            foreach (var segment in expression.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }
            if (current.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return null;
            }
            var value = ProviderHelpers.ToText(current);
            return html && !safe ? WebUtility.HtmlEncode(value) : value;
        });
    }
}
