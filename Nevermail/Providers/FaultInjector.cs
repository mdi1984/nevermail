using System.Globalization;

namespace Nevermail.Providers;

/// <summary>A simulated provider failure: HTTP status (or SMTP reply code) plus a human readable reason.</summary>
public sealed record Fault(int Code, string Reason);

/// <summary>
/// Makes sends fail on purpose, so apps can test their error handling and retries:
/// <list type="bullet">
/// <item>Recipients at <c>fail-&lt;code&gt;.test</c> (e.g. <c>someone@fail-429.test</c>) always fail with that code.</item>
/// <item><c>Faults:Rate</c> (0..1) fails that share of all sends with <c>Faults:StatusCode</c> (default 503).</item>
/// <item><c>Faults:DelayMs</c> slows every send down, like a sluggish provider.</item>
/// </list>
/// </summary>
public sealed class FaultInjector(IConfiguration config)
{
    public const string TriggerPrefix = "fail-";
    public const string TriggerSuffix = ".test";

    public TimeSpan Delay =>
        TimeSpan.FromMilliseconds(int.TryParse(config["Faults:DelayMs"], out var delay) && delay > 0 ? delay : 0);

    /// <summary>HTTP fault for an API send to these recipients, or null.</summary>
    public Fault? ForHttp(IEnumerable<string?> recipients) =>
        Triggered(recipients, code => code is >= 400 and <= 599) ?? Random(DefaultHttpStatus);

    /// <summary>SMTP reply code for a single RCPT TO address, or null.</summary>
    public Fault? ForSmtpRecipient(string recipient) =>
        Triggered([recipient], code => code is >= 400 and <= 599);

    /// <summary>Random SMTP failure at the end of DATA (temporary by default), or null.</summary>
    public Fault? ForSmtpMessage() => Random(451);

    /// <summary>Waits for the configured delay.</summary>
    public Task DelayAsync(CancellationToken ct) => Delay > TimeSpan.Zero ? Task.Delay(Delay, ct) : Task.CompletedTask;

    private int DefaultHttpStatus => int.TryParse(config["Faults:StatusCode"], out var status) && status is >= 400 and <= 599 ? status : 503;

    private static Fault? Triggered(IEnumerable<string?> recipients, Func<int, bool> valid)
    {
        foreach (var recipient in recipients)
        {
            if (TryGetTriggerCode(recipient, out var code) && valid(code))
            {
                return new Fault(code, $"Simulated failure ({code}) triggered by recipient {recipient!.Trim()}");
            }
        }
        return null;
    }

    private Fault? Random(int code)
    {
        if (!double.TryParse(config["Faults:Rate"], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || rate <= 0)
        {
            return null;
        }
        return System.Random.Shared.NextDouble() < rate
            ? new Fault(code, $"Simulated failure ({code}) from Faults:Rate={rate.ToString(CultureInfo.InvariantCulture)}")
            : null;
    }

    /// <summary>"x@fail-429.test" → 429.</summary>
    public static bool TryGetTriggerCode(string? email, out int code)
    {
        code = 0;
        var at = email?.LastIndexOf('@') ?? -1;
        if (at < 0)
        {
            return false;
        }
        var domain = email!.AsSpan(at + 1).Trim();
        return domain.StartsWith(TriggerPrefix, StringComparison.OrdinalIgnoreCase)
            && domain.EndsWith(TriggerSuffix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(domain[TriggerPrefix.Length..^TriggerSuffix.Length], NumberStyles.None, CultureInfo.InvariantCulture, out code);
    }
}
