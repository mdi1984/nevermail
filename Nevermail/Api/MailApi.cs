using Nevermail.Mail;
using Nevermail.Storage;

namespace Nevermail.Api;

/// <summary>Nevermail's own API, used by the web UI and by tests that want to inspect captured mails.</summary>
public static class MailApi
{
    public static IEndpointRouteBuilder MapMailApi(this IEndpointRouteBuilder app)
    {
        var mails = app.MapGroup("/api/mails");

        mails.MapGet("/", (IMailStore store, string? search, string? provider, string? to, string? requestId, int? limit) =>
        {
            var result = store.Query(new MailQuery(search, provider, to, requestId, limit));
            return TypedResults.Ok(result.Select(MailSummary.Create).ToList());
        });

        mails.MapGet("/{id}", (string id, IMailStore store) =>
            store.Get(id) is { } mail ? Results.Ok(mail) : Results.NotFound());

        mails.MapGet("/{id}/attachments/{index:int}", (string id, int index, IMailStore store) =>
        {
            if (store.Get(id) is not { } mail || index < 0 || index >= mail.Message.Attachments.Count)
            {
                return Results.NotFound();
            }
            var attachment = mail.Message.Attachments[index];
            return Results.File(
                Convert.FromBase64String(attachment.Content),
                attachment.ContentType ?? "application/octet-stream",
                attachment.Filename ?? $"attachment-{index}");
        });

        mails.MapDelete("/{id}", (string id, IMailStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound());

        mails.MapDelete("/", (IMailStore store) =>
        {
            store.Clear();
            return Results.NoContent();
        });

        return app;
    }
}
