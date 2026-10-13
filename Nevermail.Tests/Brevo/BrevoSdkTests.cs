using brevo_csharp.Api;
using brevo_csharp.Client;
using brevo_csharp.Model;
using Task = System.Threading.Tasks.Task;
using Configuration = brevo_csharp.Client.Configuration;

namespace Nevermail.Tests.Brevo;

/// <summary>Sends mails through the official Brevo SDK (brevo_csharp) over real HTTP.</summary>
public sealed class BrevoSdkTests : IDisposable
{
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private readonly NevermailFactory _factory = new();
    private readonly TransactionalEmailsApi _api;

    public BrevoSdkTests()
    {
        var baseAddress = _factory.StartKestrel();
        var config = new Configuration { BasePath = new Uri(baseAddress, "/v3").ToString() };
        config.ApiKey["api-key"] = NevermailFactory.ApiKey;
        _api = new TransactionalEmailsApi(config);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SimpleMail_IsStored()
    {
        var email = new SendSmtpEmail(
            sender: new SendSmtpEmailSender("Shop", "shop@example.com"),
            to: [new SendSmtpEmailTo("alice@example.com", "Alice")],
            cc: [new SendSmtpEmailCc("cc@example.com")],
            bcc: [new SendSmtpEmailBcc("bcc@example.com")],
            replyTo: new SendSmtpEmailReplyTo("support@example.com", "Support"),
            subject: "Order confirmation",
            htmlContent: "<p>Thanks!</p>",
            textContent: "Thanks!",
            attachment: [new SendSmtpEmailAttachment(content: Convert.FromBase64String(PngBase64), name: "receipt.png")],
            headers: new Dictionary<string, object> { ["X-Order"] = "42" },
            tags: ["orders"]);

        var result = await _api.SendTransacEmailAsync(email);

        Assert.StartsWith("<", result.MessageId);
        var mail = Assert.Single(_factory.Store.Query(new()));
        Assert.Equal($"<{mail.Id}@nevermail.local>", result.MessageId);
        Assert.Equal("brevo", mail.Provider);
        var m = mail.Message;
        Assert.Equal(new Mail.MailAddress("shop@example.com", "Shop"), m.From);
        Assert.Equal("alice@example.com", Assert.Single(m.To).Email);
        Assert.Equal("cc@example.com", Assert.Single(m.Cc).Email);
        Assert.Equal("bcc@example.com", Assert.Single(m.Bcc).Email);
        Assert.Equal("support@example.com", Assert.Single(m.ReplyTo).Email);
        Assert.Equal("Order confirmation", m.Subject);
        Assert.Equal("<p>Thanks!</p>", m.Html);
        Assert.Equal("Thanks!", m.Text);
        Assert.Equal("42", m.Headers["X-Order"]);
        Assert.Equal(["orders"], m.Tags);
        var attachment = Assert.Single(m.Attachments);
        Assert.Equal("receipt.png", attachment.Filename);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal(PngBase64, attachment.Content);
    }

    [Fact]
    public async Task MessageVersions_BecomeSeparateMailsWithParams()
    {
        var email = new SendSmtpEmail(
            sender: new SendSmtpEmailSender(email: "shop@example.com"),
            subject: "Hello {{ params.name }}",
            htmlContent: "<p>Your code: {{params.code}}</p>",
            _params: new Dictionary<string, object> { ["name"] = "you", ["code"] = "000" },
            messageVersions:
            [
                new SendSmtpEmailMessageVersions(
                    to: [new SendSmtpEmailTo1("alice@example.com")],
                    _params: new Dictionary<string, object> { ["name"] = "Alice", ["code"] = "111" }),
                new SendSmtpEmailMessageVersions(
                    to: [new SendSmtpEmailTo1("bob@example.com")],
                    subject: "Special for Bob",
                    _params: new Dictionary<string, object> { ["code"] = "222" }),
            ]);

        var result = await _api.SendTransacEmailAsync(email);

        Assert.Equal(2, result.MessageIds.Count);
        var mails = _factory.Store.Query(new()).OrderBy(m => m.Message.To[0].Email).ToList();
        Assert.Equal(2, mails.Count);
        Assert.Single(mails.Select(m => m.RequestId).Distinct());

        Assert.Equal("Hello Alice", mails[0].Message.Subject);
        Assert.Equal("<p>Your code: 111</p>", mails[0].Message.Html);
        Assert.Equal("Special for Bob", mails[1].Message.Subject);
        Assert.Equal("<p>Your code: 222</p>", mails[1].Message.Html);
    }

    [Fact]
    public async Task Template_StoresTemplateIdAndParams()
    {
        var email = new SendSmtpEmail(
            to: [new SendSmtpEmailTo("alice@example.com")],
            templateId: 17,
            _params: new Dictionary<string, object> { ["firstName"] = "Alice" });

        await _api.SendTransacEmailAsync(email);

        var m = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("17", m.TemplateId);
        Assert.Equal("Alice", m.TemplateData!.Value.GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task WrongKey_IsRejected()
    {
        var config = new Configuration { BasePath = _api.Configuration.BasePath };
        config.ApiKey["api-key"] = "wrong";
        var api = new TransactionalEmailsApi(config);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.SendTransacEmailAsync(new SendSmtpEmail(
            sender: new SendSmtpEmailSender(email: "shop@example.com"),
            to: [new SendSmtpEmailTo("alice@example.com")],
            subject: "x",
            htmlContent: "x")));

        Assert.Equal(401, ex.ErrorCode);
        Assert.Empty(_factory.Store.Query(new()));
    }
}
