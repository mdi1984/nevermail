using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nevermail.Providers.SendGrid;

// Request/response shapes of the SendGrid v3 Mail Send API.
// https://www.twilio.com/docs/sendgrid/api-reference/mail-send/mail-send

public sealed class SendGridRequest
{
    [JsonPropertyName("from")] public SendGridAddress? From { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("personalizations")] public List<SendGridPersonalization>? Personalizations { get; set; }
    [JsonPropertyName("content")] public List<SendGridContent>? Content { get; set; }
    [JsonPropertyName("attachments")] public List<SendGridAttachment>? Attachments { get; set; }
    [JsonPropertyName("template_id")] public string? TemplateId { get; set; }
    [JsonPropertyName("reply_to")] public SendGridAddress? ReplyTo { get; set; }
    [JsonPropertyName("reply_to_list")] public List<SendGridAddress>? ReplyToList { get; set; }
    [JsonPropertyName("send_at")] public long? SendAt { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; set; }
    [JsonPropertyName("custom_args")] public Dictionary<string, string>? CustomArgs { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("batch_id")] public string? BatchId { get; set; }
    [JsonPropertyName("ip_pool_name")] public string? IpPoolName { get; set; }
    [JsonPropertyName("asm")] public JsonElement? Asm { get; set; }
    [JsonPropertyName("mail_settings")] public SendGridMailSettings? MailSettings { get; set; }
    [JsonPropertyName("tracking_settings")] public JsonElement? TrackingSettings { get; set; }
}

public sealed class SendGridAddress
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class SendGridPersonalization
{
    [JsonPropertyName("from")] public SendGridAddress? From { get; set; }
    [JsonPropertyName("to")] public List<SendGridAddress>? To { get; set; }
    [JsonPropertyName("cc")] public List<SendGridAddress>? Cc { get; set; }
    [JsonPropertyName("bcc")] public List<SendGridAddress>? Bcc { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; set; }
    [JsonPropertyName("substitutions")] public Dictionary<string, string>? Substitutions { get; set; }
    [JsonPropertyName("dynamic_template_data")] public JsonElement? DynamicTemplateData { get; set; }
    [JsonPropertyName("custom_args")] public Dictionary<string, string>? CustomArgs { get; set; }
    [JsonPropertyName("send_at")] public long? SendAt { get; set; }
}

public sealed class SendGridContent
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

public sealed class SendGridAttachment
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("disposition")] public string? Disposition { get; set; }
    [JsonPropertyName("content_id")] public string? ContentId { get; set; }
}

public sealed class SendGridMailSettings
{
    [JsonPropertyName("sandbox_mode")] public SendGridToggle? SandboxMode { get; set; }
}

public sealed class SendGridToggle
{
    [JsonPropertyName("enable")] public bool? Enable { get; set; }
}

public sealed record SendGridError(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("field")] string? Field = null,
    [property: JsonPropertyName("help")] string? Help = null);

public sealed record SendGridErrorResponse(
    [property: JsonPropertyName("errors")] IReadOnlyList<SendGridError> Errors);

public sealed record SendGridScopesResponse(
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes);

public sealed record SendGridBatchResponse(
    [property: JsonPropertyName("batch_id")] string BatchId);

[JsonSerializable(typeof(SendGridRequest))]
[JsonSerializable(typeof(SendGridErrorResponse))]
[JsonSerializable(typeof(SendGridScopesResponse))]
[JsonSerializable(typeof(SendGridBatchResponse))]
internal sealed partial class SendGridJsonContext : JsonSerializerContext;
