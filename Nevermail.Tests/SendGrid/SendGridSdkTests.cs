using System.Net;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Nevermail.Tests.SendGrid;

/// <summary>Sends mails through the official SendGrid SDK and checks what Nevermail captured.</summary>
public sealed class SendGridSdkTests : IDisposable
{
    // 1x1 transparent PNG
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private readonly NevermailFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private SendGridClient CreateClient(string apiKey = NevermailFactory.ApiKey) =>
        new(_factory.CreateClient(), new SendGridClientOptions { ApiKey = apiKey, Host = "http://localhost" });

    [Fact]
    public async Task PlainText_IsAcceptedAndStored()
    {
        var mail = MailHelper.CreateSingleEmailToMultipleRecipients(
            new EmailAddress("sender@example.com", "Sender McSendFace"),
            [new EmailAddress("one@example.com", "One"), new EmailAddress("two@example.com")],
            "Hello",
            "Plain body",
            null,
            showAllRecipients: true);

        var response = await CreateClient().SendEmailAsync(mail);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var messageId = Assert.Single(response.Headers.GetValues("X-Message-Id"));

        var stored = Assert.Single(_factory.Store.Query(new()));
        Assert.Equal(messageId, stored.RequestId);
        Assert.Equal("sendgrid", stored.Provider);
        Assert.False(stored.Sandbox);
        Assert.Equal("sender@example.com", stored.Message.From!.Email);
        Assert.Equal("Sender McSendFace", stored.Message.From.Name);
        Assert.Equal(["one@example.com", "two@example.com"], stored.Message.To.Select(a => a.Email));
        Assert.Equal("Hello", stored.Message.Subject);
        Assert.Equal("Plain body", stored.Message.Text);
        Assert.Null(stored.Message.Html);
        Assert.NotNull(stored.RawRequest);
    }

    [Fact]
    public async Task HiddenRecipients_EachGetTheirOwnMail()
    {
        // Without showAllRecipients the SDK creates one personalization per recipient.
        var mail = MailHelper.CreateSingleEmailToMultipleRecipients(
            new EmailAddress("sender@example.com"),
            [new EmailAddress("one@example.com"), new EmailAddress("two@example.com")],
            "Hello",
            "Plain body",
            null);

        var response = await CreateClient().SendEmailAsync(mail);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = _factory.Store.Query(new());
        Assert.Equal(["two@example.com", "one@example.com"], stored.Select(m => Assert.Single(m.Message.To).Email));
    }

    [Fact]
    public async Task HtmlWithAttachments_IsStored()
    {
        var mail = MailHelper.CreateSingleEmail(
            new EmailAddress("sender@example.com"),
            new EmailAddress("recipient@example.com"),
            "With attachments",
            "text version",
            "<p>html version <img src=\"cid:logo\"></p>");
        mail.AddAttachment("report.png", PngBase64, "image/png");
        mail.AddAttachment("logo.png", PngBase64, "image/png", "inline", "logo");

        var response = await CreateClient().SendEmailAsync(mail);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("text version", stored.Text);
        Assert.Contains("cid:logo", stored.Html);
        Assert.Collection(stored.Attachments,
            a =>
            {
                Assert.Equal("report.png", a.Filename);
                Assert.False(a.Inline);
                Assert.Equal(PngBase64, a.Content);
            },
            a =>
            {
                Assert.Equal("logo", a.ContentId);
                Assert.True(a.Inline);
            });
    }

    [Fact]
    public async Task EachPersonalization_BecomesItsOwnMail()
    {
        var mail = new SendGridMessage
        {
            From = new EmailAddress("sender@example.com"),
            Subject = "Hi -name-",
            PlainTextContent = "Hello -name-, your code is -code-.",
        };
        mail.AddTo(new EmailAddress("alice@example.com"), 0);
        mail.AddSubstitutions(new Dictionary<string, string> { ["-name-"] = "Alice", ["-code-"] = "111" }, 0);
        mail.AddTo(new EmailAddress("bob@example.com"), 1);
        mail.AddCc(new EmailAddress("boss@example.com"), 1);
        mail.AddSubstitutions(new Dictionary<string, string> { ["-name-"] = "Bob", ["-code-"] = "222" }, 1);
        mail.SetSubject("Special subject for Bob", 1);

        var response = await CreateClient().SendEmailAsync(mail);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = _factory.Store.Query(new()).OrderBy(m => m.Message.To[0].Email).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Single(stored.Select(m => m.RequestId).Distinct());

        var alice = stored[0].Message;
        Assert.Equal("Hi Alice", alice.Subject);
        Assert.Equal("Hello Alice, your code is 111.", alice.Text);
        Assert.Empty(alice.Cc);

        var bob = stored[1].Message;
        Assert.Equal("Special subject for Bob", bob.Subject);
        Assert.Equal("Hello Bob, your code is 222.", bob.Text);
        Assert.Equal("boss@example.com", Assert.Single(bob.Cc).Email);
    }

    [Fact]
    public async Task DynamicTemplate_StoresTemplateIdAndData()
    {
        var mail = MailHelper.CreateSingleTemplateEmail(
            new EmailAddress("sender@example.com"),
            new EmailAddress("recipient@example.com"),
            "d-1234567890abcdef",
            new { firstName = "Alice", items = new[] { "a", "b" } });

        var response = await CreateClient().SendEmailAsync(mail);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("d-1234567890abcdef", stored.TemplateId);
        Assert.Equal("Alice", stored.TemplateData!.Value.GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task HeadersCategoriesAndCustomArgs_AreStored()
    {
        var mail = MailHelper.CreateSingleEmail(
            new EmailAddress("sender@example.com"),
            new EmailAddress("recipient@example.com"),
            "Meta",
            "body",
            null);
        mail.AddHeader("X-Global", "global");
        mail.AddHeader("X-Personal", "personal", 0);
        mail.AddCategory("welcome");
        mail.AddCustomArg("user_id", "42");
        mail.SetReplyTo(new EmailAddress("reply@example.com", "Support"));

        var response = await CreateClient().SendEmailAsync(mail);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("global", stored.Headers["x-global"]);
        Assert.Equal("personal", stored.Headers["X-Personal"]);
        Assert.Equal(["welcome"], stored.Tags);
        Assert.Equal("42", stored.Metadata["user_id"]);
        Assert.Equal("reply@example.com", Assert.Single(stored.ReplyTo).Email);
    }

    [Fact]
    public async Task SandboxMode_Returns200AndFlagsMail()
    {
        var mail = MailHelper.CreateSingleEmail(
            new EmailAddress("sender@example.com"),
            new EmailAddress("recipient@example.com"),
            "Sandboxed",
            "body",
            null);
        mail.SetSandBoxMode(true);

        var response = await CreateClient().SendEmailAsync(mail);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Assert.Single(_factory.Store.Query(new())).Sandbox);
    }

    [Fact]
    public async Task WrongApiKey_IsRejected()
    {
        var mail = MailHelper.CreateSingleEmail(
            new EmailAddress("sender@example.com"),
            new EmailAddress("recipient@example.com"),
            "Nope",
            "body",
            null);

        var response = await CreateClient("wrong_key").SendEmailAsync(mail);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Store.Query(new()));
    }
}
