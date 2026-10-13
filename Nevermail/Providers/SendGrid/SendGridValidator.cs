namespace Nevermail.Providers.SendGrid;

/// <summary>
/// Mirrors the validation rules of the SendGrid Mail Send API, so that requests the real API would
/// reject are rejected here too. All errors are collected, like SendGrid does.
/// https://www.twilio.com/docs/sendgrid/api-reference/mail-send/errors
/// </summary>
public static class SendGridValidator
{
    private const int MaxPersonalizations = 1000;
    private const int MaxRecipientsPerPersonalization = 1000;
    private const int MaxCategories = 10;
    private const int MaxCategoryLength = 255;
    private static readonly TimeSpan MaxSendAtAhead = TimeSpan.FromHours(72);

    public static List<SendGridError> Validate(SendGridRequest req, TimeProvider time)
    {
        var errors = new List<SendGridError>();
        var hasTemplate = !string.IsNullOrWhiteSpace(req.TemplateId);

        if (req.From is null || string.IsNullOrWhiteSpace(req.From.Email))
        {
            errors.Add(new("The from object must be provided for every email send. It is an object that requires the email parameter, but may also contain a name parameter.", "from"));
        }
        else if (!EmailValidation.IsValid(req.From.Email))
        {
            errors.Add(new("The from email does not contain a valid address.", "from.email"));
        }

        ValidatePersonalizations(req, time, errors);
        ValidateSubject(req, hasTemplate, errors);
        ValidateContent(req, hasTemplate, errors);
        ValidateAttachments(req, errors);

        if (req.ReplyTo is { } replyTo && !EmailValidation.IsValid(replyTo.Email))
        {
            errors.Add(new("The reply_to email does not contain a valid address.", "reply_to.email"));
        }
        if (req.ReplyTo is not null && req.ReplyToList is { Count: > 0 })
        {
            errors.Add(new("The reply_to and reply_to_list fields are mutually exclusive.", "reply_to_list"));
        }
        for (var i = 0; i < req.ReplyToList?.Count; i++)
        {
            if (!EmailValidation.IsValid(req.ReplyToList[i]?.Email))
            {
                errors.Add(new("The reply_to_list email does not contain a valid address.", $"reply_to_list.{i}.email"));
            }
        }

        ValidateSendAt(req.SendAt, "send_at", time, errors);

        if (req.Categories is { } categories)
        {
            if (categories.Count > MaxCategories)
            {
                errors.Add(new($"Too many categories. You may only use up to {MaxCategories} categories per request.", "categories"));
            }
            for (var i = 0; i < categories.Count; i++)
            {
                if (categories[i] is null || categories[i].Length > MaxCategoryLength)
                {
                    errors.Add(new($"A category must be a string with at most {MaxCategoryLength} characters.", $"categories.{i}"));
                }
            }
        }

        return errors;
    }

    private static void ValidatePersonalizations(SendGridRequest req, TimeProvider time, List<SendGridError> errors)
    {
        var personalizations = req.Personalizations;
        if (personalizations is null || personalizations.Count == 0)
        {
            errors.Add(new("The personalizations field is required and must have at least one personalization.", "personalizations"));
            return;
        }
        if (personalizations.Count > MaxPersonalizations)
        {
            errors.Add(new($"The personalizations field is limited to {MaxPersonalizations} personalizations per API request.", "personalizations"));
        }

        for (var i = 0; i < personalizations.Count; i++)
        {
            var p = personalizations[i] ?? new SendGridPersonalization();
            var prefix = $"personalizations.{i}";

            if (p.To is null || p.To.Count == 0)
            {
                errors.Add(new("The to array is required for all personalization objects, and must have at least one email object with a valid email address.", $"{prefix}.to"));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            foreach (var (kind, list) in new[] { ("to", p.To), ("cc", p.Cc), ("bcc", p.Bcc) })
            {
                for (var j = 0; j < list?.Count; j++)
                {
                    total++;
                    var email = list[j]?.Email;
                    if (!EmailValidation.IsValid(email))
                    {
                        errors.Add(new("Does not contain a valid address.", $"{prefix}.{kind}.{j}.email"));
                    }
                    else if (!seen.Add(email!.Trim()))
                    {
                        errors.Add(new($"Each email address in the personalization block should be unique between to, cc, and bcc. We found the first duplicate instance of [{email}] in the {prefix}.{kind} field.", $"{prefix}.{kind}"));
                    }
                }
            }
            if (total > MaxRecipientsPerPersonalization)
            {
                errors.Add(new($"The total number of recipients (to, cc, bcc) in a personalization must not exceed {MaxRecipientsPerPersonalization}.", prefix));
            }

            if (p.From is { } from && !EmailValidation.IsValid(from.Email))
            {
                errors.Add(new("The from email does not contain a valid address.", $"{prefix}.from.email"));
            }
            ValidateSendAt(p.SendAt, $"{prefix}.send_at", time, errors);
        }
    }

    private static void ValidateSubject(SendGridRequest req, bool hasTemplate, List<SendGridError> errors)
    {
        if (hasTemplate || !string.IsNullOrEmpty(req.Subject))
        {
            return;
        }
        var everyPersonalizationHasSubject = req.Personalizations is { Count: > 0 } ps
            && ps.All(p => !string.IsNullOrEmpty(p.Subject));
        if (!everyPersonalizationHasSubject)
        {
            errors.Add(new("The subject is required. You can get around this requirement if you use a template with a subject defined or if every personalization has a subject defined.", "subject"));
        }
    }

    private static void ValidateContent(SendGridRequest req, bool hasTemplate, List<SendGridError> errors)
    {
        var content = req.Content;
        if (content is null || content.Count == 0)
        {
            if (!hasTemplate)
            {
                errors.Add(new("Unless a valid template_id is provided, the content parameter is required. There must be at least one defined content block. We typically suggest both text/plain and text/html blocks are included, but only one block is required.", "content"));
            }
            return;
        }

        var lastRank = 0;
        var orderReported = false;
        for (var i = 0; i < content.Count; i++)
        {
            var c = content[i] ?? new SendGridContent();
            if (string.IsNullOrWhiteSpace(c.Type) || c.Type.Contains(';') || c.Type.Contains('\r') || c.Type.Contains('\n'))
            {
                errors.Add(new("The content type cannot contain ';', or CRLF characters and must be at least one character.", $"content.{i}.type"));
            }
            if (string.IsNullOrEmpty(c.Value))
            {
                errors.Add(new("The content value must be a string at least one character in length.", $"content.{i}.value"));
            }

            var rank = c.Type?.ToLowerInvariant() switch
            {
                "text/plain" => 0,
                "text/html" => 1,
                _ => 2,
            };
            if (rank < lastRank && !orderReported)
            {
                errors.Add(new("If present, text/plain and text/html may only be provided in that order.", "content"));
                orderReported = true;
            }
            lastRank = Math.Max(lastRank, rank);
        }
    }

    private static void ValidateAttachments(SendGridRequest req, List<SendGridError> errors)
    {
        for (var i = 0; i < req.Attachments?.Count; i++)
        {
            var a = req.Attachments[i] ?? new SendGridAttachment();
            if (string.IsNullOrWhiteSpace(a.Filename))
            {
                errors.Add(new("The attachment filename parameter is required.", $"attachments.{i}.filename"));
            }
            if (string.IsNullOrEmpty(a.Content))
            {
                errors.Add(new("The attachment content is required.", $"attachments.{i}.content"));
            }
            else if (!ProviderHelpers.IsBase64(a.Content))
            {
                errors.Add(new("The attachment content must be base64 encoded.", $"attachments.{i}.content"));
            }
            if (string.Equals(a.Disposition, "inline", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(a.ContentId))
            {
                errors.Add(new("The content_id parameter is required if the attachment disposition is inline.", $"attachments.{i}.content_id"));
            }
            if (a.Disposition is { } d && !d.Equals("inline", StringComparison.OrdinalIgnoreCase) && !d.Equals("attachment", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new("The disposition must be either inline or attachment.", $"attachments.{i}.disposition"));
            }
        }
    }

    private static void ValidateSendAt(long? sendAt, string field, TimeProvider time, List<SendGridError> errors)
    {
        if (sendAt is { } value && (value < 0 || value > (time.GetUtcNow() + MaxSendAtAhead).ToUnixTimeSeconds()))
        {
            errors.Add(new("The send_at parameter must be a unix timestamp, at most 72 hours in the future.", field));
        }
    }
}
