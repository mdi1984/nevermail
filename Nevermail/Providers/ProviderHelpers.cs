using System.Text;
using System.Text.Json;

namespace Nevermail.Providers;

public static class ProviderHelpers
{
    public static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(ctx.RequestAborted);
    }

    public static bool IsBase64(string value) => System.Buffers.Text.Base64.IsValid(value);

    /// <summary>Strings as-is, everything else (numbers, objects, ...) as raw JSON.</summary>
    public static string ToText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    /// <summary>
    /// Builds a JSON object from existing values. Written by hand because JsonNode/JsonSerializer
    /// would need reflection, which is disabled in the AOT build.
    /// </summary>
    public static JsonElement BuildObject(IEnumerable<KeyValuePair<string, JsonElement>> properties)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in properties)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>True if the request was rejected because a JSON value had the wrong type (as opposed to malformed JSON).</summary>
    public static bool IsTypeMismatch(JsonException ex) =>
        ex.Message.StartsWith("The JSON value could not be converted", StringComparison.Ordinal);

    /// <summary>Converts a System.Text.Json path ("$.personalizations[0].to") to dotted notation ("personalizations.0.to").</summary>
    public static string? ToFieldPath(string? jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath) || jsonPath == "$")
        {
            return null;
        }
        var sb = new StringBuilder(jsonPath.Length);
        foreach (var ch in jsonPath.AsSpan(jsonPath.StartsWith("$.") ? 2 : 1))
        {
            switch (ch)
            {
                case '[':
                    sb.Append('.');
                    break;
                case ']' or '\'':
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString().TrimStart('.');
    }

    /// <summary>
    /// Replaces simple placeholders like <c>*|NAME|*</c> or <c>{{ params.name }}</c>, given the text between
    /// <paramref name="open"/> and <paramref name="close"/> (trimmed) resolves to a value. Unknown placeholders stay as-is.
    /// </summary>
    public static string? ReplacePlaceholders(string? text, string open, string close, Func<string, string?> resolve)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(open, StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var pos = 0;
        while (pos < text.Length)
        {
            var start = text.IndexOf(open, pos, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }
            var end = text.IndexOf(close, start + open.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            sb.Append(text, pos, start - pos);
            var name = text.Substring(start + open.Length, end - start - open.Length).Trim();
            sb.Append(resolve(name) ?? text.Substring(start, end + close.Length - start));
            pos = end + close.Length;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }
}
