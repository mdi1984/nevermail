namespace Nevermail.Mail;

public sealed class StoredMail
{
    public string Id { get; set; } = MailIds.New();

    /// <summary>
    /// Id of the API request this mail came from. One request can produce several mails
    /// (e.g. one per SendGrid personalization); they share this id.
    /// </summary>
    public string RequestId { get; set; } = "";

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Provider key, e.g. "sendgrid".</summary>
    public string Provider { get; set; } = "";

    /// <summary>True if the provider was asked not to deliver (e.g. SendGrid sandbox mode).</summary>
    public bool Sandbox { get; set; }

    public MailMessage Message { get; set; } = new();

    /// <summary>The request body exactly as received.</summary>
    public string? RawRequest { get; set; }
}

/// <summary>Lightweight projection for list views.</summary>
public sealed record MailSummary(
    string Id,
    string RequestId,
    DateTimeOffset ReceivedAt,
    string Provider,
    bool Sandbox,
    MailAddress? From,
    IReadOnlyList<MailAddress> To,
    int RecipientCount,
    string? Subject,
    bool HasHtml,
    int AttachmentCount)
{
    public static MailSummary Create(StoredMail mail)
    {
        var m = mail.Message;
        return new MailSummary(
            mail.Id,
            mail.RequestId,
            mail.ReceivedAt,
            mail.Provider,
            mail.Sandbox,
            m.From,
            m.To.Take(3).ToList(),
            m.To.Count + m.Cc.Count + m.Bcc.Count,
            m.Subject,
            !string.IsNullOrEmpty(m.Html),
            m.Attachments.Count);
    }
}

public static class MailIds
{
    private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    // Random.Shared is enough for mock ids, and avoids System.Security.Cryptography
    // which needs libssl in the scratch image.
    public static string New(int length = 22) => string.Create(length, 0, static (span, _) =>
    {
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = Chars[Random.Shared.Next(Chars.Length)];
        }
    });
}
