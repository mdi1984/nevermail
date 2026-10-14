using System.Text.Json.Serialization;

namespace Nevermail.Providers.AzureEmail;

// Request/response shapes of the Azure Communication Services Email API.
// https://learn.microsoft.com/rest/api/communication/email/email/send

public sealed class AzureEmailRequest
{
    [JsonPropertyName("senderAddress")] public string? SenderAddress { get; set; }
    [JsonPropertyName("recipients")] public AzureEmailRecipients? Recipients { get; set; }
    [JsonPropertyName("content")] public AzureEmailContent? Content { get; set; }
    [JsonPropertyName("attachments")] public List<AzureEmailAttachment>? Attachments { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; set; }
    [JsonPropertyName("replyTo")] public List<AzureEmailAddress>? ReplyTo { get; set; }
    [JsonPropertyName("userEngagementTrackingDisabled")] public bool? UserEngagementTrackingDisabled { get; set; }
}

public sealed class AzureEmailRecipients
{
    [JsonPropertyName("to")] public List<AzureEmailAddress>? To { get; set; }
    [JsonPropertyName("cc")] public List<AzureEmailAddress>? Cc { get; set; }
    [JsonPropertyName("bcc")] public List<AzureEmailAddress>? Bcc { get; set; }
}

public sealed class AzureEmailAddress
{
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

public sealed class AzureEmailContent
{
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("plainText")] public string? PlainText { get; set; }
    [JsonPropertyName("html")] public string? Html { get; set; }
}

public sealed class AzureEmailAttachment
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    [JsonPropertyName("contentInBase64")] public string? ContentInBase64 { get; set; }
    [JsonPropertyName("contentId")] public string? ContentId { get; set; }
}

/// <summary>Status of the long running send operation.</summary>
public sealed record AzureEmailSendResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error")] AzureEmailErrorDetail? Error);

public sealed record AzureEmailErrorResponse(
    [property: JsonPropertyName("error")] AzureEmailErrorDetail Error);

public sealed record AzureEmailErrorDetail(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("target")] string? Target = null);

[JsonSerializable(typeof(AzureEmailRequest))]
[JsonSerializable(typeof(AzureEmailSendResult))]
[JsonSerializable(typeof(AzureEmailErrorResponse))]
internal sealed partial class AzureEmailJsonContext : JsonSerializerContext;
