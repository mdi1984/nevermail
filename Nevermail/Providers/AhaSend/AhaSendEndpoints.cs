using System.Collections.Concurrent;
using System.Text.Json;
using Nevermail.Mail;
using Nevermail.Storage;

namespace Nevermail.Providers.AhaSend;

/// <summary>AhaSend API v2: <c>POST /v2/accounts/{account_id}/messages</c>, one message per recipient.</summary>
public static class AhaSendEndpoints
{
    public const string ProviderKey = "ahasend";
    private const string ApiKeyConfig = "Providers:AhaSend:ApiKey";

    public static IEndpointRouteBuilder MapAhaSend(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v2/accounts/{accountId}/messages", SendAsync);
        return app;
    }

    private static async Task<IResult> SendAsync(string accountId, HttpContext ctx, IMailStore store, IConfiguration config,
        TimeProvider time, FaultInjector faults, AhaSendIdempotency idempotency)
    {
        if (Authorize(ctx, config) is { } unauthorized)
        {
            return unauthorized;
        }
        if (!Guid.TryParse(accountId, out _))
        {
            return Error(404, "account not found");
        }

        var raw = await ProviderHelpers.ReadBodyAsync(ctx);
        var key = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length == 0)
        {
            return await ExecuteAsync(raw, ctx, store, time, faults);
        }
        if (AhaSendValidator.ValidateIdempotencyKey(key) is { } keyError)
        {
            return Error(400, keyError);
        }

        // Keys are scoped to the account; the fingerprint covers what AhaSend compares (method, path and body).
        var fingerprint = $"{ctx.Request.Path}\n{raw}";
        switch (idempotency.Begin($"{accountId}:{key}", fingerprint, time.GetUtcNow()))
        {
            case { Outcome: IdempotencyOutcome.InProgress }:
                ctx.Response.Headers["Idempotent-Replayed"] = "false";
                ctx.Response.Headers.RetryAfter = "1";
                return Error(409, "a request with this idempotency key is still in progress");
            case { Outcome: IdempotencyOutcome.Mismatch }:
                return Error(422, "idempotency key was already used with a different request");
            case { Outcome: IdempotencyOutcome.Replay, Response: { } stored }:
                ctx.Response.Headers["Idempotent-Replayed"] = "true";
                return Results.Content(stored.Body, "application/json", statusCode: stored.StatusCode);
        }

        StoredResponse? response = null;
        try
        {
            var buffered = new BufferedResult(await ExecuteAsync(raw, ctx, store, time, faults));
            response = await buffered.RenderAsync(ctx);
            return buffered;
        }
        finally
        {
            // Like AhaSend, 5xx outcomes, rate limits and failures aren't stored, so the same request can be retried.
            idempotency.Complete($"{accountId}:{key}", response is { StatusCode: < 500 and not 429 } ? response : null);
        }
    }

    private static async Task<IResult> ExecuteAsync(string raw, HttpContext ctx, IMailStore store, TimeProvider time, FaultInjector faults)
    {
        AhaSendRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(raw, AhaSendJsonContext.Default.AhaSendRequest);
        }
        catch (JsonException ex)
        {
            return Error(400, ProviderHelpers.IsTypeMismatch(ex) && ProviderHelpers.ToFieldPath(ex.Path) is { } field
                ? $"{field} has an invalid type"
                : "invalid request body");
        }
        if (request is null)
        {
            return Error(400, "invalid request body");
        }

        var now = time.GetUtcNow();
        if (AhaSendValidator.Validate(request, now) is { } error)
        {
            return Error(400, error.Message);
        }

        await faults.DelayAsync(ctx.RequestAborted);
        if (faults.ForHttp(request.Recipients!.Select(r => r.Email)) is { } fault)
        {
            ProviderHelpers.AddRetryAfter(ctx, fault);
            return Error(fault.Code, fault.Reason);
        }

        var mails = AhaSendMapper.Map(request, raw, MailIds.New(), now);
        var messages = new List<AhaSendMessage>();
        var scheduled = request.Schedule?.FirstAttempt > now;
        for (var i = 0; i < mails.Count; i++)
        {
            // AhaSend-style "<id@domain>" message id that maps back to the captured mail.
            var messageId = $"<{mails[i].Id}@nevermail.local>";
            mails[i].Message.Headers["Message-ID"] = messageId;
            var recipient = request.Recipients![i];
            messages.Add(new AhaSendMessage(
                messageId,
                new AhaSendResponseRecipient(recipient.Email!.Trim(), recipient.Name,
                    recipient.Substitutions is { ValueKind: JsonValueKind.Object } s ? s : null),
                scheduled ? "scheduled" : "queued",
                null,
                request.Schedule));
        }
        await store.AddAsync(mails);

        return Results.Json(new AhaSendMessageList(messages), AhaSendJsonContext.Default.AhaSendMessageList, statusCode: 202);
    }

    /// <summary>
    /// AhaSend authenticates with "Authorization: Bearer &lt;api key&gt;". If no key is configured, any key is accepted.
    /// </summary>
    private static IResult? Authorize(HttpContext ctx, IConfiguration config)
    {
        var authorization = ctx.Request.Headers.Authorization.ToString();
        var key = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..].Trim() : "";
        if (key.Length == 0)
        {
            return Error(401, "missing or invalid credentials");
        }

        var expected = config[ApiKeyConfig];
        return !string.IsNullOrEmpty(expected) && key != expected ? Error(401, "missing or invalid credentials") : null;
    }

    private static IResult Error(int statusCode, string message) =>
        Results.Json(new AhaSendError(message), AhaSendJsonContext.Default.AhaSendError, statusCode: statusCode);

    /// <summary>Renders a JSON result once, so the exact response can be stored for idempotent replays.</summary>
    private sealed class BufferedResult(IResult inner) : IResult
    {
        private StoredResponse? _response;

        public async Task<StoredResponse> RenderAsync(HttpContext ctx)
        {
            var original = ctx.Response.Body;
            using var buffer = new MemoryStream();
            ctx.Response.Body = buffer;
            try
            {
                await inner.ExecuteAsync(ctx);
            }
            finally
            {
                ctx.Response.Body = original;
            }
            return _response = new StoredResponse(ctx.Response.StatusCode, System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }

        public Task ExecuteAsync(HttpContext ctx) =>
            Results.Content(_response!.Body, "application/json", statusCode: _response.StatusCode).ExecuteAsync(ctx);
    }
}

public sealed record StoredResponse(int StatusCode, string Body);

public enum IdempotencyOutcome { Execute, InProgress, Mismatch, Replay }

public sealed record IdempotencyResult(IdempotencyOutcome Outcome, StoredResponse? Response = null);

/// <summary>
/// AhaSend's <c>Idempotency-Key</c> handling: a completed key replays the stored response, a key still running
/// gets 409, a key reused for a different request gets 422. Keys expire after 24 hours. Bounded, oldest first out.
/// </summary>
public sealed class AhaSendIdempotency
{
    private const int Capacity = 10_000;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private sealed record Entry(string Fingerprint, DateTimeOffset Created, StoredResponse? Response);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public IdempotencyResult Begin(string key, string fingerprint, DateTimeOffset now)
    {
        while (true)
        {
            if (_entries.TryAdd(key, new Entry(fingerprint, now, null)))
            {
                _order.Enqueue(key);
                while (_order.Count > Capacity && _order.TryDequeue(out var oldest))
                {
                    _entries.TryRemove(oldest, out _);
                }
                return new(IdempotencyOutcome.Execute);
            }
            if (!_entries.TryGetValue(key, out var existing))
            {
                continue;
            }
            if (now - existing.Created > Lifetime)
            {
                _entries.TryRemove(KeyValuePair.Create(key, existing));
                continue;
            }
            if (existing.Fingerprint != fingerprint)
            {
                return new(IdempotencyOutcome.Mismatch);
            }
            return existing.Response is null ? new(IdempotencyOutcome.InProgress) : new(IdempotencyOutcome.Replay, existing.Response);
        }
    }

    /// <summary>Stores the outcome for replays, or releases the key if <paramref name="response"/> is null.</summary>
    public void Complete(string key, StoredResponse? response)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return;
        }
        if (response is null)
        {
            _entries.TryRemove(KeyValuePair.Create(key, entry));
        }
        else
        {
            _entries.TryUpdate(key, entry with { Response = response }, entry);
        }
    }
}
