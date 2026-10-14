using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nevermail.Mail;

namespace Nevermail.Tests.AzureEmail;

public sealed class AzureEmailValidationTests : IDisposable
{
    private const string HmacAuthorization = "HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature=c2lnbmF0dXJl";

    private readonly NevermailFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static JsonObject ValidRequest() => JsonNode.Parse("""
        {
          "senderAddress": "from@example.com",
          "recipients": { "to": [{ "address": "to@example.com", "displayName": "To" }] },
          "content": { "subject": "Subject", "html": "<p>Body</p>" }
        }
        """)!.AsObject();

    private static HttpRequestMessage Signed(HttpMethod method, string url, string? authorization = HmacAuthorization)
    {
        var request = new HttpRequestMessage(method, url);
        if (authorization != null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.Add("x-ms-date", DateTimeOffset.UtcNow.ToString("R"));
            request.Headers.Add("x-ms-content-sha256", "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=");
        }
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(string body, string? authorization = HmacAuthorization, string apiVersion = "2025-09-01", string? operationId = null)
    {
        var request = Signed(HttpMethod.Post, $"/emails:send?api-version={apiVersion}", authorization);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (operationId != null)
        {
            request.Headers.Add("Operation-Id", operationId);
        }
        return await _factory.CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");

    [Fact]
    public async Task ValidRequest_Returns202WithOperationLocation()
    {
        var response = await SendAsync(ValidRequest().ToJsonString());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("id").GetString();
        Assert.Equal("Running", json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("error").ValueKind);
        Assert.Equal($"http://localhost/emails/operations/{id}?api-version=2025-09-01", response.Headers.GetValues("Operation-Location").Single());
        Assert.Equal(id, Assert.Single(_factory.Store.Query(new())).RequestId);

        var poll = await _factory.CreateClient().SendAsync(Signed(HttpMethod.Get, $"/emails/operations/{id}?api-version=2025-09-01"));
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        Assert.Equal("Succeeded", (await poll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task OperationId_IsUsedAndRetriesDontSendTwice()
    {
        var first = await SendAsync(ValidRequest().ToJsonString(), operationId: "5a1c34f4-0000-4000-8000-000000000001");
        var second = await SendAsync(ValidRequest().ToJsonString(), operationId: "5a1c34f4-0000-4000-8000-000000000001");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("5a1c34f4-0000-4000-8000-000000000001", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        Assert.Single(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task BccOnly_PlainTextOnly_IsAccepted()
    {
        var request = ValidRequest();
        request["recipients"] = JsonNode.Parse("""{ "bcc": [{ "address": "bcc@example.com" }] }""");
        request["content"] = JsonNode.Parse("""{ "subject": "s", "plainText": "text" }""");

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(request.ToJsonString())).StatusCode);
    }

    [Fact]
    public async Task ContentTypeWithParameters_IsAccepted()
    {
        var request = ValidRequest();
        request["attachments"] = JsonNode.Parse("""[{ "name": "a.txt", "contentType": "Text/Plain; charset=utf-8", "contentInBase64": "aGVsbG8=" }]""");

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(request.ToJsonString())).StatusCode);
    }

    [Theory]
    [InlineData("""{ "senderAddress": null }""", "senderAddress")]
    [InlineData("""{ "senderAddress": "Shop <shop@example.com>" }""", "senderAddress")]
    [InlineData("""{ "content": null }""", "content")]
    [InlineData("""{ "content": { "html": "<p>x</p>" } }""", "content.subject")]
    [InlineData("""{ "content": { "subject": "s" } }""", "content")]
    [InlineData("""{ "recipients": null }""", "recipients")]
    [InlineData("""{ "recipients": { "to": [] } }""", "recipients")]
    [InlineData("""{ "recipients": { "to": [{ "displayName": "x" }] } }""", "recipients.to[0].address")]
    [InlineData("""{ "recipients": { "to": [{ "address": "to@example.com" }], "cc": [{ "address": "nope" }] } }""", "recipients.cc[0].address")]
    [InlineData("""{ "replyTo": [{ "address": "nope" }] }""", "replyTo[0].address")]
    [InlineData("""{ "headers": { "X-Null": null } }""", "headers.X-Null")]
    [InlineData("""{ "attachments": [{ "contentType": "text/plain", "contentInBase64": "aGVsbG8=" }] }""", "attachments[0].name")]
    [InlineData("""{ "attachments": [{ "name": "a.exe", "contentType": "application/x-msdownload", "contentInBase64": "aGVsbG8=" }] }""", "attachments[0].contentType")]
    [InlineData("""{ "attachments": [{ "name": "a.txt", "contentType": "text/plain", "contentInBase64": "not base64!" }] }""", "attachments[0].contentInBase64")]
    [InlineData("""{ "recipients": { "to": "to@example.com" } }""", "recipients.to")]
    public async Task InvalidRequest_Returns400WithProperty(string patch, string property)
    {
        var request = ValidRequest();
        foreach (var (key, value) in JsonNode.Parse(patch)!.AsObject())
        {
            request[key] = value?.DeepClone();
        }

        var response = await SendAsync(request.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BadRequest", response.Headers.GetValues("x-ms-error-code").Single());
        var error = await ErrorAsync(response);
        Assert.Equal("BadRequest", error.GetProperty("code").GetString());
        Assert.EndsWith($"See property '{property}'", error.GetProperty("message").GetString());
        Assert.Empty(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task TooManyRecipients_Returns400()
    {
        var request = ValidRequest();
        request["recipients"] = new JsonObject
        {
            ["to"] = new JsonArray(Enumerable.Range(0, 51).Select(i => (JsonNode)new JsonObject { ["address"] = $"r{i}@example.com" }).ToArray()),
        };

        var response = await SendAsync(request.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at most 50 recipients", (await ErrorAsync(response)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task MalformedJson_Returns400()
    {
        var response = await SendAsync("{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Request body validation error. Invalid Json payload.", (await ErrorAsync(response)).GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer ")]
    public async Task MissingOrMalformedAuthorization_Returns401(string? authorization)
    {
        var response = await SendAsync(ValidRequest().ToJsonString(), authorization);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Denied", (await ErrorAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task HmacWithoutDateHeader_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/emails:send?api-version=2025-09-01")
        {
            Content = new StringContent(ValidRequest().ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", HmacAuthorization);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task BearerToken_IsAccepted()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(ValidRequest().ToJsonString(), "Bearer eyJ0eXAiOiJKV1QifQ")).StatusCode);
    }

    [Theory]
    [InlineData("", HttpStatusCode.BadRequest)]
    [InlineData("latest", HttpStatusCode.BadRequest)]
    [InlineData("2023-03-31", HttpStatusCode.Accepted)]
    [InlineData("2099-01-15-preview", HttpStatusCode.Accepted)]
    public async Task ApiVersion_IsRequired(string apiVersion, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await SendAsync(ValidRequest().ToJsonString(), apiVersion: apiVersion)).StatusCode);
    }

    [Fact]
    public async Task UnknownOperation_Returns404()
    {
        var response = await _factory.CreateClient().SendAsync(Signed(HttpMethod.Get, "/emails/operations/unknown?api-version=2025-09-01"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NotFound", (await ErrorAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Poll_FallsBackToStoredMail()
    {
        // E.g. after a restart with persistent storage, when the operation itself is no longer known.
        await _factory.Store.AddAsync([new StoredMail { Provider = "azure", RequestId = "op-from-before-restart" }]);

        var response = await _factory.CreateClient().SendAsync(Signed(HttpMethod.Get, "/emails/operations/op-from-before-restart?api-version=2025-09-01"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Succeeded", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Disabled_EndpointsAreGone()
    {
        using var factory = new NevermailFactory(new() { ["Providers:Azure:Enabled"] = "false" });
        var request = Signed(HttpMethod.Post, "/emails:send?api-version=2025-09-01");
        request.Content = new StringContent(ValidRequest().ToJsonString(), Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }
}
