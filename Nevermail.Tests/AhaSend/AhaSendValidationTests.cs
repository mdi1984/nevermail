using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nevermail.Tests.AhaSend;

public sealed class AhaSendValidationTests : IDisposable
{
    private const string AccountPath = "/v2/accounts/4cdd7bc5-1e8d-4b3c-9c1a-6b5f8e2d7a10/messages";

    private readonly NevermailFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static JsonObject ValidRequest() => JsonNode.Parse("""
        {
          "from": { "email": "from@example.com" },
          "recipients": [{ "email": "to@example.com" }],
          "subject": "Subject",
          "html_content": "<p>Body</p>"
        }
        """)!.AsObject();

    private async Task<HttpResponseMessage> PostAsync(string body, string? apiKey = NevermailFactory.ApiKey, string path = AccountPath)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (apiKey != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task AssertRejectedAsync(JsonObject request, string expectedMessagePart)
    {
        var response = await PostAsync(request.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(expectedMessagePart, json.GetProperty("message").GetString());
        Assert.Single(json.EnumerateObject());
        Assert.Empty(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task ValidRequest_Returns202()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(ValidRequest().ToJsonString())).StatusCode);
    }

    [Fact]
    public async Task TextContentOnly_IsAccepted()
    {
        var request = ValidRequest();
        request.Remove("html_content");
        request["text_content"] = "plain";

        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(request.ToJsonString())).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic dGVzdA==")]
    [InlineData("Bearer wrong")]
    public async Task MissingOrWrongKey_Returns401(string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AccountPath)
        {
            Content = new StringContent(ValidRequest().ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (authorization != null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("message", out _));
    }

    [Fact]
    public async Task WithoutConfiguredKey_AnyKeyIsAccepted()
    {
        using var factory = new NevermailFactory(new() { ["Providers:AhaSend:ApiKey"] = "" });
        var request = new HttpRequestMessage(HttpMethod.Post, AccountPath)
        {
            Content = new StringContent(ValidRequest().ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "aha-sk-anything");

        Assert.Equal(HttpStatusCode.Accepted, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task InvalidAccountId_Returns404()
    {
        var response = await PostAsync(ValidRequest().ToJsonString(), path: "/v2/accounts/not-a-uuid/messages");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJson_Returns400()
    {
        var response = await PostAsync("{not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid request body", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    [Fact]
    public async Task WrongType_NamesTheField()
    {
        var request = ValidRequest();
        request["recipients"] = "to@example.com";

        await AssertRejectedAsync(request, "recipients");
    }

    [Fact]
    public Task MissingFrom_IsRejected()
    {
        var request = ValidRequest();
        request.Remove("from");
        return AssertRejectedAsync(request, "from.email");
    }

    [Fact]
    public Task InvalidFrom_IsRejected()
    {
        var request = ValidRequest();
        request["from"] = new JsonObject { ["email"] = "not-an-address" };
        return AssertRejectedAsync(request, "from.email");
    }

    [Fact]
    public Task NoRecipients_IsRejected()
    {
        var request = ValidRequest();
        request["recipients"] = new JsonArray();
        return AssertRejectedAsync(request, "recipients");
    }

    [Fact]
    public Task TooManyRecipients_IsRejected()
    {
        var request = ValidRequest();
        request["recipients"] = new JsonArray(Enumerable.Range(0, 101).Select(i => (JsonNode)new JsonObject { ["email"] = $"r{i}@example.com" }).ToArray());
        return AssertRejectedAsync(request, "at most 100");
    }

    [Fact]
    public Task InvalidRecipient_IsRejected()
    {
        var request = ValidRequest();
        request["recipients"]!.AsArray().Add(new JsonObject { ["email"] = "bad" });
        return AssertRejectedAsync(request, "recipients[1].email");
    }

    [Fact]
    public Task MissingSubject_IsRejected()
    {
        var request = ValidRequest();
        request.Remove("subject");
        return AssertRejectedAsync(request, "subject");
    }

    [Fact]
    public Task MissingContent_IsRejected()
    {
        var request = ValidRequest();
        request.Remove("html_content");
        return AssertRejectedAsync(request, "text_content or html_content");
    }

    [Fact]
    public Task InvalidReplyTo_IsRejected()
    {
        var request = ValidRequest();
        request["reply_to"] = new JsonObject { ["email"] = "nope" };
        return AssertRejectedAsync(request, "reply_to.email");
    }

    [Fact]
    public Task ReplyToHeaderWithReplyTo_IsRejected()
    {
        var request = ValidRequest();
        request["reply_to"] = new JsonObject { ["email"] = "support@example.com" };
        request["headers"] = new JsonObject { ["Reply-To"] = "other@example.com" };
        return AssertRejectedAsync(request, "reply-to");
    }

    [Theory]
    [InlineData("X Space")]
    [InlineData("X:Colon")]
    [InlineData("")]
    public Task InvalidHeaderName_IsRejected(string name)
    {
        var request = ValidRequest();
        request["headers"] = new JsonObject { [name] = "value" };
        return AssertRejectedAsync(request, "header name");
    }

    [Fact]
    public Task HeaderValueWithLineBreak_IsRejected()
    {
        var request = ValidRequest();
        request["headers"] = new JsonObject { ["X-Injected"] = "a\r\nBcc: evil@example.com" };
        return AssertRejectedAsync(request, "X-Injected");
    }

    [Fact]
    public Task TemplateSyntaxInSubstitutionValue_IsRejected()
    {
        var request = ValidRequest();
        request["recipients"]![0]!["substitutions"] = new JsonObject { ["name"] = "{{ evil }}" };
        return AssertRejectedAsync(request, "recipients[0].substitutions.name");
    }

    [Theory]
    [InlineData("data")]
    [InlineData("content_type")]
    [InlineData("file_name")]
    public Task AttachmentMissingField_IsRejected(string field)
    {
        var attachment = new JsonObject { ["data"] = "aGk=", ["base64"] = true, ["content_type"] = "text/plain", ["file_name"] = "a.txt" };
        attachment.Remove(field);
        var request = ValidRequest();
        request["attachments"] = new JsonArray(attachment);
        return AssertRejectedAsync(request, $"attachments[0].{field}");
    }

    [Fact]
    public Task AttachmentInvalidBase64_IsRejected()
    {
        var request = ValidRequest();
        request["attachments"] = new JsonArray(new JsonObject
        {
            ["data"] = "not base64!", ["base64"] = true, ["content_type"] = "text/plain", ["file_name"] = "a.txt",
        });
        return AssertRejectedAsync(request, "base64");
    }

    [Fact]
    public Task UnknownSandboxResult_IsRejected()
    {
        var request = ValidRequest();
        request["sandbox"] = true;
        request["sandbox_result"] = "explode";
        return AssertRejectedAsync(request, "sandbox_result");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(31, 0)]
    [InlineData(7, 31)]
    public Task RetentionOutOfRange_IsRejected(int metadata, int data)
    {
        var request = ValidRequest();
        request["retention"] = new JsonObject { ["metadata"] = metadata, ["data"] = data };
        return AssertRejectedAsync(request, "retention");
    }

    [Theory]
    [InlineData(-1, null, "first_attempt")]
    [InlineData(24 * 8, null, "first_attempt")]
    [InlineData(null, 24 * 9, "expires")]
    [InlineData(5, 2, "after schedule.first_attempt")]
    public Task InvalidSchedule_IsRejected(int? firstAttemptHours, int? expiresHours, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new JsonObject();
        if (firstAttemptHours is { } first)
        {
            schedule["first_attempt"] = now.AddHours(first).ToString("O");
        }
        if (expiresHours is { } expires)
        {
            schedule["expires"] = now.AddHours(expires).ToString("O");
        }
        var request = ValidRequest();
        request["schedule"] = schedule;
        return AssertRejectedAsync(request, expected);
    }

    [Fact]
    public async Task TooLongIdempotencyKey_IsRejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AccountPath)
        {
            Content = new StringContent(ValidRequest().ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NevermailFactory.ApiKey);
        request.Headers.Add("Idempotency-Key", new string('k', 256));

        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.CreateClient().SendAsync(request)).StatusCode);
    }
}
