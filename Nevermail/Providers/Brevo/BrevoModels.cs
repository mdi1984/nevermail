using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nevermail.Providers.Brevo;

// Request/response shapes of Brevo's (formerly Sendinblue) transactional email API.
// https://developers.brevo.com/reference/sendtransacemail

public sealed class BrevoRequest
{
    [JsonPropertyName("sender")] public BrevoAddress? Sender { get; set; }
    [JsonPropertyName("to")] public List<BrevoAddress>? To { get; set; }
    [JsonPropertyName("cc")] public List<BrevoAddress>? Cc { get; set; }
    [JsonPropertyName("bcc")] public List<BrevoAddress>? Bcc { get; set; }
    [JsonPropertyName("replyTo")] public BrevoAddress? ReplyTo { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("htmlContent")] public string? HtmlContent { get; set; }
    [JsonPropertyName("textContent")] public string? TextContent { get; set; }
    [JsonPropertyName("attachment")] public List<BrevoAttachment>? Attachment { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, JsonElement>? Headers { get; set; }
    [JsonPropertyName("templateId")] public long? TemplateId { get; set; }
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
    [JsonPropertyName("messageVersions")] public List<BrevoMessageVersion>? MessageVersions { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("scheduledAt")] public DateTimeOffset? ScheduledAt { get; set; }
    [JsonPropertyName("batchId")] public string? BatchId { get; set; }
}

public sealed class BrevoAddress
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("id")] public long? Id { get; set; }
}

public sealed class BrevoAttachment
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class BrevoMessageVersion
{
    [JsonPropertyName("to")] public List<BrevoAddress>? To { get; set; }
    [JsonPropertyName("cc")] public List<BrevoAddress>? Cc { get; set; }
    [JsonPropertyName("bcc")] public List<BrevoAddress>? Bcc { get; set; }
    [JsonPropertyName("replyTo")] public BrevoAddress? ReplyTo { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("htmlContent")] public string? HtmlContent { get; set; }
    [JsonPropertyName("textContent")] public string? TextContent { get; set; }
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
    [JsonPropertyName("templateId")] public long? TemplateId { get; set; }
}

public sealed record BrevoError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record BrevoSendResponse(
    [property: JsonPropertyName("messageId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MessageId,
    [property: JsonPropertyName("messageIds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? MessageIds);

[JsonSerializable(typeof(BrevoRequest))]
[JsonSerializable(typeof(BrevoError))]
[JsonSerializable(typeof(BrevoSendResponse))]
internal sealed partial class BrevoJsonContext : JsonSerializerContext;
