using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nevermail.Mail;

namespace Nevermail.Tests;

public sealed class MailApiTests : IDisposable
{
    private readonly NevermailFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<StoredMail> AddAsync(string subject, string to = "to@example.com")
    {
        var mail = new StoredMail
        {
            Provider = "sendgrid",
            RequestId = "req-" + subject,
            Message = new MailMessage
            {
                From = new MailAddress("from@example.com"),
                To = [new MailAddress(to)],
                Subject = subject,
                Html = "<b>hi</b>",
                Attachments = [new MailAttachment { Filename = "hello.txt", ContentType = "text/plain", Content = "aGVsbG8=" }],
            },
            RawRequest = "{}",
        };
        await _factory.Store.AddAsync([mail]);
        return mail;
    }

    [Fact]
    public async Task List_ReturnsSummaries()
    {
        await AddAsync("one", "alice@example.com");
        await AddAsync("two", "bob@example.com");

        var all = await _factory.ListMailsAsync();
        Assert.Equal(["two", "one"], all.Select(m => m.Subject));
        Assert.True(all[0].HasHtml);
        Assert.Equal(1, all[0].AttachmentCount);

        var filtered = await _factory.ListMailsAsync("?to=alice@example.com");
        Assert.Equal("one", Assert.Single(filtered).Subject);

        Assert.Single(await _factory.ListMailsAsync("?requestId=req-two"));
        Assert.Single(await _factory.ListMailsAsync("?limit=1"));
    }

    [Fact]
    public async Task List_DoesNotIncludeAttachmentContent()
    {
        await AddAsync("one");
        var client = _factory.CreateClient();

        var json = await client.GetStringAsync("/api/mails");

        Assert.DoesNotContain("aGVsbG8=", json);
    }

    [Fact]
    public async Task Get_ReturnsFullMail()
    {
        var mail = await AddAsync("one");
        var client = _factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>($"/api/mails/{mail.Id}");

        Assert.Equal("one", json.GetProperty("message").GetProperty("subject").GetString());
        Assert.Equal("{}", json.GetProperty("rawRequest").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/mails/doesnotexist")).StatusCode);
    }

    [Fact]
    public async Task Attachment_CanBeDownloaded()
    {
        var mail = await AddAsync("one");
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/mails/{mail.Id}/attachments/0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("hello.txt", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/mails/{mail.Id}/attachments/1")).StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesSingleOrAll()
    {
        var one = await AddAsync("one");
        await AddAsync("two");
        await AddAsync("three");
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/mails/{one.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/mails/{one.Id}")).StatusCode);
        Assert.Equal(2, (await _factory.ListMailsAsync()).Count);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/mails")).StatusCode);
        Assert.Empty(await _factory.ListMailsAsync());
    }

    [Fact]
    public async Task Health_IsOk()
    {
        var response = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
