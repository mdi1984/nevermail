using System.Text.Json;
using Nevermail.Mail;
using Nevermail.Storage;

namespace Nevermail.Providers.SendGrid;

public static class SendGridEndpoints
{
    public const string ProviderKey = "sendgrid";
    private const string ApiKeyConfig = "Providers:SendGrid:ApiKey";

    private static readonly string[] Scopes =
    [
        "mail.send",
        "mail.batch.create",
        "mail.batch.read",
        "mail.batch.update",
        "mail.batch.delete",
    ];

    public static IEndpointRouteBuilder MapSendGrid(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v3/mail/send", SendAsync);
        app.MapGet("/v3/scopes", GetScopes);
        app.MapPost("/v3/mail/batch", CreateBatch);
        app.MapGet("/v3/mail/batch/{batchId}", GetBatch);
        return app;
    }

    private static IResult GetScopes(HttpContext ctx, IConfiguration config) =>
        Authorize(ctx, config) ?? Results.Json(new SendGridScopesResponse(Scopes), SendGridJsonContext.Default.SendGridScopesResponse);

    private static IResult CreateBatch(HttpContext ctx, IConfiguration config) =>
        Authorize(ctx, config) ?? Results.Json(new SendGridBatchResponse(MailIds.New(32)), SendGridJsonContext.Default.SendGridBatchResponse, statusCode: 201);

    private static IResult GetBatch(string batchId, HttpContext ctx, IConfiguration config) =>
        Authorize(ctx, config) ?? Results.Json(new SendGridBatchResponse(batchId), SendGridJsonContext.Default.SendGridBatchResponse);

    private static async Task<IResult> SendAsync(HttpContext ctx, IMailStore store, IConfiguration config, TimeProvider time, FaultInjector faults)
    {
        if (Authorize(ctx, config) is { } unauthorized)
        {
            return unauthorized;
        }

        var raw = await ProviderHelpers.ReadBodyAsync(ctx);

        SendGridRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(raw, SendGridJsonContext.Default.SendGridRequest);
        }
        catch (JsonException ex)
        {
            return BadRequest(ToSendGridError(ex));
        }
        if (request is null)
        {
            return BadRequest(new SendGridError("The request body must be a JSON object."));
        }

        var errors = SendGridValidator.Validate(request, time);
        if (errors.Count > 0)
        {
            return BadRequest([.. errors]);
        }

        // Sandbox mode: SendGrid validates the request and answers 200 instead of 202, but delivers nothing.
        // We keep the mail (flagged) so it can still be inspected.
        await faults.DelayAsync(ctx.RequestAborted);
        var recipients = request.Personalizations!.SelectMany(p => (p.To ?? []).Concat(p.Cc ?? []).Concat(p.Bcc ?? [])).Select(a => a.Email);
        if (faults.ForHttp(recipients) is { } fault)
        {
            ProviderHelpers.AddRetryAfter(ctx, fault);
            return Results.Json(new SendGridErrorResponse([new SendGridError(fault.Reason)]), SendGridJsonContext.Default.SendGridErrorResponse, statusCode: fault.Code);
        }

        var sandbox = request.MailSettings?.SandboxMode?.Enable == true;
        var requestId = MailIds.New();
        await store.AddAsync(SendGridMapper.Map(request, raw, requestId, sandbox, time.GetUtcNow()));

        ctx.Response.Headers["X-Message-Id"] = requestId;
        return sandbox ? Results.Ok() : Results.StatusCode(StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Requires "Authorization: Bearer &lt;key&gt;" like SendGrid does. If no key is configured, any key is accepted.
    /// Returns null when authorized.
    /// </summary>
    private static IResult? Authorize(HttpContext ctx, IConfiguration config)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) || header.Length == scheme.Length)
        {
            return Unauthorized("authorization required");
        }

        var expected = config[ApiKeyConfig];
        if (!string.IsNullOrEmpty(expected) && header[scheme.Length..].Trim() != expected)
        {
            return Unauthorized("The provided authorization grant is invalid, expired, or revoked");
        }
        return null;
    }

    private static IResult Unauthorized(string message) =>
        Results.Json(new SendGridErrorResponse([new SendGridError(message)]), SendGridJsonContext.Default.SendGridErrorResponse, statusCode: 401);

    private static IResult BadRequest(params SendGridError[] errors) =>
        Results.Json(new SendGridErrorResponse(errors), SendGridJsonContext.Default.SendGridErrorResponse, statusCode: 400);

    private static SendGridError ToSendGridError(JsonException ex)
    {
        var field = ProviderHelpers.ToFieldPath(ex.Path);
        // Type mismatches carry a path to the offending property; syntax errors usually don't.
        return ProviderHelpers.IsTypeMismatch(ex) && field is not null
            ? new SendGridError($"Invalid type for {field}.", field)
            : new SendGridError("Bad Request: the request body is not valid JSON.");
    }
}
