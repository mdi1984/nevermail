using System.Text;
using MimeKit;
using MimeKit.Utils;
using Nevermail.Mail;
using Nevermail.Providers.Smtp;

namespace Nevermail.Api;

/// <summary>
/// Turns a captured mail into an RFC 5322 message that mail clients can open.
/// </summary>
/// <remarks>
/// Written by hand instead of with MimeKit's MimeMessage/BodyBuilder: those generate random MIME boundaries
/// and message ids via System.Security.Cryptography, which needs libssl, and the scratch image has none
/// (the process aborts). MimeKit is only used for header encoding, which needs no randomness.
/// </remarks>
public static class EmlExport
{
    // Written by this exporter; custom headers with these names are not copied.
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "From", "To", "Cc", "Bcc", "Reply-To", "Subject", "Date", "Message-ID", "MIME-Version",
        "Content-Type", "Content-Transfer-Encoding", "Content-Disposition", "Content-ID",
    };

    private abstract record Part;
    private sealed record Leaf(string ContentType, byte[] Content, string? FileName = null, string? ContentId = null, string? Disposition = null) : Part;
    private sealed record Multi(string Subtype, List<Part> Parts) : Part;

    public static byte[] ToEml(StoredMail mail)
    {
        // SMTP mails were received as RFC 5322 already: hand them back unchanged.
        if (mail.Provider == SmtpMessageMapper.ProviderKey && mail.RawRequest is { } raw)
        {
            return Encoding.UTF8.GetBytes(raw);
        }

        var m = mail.Message;
        var sb = new StringBuilder();
        void Header(string name, string value) => sb.Append(name).Append(": ").Append(value).Append("\r\n");

        if (m.From is { } from)
        {
            Header("From", Address(from));
        }
        foreach (var (name, list) in new[] { ("To", m.To), ("Cc", m.Cc), ("Bcc", m.Bcc), ("Reply-To", m.ReplyTo) })
        {
            if (list.Count > 0)
            {
                Header(name, string.Join(", ", list.Select(Address)));
            }
        }
        Header("Subject", EncodeText(m.Subject ?? ""));
        Header("Date", mail.ReceivedAt.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", System.Globalization.CultureInfo.InvariantCulture));
        Header("Message-ID", m.Headers.TryGetValue("Message-ID", out var messageId) ? $"<{messageId.Trim('<', '>')}>" : $"<{mail.Id}@nevermail.local>");
        foreach (var (name, value) in m.Headers)
        {
            if (!ReservedHeaders.Contains(name) && IsValidHeaderName(name))
            {
                Header(name, EncodeText(value.ReplaceLineEndings(" ")));
            }
        }
        Header("X-Nevermail-Provider", mail.Provider);
        Header("MIME-Version", "1.0");

        WritePart(sb, BuildBody(m), mail.Id, depth: 0);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static string FileName(StoredMail mail)
    {
        var subject = mail.Message.Subject;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return $"{mail.Id}.eml";
        }
        // Characters no common file system accepts (the server may run on Linux, the browser on Windows).
        var safe = new string(subject.Select(c => "\\/:*?\"<>|".Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return $"{(safe.Length > 80 ? safe[..80] : safe)}.eml";
    }

    /// <summary>mixed( related( alternative(text, html), inline... ), attachments... ), collapsing levels that would have one part.</summary>
    private static Part BuildBody(Mail.MailMessage m)
    {
        var alternatives = new List<Part>();
        if (m.Text is not null || m.Html is null)
        {
            alternatives.Add(new Leaf("text/plain; charset=utf-8", Encoding.UTF8.GetBytes(m.Text ?? "")));
        }
        if (m.Html is not null)
        {
            alternatives.Add(new Leaf("text/html; charset=utf-8", Encoding.UTF8.GetBytes(m.Html)));
        }
        var body = Wrap("alternative", alternatives);

        var files = m.Attachments.Where(a => a.Content.Length > 0).ToList();
        var inline = files.Where(a => a.Inline && a.ContentId is not null).ToList();
        if (inline.Count > 0)
        {
            body = new Multi("related", [body, .. inline.Select(a => File(a, "inline"))]);
        }
        var attachments = files.Except(inline).ToList();
        return attachments.Count > 0 ? new Multi("mixed", [body, .. attachments.Select(a => File(a, "attachment"))]) : body;
    }

    private static Part Wrap(string subtype, List<Part> parts) => parts.Count == 1 ? parts[0] : new Multi(subtype, parts);

    private static Leaf File(MailAttachment a, string disposition) => new(
        ContentType.TryParse(a.ContentType ?? "", out var type) ? type.MimeType : "application/octet-stream",
        Convert.FromBase64String(a.Content),
        a.Filename ?? a.ContentId ?? "attachment",
        a.ContentId?.Trim('<', '>'),
        disposition);

    private static void WritePart(StringBuilder sb, Part part, string mailId, int depth)
    {
        switch (part)
        {
            case Multi multi:
            {
                // Deterministic boundary: unique per mail and nesting level, never part of base64 content.
                var boundary = $"=_nevermail_{depth}_{mailId}";
                sb.Append($"Content-Type: multipart/{multi.Subtype}; boundary=\"{boundary}\"\r\n\r\n");
                foreach (var child in multi.Parts)
                {
                    sb.Append($"--{boundary}\r\n");
                    WritePart(sb, child, mailId, depth + 1);
                    sb.Append("\r\n");
                }
                sb.Append($"--{boundary}--\r\n");
                break;
            }
            case Leaf leaf:
            {
                sb.Append("Content-Type: ").Append(leaf.ContentType);
                if (leaf.FileName is { } name)
                {
                    sb.Append("; ").Append(Parameter("name", name));
                }
                sb.Append("\r\nContent-Transfer-Encoding: base64\r\n");
                if (leaf.Disposition is { } disposition)
                {
                    sb.Append("Content-Disposition: ").Append(disposition);
                    if (leaf.FileName is { } fileName)
                    {
                        sb.Append("; ").Append(Parameter("filename", fileName));
                    }
                    sb.Append("\r\n");
                }
                if (leaf.ContentId is { } contentId)
                {
                    sb.Append("Content-ID: <").Append(contentId).Append(">\r\n");
                }
                sb.Append("\r\n");
                // InsertLineBreaks wraps at 76 characters with CRLF.
                sb.Append(Convert.ToBase64String(leaf.Content, Base64FormattingOptions.InsertLineBreaks)).Append("\r\n");
                break;
            }
        }
    }

    /// <summary>Quoted ASCII parameter, or RFC 2231 encoding for anything else.</summary>
    private static string Parameter(string name, string value) =>
        value.All(c => c >= 32 && c < 127 && c != '"' && c != '\\')
            ? $"{name}=\"{value}\""
            : $"{name}*=UTF-8''{Uri.EscapeDataString(value)}";

    private static string Address(Mail.MailAddress address) =>
        new MailboxAddress(address.Name ?? "", address.Email).ToString(FormatOptions.Default, encode: true);

    private static string EncodeText(string value) =>
        value.All(c => c >= 32 && c < 127) ? value : Encoding.ASCII.GetString(Rfc2047.EncodeText(FormatOptions.Default, Encoding.UTF8, value));

    private static bool IsValidHeaderName(string name) =>
        name.Length > 0 && name.All(c => c > 32 && c < 127 && c != ':');
}
