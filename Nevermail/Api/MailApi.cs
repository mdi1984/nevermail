using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nevermail.Mail;
using Nevermail.Providers.Smtp;
using Nevermail.Storage;

namespace Nevermail.Api;

/// <summary>Nevermail's own API, used by the web UI and by tests that want to inspect captured mails.</summary>
public static class MailApi
{
    public static IEndpointRouteBuilder MapMailApi(this IEndpointRouteBuilder app, IReadOnlyList<string> enabledProviders)
    {
        app.MapGet("/api/info", (SmtpServer smtp) =>
            TypedResults.Ok(new NevermailInfo(enabledProviders, smtp.Port)));

        // Live updates for the UI: one event per store change ("added", "deleted", "cleared"), data = mail ids.
        app.MapGet("/api/events", (IMailStore store, HttpContext ctx) =>
            TypedResults.ServerSentEvents(StreamChangesAsync(store, ctx.RequestAborted)));

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
            if (attachment.Url is { } url && attachment.Content.Length == 0)
            {
                // Remote attachment (Brevo): Nevermail never fetches it, the browser may.
                return Results.Redirect(url);
            }
            return Results.File(
                Convert.FromBase64String(attachment.Content),
                attachment.ContentType ?? "application/octet-stream",
                attachment.Filename ?? $"attachment-{index}");
        });

        mails.MapGet("/{id}/eml", (string id, IMailStore store) =>
            store.Get(id) is { } mail
                ? Results.File(EmlExport.ToEml(mail), "message/rfc822", EmlExport.FileName(mail))
                : Results.NotFound());

        mails.MapDelete("/{id}", (string id, IMailStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound());

        mails.MapDelete("/", (IMailStore store) =>
        {
            store.Clear();
            return Results.NoContent();
        });

        return app;
    }

    private static async IAsyncEnumerable<SseItem<string>> StreamChangesAsync(IMailStore store, [EnumeratorCancellation] CancellationToken ct)
    {
        // Slow clients lose old events rather than blocking the store; the UI reloads on every event anyway.
        var channel = Channel.CreateBounded<MailStoreChange>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnChanged(MailStoreChange change) => channel.Writer.TryWrite(change);

        store.Changed += OnChanged;
        try
        {
            yield return new SseItem<string>("", "ready");
            await foreach (var change in channel.Reader.ReadAllAsync(ct))
            {
                yield return new SseItem<string>(string.Join(',', change.Ids), change.Kind);
            }
        }
        finally
        {
            store.Changed -= OnChanged;
        }
    }
}

public sealed record NevermailInfo(IReadOnlyList<string> Providers, int? SmtpPort);
