using System.Text.RegularExpressions;

namespace Hephaisto.Core.Safety;

/// <summary>
/// Scrubs the credential shapes that turn up in logs: keeps the key, drops the value.
/// </summary>
/// <remarks>
/// One place for two readers that hand incident text to somebody else's model: the coder's
/// request (<c>CodeFixRequestBuilder</c>, where these four patterns were first written) and the
/// MCP endpoint. A log line that carried a password into an investigation must not carry it any
/// further, whichever way it leaves.
/// </remarks>
public static partial class SecretRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var scrubbed = KeyValueSecret().Replace(text, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}[redacted]");
        scrubbed = Bearer().Replace(scrubbed, "Bearer [redacted]");
        scrubbed = KnownToken().Replace(scrubbed, "[redacted]");
        scrubbed = UrlCredentials().Replace(scrubbed, "${scheme}[redacted]@");

        return scrubbed;
    }

    [GeneratedRegex(@"(?<key>\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret)\b)(?<sep>\s*[=:]\s*)(?<value>""[^""]*""|'[^']*'|[^\s;,&""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(?:ghp_|gho_|ghs_|github_pat_|sk-ant-|sk-|xox[bap]-|AKIA)[A-Za-z0-9_-]{8,}")]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^\s/@:]+:[^\s/@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();
}
