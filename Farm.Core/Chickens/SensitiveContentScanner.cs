using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Farm.Core.Chickens;

public interface ISensitiveContentScanner
{
    IReadOnlyList<string> Find(string? content);
}

public sealed partial class SensitiveContentScanner : ISensitiveContentScanner
{
    public static SensitiveContentScanner Empty { get; } = new([]);

    private readonly string[] configuredSecretForms;

    public SensitiveContentScanner(IEnumerable<string?> configuredSecrets)
    {
        configuredSecretForms = configuredSecrets
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .SelectMany(Expand)
            .Where(secret => secret.Length >= 8)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> Find(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var findings = new HashSet<string>(StringComparer.Ordinal);
        if (configuredSecretForms.Any(secret => content.Contains(secret, StringComparison.Ordinal))) findings.Add("configured secret");
        if (GitHubToken().IsMatch(content)) findings.Add("GitHub token");
        if (AzureDevOpsPat().IsMatch(content) || LegacyAzureDevOpsPat().IsMatch(content)) findings.Add("Azure DevOps PAT");
        if (Jwt().IsMatch(content)) findings.Add("JWT");
        if (PemPrivateKey().IsMatch(content)) findings.Add("PEM private key");
        if (ConnectionStringPassword().IsMatch(content)) findings.Add("connection string password");
        if (SecretAssignment().IsMatch(content)) findings.Add("secret assignment");
        if (CredentialedUrl().IsMatch(content)) findings.Add("credentialed URL");
        return findings.Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> Expand(string? secret)
    {
        var value = secret!.Trim();
        yield return value;
        yield return Uri.EscapeDataString(value);
        yield return WebUtility.UrlEncode(value);
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\b[A-Za-z0-9]{75}AZDO[A-Za-z0-9]{5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AzureDevOpsPat();

    [GeneratedRegex(@"\b[A-Za-z0-9]{52}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyAzureDevOpsPat();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PemPrivateKey();

    [GeneratedRegex(@"(?i)(?:Server|Data Source)\s*=.+?;(?:Password|Pwd)\s*=\s*[^;\s]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPassword();

    [GeneratedRegex(@"(?im)\b(?:password|passwd|pwd|pat|token|secret|client[_-]?secret|api[_-]?key)\s*[:=]\s*[""'][^""'\r\n]{8,}[""']", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?i)https?://[^/@\s:]+:[^/@\s]+@", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialedUrl();
}