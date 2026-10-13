namespace Nevermail.Providers;

public static class EmailValidation
{
    /// <summary>
    /// Pragmatic address check (local@domain.tld, no whitespace). Deliberately not RFC 5322 complete:
    /// it should reject what real providers reject in practice, not every exotic edge case.
    /// </summary>
    public static bool IsValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }
        email = email.Trim();

        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1 || email.IndexOf('@') != at)
        {
            return false;
        }
        foreach (var ch in email)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '<' or '>' or ',' or ';')
            {
                return false;
            }
        }

        var domain = email.AsSpan(at + 1);
        var dot = domain.LastIndexOf('.');
        return dot > 0 && dot < domain.Length - 1 && !domain.Contains("..", StringComparison.Ordinal);
    }
}
