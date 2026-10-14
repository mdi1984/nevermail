using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nevermail.Mail;
using Nevermail.Storage;

namespace Nevermail.Providers.AzureEmail;

/// <summary>
/// Azure Communication Services Email: <c>POST /emails:send</c> starts a long running operation (202 + Operation-Location),
/// <c>GET /emails/operations/{id}</c> reports its status. Nevermail completes operations immediately, so the first poll
/// already returns the final status.
/// </summary>
public static class AzureEmailEndpoints
{
    public const string ProviderKey = "azure";

    /// <summary>Recipients at this domain are accepted (202) but the operation then fails with EmailDropped.</summary>
    public const string DroppedDomain = "fail-dropped.test";

    private const int MaxRequestBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapAzureEmail(this IEndpointRouteBuilder app)
    {
        app.MapPost("/emails:send", SendAsync);
        app.MapGet("/emails/operations/{operationId}", GetSendResult);
        return app;
    }

    private static async Task<IResult> SendAsync(HttpContext ctx, IMailStore store, AzureEmailOperations operations, TimeProvider time, FaultInjector faults)
    {
        if ((Authorize(ctx) ?? CheckApiVersion(ctx)) is { } rejected)
        {
            return rejected;
        }

        var raw = await ProviderHelpers.ReadBodyAsync(ctx);
        if (Encoding.UTF8.GetByteCount(raw) > MaxRequestBytes)
        {
            return Error(ctx, 413, new("RequestEntityTooLarge", "The total email request size (including attachments) must not exceed 10 MB."));
        }
        AzureEmailRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(raw, AzureEmailJsonContext.Default.AzureEmailRequest);
        }
        catch (JsonException ex)
        {
            return Error(ctx, 400, ProviderHelpers.IsTypeMismatch(ex) && ToPropertyPath(ex.Path) is { } property
                ? AzureEmailValidator.Missing(property)
                : new("BadRequest", "Request body validation error. Invalid Json payload."));
        }
        if (request is null)
        {
            return Error(ctx, 400, new("BadRequest", "Request body validation error. Invalid Json payload."));
        }
        if (AzureEmailValidator.Validate(request) is { } error)
        {
            return Error(ctx, 400, error);
        }

        var operationId = ctx.Request.Headers["Operation-Id"].ToString().Trim();
        if (operationId.Length == 0)
        {
            operationId = Guid.NewGuid().ToString();
        }
        // Retries with the same Operation-Id don't send the mail twice.
        if (operations.Get(operationId) is not null)
        {
            return Accepted(ctx, operationId);
        }

        await faults.DelayAsync(ctx.RequestAborted);
        var recipients = Recipients(request).ToList();
        if (faults.ForHttp(recipients) is { } fault)
        {
            ProviderHelpers.AddRetryAfter(ctx, fault);
            var code = fault.Code switch
            {
                401 => "Denied",
                403 => "Forbidden",
                404 => "NotFound",
                409 => "Conflict",
                413 => "RequestEntityTooLarge",
                429 => "TooManyRequests",
                503 => "ServiceUnavailable",
                >= 500 => "InternalServerError",
                _ => "BadRequest",
            };
            return Error(ctx, fault.Code, new(code, fault.Reason));
        }

        if (recipients.FirstOrDefault(IsDropped) is { } dropped)
        {
            operations.Set(operationId, new AzureEmailSendResult(operationId, "Failed",
                new("EmailDropped", $"Email was dropped after several attempts to deliver (simulated by recipient {dropped.Trim()}).")));
            return Accepted(ctx, operationId);
        }

        await store.AddAsync([AzureEmailMapper.Map(request, raw, operationId, time.GetUtcNow())]);
        operations.Set(operationId, new AzureEmailSendResult(operationId, "Succeeded", null));
        return Accepted(ctx, operationId);
    }

    private static IResult GetSendResult(string operationId, HttpContext ctx, IMailStore store, AzureEmailOperations operations)
    {
        if ((Authorize(ctx) ?? CheckApiVersion(ctx)) is { } rejected)
        {
            return rejected;
        }

        EchoClientRequestId(ctx);
        // After a restart (with persistent storage) only the captured mail is left.
        var result = operations.Get(operationId)
            ?? (store.Query(new MailQuery(Provider: ProviderKey, RequestId: operationId, Limit: 1)).Count > 0
                ? new AzureEmailSendResult(operationId, "Succeeded", null)
                : null);
        return result is null
            ? Error(ctx, 404, new("NotFound", $"Operation '{operationId}' was not found."))
            : Results.Json(result, AzureEmailJsonContext.Default.AzureEmailSendResult);
    }

    private static IResult Accepted(HttpContext ctx, string operationId)
    {
        var req = ctx.Request;
        var apiVersion = Uri.EscapeDataString(req.Query["api-version"].ToString());
        ctx.Response.Headers["Operation-Location"] =
            $"{req.Scheme}://{req.Host}{req.PathBase}/emails/operations/{Uri.EscapeDataString(operationId)}?api-version={apiVersion}";
        ctx.Response.Headers.RetryAfter = "1";
        EchoClientRequestId(ctx);
        // Like the real service, the send itself always reports "Running"; the first poll returns the outcome.
        return Results.Json(new AzureEmailSendResult(operationId, "Running", null), AzureEmailJsonContext.Default.AzureEmailSendResult, statusCode: 202);
    }

    /// <summary>
    /// Requires an HMAC-SHA256 signed request (access key / connection string) or a bearer token (Entra ID).
    /// Signatures aren't verified: SHA-256 would need OpenSSL, which the scratch image doesn't have.
    /// </summary>
    private static IResult? Authorize(HttpContext ctx)
    {
        var headers = ctx.Request.Headers;
        var authorization = headers.Authorization.ToString();
        var valid = authorization.StartsWith("HMAC-SHA256 ", StringComparison.OrdinalIgnoreCase)
            ? authorization.Contains("Signature=", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(headers["x-ms-date"]) && !string.IsNullOrEmpty(headers["x-ms-content-sha256"])
            : authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && authorization.Length > "Bearer ".Length;
        return valid ? null : Error(ctx, 401, new("Denied", "Denied by the resource provider."));
    }

    /// <summary>Any date-style version ("2023-03-31", "2025-01-15-preview") is accepted, so newer SDKs keep working.</summary>
    private static IResult? CheckApiVersion(HttpContext ctx)
    {
        var version = ctx.Request.Query["api-version"].ToString();
        if (version.Length == 0)
        {
            return Error(ctx, 400, new("MissingApiVersionParameter", "The api-version query parameter (?api-version=) is required for all requests."));
        }
        var date = version.EndsWith("-preview", StringComparison.Ordinal) ? version[..^"-preview".Length] : version;
        return DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? null
            : Error(ctx, 400, new("UnsupportedApiVersion", $"The HTTP resource does not support the API version '{version}'."));
    }

    private static void EchoClientRequestId(HttpContext ctx)
    {
        if (ctx.Request.Headers["x-ms-client-request-id"].ToString() is { Length: > 0 } clientRequestId)
        {
            ctx.Response.Headers["x-ms-client-request-id"] = clientRequestId;
        }
    }

    private static IEnumerable<string?> Recipients(AzureEmailRequest r) =>
        new[] { r.Recipients!.To, r.Recipients.Cc, r.Recipients.Bcc }.SelectMany(list => list ?? []).Select(a => a.Address);

    private static bool IsDropped(string? address) =>
        address?.Trim().EndsWith("@" + DroppedDomain, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>"$.attachments[0].contentType" → "attachments[0].contentType", the notation ACS uses in errors.</summary>
    private static string? ToPropertyPath(string? jsonPath) =>
        string.IsNullOrEmpty(jsonPath) || jsonPath == "$" ? null : jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath;

    private static IResult Error(HttpContext ctx, int statusCode, AzureEmailErrorDetail error)
    {
        ctx.Response.Headers["x-ms-error-code"] = error.Code;
        EchoClientRequestId(ctx);
        return Results.Json(new AzureEmailErrorResponse(error), AzureEmailJsonContext.Default.AzureEmailErrorResponse, statusCode: statusCode);
    }
}

/// <summary>Outcomes of recent send operations, for polling. Bounded, oldest first out.</summary>
public sealed class AzureEmailOperations
{
    private const int Capacity = 10_000;

    private readonly ConcurrentDictionary<string, AzureEmailSendResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _order = new();

    public AzureEmailSendResult? Get(string operationId) => _results.GetValueOrDefault(operationId);

    public void Set(string operationId, AzureEmailSendResult result)
    {
        if (_results.TryAdd(operationId, result))
        {
            _order.Enqueue(operationId);
            while (_order.Count > Capacity && _order.TryDequeue(out var oldest))
            {
                _results.TryRemove(oldest, out _);
            }
        }
    }
}
