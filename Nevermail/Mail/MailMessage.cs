using System.Text.Json;

namespace Nevermail.Mail;

/// <summary>
/// Provider-agnostic representation of a single email as it would have been delivered.
/// Every provider adapter maps its request format onto this model.
/// </summary>
public sealed class MailMessage
{
    public MailAddress? From { get; set; }
    public List<MailAddress> To { get; set; } = [];
    public List<MailAddress> Cc { get; set; } = [];
    public List<MailAddress> Bcc { get; set; } = [];
    public List<MailAddress> ReplyTo { get; set; } = [];
    public string? Subject { get; set; }
    public string? Html { get; set; }
    public string? Text { get; set; }
    public List<MailAttachment> Attachments { get; set; } = [];
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Categories (SendGrid), tags (Brevo, Mandrill).</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Custom args (SendGrid), metadata (Mandrill).</summary>
    public Dictionary<string, string> Metadata { get; set; } = [];

    public string? TemplateId { get; set; }
    public JsonElement? TemplateData { get; set; }
    public DateTimeOffset? SendAt { get; set; }
}

public sealed record MailAddress(string Email, string? Name = null)
{
    public override string ToString() => string.IsNullOrEmpty(Name) ? Email : $"{Name} <{Email}>";
}

public sealed class MailAttachment
{
    public string? Filename { get; set; }
    public string? ContentType { get; set; }
    public string? ContentId { get; set; }
    public bool Inline { get; set; }

    /// <summary>Base64 encoded content.</summary>
    public string Content { get; set; } = "";

    public int Size => Content.Length / 4 * 3 - (Content.EndsWith("==") ? 2 : Content.EndsWith('=') ? 1 : 0);
}
