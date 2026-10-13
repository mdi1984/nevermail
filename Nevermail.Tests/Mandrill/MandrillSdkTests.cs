using Mandrill;
using Mandrill.Model;

namespace Nevermail.Tests.Mandrill;

/// <summary>Sends mails through Mandrill.net, the common .NET client for Mailchimp Transactional.</summary>
public sealed class MandrillSdkTests : IDisposable
{
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private readonly NevermailFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private MandrillApi CreateApi(string apiKey = NevermailFactory.ApiKey)
    {
        // Mandrill.net treats the HttpClient's base address as the API root.
        var client = _factory.CreateClient();
        client.BaseAddress = new Uri(client.BaseAddress!, "/api/1.3/");
        return new MandrillApi(apiKey, client);
    }

    [Fact]
    public async Task Ping_Works()
    {
        Assert.Equal("PONG!", await CreateApi().Users.PingAsync());
    }

    [Fact]
    public async Task EachRecipient_GetsTheirOwnMail()
    {
        var message = new MandrillMessage
        {
            FromEmail = "shop@example.com",
            FromName = "Shop",
            Subject = "Hello *|NAME|*",
            Html = "<p>Your code: *|code|*</p>",
            Text = "Your code: *|CODE|*",
        };
        message.AddTo("alice@example.com", "Alice");
        message.AddTo("bob@example.com", "Bob", MandrillMailAddressType.Cc);
        message.AddGlobalMergeVars("NAME", "you");
        message.AddGlobalMergeVars("CODE", "000");
        message.AddRcptMergeVars("alice@example.com", "NAME", "Alice");
        message.AddRcptMergeVars("alice@example.com", "CODE", "111");
        message.AddMetadata("order", "42");
        message.AddRecipientMetadata("bob@example.com", "vip", "yes");
        message.Tags.Add("welcome");
        message.ReplyTo = "support@example.com";

        var results = await CreateApi().Messages.SendAsync(message);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(MandrillSendMessageResponseStatus.Sent, r.Status));
        var mails = _factory.Store.Query(new());
        Assert.Equal(2, mails.Count);

        var alice = mails.Single(m => m.Message.To.Any(a => a.Email == "alice@example.com"));
        Assert.Equal(results.Single(r => r.Email == "alice@example.com").Id, alice.Id);
        Assert.Equal("mandrill", alice.Provider);
        Assert.Equal(new Mail.MailAddress("shop@example.com", "Shop"), alice.Message.From);
        Assert.Equal("Hello Alice", alice.Message.Subject);
        Assert.Equal("<p>Your code: 111</p>", alice.Message.Html);
        Assert.Equal("Your code: 111", alice.Message.Text);
        Assert.Equal("42", alice.Message.Metadata["order"]);
        Assert.False(alice.Message.Metadata.ContainsKey("vip"));
        Assert.Equal(["welcome"], alice.Message.Tags);
        Assert.Equal("support@example.com", Assert.Single(alice.Message.ReplyTo).Email);

        var bob = mails.Single(m => m.Message.Cc.Any(a => a.Email == "bob@example.com"));
        Assert.Empty(bob.Message.To);
        Assert.Equal("Hello you", bob.Message.Subject);
        Assert.Equal("yes", bob.Message.Metadata["vip"]);
    }

    [Fact]
    public async Task PreserveRecipients_SendsOneMail()
    {
        var message = new MandrillMessage("shop@example.com", "alice@example.com", "Together", "<p>hi</p>")
        {
            PreserveRecipients = true,
            BccAddress = "archive@example.com",
        };
        message.AddTo("bob@example.com");
        message.AddTo("hidden@example.com", null, MandrillMailAddressType.Bcc);

        var results = await CreateApi().Messages.SendAsync(message);

        Assert.Equal(3, results.Count);
        var mail = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal(["alice@example.com", "bob@example.com"], mail.To.Select(a => a.Email));
        Assert.Equal(["hidden@example.com", "archive@example.com"], mail.Bcc.Select(a => a.Email));
    }

    [Fact]
    public async Task Handlebars_AttachmentsAndImages()
    {
        var message = new MandrillMessage("shop@example.com", "alice@example.com", "Hi {{name}}", "<p>{{name}} &amp; {{{raw}}}</p><img src=\"cid:logo\">")
        {
            MergeLanguage = MandrillMessageMergeLanguage.Handlebars,
        };
        message.AddGlobalMergeVars("name", "<Alice>");
        message.AddGlobalMergeVars("raw", "<b>bold</b>");
        message.Attachments.Add(new MandrillAttachment("text/plain", "notes.txt", "hello"u8.ToArray()));
        message.Images.Add(new MandrillImage("image/png", "logo", Convert.FromBase64String(PngBase64)));

        await CreateApi().Messages.SendAsync(message);

        var mail = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("Hi <Alice>", mail.Subject);
        Assert.Equal("<p>&lt;Alice&gt; &amp; <b>bold</b></p><img src=\"cid:logo\">", mail.Html);
        Assert.Collection(mail.Attachments,
            a => Assert.Equal(("notes.txt", false), (a.Filename, a.Inline)),
            a => Assert.Equal(("logo", true, "logo"), (a.Filename, a.Inline, a.ContentId)));
    }

    [Fact]
    public async Task InvalidRecipient_IsReportedNotDelivered()
    {
        var message = new MandrillMessage("shop@example.com", "alice@example.com", "Hi", "<p>hi</p>");
        message.AddTo("not-an-address");

        var results = await CreateApi().Messages.SendAsync(message);

        Assert.Equal(MandrillSendMessageResponseStatus.Invalid, results.Single(r => r.Email == "not-an-address").Status);
        Assert.Single(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task Template_StoresTemplateNameAndContent()
    {
        var message = new MandrillMessage { FromEmail = "shop@example.com" };
        message.AddTo("alice@example.com");
        message.AddGlobalMergeVars("NAME", "Alice");

        var results = await CreateApi().Messages.SendTemplateAsync(message, "welcome-mail",
            [new MandrillTemplateContent { Name = "header", Content = "Welcome!" }]);

        Assert.Equal(MandrillSendMessageResponseStatus.Sent, Assert.Single(results).Status);
        var mail = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("welcome-mail", mail.TemplateId);
        Assert.Equal("Welcome!", mail.TemplateData!.Value.GetProperty("template_content")[0].GetProperty("content").GetString());
        Assert.Equal("Alice", mail.TemplateData!.Value.GetProperty("merge_vars").GetProperty("NAME").GetString());
    }

    [Fact]
    public async Task ScheduledSend_ReportsScheduled()
    {
        var message = new MandrillMessage("shop@example.com", "alice@example.com", "Later", "<p>later</p>");

        var results = await CreateApi().Messages.SendAsync(message, sendAtUtc: DateTime.UtcNow.AddHours(2));

        Assert.Equal(MandrillSendMessageResponseStatus.Scheduled, Assert.Single(results).Status);
    }

    [Fact]
    public async Task WrongKey_Throws()
    {
        var ex = await Assert.ThrowsAsync<MandrillException>(() =>
            CreateApi("wrong").Messages.SendAsync(new MandrillMessage("shop@example.com", "alice@example.com", "x", "x")));

        Assert.Equal("Invalid_Key", ex.Name);
        Assert.Empty(_factory.Store.Query(new()));
    }
}
