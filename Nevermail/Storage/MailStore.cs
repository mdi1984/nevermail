using System.Text.Json;
using Nevermail.Mail;

namespace Nevermail.Storage;

public sealed record MailQuery(
    string? Search = null,
    string? Provider = null,
    string? To = null,
    string? RequestId = null,
    int? Limit = null);

public interface IMailStore
{
    Task AddAsync(IReadOnlyList<StoredMail> mails);
    IReadOnlyList<StoredMail> Query(MailQuery query);
    StoredMail? Get(string id);
    bool Delete(string id);
    void Clear();
}

/// <summary>
/// Keeps all mails in memory (newest last) and optionally mirrors them to one JSON file per mail
/// so they survive restarts. The oldest mails are evicted once <c>maxMessages</c> is exceeded.
/// </summary>
public sealed class MailStore : IMailStore
{
    private readonly Lock _lock = new();
    private readonly List<StoredMail> _mails = [];
    private readonly Dictionary<string, StoredMail> _byId = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly int _maxMessages;
    private readonly ILogger<MailStore> _logger;

    public MailStore(string? path, int maxMessages, ILogger<MailStore> logger)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _maxMessages = maxMessages > 0 ? maxMessages : int.MaxValue;
        _logger = logger;

        if (_path != null)
        {
            Directory.CreateDirectory(_path);
            Load();
        }
    }

    public static MailStore FromConfiguration(IConfiguration config, ILogger<MailStore> logger)
    {
        var maxMessages = int.TryParse(config["Storage:MaxMessages"], out var max) ? max : 1000;
        return new MailStore(config["Storage:Path"], maxMessages, logger);
    }

    public async Task AddAsync(IReadOnlyList<StoredMail> mails)
    {
        if (_path != null)
        {
            foreach (var mail in mails)
            {
                await using var stream = File.Create(FilePath(mail.Id));
                await JsonSerializer.SerializeAsync(stream, mail, NevermailJsonContext.Default.StoredMail);
            }
        }

        List<StoredMail> evicted;
        lock (_lock)
        {
            foreach (var mail in mails)
            {
                _mails.Add(mail);
                _byId[mail.Id] = mail;
            }
            evicted = EvictOverflow();
        }
        DeleteFiles(evicted);
    }

    public IReadOnlyList<StoredMail> Query(MailQuery query)
    {
        lock (_lock)
        {
            IEnumerable<StoredMail> result = Enumerable.Reverse(_mails);

            if (!string.IsNullOrWhiteSpace(query.Provider))
            {
                result = result.Where(m => m.Provider.Equals(query.Provider, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(query.RequestId))
            {
                result = result.Where(m => m.RequestId == query.RequestId);
            }
            if (!string.IsNullOrWhiteSpace(query.To))
            {
                result = result.Where(m => Recipients(m.Message).Any(a => a.Email.Equals(query.To, StringComparison.OrdinalIgnoreCase)));
            }
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                result = result.Where(m => Matches(m.Message, query.Search));
            }
            if (query.Limit is > 0)
            {
                result = result.Take(query.Limit.Value);
            }
            return result.ToList();
        }
    }

    public StoredMail? Get(string id)
    {
        lock (_lock)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (!_byId.Remove(id, out var mail))
            {
                return false;
            }
            _mails.Remove(mail);
        }
        DeleteFiles([id]);
        return true;
    }

    public void Clear()
    {
        List<string> ids;
        lock (_lock)
        {
            ids = [.. _byId.Keys];
            _mails.Clear();
            _byId.Clear();
        }
        DeleteFiles(ids);
    }

    private void Load()
    {
        var loaded = new List<StoredMail>();
        foreach (var file in Directory.EnumerateFiles(_path!, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var mail = JsonSerializer.Deserialize(stream, NevermailJsonContext.Default.StoredMail);
                if (mail != null && Path.GetFileNameWithoutExtension(file) == mail.Id)
                {
                    loaded.Add(mail);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning("Skipping unreadable mail file {File}: {Error}", file, ex.Message);
            }
        }

        foreach (var mail in loaded.OrderBy(m => m.ReceivedAt))
        {
            _mails.Add(mail);
            _byId[mail.Id] = mail;
        }
        DeleteFiles(EvictOverflow());
        _logger.LogInformation("Loaded {Count} mails from {Path}", _mails.Count, _path);
    }

    private List<StoredMail> EvictOverflow()
    {
        var overflow = _mails.Count - _maxMessages;
        if (overflow <= 0)
        {
            return [];
        }
        var evicted = _mails.GetRange(0, overflow);
        _mails.RemoveRange(0, overflow);
        foreach (var mail in evicted)
        {
            _byId.Remove(mail.Id);
        }
        return evicted;
    }

    private void DeleteFiles(IEnumerable<StoredMail> mails) => DeleteFiles(mails.Select(m => m.Id));

    private void DeleteFiles(IEnumerable<string> ids)
    {
        if (_path == null)
        {
            return;
        }
        foreach (var id in ids)
        {
            try
            {
                File.Delete(FilePath(id));
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Could not delete mail file for {Id}: {Error}", id, ex.Message);
            }
        }
    }

    // Ids are generated by MailIds (alphanumeric only), so they are safe file names.
    private string FilePath(string id) => Path.Combine(_path!, $"{id}.json");

    private static IEnumerable<MailAddress> Recipients(MailMessage m) => m.To.Concat(m.Cc).Concat(m.Bcc);

    private static bool Matches(MailMessage m, string term)
    {
        static bool Has(string? value, string term) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

        return Has(m.Subject, term)
            || (m.From is { } from && (Has(from.Email, term) || Has(from.Name, term)))
            || Recipients(m).Any(a => Has(a.Email, term) || Has(a.Name, term));
    }
}
