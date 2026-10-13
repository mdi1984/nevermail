using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nevermail.Providers.Mandrill;

// Request/response shapes of Mailchimp Transactional (Mandrill).
// https://mailchimp.com/developer/transactional/api/messages/send/

/// <summary>Body of messages/send and messages/send-template (the template fields are only used by the latter).</summary>
public sealed class MandrillRequest
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("message")] public MandrillMessage? Message { get; set; }
    [JsonPropertyName("async")] public bool? Async { get; set; }
    [JsonPropertyName("ip_pool")] public string? IpPool { get; set; }
    [JsonPropertyName("send_at")] public string? SendAt { get; set; }
    [JsonPropertyName("template_name")] public string? TemplateName { get; set; }
    [JsonPropertyName("template_content")] public JsonElement? TemplateContent { get; set; }
}

public sealed class MandrillMessage
{
    [JsonPropertyName("html")] public string? Html { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("from_email")] public string? FromEmail { get; set; }
    [JsonPropertyName("from_name")] public string? FromName { get; set; }
    [JsonPropertyName("to")] public List<MandrillRecipient>? To { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, JsonElement>? Headers { get; set; }
    [JsonPropertyName("preserve_recipients")] public bool? PreserveRecipients { get; set; }
    [JsonPropertyName("bcc_address")] public string? BccAddress { get; set; }
    [JsonPropertyName("merge")] public bool? Merge { get; set; }
    [JsonPropertyName("merge_language")] public string? MergeLanguage { get; set; }
    [JsonPropertyName("global_merge_vars")] public List<MandrillVar>? GlobalMergeVars { get; set; }
    [JsonPropertyName("merge_vars")] public List<MandrillRecipientVars>? MergeVars { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("metadata")] public Dictionary<string, JsonElement>? Metadata { get; set; }
    [JsonPropertyName("recipient_metadata")] public List<MandrillRecipientMetadata>? RecipientMetadata { get; set; }
    [JsonPropertyName("attachments")] public List<MandrillFile>? Attachments { get; set; }
    [JsonPropertyName("images")] public List<MandrillFile>? Images { get; set; }
}

public sealed class MandrillRecipient
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public sealed class MandrillVar
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("content")] public JsonElement? Content { get; set; }
}

public sealed class MandrillRecipientVars
{
    [JsonPropertyName("rcpt")] public string? Rcpt { get; set; }
    [JsonPropertyName("vars")] public List<MandrillVar>? Vars { get; set; }
}

public sealed class MandrillRecipientMetadata
{
    [JsonPropertyName("rcpt")] public string? Rcpt { get; set; }
    [JsonPropertyName("values")] public Dictionary<string, JsonElement>? Values { get; set; }
}

public sealed class MandrillFile
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

/// <summary>Only the key matters for the users/* calls.</summary>
public sealed class MandrillKeyRequest
{
    [JsonPropertyName("key")] public string? Key { get; set; }
}

public sealed record MandrillSendResult(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reject_reason")] string? RejectReason,
    [property: JsonPropertyName("_id")] string Id);

public sealed record MandrillError(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("message")] string Message);

public sealed record MandrillPing2(
    [property: JsonPropertyName("PING")] string Ping);

[JsonSerializable(typeof(MandrillRequest))]
[JsonSerializable(typeof(MandrillKeyRequest))]
[JsonSerializable(typeof(List<MandrillSendResult>))]
[JsonSerializable(typeof(MandrillError))]
[JsonSerializable(typeof(MandrillPing2))]
[JsonSerializable(typeof(string))]
internal sealed partial class MandrillJsonContext : JsonSerializerContext;
