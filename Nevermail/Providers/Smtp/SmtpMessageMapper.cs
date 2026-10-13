using System.Text;
using MimeKit;
using Nevermail.Mail;
using MailAddress = Nevermail.Mail.MailAddress;

namespace Nevermail.Providers.Smtp;

public sealed record SmtpEnvelope(string MailFrom, IReadOnlyList<string> Recipients, string? Helo, string? User);

/// <summary>Turns a received RFC 5322 message plus its SMTP envelope into a stored mail.</summary>
public static class SmtpMessageMapper
{
    public const string ProviderKey = "smtp";

    public static async Task<StoredMail> MapAsync(byte[] data, SmtpEnvelope envelope, DateTimeOffset receivedAt, CancellationToken ct)
    {
        var message = new Mail.MailMessage();
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            Fill(message, await MimeMessage.LoadAsync(stream, ct));
        }
        catch (FormatException)
        {
            // Not parseable as MIME at all: keep the raw data and the envelope.
            message.Text = Encoding.UTF8.GetString(data);
        }

        // Envelope recipients missing from the To/Cc headers were Bcc'ed.
        var visible = message.To.Concat(message.Cc).Concat(message.Bcc).Select(a => a.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        message.Bcc.AddRange(envelope.Recipients.Where(r => !visible.Contains(r)).Select(r => new MailAddress(r)));
        if (message.From is null && envelope.MailFrom.Length > 0)
        {
            message.From = new MailAddress(envelope.MailFrom);
        }

        message.Metadata["smtp.mail_from"] = envelope.MailFrom;
        message.Metadata["smtp.rcpt_to"] = string.Join(", ", envelope.Recipients);
        if (envelope.Helo is { } helo)
        {
            message.Metadata["smtp.helo"] = helo;
        }
        if (envelope.User is { } user)
        {
            message.Metadata["smtp.auth_user"] = user;
        }

        return new StoredMail
        {
            RequestId = MailIds.New(),
            ReceivedAt = receivedAt,
            Provider = ProviderKey,
            Message = message,
            RawRequest = Encoding.UTF8.GetString(data),
        };
    }

    private static void Fill(Mail.MailMessage target, MimeMessage mime)
    {
        target.From = Addresses(mime.From).FirstOrDefault() ?? Addresses(mime.Sender).FirstOrDefault();
        target.To = Addresses(mime.To);
        target.Cc = Addresses(mime.Cc);
        target.Bcc = Addresses(mime.Bcc);
        target.ReplyTo = Addresses(mime.ReplyTo);
        target.Subject = mime.Subject;
        target.Html = mime.HtmlBody;
        target.Text = mime.TextBody;

        foreach (var header in mime.Headers)
        {
            target.Headers[header.Field] = target.Headers.TryGetValue(header.Field, out var existing)
                ? $"{existing}\n{header.Value}"
                : header.Value;
        }

        foreach (var part in mime.BodyParts.OfType<MimePart>())
        {
            // Inline text/html parts are the body; everything else is an attachment or embedded resource.
            if (!part.IsAttachment && part is TextPart { IsPlain: true } or TextPart { IsHtml: true })
            {
                continue;
            }
            using var content = new MemoryStream();
            part.Content?.DecodeTo(content);
            target.Attachments.Add(new MailAttachment
            {
                Filename = part.FileName,
                ContentType = part.ContentType.MimeType,
                ContentId = part.ContentId,
                Inline = !part.IsAttachment,
                Content = Convert.ToBase64String(content.GetBuffer(), 0, (int)content.Length),
            });
        }
    }

    private static List<MailAddress> Addresses(InternetAddressList list) =>
        list.Mailboxes
            .Where(m => !string.IsNullOrWhiteSpace(m.Address))
            .Select(m => new MailAddress(m.Address, string.IsNullOrWhiteSpace(m.Name) ? null : m.Name))
            .ToList();

    private static IEnumerable<MailAddress> Addresses(MailboxAddress? mailbox) =>
        mailbox is null || string.IsNullOrWhiteSpace(mailbox.Address) ? [] : [new MailAddress(mailbox.Address, string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name)];
}
