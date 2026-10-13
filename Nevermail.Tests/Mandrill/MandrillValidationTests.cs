using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Nevermail.Tests.Mandrill;

public sealed class MandrillValidationTests : IDisposable
{
    private readonly NevermailFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> PostAsync(string path, string body) =>
        await _factory.CreateClient().PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));

    private const string ValidMessage = """
        { "key": "test_api_key", "message": { "from_email": "from@example.com", "to": [{ "email": "to@example.com" }], "subject": "Hi", "html": "<p>hi</p>" } }
        """;

    [Theory]
    [InlineData("/api/1.0/messages/send.json")]
    [InlineData("/api/1.0/messages/send")]
    [InlineData("/api/1.3/messages/send")]
    [InlineData("/api/1.4/messages/send")]
    public async Task AllVersionsAndSuffixes_Work(string path)
    {
        var response = await PostAsync(path, ValidMessage);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal("to@example.com", result.GetProperty("email").GetString());
        Assert.Equal("sent", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownVersion_Returns404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/api/2.0/messages/send", ValidMessage)).StatusCode);
    }

    [Fact]
    public async Task Ping2_Works()
    {
        var response = await PostAsync("/api/1.0/users/ping2.json", """{ "key": "test_api_key" }""");

        Assert.Equal("PONG!", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("PING").GetString());
    }

    public static TheoryData<string, string, string> InvalidRequests => new()
    {
        { """{ "message": { "from_email": "a@example.com", "to": [{ "email": "to@example.com" }] } }""", "Invalid_Key", "" },
        { """{ "key": "test_api_key" }""", "ValidationError", "message" },
        { """{ "key": "test_api_key", "message": { "from_email": "a@example.com" } }""", "ValidationError", "message.to" },
        { """{ "key": "test_api_key", "message": { "to": [{ "email": "to@example.com" }] } }""", "ValidationError", "from_email" },
        { """{ "key": "test_api_key", "message": { "from_email": "nope", "to": [{ "email": "to@example.com" }] } }""", "ValidationError", "from_email" },
        { """{ "key": "test_api_key", "message": { "from_email": "a@example.com", "to": [{ "email": "to@example.com", "type": "reply" }] } }""", "ValidationError", "type" },
        { """{ "key": "test_api_key", "message": { "from_email": "a@example.com", "to": [{ "email": "to@example.com" }], "tags": ["_internal"] } }""", "ValidationError", "tags" },
        { """{ "key": "test_api_key", "message": { "from_email": "a@example.com", "to": [{ "email": "to@example.com" }], "images": [{ "type": "text/plain", "name": "x", "content": "aGVsbG8=" }] } }""", "ValidationError", "images" },
        { """{ "key": "test_api_key", "send_at": "tomorrow", "message": { "from_email": "a@example.com", "to": [{ "email": "to@example.com" }] } }""", "ValidationError", "send_at" },
        { """{ "key": "test_api_key", "message": { "from_email": "a@example.com", "to": "to@example.com" } }""", "ValidationError", "message.to" },
        { "not json", "ValidationError", "JSON" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidRequest_Returns500WithMandrillError(string body, string expectedName, string expectedInMessage)
    {
        var response = await PostAsync("/api/1.0/messages/send.json", body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal(expectedName, json.GetProperty("name").GetString());
        Assert.Contains(expectedInMessage, json.GetProperty("message").GetString());
        Assert.Empty(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task SendTemplate_WithoutFromEmail_IsAccepted()
    {
        var response = await PostAsync("/api/1.0/messages/send-template.json",
            """{ "key": "test_api_key", "template_name": "welcome", "message": { "to": [{ "email": "to@example.com" }] } }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(_factory.Store.Query(new())).Message.From);
    }

    [Fact]
    public async Task Results_KeepRecipientOrder()
    {
        var response = await PostAsync("/api/1.0/messages/send.json", """
            { "key": "test_api_key", "message": { "from_email": "a@example.com",
              "to": [{ "email": "one@example.com" }, { "email": "broken" }, { "email": "three@example.com" }] } }
            """);

        var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Select(r => (r.GetProperty("email").GetString(), r.GetProperty("status").GetString()));
        Assert.Equal([("one@example.com", "sent"), ("broken", "invalid"), ("three@example.com", "sent")], results);
    }

    [Fact]
    public async Task SendTemplate_RequiresTemplateName()
    {
        var response = await PostAsync("/api/1.0/messages/send-template.json", ValidMessage);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("template_name", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }
}
