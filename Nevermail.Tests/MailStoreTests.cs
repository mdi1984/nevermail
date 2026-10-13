using Microsoft.Extensions.Logging.Abstractions;
using Nevermail.Mail;
using Nevermail.Storage;

namespace Nevermail.Tests;

public sealed class MailStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nevermail-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static StoredMail Mail(string subject, string to = "to@example.com", DateTimeOffset? at = null) => new()
    {
        Provider = "sendgrid",
        RequestId = "req",
        ReceivedAt = at ?? DateTimeOffset.UtcNow,
        Message = new MailMessage
        {
            From = new MailAddress("from@example.com", "Sender"),
            To = [new MailAddress(to)],
            Subject = subject,
            Attachments = [new MailAttachment { Filename = "a.txt", Content = "aGVsbG8=" }],
        },
    };

    private MailStore Create(string? path = null, int max = 1000) => new(path, max, NullLogger<MailStore>.Instance);

    [Fact]
    public async Task Query_ReturnsNewestFirst()
    {
        var store = Create();
        await store.AddAsync([Mail("first")]);
        await store.AddAsync([Mail("second")]);

        Assert.Equal(["second", "first"], store.Query(new()).Select(m => m.Message.Subject));
    }

    [Fact]
    public async Task Query_Filters()
    {
        var store = Create();
        await store.AddAsync([Mail("Welcome aboard", "alice@example.com"), Mail("Password reset", "bob@example.com")]);

        Assert.Equal("Password reset", Assert.Single(store.Query(new(Search: "reset"))).Message.Subject);
        Assert.Equal("Welcome aboard", Assert.Single(store.Query(new(To: "ALICE@example.com"))).Message.Subject);
        Assert.Equal(2, store.Query(new(Search: "sender")).Count);
        Assert.Empty(store.Query(new(Provider: "brevo")));
        Assert.Single(store.Query(new(Limit: 1)));
    }

    [Fact]
    public async Task OldestMails_AreEvicted()
    {
        var store = Create(_dir, max: 2);
        await store.AddAsync([Mail("1"), Mail("2"), Mail("3")]);

        Assert.Equal(["3", "2"], store.Query(new()).Select(m => m.Message.Subject));
        Assert.Equal(2, Directory.GetFiles(_dir, "*.json").Length);
    }

    [Fact]
    public async Task Changes_AreReported()
    {
        var store = Create(max: 2);
        var changes = new List<MailStoreChange>();
        store.Changed += changes.Add;
        var (one, two, three) = (Mail("1"), Mail("2"), Mail("3"));

        await store.AddAsync([one, two]);
        await store.AddAsync([three]);
        store.Delete(three.Id);
        store.Clear();

        Assert.Equal(
            [$"added:{one.Id},{two.Id}", $"added:{three.Id}", $"deleted:{one.Id}", $"deleted:{three.Id}", "cleared:"],
            changes.Select(c => $"{c.Kind}:{string.Join(',', c.Ids)}"));
    }

    [Fact]
    public async Task FileStore_SurvivesRestart()
    {
        var mail = Mail("persisted");
        await Create(_dir).AddAsync([mail, Mail("other")]);

        var reloaded = Create(_dir);

        var loaded = reloaded.Get(mail.Id);
        Assert.NotNull(loaded);
        Assert.Equal("persisted", loaded.Message.Subject);
        Assert.Equal("Sender", loaded.Message.From!.Name);
        Assert.Equal("aGVsbG8=", Assert.Single(loaded.Message.Attachments).Content);
        Assert.Equal(2, reloaded.Query(new()).Count);
    }

    [Fact]
    public async Task FileStore_SkipsCorruptFiles()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "garbage.json"), "{ not json");
        await Create(_dir).AddAsync([Mail("fine")]);

        Assert.Single(Create(_dir).Query(new()));
    }

    [Fact]
    public async Task Delete_And_Clear_RemoveFiles()
    {
        var store = Create(_dir);
        var keep = Mail("keep");
        var remove = Mail("remove");
        await store.AddAsync([keep, remove]);

        Assert.True(store.Delete(remove.Id));
        Assert.False(store.Delete(remove.Id));
        Assert.Null(store.Get(remove.Id));
        Assert.Single(Directory.GetFiles(_dir, "*.json"));

        store.Clear();
        Assert.Empty(store.Query(new()));
        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
    }
}
