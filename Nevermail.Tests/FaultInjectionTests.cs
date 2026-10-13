using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nevermail.Providers;

namespace Nevermail.Tests;

public sealed class FaultInjectionTests
{
    private static string SendGridBody(string to) => $$"""
        { "personalizations": [{ "to": [{ "email": "ok@example.com" }, { "email": "{{to}}" }] }],
          "from": { "email": "from@example.com" }, "subject": "s", "content": [{ "type": "text/plain", "value": "b" }] }
        """;

    private static async Task<HttpResponseMessage> SendGridAsync(NevermailFactory factory, string to)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", NevermailFactory.ApiKey);
        return await client.PostAsync("/v3/mail/send", new StringContent(SendGridBody(to), Encoding.UTF8, "application/json"));
    }

    [Theory]
    [InlineData("someone@example.com", false, 0)]
    [InlineData("someone@fail-429.test", true, 429)]
    [InlineData("SOMEONE@FAIL-503.TEST", true, 503)]
    [InlineData("someone@fail-abc.test", false, 0)]
    [InlineData("someone@fail-.test", false, 0)]
    [InlineData("fail-429.test", false, 0)]
    [InlineData(null, false, 0)]
    public void TriggerAddresses_AreRecognized(string? email, bool expected, int expectedCode)
    {
        Assert.Equal(expected, FaultInjector.TryGetTriggerCode(email, out var code));
        Assert.Equal(expectedCode, expected ? code : 0);
    }

    [Fact]
    public async Task SendGrid_TriggerRecipient_FailsWithSendGridError()
    {
        using var factory = new NevermailFactory();

        var response = await SendGridAsync(factory, "limited@fail-429.test");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter!.Delta);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors")[0];
        Assert.Contains("limited@fail-429.test", error.GetProperty("message").GetString());
        Assert.Empty(factory.Store.Query(new()));
    }

    [Fact]
    public async Task Brevo_TriggerRecipient_FailsWithBrevoError()
    {
        using var factory = new NevermailFactory();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v3/smtp/email")
        {
            Content = new StringContent("""
                { "sender": { "email": "from@example.com" }, "to": [{ "email": "x@fail-500.test" }], "subject": "s", "htmlContent": "b" }
                """, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("api-key", NevermailFactory.ApiKey);

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mandrill_TriggerRecipient_FailsWithMandrillError()
    {
        using var factory = new NevermailFactory();

        var response = await factory.CreateClient().PostAsync("/api/1.0/messages/send.json", new StringContent("""
            { "key": "test_api_key", "message": { "from_email": "a@example.com", "to": [{ "email": "x@fail-402.test" }] } }
            """, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PaymentRequired", json.GetProperty("name").GetString());
        Assert.Empty(factory.Store.Query(new()));
    }

    [Fact]
    public async Task Rate_FailsEverySendWhenOne()
    {
        using var factory = new NevermailFactory(new() { ["Faults:Rate"] = "1", ["Faults:StatusCode"] = "502" });

        var response = await SendGridAsync(factory, "normal@example.com");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Rate_Zero_NeverFails()
    {
        using var factory = new NevermailFactory(new() { ["Faults:Rate"] = "0" });

        Assert.Equal(HttpStatusCode.Accepted, (await SendGridAsync(factory, "normal@example.com")).StatusCode);
    }

    [Fact]
    public async Task Delay_SlowsDownSends()
    {
        using var factory = new NevermailFactory(new() { ["Faults:DelayMs"] = "300" });
        var stopwatch = Stopwatch.StartNew();

        await SendGridAsync(factory, "normal@example.com");

        Assert.True(stopwatch.ElapsedMilliseconds >= 280, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Smtp_TriggerRecipient_IsRejectedOthersAccepted()
    {
        using var factory = new NevermailFactory();
        using var client = new TcpClient();
        await client.ConnectAsync("localhost", factory.SmtpPort);
        var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        async Task<string> Command(string line)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
            string reply;
            do { reply = (await reader.ReadLineAsync())!; } while (reply.Length > 3 && reply[3] == '-');
            return reply;
        }
        await reader.ReadLineAsync();

        await Command("EHLO x");
        await Command("MAIL FROM:<a@example.com>");
        Assert.StartsWith("550 5.0.0", await Command("RCPT TO:<bounce@fail-550.test>"));
        Assert.StartsWith("450 4.0.0", await Command("RCPT TO:<busy@fail-450.test>"));
        Assert.StartsWith("250", await Command("RCPT TO:<ok@example.com>"));
        Assert.StartsWith("354", await Command("DATA"));
        Assert.StartsWith("250", await Command("Subject: partial\r\n\r\nbody\r\n."));

        var mail = Assert.Single(factory.Store.Query(new()));
        Assert.Equal("ok@example.com", mail.Message.Metadata["smtp.rcpt_to"]);
    }

    [Fact]
    public async Task Smtp_Rate_FailsAtEndOfData()
    {
        using var factory = new NevermailFactory(new() { ["Faults:Rate"] = "1" });
        var message = new MimeKit.MimeMessage();
        message.From.Add(MimeKit.MailboxAddress.Parse("a@example.com"));
        message.To.Add(MimeKit.MailboxAddress.Parse("b@example.com"));
        message.Body = new MimeKit.TextPart("plain") { Text = "hi" };
        using var smtp = new MailKit.Net.Smtp.SmtpClient();
        await smtp.ConnectAsync("localhost", factory.SmtpPort, MailKit.Security.SecureSocketOptions.None);

        var ex = await Assert.ThrowsAsync<MailKit.Net.Smtp.SmtpCommandException>(() => smtp.SendAsync(message));

        Assert.Equal(451, (int)ex.StatusCode);
        Assert.Empty(factory.Store.Query(new()));
    }
}
