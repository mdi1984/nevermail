using Nevermail.Mail;

namespace Nevermail.Providers.AzureEmail;

/// <summary>
/// Maps a validated Azure Communication Services request to the delivered mail. ACS sends one message to all
/// recipients, so a request is always exactly one mail; its request id is the operation id.
/// </summary>
public static class AzureEmailMapper
{
    public static StoredMail Map(AzureEmailRequest req, string rawRequest, string operationId, DateTimeOffset receivedAt)
    {
        var message = new MailMessage
        {
            From = new MailAddress(req.SenderAddress!.Trim()),
            To = MapAddresses(req.Recipients!.To),
            Cc = MapAddresses(req.Recipients.Cc),
            Bcc = MapAddresses(req.Recipients.Bcc),
            ReplyTo = MapAddresses(req.ReplyTo),
            Subject = req.Content!.Subject,
            Html = NullIfEmpty(req.Content.Html),
            Text = NullIfEmpty(req.Content.PlainText),
            Attachments = (req.Attachments ?? []).Select(a => new MailAttachment
            {
                Filename = a.Name,
                ContentType = a.ContentType,
                ContentId = NullIfEmpty(a.ContentId),
                Inline = !string.IsNullOrEmpty(a.ContentId),
                Content = a.ContentInBase64!,
            }).ToList(),
        };
        foreach (var (name, value) in req.Headers ?? [])
        {
            message.Headers[name] = value;
        }
        if (req.UserEngagementTrackingDisabled is { } trackingDisabled)
        {
            message.Metadata["userEngagementTrackingDisabled"] = trackingDisabled ? "true" : "false";
        }

        return new StoredMail
        {
            RequestId = operationId,
            ReceivedAt = receivedAt,
            Provider = AzureEmailEndpoints.ProviderKey,
            Message = message,
            RawRequest = rawRequest,
        };
    }

    private static List<MailAddress> MapAddresses(List<AzureEmailAddress>? list) =>
        list?.Select(a => new MailAddress(a.Address!.Trim(), NullIfEmpty(a.DisplayName))).ToList() ?? [];

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
