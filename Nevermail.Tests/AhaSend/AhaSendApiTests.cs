using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nevermail.Tests.AhaSend;

/// <summary>
/// AhaSend has no .NET SDK, so these send raw HTTP requests shaped like the API v2 docs and the official SDKs.
/// </summary>
public sealed class AhaSendApiTests : IDisposable
{
    private const string AccountId = "4cdd7bc5-1e8d-4b3c-9c1a-6b5f8e2d7a10";
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private readonly NevermailFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> PostAsync(string body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v2/accounts/{AccountId}/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NevermailFactory.ApiKey);
        if (idempotencyKey != null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return await _factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task FullMessage_IsStored()
    {
        var body = $$"""
            {
              "from": { "email": "shop@example.com", "name": "Shop" },
              "recipients": [{ "email": "alice@example.com", "name": "Alice" }],
              "reply_to": { "email": "support@example.com", "name": "Support" },
              "subject": "Order confirmation",
              "html_content": "<p>Thanks!</p><img src=\"cid:logo\">",
              "text_content": "Thanks!",
              "attachments": [
                { "data": "{{PngBase64}}", "base64": true, "content_type": "image/png", "file_name": "logo.png", "content_id": "<logo>" },
                { "data": "hello", "content_type": "text/plain", "file_name": "note.txt" }
              ],
              "headers": { "X-Order": "42", "Message-ID": "<ignored@example.com>" },
              "tags": ["orders"],
              "tracking": { "open": true, "click": false },
              "retention": { "metadata": 7, "data": 0 }
            }
            """;

        var response = await PostAsync(body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("list", json.GetProperty("object").GetString());
        var item = Assert.Single(json.GetProperty("data").EnumerateArray());
        Assert.Equal("message", item.GetProperty("object").GetString());
        Assert.Equal("queued", item.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("error").ValueKind);
        Assert.Equal("alice@example.com", item.GetProperty("recipient").GetProperty("email").GetString());
        Assert.False(item.TryGetProperty("schedule", out _));

        var mail = Assert.Single(_factory.Store.Query(new()));
        Assert.Equal($"<{mail.Id}@nevermail.local>", item.GetProperty("id").GetString());
        Assert.Equal("ahasend", mail.Provider);
        Assert.False(mail.Sandbox);
        var m = mail.Message;
        Assert.Equal(new Mail.MailAddress("shop@example.com", "Shop"), m.From);
        Assert.Equal(new Mail.MailAddress("alice@example.com", "Alice"), Assert.Single(m.To));
        Assert.Equal("support@example.com", Assert.Single(m.ReplyTo).Email);
        Assert.Equal("Order confirmation", m.Subject);
        Assert.Equal("<p>Thanks!</p><img src=\"cid:logo\">", m.Html);
        Assert.Equal("Thanks!", m.Text);
        Assert.Equal("42", m.Headers["X-Order"]);
        Assert.Equal($"<{mail.Id}@nevermail.local>", m.Headers["Message-ID"]);
        Assert.Equal(["orders"], m.Tags);

        Assert.Equal(2, m.Attachments.Count);
        var logo = m.Attachments[0];
        Assert.True(logo.Inline);
        Assert.Equal("logo", logo.ContentId);
        Assert.Equal(PngBase64, logo.Content);
        var note = m.Attachments[1];
        Assert.False(note.Inline);
        Assert.Equal("text/plain", note.ContentType);
        Assert.Equal("hello", Encoding.UTF8.GetString(Convert.FromBase64String(note.Content)));
    }

    [Fact]
    public async Task EachRecipient_GetsTheirOwnMailWithSubstitutions()
    {
        var body = """
            {
              "from": { "email": "shop@example.com" },
              "recipients": [
                { "email": "alice@example.com", "substitutions": { "name": "Alice & Co", "order": { "id": 111 } } },
                { "email": "bob@example.com" }
              ],
              "subject": "Hello {{ name }}",
              "html_content": "<p>{{ name }} / {{ name|safe }} / {{ order.id }} / {{ unknown }} / {{ name|upper }}</p>",
              "text_content": "Hi {{name}}",
              "headers": { "X-Name": "{{ name }}" },
              "substitutions": { "name": "you", "order": { "id": 0 } }
            }
            """;

        var response = await PostAsync(body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("data").GetArrayLength());

        var mails = _factory.Store.Query(new());
        Assert.Equal(2, mails.Count);
        Assert.Single(mails.Select(m => m.RequestId).Distinct());

        var alice = mails.Single(m => m.Message.To[0].Email == "alice@example.com").Message;
        Assert.Equal("Hello Alice & Co", alice.Subject);
        Assert.Equal("<p>Alice &amp; Co / Alice & Co / 111 / {{ unknown }} / {{ name|upper }}</p>", alice.Html);
        Assert.Equal("Hi Alice & Co", alice.Text);
        Assert.Equal("Alice & Co", alice.Headers["X-Name"]);
        Assert.Single(alice.To);

        var bob = mails.Single(m => m.Message.To[0].Email == "bob@example.com").Message;
        Assert.Equal("Hello you", bob.Subject);
        Assert.Equal("Hi you", bob.Text);
    }

    [Fact]
    public async Task WithoutSubstitutions_TemplatesAreLeftAsWritten()
    {
        var body = """{"from":{"email":"a@example.com"},"recipients":[{"email":"b@example.com"}],"subject":"{{ name }}","text_content":"x"}""";

        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(body)).StatusCode);

        Assert.Equal("{{ name }}", Assert.Single(_factory.Store.Query(new())).Message.Subject);
    }

    [Fact]
    public async Task Sandbox_IsCapturedAndFlagged()
    {
        var body = """{"from":{"email":"a@example.com"},"recipients":[{"email":"b@example.com"}],"subject":"S","text_content":"x","sandbox":true,"sandbox_result":"bounce"}""";

        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(body)).StatusCode);

        var mail = Assert.Single(_factory.Store.Query(new()));
        Assert.True(mail.Sandbox);
        Assert.Equal("bounce", mail.Message.Metadata["sandbox_result"]);
    }

    [Fact]
    public async Task Schedule_IsEchoedAndStored()
    {
        var first = DateTimeOffset.UtcNow.AddHours(2);
        var request = JsonNode.Parse("""{"from":{"email":"a@example.com"},"recipients":[{"email":"b@example.com"}],"subject":"S","text_content":"x"}""")!.AsObject();
        request["schedule"] = new JsonObject
        {
            ["first_attempt"] = first.ToString("O"),
            ["expires"] = first.AddDays(1).ToString("O"),
        };

        var response = await PostAsync(request.ToJsonString());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")[0];
        Assert.Equal("scheduled", item.GetProperty("status").GetString());
        Assert.Equal(first, item.GetProperty("schedule").GetProperty("first_attempt").GetDateTimeOffset());
        Assert.Equal(first, Assert.Single(_factory.Store.Query(new())).Message.SendAt);
    }

    [Fact]
    public async Task IdempotencyKey_ReplaysWithoutSendingTwice()
    {
        var body = """{"from":{"email":"a@example.com"},"recipients":[{"email":"b@example.com"}],"subject":"S","text_content":"x"}""";

        var first = await PostAsync(body, "order-42");
        var second = await PostAsync(body, "order-42");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Single(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task IdempotencyKey_ReusedForDifferentBody_Returns422()
    {
        var body = """{"from":{"email":"a@example.com"},"recipients":[{"email":"b@example.com"}],"subject":"S","text_content":"x"}""";

        await PostAsync(body, "key");
        var response = await PostAsync(body.Replace("\"S\"", "\"Other\""), "key");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task IdempotencyKey_ValidationErrorsAreReplayed_FaultsAreNot()
    {
        var invalid = """{"from":{"email":"a@example.com"},"recipients":[]}""";
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(invalid, "bad")).StatusCode);
        var replay = await PostAsync(invalid, "bad");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());

        foreach (var code in new[] { 503, 429 })
        {
            var failing = $$"""{"from":{"email":"a@example.com"},"recipients":[{"email":"b@fail-{{code}}.test"}],"subject":"S","text_content":"x"}""";
            Assert.Equal((HttpStatusCode)code, (await PostAsync(failing, $"key-{code}")).StatusCode);
            var retry = await PostAsync(failing, $"key-{code}");
            Assert.Equal((HttpStatusCode)code, retry.StatusCode);
            Assert.False(retry.Headers.Contains("Idempotent-Replayed"));
        }
    }

    [Fact]
    public async Task FaultTrigger_ReturnsAhaSendError()
    {
        var body = """{"from":{"email":"a@example.com"},"recipients":[{"email":"b@fail-429.test"}],"subject":"S","text_content":"x"}""";

        var response = await PostAsync(body);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("429", json.GetProperty("message").GetString());
        Assert.Empty(_factory.Store.Query(new()));
    }
}
