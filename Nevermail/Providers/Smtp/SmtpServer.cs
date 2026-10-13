using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Nevermail.Storage;

namespace Nevermail.Providers.Smtp;

/// <summary>Plain SMTP listener (like Mailpit/MailHog) that captures every message it receives.</summary>
public sealed class SmtpServer(IConfiguration config, IMailStore store, TimeProvider time, ILogger<SmtpServer> logger) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, bool> _sessions = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private SmtpOptions? _options;

    /// <summary>The port actually listened on (useful when configured as 0), or null if SMTP is disabled.</summary>
    public int? Port { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _options = SmtpOptions.FromConfiguration(config);
        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        try
        {
            _listener = StartListener(_options.Port);
        }
        catch (SocketException ex)
        {
            // Typically the port is taken (e.g. Mailpit already running). Keep serving the HTTP APIs.
            logger.LogError("SMTP server could not listen on port {Port}: {Error}. SMTP is disabled; set Smtp__Port or Smtp__Enabled=false.", _options.Port, ex.Message);
            return Task.CompletedTask;
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        logger.LogInformation("SMTP server listening on port {Port}{Auth}", Port, _options.RequiresAuth ? " (authentication required)" : "");

        _acceptLoop = AcceptLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }
        await _stopping.CancelAsync();
        _listener.Stop();
        try
        {
            await Task.WhenAll(_sessions.Keys.Append(_acceptLoop!)).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Host shutdown timeout reached; remaining sessions are abandoned.
        }
    }

    private static TcpListener StartListener(int port)
    {
        // Dual-stack where available; containers without IPv6 fall back to IPv4.
        if (Socket.OSSupportsIPv6)
        {
            var listener = Exclusive(new TcpListener(IPAddress.IPv6Any, port));
            try
            {
                listener.Server.DualMode = true;
                listener.Start();
                return listener;
            }
            catch (SocketException ex)
            {
                listener.Dispose();
                // A busy port is a real error. Only fall back to IPv4 when IPv6 itself is unavailable.
                if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    throw;
                }
            }
        }
        var ipv4 = Exclusive(new TcpListener(IPAddress.Any, port));
        ipv4.Start();
        return ipv4;
    }

    // Windows lets a socket bind a port another process already listens on unless it asks for exclusive use.
    // Linux refuses such binds anyway.
    private static TcpListener Exclusive(TcpListener listener)
    {
        if (OperatingSystem.IsWindows())
        {
            listener.ExclusiveAddressUse = true;
        }
        return listener;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException || ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogWarning("Failed to accept SMTP connection: {Error}", ex.Message);
                continue;
            }

            var session = HandleClientAsync(client, ct);
            _sessions[session] = true;
            _ = session.ContinueWith(t => _sessions.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await Task.Yield();
        var remote = client.Client.RemoteEndPoint;
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                using var session = new SmtpSession(stream, _options!, store, time, logger);
                await session.RunAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogDebug("SMTP connection from {Remote} ended: {Error}", remote, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SMTP session with {Remote} failed", remote);
        }
    }
}
