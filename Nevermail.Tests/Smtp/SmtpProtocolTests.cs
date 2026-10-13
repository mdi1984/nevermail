using System.Net.Sockets;
using System.Text;

namespace Nevermail.Tests.Smtp;

/// <summary>Talks raw SMTP to check protocol details that well-behaved clients never exercise.</summary>
public sealed class SmtpProtocolTests : IDisposable
{
    private readonly NevermailFactory _factory = new(new() { ["Smtp:MaxMessageSize"] = "1000" });

    public void Dispose() => _factory.Dispose();

    private sealed class Connection : IDisposable
    {
        private readonly TcpClient _client = new();
        private NetworkStream _stream = null!;
        private StreamReader _reader = null!;

        public static async Task<Connection> OpenAsync(int port)
        {
            var connection = new Connection();
            await connection._client.ConnectAsync("localhost", port);
            connection._stream = connection._client.GetStream();
            connection._reader = new StreamReader(connection._stream, Encoding.UTF8);
            Assert.StartsWith("220 ", await connection.ReadReplyAsync());
            return connection;
        }

        public async Task SendRawAsync(string data) => await _stream.WriteAsync(Encoding.UTF8.GetBytes(data));

        /// <summary>Reads a (possibly multi-line) reply and returns its last line.</summary>
        public async Task<string> ReadReplyAsync()
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("connection closed");
                if (line.Length < 4 || line[3] != '-')
                {
                    return line;
                }
            }
        }

        public async Task<string> CommandAsync(string command)
        {
            await SendRawAsync(command + "\r\n");
            return await ReadReplyAsync();
        }

        public void Dispose() => _client.Dispose();
    }

    private Task<Connection> ConnectAsync() => Connection.OpenAsync(_factory.SmtpPort);

    [Fact]
    public async Task FullConversation_StoresMail()
    {
        using var c = await ConnectAsync();

        Assert.StartsWith("250 ", await c.CommandAsync("EHLO client.example"));
        Assert.StartsWith("250 ", await c.CommandAsync("MAIL FROM:<from@example.com> SIZE=100 BODY=8BITMIME"));
        Assert.StartsWith("250 ", await c.CommandAsync("RCPT TO:<to@example.com>"));
        Assert.StartsWith("354 ", await c.CommandAsync("DATA"));
        var reply = await c.CommandAsync("Subject: Raw\r\nFrom: from@example.com\r\nTo: to@example.com\r\n\r\n..leading dot\r\nline 2\r\n.");
        Assert.StartsWith("250 ", reply);
        Assert.Equal("221 2.0.0 Bye", await c.CommandAsync("QUIT"));

        var mail = Assert.Single(_factory.Store.Query(new()));
        Assert.EndsWith(mail.Id, reply);
        Assert.Equal("Raw", mail.Message.Subject);
        Assert.Equal(".leading dot\r\nline 2\r\n", mail.Message.Text);
        Assert.Equal("client.example", mail.Message.Metadata["smtp.helo"]);
    }

    [Fact]
    public async Task Pipelining_AndBareLineFeeds_Work()
    {
        using var c = await ConnectAsync();

        await c.SendRawAsync("EHLO x\nMAIL FROM:<a@example.com>\nRCPT TO:<b@example.com>\nRCPT TO:<c@example.com>\nDATA\n");
        for (var i = 0; i < 4; i++)
        {
            Assert.StartsWith("250", await c.ReadReplyAsync());
        }
        Assert.StartsWith("354", await c.ReadReplyAsync());
        Assert.StartsWith("250", await c.CommandAsync("Subject: piped\n\nbody\n."));

        var m = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("piped", m.Subject);
        Assert.Equal(["b@example.com", "c@example.com"], m.Bcc.Select(a => a.Email));
    }

    [Fact]
    public async Task SeveralMails_OnOneConnection()
    {
        using var c = await ConnectAsync();
        await c.CommandAsync("HELO x");

        foreach (var subject in new[] { "one", "two" })
        {
            Assert.StartsWith("250", await c.CommandAsync("MAIL FROM:<>"));
            Assert.StartsWith("250", await c.CommandAsync("RCPT TO:<postmaster@localhost>"));
            Assert.StartsWith("354", await c.CommandAsync("DATA"));
            Assert.StartsWith("250", await c.CommandAsync($"Subject: {subject}\r\n\r\nbody\r\n."));
        }

        Assert.Equal(["two", "one"], _factory.Store.Query(new()).Select(m => m.Message.Subject));
    }

    [Theory]
    [InlineData("RCPT TO:<a@example.com>", "503")]
    [InlineData("DATA", "503")]
    [InlineData("MAIL FROM:", "501")]
    [InlineData("MAIL FROM:<unterminated", "501")]
    [InlineData("MAIL FROM:<a@example.com> SIZE=999999", "552")]
    [InlineData("EHLO", "501")]
    [InlineData("STARTTLS", "454")]
    [InlineData("AUTH CRAM-MD5", "504")]
    [InlineData("WHATEVER", "500")]
    [InlineData("VRFY someone", "252")]
    [InlineData("NOOP", "250")]
    public async Task Command_GetsExpectedReply(string command, string expectedCode)
    {
        using var c = await ConnectAsync();

        Assert.StartsWith(expectedCode, await c.CommandAsync(command));
        // The session stays usable after errors.
        Assert.StartsWith("250", await c.CommandAsync("NOOP"));
    }

    [Fact]
    public async Task NestedMail_And_RcptWithoutAddress_AreRejected()
    {
        using var c = await ConnectAsync();
        await c.CommandAsync("EHLO x");
        await c.CommandAsync("MAIL FROM:<a@example.com>");

        Assert.StartsWith("503", await c.CommandAsync("MAIL FROM:<b@example.com>"));
        Assert.StartsWith("501", await c.CommandAsync("RCPT TO:<>"));
        Assert.StartsWith("503", await c.CommandAsync("DATA"));
        Assert.StartsWith("250", await c.CommandAsync("RSET"));
        Assert.StartsWith("503", await c.CommandAsync("RCPT TO:<a@example.com>"));
    }

    [Fact]
    public async Task OversizedMessage_IsRejectedAndSessionContinues()
    {
        using var c = await ConnectAsync();
        await c.CommandAsync("EHLO x");
        await c.CommandAsync("MAIL FROM:<a@example.com>");
        await c.CommandAsync("RCPT TO:<b@example.com>");
        await c.CommandAsync("DATA");

        Assert.StartsWith("552", await c.CommandAsync("Subject: big\r\n\r\n" + new string('x', 2000) + "\r\n."));
        Assert.StartsWith("250", await c.CommandAsync("NOOP"));
        Assert.Empty(_factory.Store.Query(new()));
    }

    [Fact]
    public async Task TooLongCommandLine_IsRejected()
    {
        using var c = await ConnectAsync();

        Assert.StartsWith("500", await c.CommandAsync("NOOP " + new string('x', 10_000)));
        Assert.StartsWith("250", await c.CommandAsync("NOOP"));
    }

    [Fact]
    public async Task AuthLogin_AndPlain_Work()
    {
        using var c = await ConnectAsync();
        await c.CommandAsync("EHLO x");

        Assert.StartsWith("334", await c.CommandAsync("AUTH LOGIN"));
        Assert.StartsWith("334", await c.CommandAsync(Convert.ToBase64String("user"u8)));
        Assert.StartsWith("235", await c.CommandAsync(Convert.ToBase64String("pass"u8)));
        Assert.StartsWith("503", await c.CommandAsync("AUTH PLAIN " + Convert.ToBase64String("\0user\0pass"u8)));

        using var c2 = await ConnectAsync();
        Assert.StartsWith("334", await c2.CommandAsync("AUTH PLAIN"));
        Assert.StartsWith("235", await c2.CommandAsync(Convert.ToBase64String("\0user\0pass"u8)));

        using var c3 = await ConnectAsync();
        Assert.StartsWith("501", await c3.CommandAsync("AUTH PLAIN not-base64!"));
        Assert.StartsWith("334", await c3.CommandAsync("AUTH LOGIN"));
        Assert.StartsWith("501", await c3.CommandAsync("*"));
    }

    [Fact]
    public async Task BusySmtpPort_DoesNotStopTheHttpApis()
    {
        using var blocker = new TcpListener(System.Net.IPAddress.IPv6Any, 0);
        blocker.Server.DualMode = true;
        blocker.Start();
        var port = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;
        using var factory = new NevermailFactory(new() { ["Smtp:Port"] = port.ToString() });

        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Throws<InvalidOperationException>(() => factory.SmtpPort);
    }

    [Fact]
    public async Task IdleConnection_IsClosed()
    {
        using var factory = new NevermailFactory(new() { ["Smtp:IdleTimeoutSeconds"] = "1" });
        using var c = await Connection.OpenAsync(factory.SmtpPort);

        Assert.StartsWith("421", await c.ReadReplyAsync());
    }

    [Fact]
    public async Task LegacyCharset_IsDecoded()
    {
        using var c = await ConnectAsync();
        await c.CommandAsync("HELO x");
        await c.CommandAsync("MAIL FROM:<a@example.com>");
        await c.CommandAsync("RCPT TO:<b@example.com>");
        await c.CommandAsync("DATA");
        Assert.StartsWith("250", await c.CommandAsync(
            "Subject: =?windows-1252?Q?Gr=FC=DFe_=80_100?=\r\nContent-Type: text/plain; charset=iso-8859-15\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n=A4 Euro\r\n."));

        var m = Assert.Single(_factory.Store.Query(new())).Message;
        Assert.Equal("Grüße € 100", m.Subject);
        Assert.Equal("€ Euro", m.Text?.Trim());
    }
}
