namespace Nevermail.Providers.AzureEmail;

/// <summary>
/// Mirrors the request validation of Azure Communication Services Email. Like the real service, only the first error
/// is reported, as "... validation error. See property '&lt;path&gt;'": "Request body" for missing or malformed values,
/// "Email body" for values that are well-formed but not acceptable (e.g. an invalid address).
/// </summary>
public static class AzureEmailValidator
{
    public const int MaxRecipients = 50;

    // https://learn.microsoft.com/azure/communication-services/concepts/email/email-attachment-allowed-mime-types
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/3gpp", "video/3gpp2", "application/x-7z-compressed", "audio/aac", "video/x-msvideo", "image/bmp", "text/csv",
        "application/msword", "application/vnd.ms-word.document.macroEnabled.12",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.ms-fontobject",
        "application/epub+zip", "image/gif", "application/gzip", "image/vnd.microsoft.icon", "text/calendar", "image/jpeg",
        "application/json", "audio/midi", "audio/mpeg", "video/mp4", "video/mpeg", "audio/ogg", "video/ogg", "application/ogg",
        "application/onenote", "audio/opus", "font/otf", "application/pdf", "image/png",
        "application/vnd.ms-powerpoint.slideshow.macroEnabled.12",
        "application/vnd.openxmlformats-officedocument.presentationml.slideshow", "application/vnd.ms-powerpoint",
        "application/vnd.ms-powerpoint.presentation.macroEnabled.12",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation", "application/vnd.ms-publisher",
        "application/x-rar-compressed", "application/vnd.ms-outlook", "application/rtf", "image/svg+xml", "application/x-tar",
        "image/tiff", "font/ttf", "text/plain", "application/vnd.visio", "audio/wav", "audio/webm", "video/webm", "image/webp",
        "audio/x-ms-wma", "video/x-ms-wmv", "font/woff", "font/woff2", "application/vnd.ms-excel",
        "application/vnd.ms-excel.sheet.binary.macroEnabled.12", "application/vnd.ms-excel.sheet.macroEnabled.12",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/xml", "text/xml", "application/zip",
    };

    public static AzureEmailErrorDetail? Validate(AzureEmailRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SenderAddress))
        {
            return Missing("senderAddress");
        }
        // The sender is a bare address; display names ("Shop <shop@example.com>") are rejected too.
        if (!EmailValidation.IsValid(req.SenderAddress))
        {
            return Invalid("senderAddress");
        }

        if (req.Content is null)
        {
            return Missing("content");
        }
        if (string.IsNullOrEmpty(req.Content.Subject))
        {
            return Missing("content.subject");
        }
        if (string.IsNullOrEmpty(req.Content.PlainText) && string.IsNullOrEmpty(req.Content.Html))
        {
            return Missing("content");
        }

        if (req.Recipients is null)
        {
            return Missing("recipients");
        }
        var count = 0;
        foreach (var (list, field) in new[] { (req.Recipients.To, "recipients.to"), (req.Recipients.Cc, "recipients.cc"), (req.Recipients.Bcc, "recipients.bcc") })
        {
            if (ValidateAddresses(list, field) is { } error)
            {
                return error;
            }
            count += list?.Count ?? 0;
        }
        if (count == 0)
        {
            return Missing("recipients");
        }
        if (count > MaxRecipients)
        {
            return new("BadRequest", $"Email body validation error. See property 'recipients'. A message can have at most {MaxRecipients} recipients.", "recipients");
        }

        if (ValidateAddresses(req.ReplyTo, "replyTo") is { } replyToError)
        {
            return replyToError;
        }

        foreach (var (name, value) in req.Headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(name) || value is null)
            {
                return Missing($"headers.{name}");
            }
        }

        for (var i = 0; i < req.Attachments?.Count; i++)
        {
            var a = req.Attachments[i];
            var field = $"attachments[{i}]";
            if (a is null)
            {
                return Missing(field);
            }
            if (string.IsNullOrWhiteSpace(a.Name))
            {
                return Missing($"{field}.name");
            }
            if (string.IsNullOrWhiteSpace(a.ContentType) || !AllowedContentTypes.Contains(MediaType(a.ContentType)))
            {
                return Missing($"{field}.contentType");
            }
            if (string.IsNullOrEmpty(a.ContentInBase64) || !ProviderHelpers.IsBase64(a.ContentInBase64))
            {
                return Missing($"{field}.contentInBase64");
            }
        }

        return null;
    }

    private static AzureEmailErrorDetail? ValidateAddresses(List<AzureEmailAddress>? list, string field)
    {
        for (var i = 0; i < list?.Count; i++)
        {
            var address = list[i];
            if (address is null || string.IsNullOrWhiteSpace(address.Address))
            {
                return Missing($"{field}[{i}].address");
            }
            if (!EmailValidation.IsValid(address.Address))
            {
                return Invalid($"{field}[{i}].address");
            }
        }
        return null;
    }

    /// <summary>"text/plain; charset=utf-8" → "text/plain".</summary>
    private static string MediaType(string contentType)
    {
        var semicolon = contentType.IndexOf(';');
        return (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
    }

    /// <summary>A required value is missing or malformed.</summary>
    public static AzureEmailErrorDetail Missing(string field) =>
        new("BadRequest", $"Request body validation error. See property '{field}'", field);

    /// <summary>A value is well-formed but not acceptable.</summary>
    private static AzureEmailErrorDetail Invalid(string field) =>
        new("BadRequest", $"Email body validation error. See property '{field}'", field);
}
