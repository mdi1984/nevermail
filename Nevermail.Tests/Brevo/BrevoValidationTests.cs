using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nevermail.Tests.Brevo;

public sealed class BrevoValidationTests : IDisposable
{
    private readonly NevermailFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static JsonObject ValidRequest() => JsonNode.Parse("""
        {
          "sender": { "email": "from@example.com" },
          "to": [{ "email": "to@example.com" }],
          "subject": "Subject",
          "htmlContent": "<p>Body</p>"
        }
        """)!.AsObject();

    private async Task<HttpResponseMessage> PostAsync(string body, string? apiKey = NevermailFactory.ApiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v3/smtp/email")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (apiKey != null)
        {
            request.Headers.Add("api-key", apiKey);
        }
        return await _factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task ValidRequest_Returns201WithMessageId()
    {
        var response = await PostAsync(ValidRequest().ToJsonString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("<", json.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task TextContentOnly_IsAccepted()
    {
        var request = ValidRequest();
        request.Remove("htmlContent");
        request["textContent"] = "plain";

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(request.ToJsonString())).StatusCode);
    }

    [Fact]
    public async Task ScheduledMail_Returns202()
    {
        var request = ValidRequest();
        request["scheduledAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var response = await PostAsync(request.ToJsonString());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(Assert.Single(_factory.Store.Query(new())).Message.SendAt);
    }

    [Fact]
    public async Task UrlAttachment_IsStoredWithoutFetching()
    {
        var request = ValidRequest();
        request["attachment"] = JsonNode.Parse("""[{ "url": "https://example.com/files/invoice.pdf" }]""");

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(request.ToJsonString())).StatusCode);
        var attachment = Assert.Single(Assert.Single(_factory.Store.Query(new())).Message.Attachments);
        Assert.Equal("invoice.pdf", attachment.Filename);
        Assert.Equal("https://example.com/files/invoice.pdf", attachment.Url);
        Assert.Equal("", attachment.Content);

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var download = await client.GetAsync($"/api/mails/{_factory.Store.Query(new())[0].Id}/attachments/0");
        Assert.Equal(HttpStatusCode.Redirect, download.StatusCode);
        Assert.Equal("https://example.com/files/invoice.pdf", download.Headers.Location!.ToString());
    }

    public static TheoryData<string, string, string> InvalidRequests => new()
    {
        { """{ "sender": null }""", "missing_parameter", "sender" },
        { """{ "sender": { "email": "nope" } }""", "invalid_parameter", "sender" },
        { """{ "to": [] }""", "missing_parameter", "to" },
        { """{ "to": [{ "email": "nope" }] }""", "invalid_parameter", "to" },
        { """{ "cc": [{ "email": "nope" }] }""", "invalid_parameter", "cc" },
        { """{ "replyTo": { "email": "nope" } }""", "invalid_parameter", "replyTo" },
        { """{ "subject": null }""", "missing_parameter", "subject" },
        { """{ "htmlContent": null }""", "missing_parameter", "htmlContent" },
        { """{ "attachment": [{ "content": "aGVsbG8=" }] }""", "missing_parameter", "name" },
        { """{ "attachment": [{ "content": "aGVsbG8=", "name": "virus.exe" }] }""", "invalid_parameter", "extension" },
        { """{ "attachment": [{ "content": "not base64!", "name": "a.txt" }] }""", "invalid_parameter", "base64" },
        { """{ "messageVersions": [{ "subject": "x" }] }""", "missing_parameter", "messageVersions[0].to" },
        { """{ "messageVersions": [{ "to": [{ "email": "a@example.com" }] }], "cc": [{ "name": "no address" }] }""", "invalid_parameter", "cc" },
        { """{ "subject": 42 }""", "invalid_parameter", "subject" },
        { """{ "sender": { "email": "from@example.com", "name": "This sender name is far too long to be accepted by Brevo, which allows at most 70 chars" } }""", "invalid_parameter", "sender name" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidRequest_Returns400(string overrides, string expectedCode, string expectedInMessage)
    {
        var request = ValidRequest();
        foreach (var (key, value) in JsonNode.Parse(overrides)!.AsObject())
        {
            request[key] = value?.DeepClone();
        }

        var response = await PostAsync(request.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, json.GetProperty("code").GetString());
        Assert.Contains(expectedInMessage, json.GetProperty("message").GetString());
        Assert.Empty(_factory.Store.Query(new()));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task MalformedBody_Returns400(string body)
    {
        var response = await PostAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_request", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingKey_Returns401()
    {
        var response = await PostAsync(ValidRequest().ToJsonString(), apiKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
