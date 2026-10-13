using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nevermail.Tests.SendGrid;

/// <summary>Raw HTTP requests that the real SendGrid API would reject, checked for SendGrid-shaped errors.</summary>
public sealed class SendGridValidationTests : IDisposable
{
    private readonly NevermailFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static JsonObject ValidRequest() => JsonNode.Parse("""
        {
          "personalizations": [{ "to": [{ "email": "to@example.com" }] }],
          "from": { "email": "from@example.com" },
          "subject": "Subject",
          "content": [{ "type": "text/plain", "value": "Body" }]
        }
        """)!.AsObject();

    private async Task<HttpResponseMessage> PostAsync(string body, string? apiKey = NevermailFactory.ApiKey)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v3/mail/send")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (apiKey != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        return await client.SendAsync(request);
    }

    private static async Task<List<(string? Field, string Message)>> ReadErrorsAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("errors").EnumerateArray()
            .Select(e => (
                e.GetProperty("field").ValueKind == JsonValueKind.Null ? null : e.GetProperty("field").GetString(),
                e.GetProperty("message").GetString()!))
            .ToList();
    }

    [Fact]
    public async Task ValidRequest_IsAccepted()
    {
        var response = await PostAsync(ValidRequest().ToJsonString());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    public static TheoryData<string, string> InvalidRequests => new()
    {
        { """{ "from": null }""", "from" },
        { """{ "from": { "email": "not-an-address" } }""", "from.email" },
        { """{ "personalizations": [] }""", "personalizations" },
        { """{ "personalizations": [{ "to": [] }] }""", "personalizations.0.to" },
        { """{ "personalizations": [{ "to": [{ "email": "nope" }] }] }""", "personalizations.0.to.0.email" },
        { """{ "personalizations": [{ "to": [{ "email": "a@example.com" }], "cc": [{ "email": "A@example.com" }] }] }""", "personalizations.0.cc" },
        { """{ "subject": null }""", "subject" },
        { """{ "content": [] }""", "content" },
        { """{ "content": [{ "type": "text/plain", "value": "" }] }""", "content.0.value" },
        { """{ "content": [{ "type": "text/html", "value": "<b>x</b>" }, { "type": "text/plain", "value": "x" }] }""", "content" },
        { """{ "attachments": [{ "content": "aGVsbG8=" }] }""", "attachments.0.filename" },
        { """{ "attachments": [{ "filename": "a.txt", "content": "not base64!" }] }""", "attachments.0.content" },
        { """{ "attachments": [{ "filename": "a.png", "content": "aGVsbG8=", "disposition": "inline" }] }""", "attachments.0.content_id" },
        { """{ "reply_to": { "email": "nope" } }""", "reply_to.email" },
        { """{ "send_at": 99999999999 }""", "send_at" },
        { """{ "categories": ["1","2","3","4","5","6","7","8","9","10","11"] }""", "categories" },
        { """{ "subject": 42 }""", "subject" },
        { """{ "personalizations": [{ "to": [{ "email": "to@example.com" }], "headers": { "X-Num": 1 } }] }""", "personalizations.0.headers.X-Num" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidRequest_Returns400WithField(string overrides, string expectedField)
    {
        var request = ValidRequest();
        foreach (var (key, value) in JsonNode.Parse(overrides)!.AsObject())
        {
            request[key] = value?.DeepClone();
        }

        var response = await PostAsync(request.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ReadErrorsAsync(response);
        Assert.Contains(errors, e => e.Field == expectedField);
        Assert.Empty(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task TemplateRequest_DoesNotNeedSubjectOrContent()
    {
        var request = ValidRequest();
        request.Remove("subject");
        request.Remove("content");
        request["template_id"] = "d-123";

        var response = await PostAsync(request.ToJsonString());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task SubjectInEveryPersonalization_ReplacesGlobalSubject()
    {
        var request = ValidRequest();
        request.Remove("subject");
        request["personalizations"]![0]!["subject"] = "Per personalization";

        var response = await PostAsync(request.ToJsonString());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task MultipleErrors_AreAllReported()
    {
        var response = await PostAsync("""{ "personalizations": [{ "to": [] }], "content": [] }""");

        var fields = (await ReadErrorsAsync(response)).Select(e => e.Field).ToList();
        Assert.Contains("from", fields);
        Assert.Contains("personalizations.0.to", fields);
        Assert.Contains("subject", fields);
        Assert.Contains("content", fields);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{ "from": """)]
    [InlineData("null")]
    [InlineData("")]
    public async Task MalformedBody_Returns400(string body)
    {
        var response = await PostAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(await ReadErrorsAsync(response));
    }

    [Fact]
    public async Task MissingAuthorization_Returns401()
    {
        var response = await PostAsync(ValidRequest().ToJsonString(), apiKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("authorization required", Assert.Single(await ReadErrorsAsync(response)).Message);
    }

    [Fact]
    public async Task WithoutConfiguredKey_AnyKeyIsAccepted()
    {
        using var factory = new NevermailFactory(new() { ["Providers:SendGrid:ApiKey"] = null });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", "whatever");

        var response = await client.PostAsync("/v3/mail/send", new StringContent(ValidRequest().ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Scopes_AndBatch_Work()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", NevermailFactory.ApiKey);

        var scopes = await client.GetFromJsonAsync<JsonElement>("/v3/scopes");
        Assert.Contains("mail.send", scopes.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));

        var created = await client.PostAsync("/v3/mail/batch", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var batchId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("batch_id").GetString();

        var fetched = await client.GetFromJsonAsync<JsonElement>($"/v3/mail/batch/{batchId}");
        Assert.Equal(batchId, fetched.GetProperty("batch_id").GetString());
    }

    [Theory]
    [InlineData("$.personalizations[0].to[1].email", "personalizations.0.to.1.email")]
    [InlineData("$.subject", "subject")]
    [InlineData("$['personalizations'][0]", "personalizations.0")]
    [InlineData("$", null)]
    public void JsonPath_IsConvertedToSendGridField(string path, string? expected)
    {
        Assert.Equal(expected, Providers.ProviderHelpers.ToFieldPath(path));
    }
}
