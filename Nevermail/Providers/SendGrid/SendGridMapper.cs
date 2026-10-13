using Nevermail.Mail;

namespace Nevermail.Providers.SendGrid;

/// <summary>
/// Maps a validated SendGrid request to the mails SendGrid would deliver:
/// one mail per personalization, with personalization-level overrides and substitutions applied.
/// </summary>
public static class SendGridMapper
{
    public static List<StoredMail> Map(SendGridRequest req, string rawRequest, string requestId, bool sandbox, DateTimeOffset receivedAt)
    {
        var html = req.Content?.FirstOrDefault(c => IsType(c, "text/html"))?.Value;
        var text = req.Content?.FirstOrDefault(c => IsType(c, "text/plain"))?.Value;
        var attachments = (req.Attachments ?? []).Select(MapAttachment).ToList();
        var replyTo = req.ReplyToList is { Count: > 0 } list
            ? list.Select(MapAddress).ToList()
            : req.ReplyTo is { } r ? [MapAddress(r)] : [];

        var mails = new List<StoredMail>();
        foreach (var p in req.Personalizations!)
        {
            var subs = p.Substitutions ?? [];
            var message = new MailMessage
            {
                From = MapAddress(p.From ?? req.From!),
                To = MapAddresses(p.To),
                Cc = MapAddresses(p.Cc),
                Bcc = MapAddresses(p.Bcc),
                ReplyTo = replyTo,
                Subject = Substitute(p.Subject ?? req.Subject, subs),
                Html = Substitute(html, subs),
                Text = Substitute(text, subs),
                Attachments = attachments,
                Tags = req.Categories ?? [],
                TemplateId = req.TemplateId,
                TemplateData = p.DynamicTemplateData,
                SendAt = (p.SendAt ?? req.SendAt) is { } sendAt ? DateTimeOffset.FromUnixTimeSeconds(sendAt) : null,
            };
            Merge(message.Headers, req.Headers, p.Headers);
            Merge(message.Metadata, req.CustomArgs, p.CustomArgs);

            mails.Add(new StoredMail
            {
                RequestId = requestId,
                ReceivedAt = receivedAt,
                Provider = SendGridEndpoints.ProviderKey,
                Sandbox = sandbox,
                Message = message,
                RawRequest = rawRequest,
            });
        }
        return mails;
    }

    private static bool IsType(SendGridContent c, string type) => string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase);

    private static MailAddress MapAddress(SendGridAddress a) => new(a.Email!.Trim(), string.IsNullOrWhiteSpace(a.Name) ? null : a.Name);

    private static List<MailAddress> MapAddresses(List<SendGridAddress>? list) => list?.Select(MapAddress).ToList() ?? [];

    private static MailAttachment MapAttachment(SendGridAttachment a) => new()
    {
        Filename = a.Filename,
        ContentType = a.Type,
        ContentId = a.ContentId,
        Inline = string.Equals(a.Disposition, "inline", StringComparison.OrdinalIgnoreCase),
        Content = a.Content ?? "",
    };

    // Legacy substitutions: plain string replacement of each key (e.g. "-name-") in subject and body.
    private static string? Substitute(string? value, Dictionary<string, string> subs)
    {
        if (value is null || subs.Count == 0)
        {
            return value;
        }
        foreach (var (key, replacement) in subs)
        {
            if (!string.IsNullOrEmpty(key))
            {
                value = value.Replace(key, replacement);
            }
        }
        return value;
    }

    private static void Merge(Dictionary<string, string> target, params Dictionary<string, string>?[] sources)
    {
        foreach (var source in sources)
        {
            foreach (var (key, value) in source ?? [])
            {
                target[key] = value;
            }
        }
    }
}
