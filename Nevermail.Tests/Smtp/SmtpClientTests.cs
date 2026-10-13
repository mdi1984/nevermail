using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace Nevermail.Tests.Smtp;

/// <summary>Sends mails with MailKit, a standards-compliant SMTP client.</summary>
public sealed class SmtpClientTests : IDisposable
{
    private readonly NevermailFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task SendAsync(MimeMessage message, string? user = null, string? password = null, NevermailFactory? factory = null)
    {
        factory ??= _factory;
        using var client = new SmtpClient();
        await client.ConnectAsync("localhost", factory.SmtpPort, SecureSocketOptions.None);
        if (user != null)
        {
            await client.AuthenticateAsync(user, password);
        }
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task MultipartMail_IsParsed()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Jörg Müller", "joerg@example.com"));
        message.To.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.Cc.Add(new MailboxAddress("", "cc@example.com"));
        message.ReplyTo.Add(new MailboxAddress("Support", "support@example.com"));
        message.Subject = "Grüße aus Wien ☕";
        message.Headers.Add("X-Custom", "value");

        var builder = new BodyBuilder { TextBody = "Plain text body" };
        var logo = builder.LinkedResources.Add("logo.png", Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="), new ContentType("image", "png"));
        logo.ContentId = MimeUtils.GenerateMessageId();
        builder.HtmlBody = $"<p>HTML body</p><img src=\"cid:{logo.ContentId}\">";
        builder.Attachments.Add("report.pdf", "%PDF-1.4 fake"u8.ToArray(), new ContentType("application", "pdf"));
        message.Body = builder.ToMessageBody();

        await SendAsync(message);

        var mail = Assert.Single(_factory.Store.Query(new()));
        Assert.Equal("smtp", mail.Provider);
        var m = mail.Message;
        Assert.Equal(new Mail.MailAddress("joerg@example.com", "Jörg Müller"), m.From);
        Assert.Equal("alice@example.com", Assert.Single(m.To).Email);
        Assert.Equal("cc@example.com", Assert.Single(m.Cc).Email);
        Assert.Empty(m.Bcc);
        Assert.Equal("support@example.com", Assert.Single(m.ReplyTo).Email);
        Assert.Equal("Grüße aus Wien ☕", m.Subject);
        Assert.Equal("Plain text body", m.Text?.Trim());
        Assert.Contains("<p>HTML body</p>", m.Html);
        Assert.Equal("value", m.Headers["X-Custom"]);
        Assert.Equal("joerg@example.com", m.Metadata["smtp.mail_from"]);

        Assert.Equal(2, m.Attachments.Count);
        var inline = m.Attachments.Single(a => a.Inline);
        Assert.Equal(logo.ContentId, inline.ContentId);
        Assert.Equal("image/png", inline.ContentType);
        var pdf = m.Attachments.Single(a => !a.Inline);
        Assert.Equal("report.pdf", pdf.Filename);
        Assert.Equal("%PDF-1.4 fake", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(pdf.Content)));
        Assert.Contains("Subject:", mail.RawRequest);
    }

    [Fact]
    public async Task EnvelopeOnlyRecipients_AreBcc()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("from@example.com"));
        message.To.Add(MailboxAddress.Parse("to@example.com"));
        message.Bcc.Add(MailboxAddress.Parse("secret@example.com"));
        message.Subject = "Bcc";
        message.Body = new TextPart("plain") { Text = "hi" };

        await SendAsync(message);

        var m = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("to@example.com", Assert.Single(m.To).Email);
        Assert.Equal("secret@example.com", Assert.Single(m.Bcc).Email);
        Assert.Equal("to@example.com, secret@example.com", m.Metadata["smtp.rcpt_to"]);
    }

    [Fact]
    public async Task Authentication_IsEnforcedWhenConfigured()
    {
        using var factory = new NevermailFactory(new() { ["Smtp:Username"] = "user", ["Smtp:Password"] = "secret" });
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("from@example.com"));
        message.To.Add(MailboxAddress.Parse("to@example.com"));
        message.Body = new TextPart("plain") { Text = "hi" };

        await Assert.ThrowsAsync<AuthenticationException>(() => SendAsync(message, "user", "wrong", factory));
        await Assert.ThrowsAsync<ServiceNotAuthenticatedException>(() => SendAsync(message, factory: factory));
        await SendAsync(message, "user", "secret", factory);

        Assert.Equal("user", Assert.Single(factory.Store.Query(new())).Message.Metadata["smtp.auth_user"]);
    }

    [Fact]
    public async Task AnyCredentials_AreAcceptedByDefault()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("from@example.com"));
        message.To.Add(MailboxAddress.Parse("to@example.com"));
        message.Body = new TextPart("plain") { Text = "hi" };

        await SendAsync(message, "whoever", "whatever");

        Assert.Single(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task SystemNetMailSmtpClient_Works()
    {
#pragma warning disable SYSLIB0014 // still widely used, so it must work
        using var client = new System.Net.Mail.SmtpClient("localhost", _factory.SmtpPort);
#pragma warning restore SYSLIB0014
        using var message = new System.Net.Mail.MailMessage("from@example.com", "to@example.com", "Legacy client", "<b>hi</b>") { IsBodyHtml = true };
        message.Attachments.Add(new System.Net.Mail.Attachment(new MemoryStream("data"u8.ToArray()), "data.txt", "text/plain"));

        await client.SendMailAsync(message);

        var m = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("Legacy client", m.Subject);
        Assert.Equal("<b>hi</b>", m.Html);
        Assert.Equal("data.txt", Assert.Single(m.Attachments).Filename);
    }
}
