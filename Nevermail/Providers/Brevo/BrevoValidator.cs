namespace Nevermail.Providers.Brevo;

/// <summary>
/// Mirrors the documented constraints of Brevo's transactional email API. Like Brevo, only the first error is reported.
/// </summary>
public static class BrevoValidator
{
    private const int MaxNameLength = 70;
    private const int MaxRecipientsPerVersion = 99;
    private const int MaxRecipientsPerRequest = 2000;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "xlsx", "xls", "ods", "docx", "docm", "doc", "csv", "pdf", "txt", "gif", "jpg", "jpeg", "png", "tif", "tiff",
        "rtf", "bmp", "cgm", "css", "shtml", "html", "htm", "zip", "xml", "ppt", "pptx", "tar", "ez", "ics", "mobi",
        "msg", "pub", "eps", "odt", "mp3", "m4a", "m4v", "wma", "ogg", "flac", "wav", "aif", "aifc", "aiff", "mp4",
        "mov", "avi", "mkv", "mpeg", "mpg", "wmv", "pkpass", "xlsm",
    };

    public static BrevoError? Validate(BrevoRequest req)
    {
        var hasTemplate = req.TemplateId is not null;

        if (req.Sender is null)
        {
            if (!hasTemplate)
            {
                return Missing("sender is missing");
            }
        }
        else if (req.Sender.Id is null && !EmailValidation.IsValid(req.Sender.Email))
        {
            return Invalid("valid sender email required");
        }
        else if (req.Sender.Name?.Length > MaxNameLength)
        {
            return Invalid($"sender name must be at most {MaxNameLength} characters");
        }

        if (req.MessageVersions is { Count: > 0 } versions)
        {
            var total = 0;
            for (var i = 0; i < versions.Count; i++)
            {
                var v = versions[i] ?? new BrevoMessageVersion();
                if (v.To is null || v.To.Count == 0)
                {
                    return Missing($"messageVersions[{i}].to is missing");
                }
                var count = v.To.Count + (v.Cc?.Count ?? 0) + (v.Bcc?.Count ?? 0);
                if (count > MaxRecipientsPerVersion)
                {
                    return Invalid($"messageVersions[{i}] has more than {MaxRecipientsPerVersion} recipients");
                }
                total += count;
                if ((ValidateAddresses(v.To, $"messageVersions[{i}].to") ?? ValidateAddresses(v.Cc, $"messageVersions[{i}].cc")
                    ?? ValidateAddresses(v.Bcc, $"messageVersions[{i}].bcc") ?? ValidateReplyTo(v.ReplyTo)) is { } error)
                {
                    return error;
                }
                if (v.TemplateId is not null && !hasTemplate)
                {
                    return Invalid("templateId can only be set per messageVersion if a global templateId is set");
                }
            }
            if (total > MaxRecipientsPerRequest)
            {
                return Invalid($"a request can have at most {MaxRecipientsPerRequest} recipients");
            }
        }
        else
        {
            if (req.To is null || req.To.Count == 0)
            {
                return Missing("to is missing");
            }
            if (req.To.Count + (req.Cc?.Count ?? 0) + (req.Bcc?.Count ?? 0) > MaxRecipientsPerRequest)
            {
                return Invalid($"a request can have at most {MaxRecipientsPerRequest} recipients");
            }
            if (ValidateAddresses(req.To, "to") is { } toError)
            {
                return toError;
            }
        }

        // Global cc/bcc also apply to versions that don't set their own.
        if ((ValidateAddresses(req.Cc, "cc") ?? ValidateAddresses(req.Bcc, "bcc")) is { } addressError)
        {
            return addressError;
        }

        if (ValidateReplyTo(req.ReplyTo) is { } replyToError)
        {
            return replyToError;
        }

        if (!hasTemplate)
        {
            var versionsHaveSubject = req.MessageVersions is { Count: > 0 } vs && vs.All(v => !string.IsNullOrEmpty(v?.Subject));
            if (string.IsNullOrEmpty(req.Subject) && !versionsHaveSubject)
            {
                return Missing("subject is missing");
            }
            if (string.IsNullOrEmpty(req.HtmlContent) && string.IsNullOrEmpty(req.TextContent))
            {
                return Missing("htmlContent is missing");
            }
        }

        for (var i = 0; i < req.Attachment?.Count; i++)
        {
            var a = req.Attachment[i] ?? new BrevoAttachment();
            if (string.IsNullOrEmpty(a.Url) && string.IsNullOrEmpty(a.Content))
            {
                return Missing($"attachment[{i}] requires either url or content");
            }
            if (!string.IsNullOrEmpty(a.Url) && !Uri.TryCreate(a.Url, UriKind.Absolute, out _))
            {
                return Invalid($"attachment[{i}].url must be an absolute url");
            }
            if (!string.IsNullOrEmpty(a.Content))
            {
                if (string.IsNullOrWhiteSpace(a.Name))
                {
                    return Missing($"attachment[{i}].name is missing");
                }
                if (!ProviderHelpers.IsBase64(a.Content))
                {
                    return Invalid($"attachment[{i}].content must be base64 encoded");
                }
            }
            var fileName = !string.IsNullOrWhiteSpace(a.Name) ? a.Name : BrevoMapper.FileNameFromUrl(a.Url);
            if (!AllowedExtensions.Contains(Path.GetExtension(fileName ?? "").TrimStart('.')))
            {
                return Invalid($"Unsupported file extension for attachment[{i}]: {fileName}");
            }
        }

        return null;
    }

    private static BrevoError? ValidateAddresses(List<BrevoAddress>? list, string field)
    {
        foreach (var address in list ?? [])
        {
            if (!EmailValidation.IsValid(address?.Email))
            {
                return Invalid($"email is not valid in {field}");
            }
            if (address!.Name?.Length > MaxNameLength)
            {
                return Invalid($"name must be at most {MaxNameLength} characters in {field}");
            }
        }
        return null;
    }

    private static BrevoError? ValidateReplyTo(BrevoAddress? replyTo) =>
        replyTo is not null && !EmailValidation.IsValid(replyTo.Email) ? Invalid("valid replyTo email required") : null;

    private static BrevoError Missing(string message) => new("missing_parameter", message);

    private static BrevoError Invalid(string message) => new("invalid_parameter", message);
}
