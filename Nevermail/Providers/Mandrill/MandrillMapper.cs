using System.Globalization;
using System.Net;
using System.Text.Json;
using Nevermail.Mail;

namespace Nevermail.Providers.Mandrill;

public sealed record MandrillDelivery(List<StoredMail> Mails, List<MandrillSendResult> Results);

/// <summary>
/// Maps a validated Mandrill request to delivered mails. Without preserve_recipients, Mandrill sends every recipient
/// an individual copy (with their own merge vars and metadata); with it, one mail shows all recipients.
/// </summary>
public static class MandrillMapper
{
    public static MandrillDelivery Map(MandrillRequest req, string rawRequest, string requestId, DateTimeOffset receivedAt)
    {
        var m = req.Message!;
        var sendAt = ParseSendAt(req.SendAt);
        var status = sendAt > receivedAt ? "scheduled" : "sent";
        var mergeEnabled = m.Merge ?? (m.GlobalMergeVars is { Count: > 0 } || m.MergeVars is { Count: > 0 });
        var handlebars = string.Equals(m.MergeLanguage, "handlebars", StringComparison.OrdinalIgnoreCase);

        // One result per recipient, in request order (clients may match them up by index).
        var results = new MandrillSendResult?[m.To!.Count];
        var valid = new List<MandrillRecipient>();
        for (var i = 0; i < m.To.Count; i++)
        {
            var recipient = m.To[i];
            if (EmailValidation.IsValid(recipient.Email))
            {
                valid.Add(recipient);
            }
            else
            {
                results[i] = new(recipient.Email ?? "", "invalid", null, MailIds.New(32));
            }
        }

        var groups = m.PreserveRecipients == true
            ? (valid.Count > 0 ? [valid] : [])
            : valid.Select(r => new List<MandrillRecipient> { r }).ToList();

        var mails = new List<StoredMail>();
        foreach (var group in groups)
        {
            // Per-recipient merge vars and metadata only apply when each recipient gets their own copy.
            var rcpt = group.Count == 1 ? group[0].Email : null;
            var vars = mergeEnabled ? CollectVars(m, rcpt) : null;

            var message = new MailMessage
            {
                From = string.IsNullOrWhiteSpace(m.FromEmail) ? null : new MailAddress(m.FromEmail.Trim(), string.IsNullOrWhiteSpace(m.FromName) ? null : m.FromName),
                To = Addresses(group, "to"),
                Cc = Addresses(group, "cc"),
                Bcc = Addresses(group, "bcc"),
                Subject = Merge(m.Subject, vars, handlebars, htmlEncode: false),
                Html = Merge(m.Html, vars, handlebars, htmlEncode: true),
                Text = Merge(m.Text, vars, handlebars, htmlEncode: false),
                Attachments = Files(m.Attachments, inline: false).Concat(Files(m.Images, inline: true)).ToList(),
                Tags = m.Tags ?? [],
                TemplateId = req.TemplateName,
                TemplateData = TemplateData(req, vars),
                SendAt = sendAt,
            };
            if (EmailValidation.IsValid(m.BccAddress))
            {
                message.Bcc.Add(new MailAddress(m.BccAddress!.Trim()));
            }
            foreach (var (key, value) in m.Headers ?? [])
            {
                message.Headers[key] = ProviderHelpers.ToText(value);
            }
            if (message.Headers.TryGetValue("Reply-To", out var replyTo) && MimeKit.InternetAddressList.TryParse(replyTo, out var replyToList))
            {
                message.ReplyTo = replyToList.Mailboxes.Select(a => new MailAddress(a.Address, string.IsNullOrEmpty(a.Name) ? null : a.Name)).ToList();
            }
            foreach (var (key, value) in Metadata(m, rcpt))
            {
                message.Metadata[key] = value;
            }

            var mail = new StoredMail
            {
                RequestId = requestId,
                ReceivedAt = receivedAt,
                Provider = MandrillEndpoints.ProviderKey,
                Message = message,
                RawRequest = rawRequest,
            };
            mails.Add(mail);
            foreach (var recipient in group)
            {
                results[m.To.IndexOf(recipient)] = new MandrillSendResult(recipient.Email!.Trim(), status, null, mail.Id);
            }
        }

        return new MandrillDelivery(mails, [.. results.Select(r => r!)]);
    }

    /// <summary>Mandrill's send_at format is "YYYY-MM-DD HH:MM:SS" in UTC.</summary>
    public static DateTimeOffset? ParseSendAt(string? value) =>
        DateTimeOffset.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result)
            ? result
            : null;

    private static string RecipientType(MandrillRecipient r) => string.IsNullOrEmpty(r.Type) ? "to" : r.Type.ToLowerInvariant();

    private static List<MailAddress> Addresses(List<MandrillRecipient> group, string type) =>
        group.Where(r => RecipientType(r) == type)
            .Select(r => new MailAddress(r.Email!.Trim(), string.IsNullOrWhiteSpace(r.Name) ? null : r.Name))
            .ToList();

    private static IEnumerable<MailAttachment> Files(List<MandrillFile>? files, bool inline) =>
        (files ?? []).Select(f => new MailAttachment
        {
            Filename = f.Name,
            ContentType = f.Type,
            // Images are referenced as cid:<name> from the HTML.
            ContentId = inline ? f.Name : null,
            Inline = inline,
            Content = f.Content ?? "",
        });

    /// <summary>Global merge vars, overridden by the recipient's own vars. Names are case-insensitive.</summary>
    private static Dictionary<string, JsonElement> CollectVars(MandrillMessage m, string? rcpt)
    {
        var vars = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<MandrillVar>? source)
        {
            foreach (var v in source ?? [])
            {
                if (!string.IsNullOrEmpty(v.Name) && v.Content is { } content)
                {
                    vars[v.Name] = content;
                }
            }
        }

        Add(m.GlobalMergeVars);
        if (rcpt != null)
        {
            Add(m.MergeVars?.Where(mv => string.Equals(mv.Rcpt?.Trim(), rcpt.Trim(), StringComparison.OrdinalIgnoreCase)).SelectMany(mv => mv.Vars ?? []));
        }
        return vars;
    }

    private static IEnumerable<KeyValuePair<string, string>> Metadata(MandrillMessage m, string? rcpt)
    {
        foreach (var (key, value) in m.Metadata ?? [])
        {
            yield return new(key, ProviderHelpers.ToText(value));
        }
        if (rcpt == null)
        {
            yield break;
        }
        foreach (var entry in m.RecipientMetadata ?? [])
        {
            if (string.Equals(entry.Rcpt?.Trim(), rcpt.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (key, value) in entry.Values ?? [])
                {
                    yield return new(key, ProviderHelpers.ToText(value));
                }
            }
        }
    }

    /// <summary>
    /// Mailchimp merge tags (<c>*|NAME|*</c>) or simple Handlebars expressions (<c>{{name}}</c>, <c>{{{name}}}</c>,
    /// <c>{{user.name}}</c>). Block helpers and unknown tags are left as they are.
    /// </summary>
    private static string? Merge(string? text, Dictionary<string, JsonElement>? vars, bool handlebars, bool htmlEncode)
    {
        if (vars is null || vars.Count == 0)
        {
            return text;
        }
        if (!handlebars)
        {
            return ProviderHelpers.ReplacePlaceholders(text, "*|", "|*", name => Lookup(vars, name));
        }

        // Triple-stash first: never escaped. Double-stash is HTML-escaped in HTML content, like Handlebars does.
        text = ProviderHelpers.ReplacePlaceholders(text, "{{{", "}}}", name => Lookup(vars, name));
        return ProviderHelpers.ReplacePlaceholders(text, "{{", "}}", name =>
        {
            var value = Lookup(vars, name);
            return value != null && htmlEncode ? WebUtility.HtmlEncode(value) : value;
        });
    }

    private static string? Lookup(Dictionary<string, JsonElement> vars, string path)
    {
        var segments = path.Split('.');
        if (!vars.TryGetValue(segments[0], out var current))
        {
            return null;
        }
        foreach (var segment in segments.Skip(1))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null ? null : ProviderHelpers.ToText(current);
    }

    private static JsonElement? TemplateData(MandrillRequest req, Dictionary<string, JsonElement>? vars)
    {
        if (req.TemplateName is null)
        {
            return null;
        }
        var data = new Dictionary<string, JsonElement>();
        if (req.TemplateContent is { } content)
        {
            data["template_content"] = content;
        }
        if (vars is { Count: > 0 })
        {
            data["merge_vars"] = ProviderHelpers.BuildObject(vars);
        }
        return ProviderHelpers.BuildObject(data);
    }
}
