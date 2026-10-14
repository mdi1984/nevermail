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
    public async Task Wait_ReturnsAsSoonAsAMatchingMailArrives()
    {
        var client = _factory.CreateClient();
        var waiting = client.GetFromJsonAsync<List<MailSummary>>("/api/mails?to=late@example.com&wait=10");
        await Task.Delay(200);
        await AddAsync("other", "someone@example.com");
        Assert.False(waiting.IsCompleted, "a non-matching mail must not end the wait");

        await AddAsync("late one", "late@example.com");

        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("late one", Assert.Single(result!).Subject);
    }

    [Fact]
    public async Task Wait_ForCount_WaitsForEnoughMails()
    {
        var client = _factory.CreateClient();
        var waiting = client.GetFromJsonAsync<List<MailSummary>>("/api/mails?wait=10&count=2");
        await AddAsync("first");
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);

        await AddAsync("second");

        Assert.Equal(2, (await waiting.WaitAsync(TimeSpan.FromSeconds(5)))!.Count);
    }

    [Fact]
    public async Task Wait_TimesOutWithWhatMatches()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await _factory.ListMailsAsync("?to=nobody@example.com&wait=1");

        Assert.Empty(result);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 900, 4000);
    }

    [Fact]
    public async Task Delete_WithFilter_OnlyDeletesMatches()
    {
        await AddAsync("keep", "keep@example.com");
        await AddAsync("drop 1", "drop@example.com");
        await AddAsync("drop 2", "drop@example.com");

        var response = await _factory.CreateClient().DeleteAsync("/api/mails?to=drop@example.com");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("keep", Assert.Single(await _factory.ListMailsAsync()).Subject);
    }

    [Fact]
    public async Task Info_ListsProvidersAndSmtpPort()
    {
        var info = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/info");

        Assert.Equal(["sendgrid", "brevo", "mandrill", "azure"], info.GetProperty("providers").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(_factory.SmtpPort, info.GetProperty("smtpPort").GetInt32());
    }

    [Fact]
    public async Task Events_StreamStoreChanges()
    {
        var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        async Task<(string Event, string Data)> NextEventAsync()
        {
            string? type = null, data = null;
            while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is { } line)
            {
                if (line.StartsWith("event: ")) type = line[7..];
                else if (line.StartsWith("data: ")) data = line[6..];
                else if (line.Length == 0 && type != null) return (type, data ?? "");
            }
            throw new EndOfStreamException();
        }

        Assert.Equal("ready", (await NextEventAsync()).Event);
        var mail = await AddAsync("live");
        Assert.Equal(("added", mail.Id), await NextEventAsync());
        _factory.Store.Delete(mail.Id);
        Assert.Equal(("deleted", mail.Id), await NextEventAsync());
        _factory.Store.Clear();
        Assert.Equal("cleared", (await NextEventAsync()).Event);
    }

    [Fact]
    public async Task Eml_IsAValidMimeMessage()
    {
        var mail = await AddAsync("Export me");
        mail.Message.Headers["X-Custom"] = "kept";
        mail.Message.Attachments.Add(new MailAttachment { Filename = "logo.png", ContentType = "image/png", ContentId = "logo", Inline = true, Content = "aGVsbG8=" });

        var response = await _factory.CreateClient().GetAsync($"/api/mails/{mail.Id}/eml");

        Assert.Equal("message/rfc822", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Export me.eml", response.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        var mime = await MimeKit.MimeMessage.LoadAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("Export me", mime.Subject);
        Assert.Equal("to@example.com", Assert.Single(mime.To.Mailboxes).Address);
        Assert.Equal("<b>hi</b>", mime.HtmlBody);
        Assert.Equal("kept", mime.Headers["X-Custom"]);
        Assert.Equal("sendgrid", mime.Headers["X-Nevermail-Provider"]);
        Assert.Equal("hello.txt", Assert.Single(mime.Attachments).ContentDisposition.FileName);
        Assert.Contains(mime.BodyParts, p => p.ContentId == "logo");
    }

    [Fact]
    public async Task Eml_EncodesNonAsciiText()
    {
        var mail = new StoredMail
        {
            Provider = "brevo",
            Message = new MailMessage
            {
                From = new MailAddress("joerg@example.com", "Jörg Müller"),
                To = [new MailAddress("to@example.com", "Zoë")],
                Subject = "Grüße ☕ – Bericht",
                Text = "Hällo",
                Attachments = [new MailAttachment { Filename = "Übersicht 2026.pdf", ContentType = "application/pdf", Content = "aGVsbG8=" }],
            },
        };
        await _factory.Store.AddAsync([mail]);

        var bytes = await _factory.CreateClient().GetByteArrayAsync($"/api/mails/{mail.Id}/eml");

        Assert.All(bytes, b => Assert.True(b < 128, "headers and base64 bodies must be 7-bit"));
        var mime = MimeKit.MimeMessage.Load(new MemoryStream(bytes));
        Assert.Equal("Grüße ☕ – Bericht", mime.Subject);
        Assert.Equal("Jörg Müller", Assert.Single(mime.From.Mailboxes).Name);
        Assert.Equal("Zoë", Assert.Single(mime.To.Mailboxes).Name);
        Assert.Equal("Hällo", mime.TextBody);
        var attachment = (MimeKit.MimePart)Assert.Single(mime.Attachments);
        Assert.Equal("Übersicht 2026.pdf", attachment.FileName);
    }

    [Fact]
    public async Task Eml_ForSmtpMail_IsTheOriginalMessage()
    {
        const string raw = "Subject: original\r\nFrom: a@example.com\r\n\r\nbody\r\n";
        var mail = new StoredMail { Provider = "smtp", RawRequest = raw, Message = new MailMessage { Subject = "original" } };
        await _factory.Store.AddAsync([mail]);

        var eml = await _factory.CreateClient().GetStringAsync($"/api/mails/{mail.Id}/eml");

        Assert.Equal(raw, eml);
    }

    [Fact]
    public async Task StaticFiles_AreRevalidated()
    {
        var response = await _factory.CreateClient().GetAsync("/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Health_IsOk()
    {
        var response = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
