using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nevermail.Providers.AhaSend;

// Request/response shapes of AhaSend's API v2 "create message" endpoint.
// https://ahasend.com/docs/api-reference/messages/create-message

public sealed class AhaSendRequest
{
    [JsonPropertyName("from")] public AhaSendAddress? From { get; set; }
    [JsonPropertyName("recipients")] public List<AhaSendRecipient>? Recipients { get; set; }
    [JsonPropertyName("reply_to")] public AhaSendAddress? ReplyTo { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("text_content")] public string? TextContent { get; set; }
    [JsonPropertyName("html_content")] public string? HtmlContent { get; set; }
    [JsonPropertyName("amp_content")] public string? AmpContent { get; set; }
    [JsonPropertyName("attachments")] public List<AhaSendAttachment>? Attachments { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; set; }
    [JsonPropertyName("substitutions")] public JsonElement? Substitutions { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("sandbox")] public bool? Sandbox { get; set; }
    [JsonPropertyName("sandbox_result")] public string? SandboxResult { get; set; }
    [JsonPropertyName("tracking")] public AhaSendTracking? Tracking { get; set; }
    [JsonPropertyName("retention")] public AhaSendRetention? Retention { get; set; }
    [JsonPropertyName("schedule")] public AhaSendSchedule? Schedule { get; set; }
}

public sealed class AhaSendAddress
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class AhaSendRecipient
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("substitutions")] public JsonElement? Substitutions { get; set; }
}

public sealed class AhaSendAttachment
{
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("base64")] public bool? Base64 { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("content_disposition")] public string? ContentDisposition { get; set; }
    [JsonPropertyName("content_id")] public string? ContentId { get; set; }
}

public sealed class AhaSendTracking
{
    [JsonPropertyName("open")] public bool? Open { get; set; }
    [JsonPropertyName("click")] public bool? Click { get; set; }
}

public sealed class AhaSendRetention
{
    [JsonPropertyName("metadata")] public int? Metadata { get; set; }
    [JsonPropertyName("data")] public int? Data { get; set; }
}

public sealed class AhaSendSchedule
{
    [JsonPropertyName("first_attempt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? FirstAttempt { get; set; }

    [JsonPropertyName("expires"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Expires { get; set; }
}

public sealed record AhaSendError([property: JsonPropertyName("message")] string Message);

public sealed record AhaSendResponseRecipient(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonPropertyName("substitutions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Substitutions);

public sealed record AhaSendMessage(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("recipient")] AhaSendResponseRecipient Recipient,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("schedule"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AhaSendSchedule? Schedule)
{
    [JsonPropertyName("object")] public string Object => "message";
}

public sealed record AhaSendMessageList([property: JsonPropertyName("data")] IReadOnlyList<AhaSendMessage> Data)
{
    [JsonPropertyName("object")] public string Object => "list";
}

[JsonSerializable(typeof(AhaSendRequest))]
[JsonSerializable(typeof(AhaSendError))]
[JsonSerializable(typeof(AhaSendMessageList))]
internal sealed partial class AhaSendJsonContext : JsonSerializerContext;
