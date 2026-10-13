using System.Text.Json;
using Nevermail.Mail;
using Nevermail.Storage;

namespace Nevermail.Providers.Brevo;

public static class BrevoEndpoints
{
    public const string ProviderKey = "brevo";
    private const string ApiKeyConfig = "Providers:Brevo:ApiKey";

    public static IEndpointRouteBuilder MapBrevo(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v3/smtp/email", SendAsync);
        return app;
    }

    private static async Task<IResult> SendAsync(HttpContext ctx, IMailStore store, IConfiguration config, TimeProvider time)
    {
        if (Authorize(ctx, config) is { } unauthorized)
        {
            return unauthorized;
        }

        var raw = await ProviderHelpers.ReadBodyAsync(ctx);
        BrevoRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(raw, BrevoJsonContext.Default.BrevoRequest);
        }
        catch (JsonException ex)
        {
            return Error(400, ProviderHelpers.IsTypeMismatch(ex) && ProviderHelpers.ToFieldPath(ex.Path) is { } field
                ? new BrevoError("invalid_parameter", $"{field} has an invalid type")
                : new BrevoError("bad_request", "Input must be a valid json object"));
        }
        if (request is null)
        {
            return Error(400, new BrevoError("bad_request", "Input must be a valid json object"));
        }

        if (BrevoValidator.Validate(request) is { } error)
        {
            return Error(400, error);
        }

        var now = time.GetUtcNow();
        var mails = BrevoMapper.Map(request, raw, request.BatchId ?? MailIds.New(), now);
        var messageIds = new List<string>();
        foreach (var mail in mails)
        {
            // Brevo-style "<id@domain>" message id that maps back to the captured mail.
            var messageId = $"<{mail.Id}@nevermail.local>";
            mail.Message.Headers["Message-ID"] = messageId;
            messageIds.Add(messageId);
        }
        await store.AddAsync(mails);

        var response = request.MessageVersions is { Count: > 0 }
            ? new BrevoSendResponse(null, messageIds)
            : new BrevoSendResponse(messageIds[0], null);
        var scheduled = request.ScheduledAt > now;
        return Results.Json(response, BrevoJsonContext.Default.BrevoSendResponse, statusCode: scheduled ? 202 : 201);
    }

    /// <summary>
    /// Brevo authenticates with an "api-key" header (or an OAuth bearer token). If no key is configured, any key is accepted.
    /// </summary>
    private static IResult? Authorize(HttpContext ctx, IConfiguration config)
    {
        var key = ctx.Request.Headers["api-key"].ToString();
        if (string.IsNullOrEmpty(key))
        {
            var authorization = ctx.Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                key = authorization["Bearer ".Length..].Trim();
            }
        }
        if (string.IsNullOrEmpty(key))
        {
            return Error(401, new BrevoError("unauthorized", "Key not found"));
        }

        var expected = config[ApiKeyConfig];
        return !string.IsNullOrEmpty(expected) && key != expected
            ? Error(401, new BrevoError("unauthorized", "Key not found"))
            : null;
    }

    private static IResult Error(int statusCode, BrevoError error) =>
        Results.Json(error, BrevoJsonContext.Default.BrevoError, statusCode: statusCode);
}
