using System.Text.Json;
using Nevermail.Mail;
using Nevermail.Storage;

namespace Nevermail.Providers.Mandrill;

public static class MandrillEndpoints
{
    public const string ProviderKey = "mandrill";
    private const string ApiKeyConfig = "Providers:Mandrill:ApiKey";

    public static IEndpointRouteBuilder MapMandrill(this IEndpointRouteBuilder app)
    {
        // Clients use different API versions (1.0 - 1.4), with or without a ".json" suffix.
        foreach (var suffix in new[] { "", ".json" })
        {
            app.MapPost($"/api/{{version}}/messages/send{suffix}", SendAsync);
            app.MapPost($"/api/{{version}}/messages/send-template{suffix}", SendTemplateAsync);
            app.MapPost($"/api/{{version}}/users/ping{suffix}", PingAsync);
            app.MapPost($"/api/{{version}}/users/ping2{suffix}", Ping2Async);
        }
        return app;
    }

    private static Task<IResult> SendAsync(string version, HttpContext ctx, IMailStore store, IConfiguration config, TimeProvider time) =>
        HandleSendAsync(version, ctx, store, config, time, template: false);

    private static Task<IResult> SendTemplateAsync(string version, HttpContext ctx, IMailStore store, IConfiguration config, TimeProvider time) =>
        HandleSendAsync(version, ctx, store, config, time, template: true);

    private static async Task<IResult> HandleSendAsync(string version, HttpContext ctx, IMailStore store, IConfiguration config, TimeProvider time, bool template)
    {
        if (!IsSupportedVersion(version))
        {
            return Results.NotFound();
        }

        var raw = await ProviderHelpers.ReadBodyAsync(ctx);
        MandrillRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(raw, MandrillJsonContext.Default.MandrillRequest);
        }
        catch (JsonException ex)
        {
            return ValidationError(ProviderHelpers.IsTypeMismatch(ex) && ProviderHelpers.ToFieldPath(ex.Path) is { } field
                ? $"{field} has an invalid type"
                : "the request body is not valid JSON");
        }
        if (request is null)
        {
            return ValidationError("the request body must be a JSON object");
        }
        if (Authorize(request.Key, config) is { } unauthorized)
        {
            return unauthorized;
        }
        if (Validate(request, template) is { } error)
        {
            return ValidationError(error);
        }

        var delivery = MandrillMapper.Map(request, raw, MailIds.New(), time.GetUtcNow());
        await store.AddAsync(delivery.Mails);
        return Results.Json(delivery.Results, MandrillJsonContext.Default.ListMandrillSendResult);
    }

    private static async Task<IResult> PingAsync(string version, HttpContext ctx, IConfiguration config) =>
        await AuthorizeBodyAsync(version, ctx, config) ?? Results.Json("PONG!", MandrillJsonContext.Default.String);

    private static async Task<IResult> Ping2Async(string version, HttpContext ctx, IConfiguration config) =>
        await AuthorizeBodyAsync(version, ctx, config) ?? Results.Json(new MandrillPing2("PONG!"), MandrillJsonContext.Default.MandrillPing2);

    private static async Task<IResult?> AuthorizeBodyAsync(string version, HttpContext ctx, IConfiguration config)
    {
        if (!IsSupportedVersion(version))
        {
            return Results.NotFound();
        }
        try
        {
            var request = JsonSerializer.Deserialize(await ProviderHelpers.ReadBodyAsync(ctx), MandrillJsonContext.Default.MandrillKeyRequest);
            return Authorize(request?.Key, config);
        }
        catch (JsonException)
        {
            return ValidationError("the request body is not valid JSON");
        }
    }

    private static bool IsSupportedVersion(string version) => version.StartsWith("1.", StringComparison.Ordinal);

    /// <summary>The key is part of the JSON body. If no key is configured, any non-empty key is accepted.</summary>
    private static IResult? Authorize(string? key, IConfiguration config)
    {
        var expected = config[ApiKeyConfig];
        return string.IsNullOrEmpty(key) || (!string.IsNullOrEmpty(expected) && key != expected)
            ? Error(new MandrillError("error", -1, "Invalid_Key", "Invalid API key"))
            : null;
    }

    private static string? Validate(MandrillRequest req, bool template)
    {
        if (template && string.IsNullOrWhiteSpace(req.TemplateName))
        {
            return "template_name: Please enter a value";
        }

        var m = req.Message;
        if (m is null)
        {
            return "message: Please enter an array";
        }
        if (m.To is null || m.To.Count == 0)
        {
            return "message.to: Please enter an array";
        }
        if (m.To.Any(r => r is null))
        {
            return "message.to: Please enter an array of recipients";
        }
        foreach (var recipient in m.To)
        {
            var type = recipient.Type?.ToLowerInvariant();
            if (type is not (null or "" or "to" or "cc" or "bcc"))
            {
                return $"message.to.type: \"{recipient.Type}\" must be one of to, cc, bcc";
            }
        }
        // Templates carry a default sender, so send-template may omit from_email.
        if (string.IsNullOrWhiteSpace(m.FromEmail) && !template)
        {
            return "message.from_email: Please enter a value";
        }
        if (!string.IsNullOrWhiteSpace(m.FromEmail) && !EmailValidation.IsValid(m.FromEmail))
        {
            return "message.from_email: An email address must contain a single @";
        }
        if (!string.IsNullOrEmpty(m.BccAddress) && !EmailValidation.IsValid(m.BccAddress))
        {
            return "message.bcc_address: An email address must contain a single @";
        }
        if (m.MergeLanguage is { } language && !language.Equals("mailchimp", StringComparison.OrdinalIgnoreCase) && !language.Equals("handlebars", StringComparison.OrdinalIgnoreCase))
        {
            return "message.merge_language: must be one of mailchimp, handlebars";
        }
        foreach (var v in (m.GlobalMergeVars ?? []).Concat((m.MergeVars ?? []).SelectMany(mv => mv?.Vars ?? [])))
        {
            if (v is null || string.IsNullOrEmpty(v.Name) || v.Name.StartsWith('_'))
            {
                return "message.merge_vars: merge var names must be non-empty and may not start with an underscore";
            }
        }
        foreach (var tag in m.Tags ?? [])
        {
            if (tag is null || tag.Length > 50 || tag.StartsWith('_'))
            {
                return "message.tags: tags must be at most 50 characters and may not start with an underscore";
            }
        }
        foreach (var (file, kind) in (m.Attachments ?? []).Select(a => (a, "attachments")).Concat((m.Images ?? []).Select(i => (i, "images"))))
        {
            if (file is null || string.IsNullOrEmpty(file.Content) || !ProviderHelpers.IsBase64(file.Content))
            {
                return $"message.{kind}.content: must be base64 encoded";
            }
            if (string.IsNullOrWhiteSpace(file.Name))
            {
                return $"message.{kind}.name: Please enter a value";
            }
            if (kind == "images" && file.Type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            {
                return "message.images.type: must start with image/";
            }
        }
        if (req.SendAt is { Length: > 0 } sendAt && MandrillMapper.ParseSendAt(sendAt) is null)
        {
            return "send_at: must be a UTC timestamp in YYYY-MM-DD HH:MM:SS format";
        }
        return null;
    }

    // Mandrill reports errors with HTTP 500 and code -2 for validation errors.
    private static IResult ValidationError(string details) =>
        Error(new MandrillError("error", -2, "ValidationError", $"Validation error: {details}"));

    private static IResult Error(MandrillError error) =>
        Results.Json(error, MandrillJsonContext.Default.MandrillError, statusCode: 500);
}
