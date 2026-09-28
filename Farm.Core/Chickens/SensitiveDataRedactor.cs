using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Farm.Core.Chickens;

public interface ISensitiveDataRedactor
{
    string Redact(string? value);
}

public sealed partial class SensitiveDataRedactor : ISensitiveDataRedactor
{
    public static SensitiveDataRedactor Empty { get; } = new([]);

    private readonly string[] secretForms;

    public SensitiveDataRedactor(IEnumerable<string?> secrets)
    {
        secretForms = secrets
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .SelectMany(Expand)
            .Where(value => value.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public string Redact(string? value)
    {
        var redacted = value ?? string.Empty;
        foreach (var secret in secretForms)
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        redacted = AuthorizationValue().Replace(redacted, "$1[REDACTED]");
        return UrlUserInfo().Replace(redacted, "$1[REDACTED]@");
    }

    private static IEnumerable<string> Expand(string? secret)
    {
        var value = secret!.Trim();
        yield return value;
        yield return Uri.EscapeDataString(value);
        yield return WebUtility.UrlEncode(value);
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    [GeneratedRegex("(?im)(authorization\\s*[:=]\\s*(?:basic|bearer)\\s+)\\S+")]
    private static partial Regex AuthorizationValue();

    [GeneratedRegex("(?i)(https?://)[^/@\\s]+@")]
    private static partial Regex UrlUserInfo();
}