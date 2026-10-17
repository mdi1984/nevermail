using System.Text.Json;

namespace Nevermail.Providers.AhaSend;

/// <summary>
/// Mirrors the documented constraints of AhaSend's create message endpoint. Like AhaSend, only the first error is reported.
/// AhaSend says its error messages may change and shouldn't be parsed, so these are descriptive rather than verbatim.
/// </summary>
public static class AhaSendValidator
{
    public const int MaxRecipients = 100;
    private const int MaxIdempotencyKeyLength = 255;

    private static readonly HashSet<string> SandboxResults = new(StringComparer.Ordinal) { "deliver", "bounce", "defer", "fail", "suppress" };

    public static string? ValidateIdempotencyKey(string key) =>
        key.Length > MaxIdempotencyKeyLength ? $"Idempotency-Key must be at most {MaxIdempotencyKeyLength} characters" : null;

    public static AhaSendError? Validate(AhaSendRequest req, DateTimeOffset now)
    {
        if (req.From is null || string.IsNullOrWhiteSpace(req.From.Email))
        {
            return Error("from.email is required");
        }
        if (!EmailValidation.IsValid(req.From.Email))
        {
            return Error("from.email is not a valid email address");
        }

        if (req.Recipients is null || req.Recipients.Count == 0)
        {
            return Error("recipients must contain at least 1 item");
        }
        if (req.Recipients.Count > MaxRecipients)
        {
            return Error($"recipients must contain at most {MaxRecipients} items");
        }
        for (var i = 0; i < req.Recipients.Count; i++)
        {
            var recipient = req.Recipients[i];
            if (!EmailValidation.IsValid(recipient?.Email))
            {
                return Error($"recipients[{i}].email is not a valid email address");
            }
            if (recipient!.Substitutions is { } s && s.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            {
                return Error($"recipients[{i}].substitutions must be an object");
            }
        }

        if (req.ReplyTo is not null && !EmailValidation.IsValid(req.ReplyTo.Email))
        {
            return Error("reply_to.email is not a valid email address");
        }

        if (string.IsNullOrEmpty(req.Subject))
        {
            return Error("subject is required");
        }
        if (string.IsNullOrEmpty(req.TextContent) && string.IsNullOrEmpty(req.HtmlContent))
        {
            return Error("either text_content or html_content is required");
        }

        if (req.Substitutions is { } global && global.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            return Error("substitutions must be an object");
        }
        if (ValidateSubstitutionValues(req.Substitutions, "substitutions") is { } globalError)
        {
            return globalError;
        }
        for (var i = 0; i < req.Recipients.Count; i++)
        {
            if (ValidateSubstitutionValues(req.Recipients[i].Substitutions, $"recipients[{i}].substitutions") is { } recipientError)
            {
                return recipientError;
            }
        }

        if (ValidateHeaders(req) is { } headerError)
        {
            return headerError;
        }

        for (var i = 0; i < req.Attachments?.Count; i++)
        {
            var a = req.Attachments[i] ?? new AhaSendAttachment();
            if (string.IsNullOrEmpty(a.Data))
            {
                return Error($"attachments[{i}].data is required");
            }
            if (string.IsNullOrWhiteSpace(a.ContentType))
            {
                return Error($"attachments[{i}].content_type is required");
            }
            if (string.IsNullOrWhiteSpace(a.FileName))
            {
                return Error($"attachments[{i}].file_name is required");
            }
            if (a.Base64 == true && !ProviderHelpers.IsBase64(a.Data))
            {
                return Error($"attachments[{i}].data is not valid base64");
            }
        }

        if (req.SandboxResult is { } result && !SandboxResults.Contains(result))
        {
            return Error("sandbox_result must be one of deliver, bounce, defer, fail, suppress");
        }

        if (req.Retention is { } retention)
        {
            if (retention.Metadata is < 1 or > 30)
            {
                return Error("retention.metadata must be between 1 and 30 days");
            }
            if (retention.Data is < 0 or > 30)
            {
                return Error("retention.data must be between 0 and 30 days");
            }
        }

        if (req.Schedule is { } schedule)
        {
            if (schedule.FirstAttempt is { } first && (first <= now || first > now.AddDays(7)))
            {
                return Error("schedule.first_attempt must be in the future and within 7 days");
            }
            if (schedule.Expires is { } expires)
            {
                if (expires <= now || expires > now.AddDays(8))
                {
                    return Error("schedule.expires must be in the future and within 8 days");
                }
                if (schedule.FirstAttempt is { } start && expires <= start)
                {
                    return Error("schedule.expires must be after schedule.first_attempt");
                }
            }
        }

        return null;
    }

    private static AhaSendError? ValidateHeaders(AhaSendRequest req)
    {
        foreach (var (name, value) in req.Headers ?? [])
        {
            if (name.Length == 0 || name.Any(ch => ch is <= ' ' or > '~' or ':'))
            {
                return Error($"invalid header name \"{name}\"");
            }
            if (value is null || value.AsSpan().ContainsAny('\r', '\n'))
            {
                return Error($"header \"{name}\" must be a string without line breaks");
            }
            if (req.ReplyTo is not null && name.Equals("reply-to", StringComparison.OrdinalIgnoreCase))
            {
                return Error("reply-to header cannot be set together with reply_to");
            }
        }
        return null;
    }

    /// <summary>Substitution values may not contain template syntax themselves.</summary>
    private static AhaSendError? ValidateSubstitutionValues(JsonElement? substitutions, string field)
    {
        if (substitutions is not { ValueKind: JsonValueKind.Object } root)
        {
            return null;
        }
        foreach (var property in root.EnumerateObject())
        {
            var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
            if (text.Contains("{{", StringComparison.Ordinal) || text.Contains("{%", StringComparison.Ordinal) || text.Contains("{#", StringComparison.Ordinal))
            {
                return Error($"{field}.{property.Name} must not contain template syntax");
            }
        }
        return null;
    }

    private static AhaSendError Error(string message) => new(message);
}
