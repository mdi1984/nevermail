using System.Text;
using Nevermail.Storage;

namespace Nevermail.Providers.Smtp;

public sealed record SmtpOptions(
    bool Enabled,
    int Port,
    string? Username,
    string? Password,
    int MaxMessageSize,
    TimeSpan IdleTimeout)
{
    public const string HostName = "nevermail";
    public const int MaxRecipients = 1000;

    public bool RequiresAuth => !string.IsNullOrEmpty(Username);

    public static SmtpOptions FromConfiguration(IConfiguration config) => new(
        Enabled: !string.Equals(config["Smtp:Enabled"], "false", StringComparison.OrdinalIgnoreCase),
        Port: int.TryParse(config["Smtp:Port"], out var port) ? port : 1025,
        Username: config["Smtp:Username"],
        Password: config["Smtp:Password"],
        MaxMessageSize: int.TryParse(config["Smtp:MaxMessageSize"], out var size) && size > 0 ? size : 25 * 1024 * 1024,
        IdleTimeout: TimeSpan.FromSeconds(int.TryParse(config["Smtp:IdleTimeoutSeconds"], out var idle) && idle > 0 ? idle : 300));
}

/// <summary>
/// One SMTP conversation (RFC 5321) with a client. Accepts every message and hands it to the mail store.
/// Supports ESMTP with SIZE, 8BITMIME, SMTPUTF8, PIPELINING and AUTH PLAIN/LOGIN; no TLS.
/// </summary>
internal sealed class SmtpSession(Stream stream, SmtpOptions options, IMailStore store, TimeProvider time, ILogger logger) : IDisposable
{
    private const int MaxCommandLength = 4096;
    private const int MaxDataLineLength = 1024 * 1024;

    private readonly SmtpLineReader _reader = new(stream);
    private string? _helo;
    private string? _user;
    private string? _mailFrom;
    private readonly List<string> _recipients = [];
    private CancellationTokenSource? _timeout;

    private bool InTransaction => _mailFrom != null;

    public async Task RunAsync(CancellationToken ct)
    {
        await ReplyAsync(ct, $"220 {SmtpOptions.HostName} ESMTP Nevermail ready");

        while (await ReadCommandAsync(ct) is { } line)
        {
            if (line.TooLong)
            {
                await ReplyAsync(ct, "500 5.5.2 Line too long");
                continue;
            }

            var text = Encoding.UTF8.GetString(line.Bytes);
            var space = text.IndexOf(' ');
            var verb = (space < 0 ? text : text[..space]).ToUpperInvariant();
            var argument = space < 0 ? "" : text[(space + 1)..].Trim();

            switch (verb)
            {
                case "HELO":
                case "EHLO":
                    await HelloAsync(verb, argument, ct);
                    break;
                case "MAIL":
                    await MailAsync(argument, ct);
                    break;
                case "RCPT":
                    await RcptAsync(argument, ct);
                    break;
                case "DATA":
                    if (!await DataAsync(argument, ct))
                    {
                        return;
                    }
                    break;
                case "AUTH":
                    if (!await AuthAsync(argument, ct))
                    {
                        return;
                    }
                    break;
                case "RSET":
                    Reset();
                    await ReplyAsync(ct, "250 2.0.0 OK");
                    break;
                case "NOOP":
                    await ReplyAsync(ct, "250 2.0.0 OK");
                    break;
                case "VRFY":
                    await ReplyAsync(ct, "252 2.5.0 Cannot VRFY user, but will accept message");
                    break;
                case "HELP":
                    await ReplyAsync(ct, "214 2.0.0 Supported: HELO EHLO MAIL RCPT DATA AUTH RSET NOOP VRFY QUIT");
                    break;
                case "QUIT":
                    await ReplyAsync(ct, "221 2.0.0 Bye");
                    return;
                case "STARTTLS":
                    await ReplyAsync(ct, "454 4.7.0 TLS not available");
                    break;
                default:
                    await ReplyAsync(ct, "500 5.5.2 Command not recognized");
                    break;
            }
        }
    }

    public void Dispose() => _timeout?.Dispose();

    private async Task HelloAsync(string verb, string argument, CancellationToken ct)
    {
        if (argument.Length == 0)
        {
            await ReplyAsync(ct, $"501 5.5.4 Syntax: {verb} hostname");
            return;
        }
        _helo = argument;
        Reset();

        if (verb == "HELO")
        {
            await ReplyAsync(ct, $"250 {SmtpOptions.HostName}");
            return;
        }
        await ReplyAsync(ct,
            $"250-{SmtpOptions.HostName} Hello {argument}",
            $"250-SIZE {options.MaxMessageSize}",
            "250-8BITMIME",
            "250-SMTPUTF8",
            "250-PIPELINING",
            "250-ENHANCEDSTATUSCODES",
            "250-AUTH PLAIN LOGIN",
            "250 HELP");
    }

    private async Task MailAsync(string argument, CancellationToken ct)
    {
        if (options.RequiresAuth && _user is null)
        {
            await ReplyAsync(ct, "530 5.7.0 Authentication required");
            return;
        }
        if (InTransaction)
        {
            await ReplyAsync(ct, "503 5.5.1 Nested MAIL command");
            return;
        }
        if (!TryParsePath(argument, "FROM:", out var address, out var parameters))
        {
            await ReplyAsync(ct, "501 5.5.4 Syntax: MAIL FROM:<address>");
            return;
        }
        foreach (var parameter in parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (parameter.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(parameter.AsSpan(5), out var size) && size > options.MaxMessageSize)
            {
                await ReplyAsync(ct, "552 5.3.4 Message size exceeds fixed maximum message size");
                return;
            }
        }

        _mailFrom = address;
        await ReplyAsync(ct, "250 2.1.0 OK");
    }

    private async Task RcptAsync(string argument, CancellationToken ct)
    {
        if (!InTransaction)
        {
            await ReplyAsync(ct, "503 5.5.1 Need MAIL command");
            return;
        }
        if (!TryParsePath(argument, "TO:", out var address, out _) || address.Length == 0)
        {
            await ReplyAsync(ct, "501 5.1.3 Syntax: RCPT TO:<address>");
            return;
        }
        if (_recipients.Count >= SmtpOptions.MaxRecipients)
        {
            await ReplyAsync(ct, "452 4.5.3 Too many recipients");
            return;
        }

        _recipients.Add(address);
        await ReplyAsync(ct, "250 2.1.5 OK");
    }

    /// <returns>False if the client went away mid-message.</returns>
    private async Task<bool> DataAsync(string argument, CancellationToken ct)
    {
        if (!InTransaction)
        {
            await ReplyAsync(ct, "503 5.5.1 Need MAIL command");
            return true;
        }
        if (_recipients.Count == 0)
        {
            await ReplyAsync(ct, "503 5.5.1 Need RCPT command");
            return true;
        }
        if (argument.Length > 0)
        {
            await ReplyAsync(ct, "501 5.5.4 Syntax: DATA");
            return true;
        }

        await ReplyAsync(ct, "354 End data with <CR><LF>.<CR><LF>");

        using var data = new MemoryStream();
        var oversized = false;
        while (true)
        {
            if (await ReadDataLineAsync(ct) is not { } line)
            {
                return false;
            }
            var bytes = line.Bytes;
            if (!line.TooLong && bytes is [(byte)'.'])
            {
                break;
            }
            if (oversized)
            {
                continue;
            }
            // Dot-stuffing: a leading dot was doubled by the client.
            var offset = bytes.Length > 0 && bytes[0] == '.' ? 1 : 0;
            if (line.TooLong || data.Length + bytes.Length - offset + 2 > options.MaxMessageSize)
            {
                oversized = true;
                continue;
            }
            data.Write(bytes, offset, bytes.Length - offset);
            data.Write("\r\n"u8);
        }

        if (oversized)
        {
            Reset();
            await ReplyAsync(ct, "552 5.3.4 Message size exceeds fixed maximum message size");
            return true;
        }

        var envelope = new SmtpEnvelope(_mailFrom!, [.. _recipients], _helo, _user);
        Reset();
        var mail = await SmtpMessageMapper.MapAsync(data.ToArray(), envelope, time.GetUtcNow(), ct);
        await store.AddAsync([mail]);
        logger.LogInformation("Received SMTP mail {Id} from {From} for {Count} recipient(s)", mail.Id, envelope.MailFrom, envelope.Recipients.Count);
        await ReplyAsync(ct, $"250 2.0.0 OK: queued as {mail.Id}");
        return true;
    }

    /// <returns>False if the client went away mid-exchange.</returns>
    private async Task<bool> AuthAsync(string argument, CancellationToken ct)
    {
        if (_user is not null)
        {
            await ReplyAsync(ct, "503 5.5.1 Already authenticated");
            return true;
        }
        if (InTransaction)
        {
            await ReplyAsync(ct, "503 5.5.1 AUTH not allowed during a mail transaction");
            return true;
        }

        var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var mechanism = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        var initial = parts.Length > 1 ? parts[1] : null;

        string? user;
        string? password;
        switch (mechanism)
        {
            case "PLAIN":
            {
                var response = initial ?? await ChallengeAsync("", ct);
                if (response is null)
                {
                    return false;
                }
                if (response == "*")
                {
                    await ReplyAsync(ct, "501 5.7.0 Authentication cancelled");
                    return true;
                }
                // authzid \0 authcid \0 password
                var fields = DecodeBase64(response)?.Split('\0');
                if (fields is not { Length: 3 })
                {
                    await ReplyAsync(ct, "501 5.5.2 Cannot decode response");
                    return true;
                }
                (user, password) = (fields[1], fields[2]);
                break;
            }
            case "LOGIN":
            {
                var userResponse = initial ?? await ChallengeAsync("VXNlcm5hbWU6", ct); // "Username:"
                if (userResponse is null)
                {
                    return false;
                }
                var passwordResponse = userResponse == "*" ? "*" : await ChallengeAsync("UGFzc3dvcmQ6", ct); // "Password:"
                if (passwordResponse is null)
                {
                    return false;
                }
                if (passwordResponse == "*")
                {
                    await ReplyAsync(ct, "501 5.7.0 Authentication cancelled");
                    return true;
                }
                (user, password) = (DecodeBase64(userResponse), DecodeBase64(passwordResponse));
                if (user is null || password is null)
                {
                    await ReplyAsync(ct, "501 5.5.2 Cannot decode response");
                    return true;
                }
                break;
            }
            default:
                await ReplyAsync(ct, "504 5.5.4 Unrecognized authentication type");
                return true;
        }

        if (options.RequiresAuth && (user != options.Username || password != (options.Password ?? "")))
        {
            await ReplyAsync(ct, "535 5.7.8 Authentication credentials invalid");
            return true;
        }

        _user = user;
        await ReplyAsync(ct, "235 2.7.0 Authentication successful");
        return true;
    }

    private async Task<string?> ChallengeAsync(string challenge, CancellationToken ct)
    {
        await ReplyAsync(ct, $"334 {challenge}");
        return await ReadCommandAsync(ct) is { TooLong: false } line ? Encoding.ASCII.GetString(line.Bytes).Trim() : null;
    }

    private void Reset()
    {
        _mailFrom = null;
        _recipients.Clear();
    }

    private async Task<SmtpLine?> ReadCommandAsync(CancellationToken ct) => await ReadWithTimeoutAsync(MaxCommandLength, ct);

    private async Task<SmtpLine?> ReadDataLineAsync(CancellationToken ct) => await ReadWithTimeoutAsync(MaxDataLineLength, ct);

    private async Task<SmtpLine?> ReadWithTimeoutAsync(int maxLength, CancellationToken ct)
    {
        // One linked source per session; CancelAfter restarts the idle timer on every read.
        _timeout ??= CancellationTokenSource.CreateLinkedTokenSource(ct);
        _timeout.CancelAfter(options.IdleTimeout);
        try
        {
            return await _reader.ReadLineAsync(maxLength, _timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await ReplyAsync(ct, "421 4.4.2 Idle timeout, closing connection");
            return null;
        }
    }

    private async Task ReplyAsync(CancellationToken ct, params string[] lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(line).Append("\r\n");
        }
        await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct);
        await stream.FlushAsync(ct);
    }

    private static string? DecodeBase64(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Parses "FROM:&lt;a@b&gt; PARAMS" / "TO:&lt;a@b&gt;". Source routes (@a,@b:user@c) are reduced to the mailbox.</summary>
    internal static bool TryParsePath(string argument, string prefix, out string address, out string parameters)
    {
        address = "";
        parameters = "";
        if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = argument[prefix.Length..].TrimStart();
        if (rest.Length == 0)
        {
            return false;
        }
        string path;
        if (rest.StartsWith('<'))
        {
            var close = rest.IndexOf('>');
            if (close < 0)
            {
                return false;
            }
            path = rest[1..close];
            parameters = rest[(close + 1)..].Trim();
        }
        else
        {
            var space = rest.IndexOf(' ');
            path = space < 0 ? rest : rest[..space];
            parameters = space < 0 ? "" : rest[(space + 1)..].Trim();
        }

        var colon = path.LastIndexOf(':');
        if (path.StartsWith('@') && colon >= 0)
        {
            path = path[(colon + 1)..];
        }
        if (path.Any(char.IsWhiteSpace))
        {
            return false;
        }
        address = path;
        return true;
    }
}
