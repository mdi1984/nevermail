using System.Diagnostics;
using Azure;
using Azure.Communication.Email;

namespace Nevermail.Tests.AzureEmail;

/// <summary>Sends mails through the official Azure Communication Services SDK (Azure.Communication.Email) over real HTTP.</summary>
public sealed class AzureEmailSdkTests : IDisposable
{
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private readonly NevermailFactory _factory = new();
    private readonly EmailClient _client;

    public AzureEmailSdkTests()
    {
        var baseAddress = _factory.StartKestrel();
        // Same shape as the Nevermail Aspire connection string; the SDK ignores the extra keys.
        var connectionString = $"Endpoint={baseAddress};SmtpHost=localhost;SmtpPort=1025;AccessKey=bmV2ZXJtYWls";
        var options = new EmailClientOptions();
        options.Retry.MaxRetries = 0;
        _client = new EmailClient(connectionString, options);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task FullMail_IsStoredAndCompletesImmediately()
    {
        var message = new EmailMessage(
            "shop@example.com",
            new EmailRecipients(
                [new EmailAddress("alice@example.com", "Alice")],
                [new EmailAddress("cc@example.com")],
                [new EmailAddress("bcc@example.com")]),
            new EmailContent("Order confirmation") { Html = "<p>Thanks! <img src=\"cid:logo\"></p>", PlainText = "Thanks!" })
        {
            UserEngagementTrackingDisabled = true,
        };
        message.ReplyTo.Add(new EmailAddress("support@example.com", "Support"));
        message.Headers.Add("X-Order", "42");
        message.Attachments.Add(new EmailAttachment("receipt.txt", "text/plain", BinaryData.FromString("hello")));
        message.Attachments.Add(new EmailAttachment("logo.png", "image/png", new BinaryData(Convert.FromBase64String(PngBase64))) { ContentId = "logo" });

        var stopwatch = Stopwatch.StartNew();
        var operation = await _client.SendAsync(WaitUntil.Completed, message);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
        Assert.Equal(EmailSendStatus.Succeeded, operation.Value.Status);
        var mail = Assert.Single(_factory.Store.Query(new()));
        Assert.Equal("azure", mail.Provider);
        Assert.Equal(operation.Id, mail.RequestId);
        var m = mail.Message;
        Assert.Equal(new Mail.MailAddress("shop@example.com"), m.From);
        Assert.Equal(new Mail.MailAddress("alice@example.com", "Alice"), Assert.Single(m.To));
        Assert.Equal(new Mail.MailAddress("cc@example.com"), Assert.Single(m.Cc));
        Assert.Equal(new Mail.MailAddress("bcc@example.com"), Assert.Single(m.Bcc));
        Assert.Equal(new Mail.MailAddress("support@example.com", "Support"), Assert.Single(m.ReplyTo));
        Assert.Equal("Order confirmation", m.Subject);
        Assert.Equal("Thanks!", m.Text);
        Assert.StartsWith("<p>Thanks!", m.Html);
        Assert.Equal("42", m.Headers["X-Order"]);
        Assert.Equal("true", m.Metadata["userEngagementTrackingDisabled"]);
        Assert.Collection(m.Attachments,
            a =>
            {
                Assert.Equal("receipt.txt", a.Filename);
                Assert.Equal("text/plain", a.ContentType);
                Assert.Equal("aGVsbG8=", a.Content);
                Assert.False(a.Inline);
            },
            a =>
            {
                Assert.Equal("logo.png", a.Filename);
                Assert.Equal("logo", a.ContentId);
                Assert.True(a.Inline);
                Assert.Equal(PngBase64, a.Content);
            });
    }

    [Fact]
    public async Task SimpleOverload_IsStored()
    {
        var operation = await _client.SendAsync(WaitUntil.Completed, "shop@example.com", "bob@example.com", "Hi", "<p>Hi Bob</p>", "Hi Bob");

        Assert.Equal(EmailSendStatus.Succeeded, operation.Value.Status);
        var m = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("bob@example.com", Assert.Single(m.To).Email);
        Assert.Null(m.To[0].Name);
        Assert.Equal("Hi Bob", m.Text);
    }

    [Fact]
    public async Task StartedOperation_CanBePolledAndRehydrated()
    {
        var operation = await _client.SendAsync(WaitUntil.Started, "shop@example.com", "bob@example.com", "Hi", "<p>Hi</p>");

        var rehydrated = new EmailSendOperation(operation.Id, _client);
        await rehydrated.UpdateStatusAsync();

        Assert.True(rehydrated.HasCompleted);
        Assert.Equal(EmailSendStatus.Succeeded, rehydrated.Value.Status);
    }

    [Fact]
    public async Task InvalidRecipient_ThrowsRequestFailedException()
    {
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            _client.SendAsync(WaitUntil.Completed, "shop@example.com", "not-an-address", "Hi", "<p>Hi</p>"));

        Assert.Equal(400, ex.Status);
        Assert.Equal("BadRequest", ex.ErrorCode);
        Assert.Contains("See property 'recipients.to[0].address'", ex.Message);
        Assert.Empty(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task DroppedRecipient_IsAcceptedThenFails()
    {
        var operation = await _client.SendAsync(WaitUntil.Started, "shop@example.com", "someone@fail-dropped.test", "Hi", "<p>Hi</p>");

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => operation.WaitForCompletionAsync().AsTask());

        Assert.Equal("EmailDropped", ex.ErrorCode);
        Assert.Empty(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task TriggerRecipient_FailsWithStatus()
    {
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            _client.SendAsync(WaitUntil.Completed, "shop@example.com", "someone@fail-429.test", "Hi", "<p>Hi</p>"));

        Assert.Equal(429, ex.Status);
        Assert.Equal("TooManyRequests", ex.ErrorCode);
    }
}
