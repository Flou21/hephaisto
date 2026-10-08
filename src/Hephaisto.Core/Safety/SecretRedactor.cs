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

    // The key may be the last part of a longer name - GITHUB_TOKEN, GitHub__Token,
    // CLAUDE_CODE_OAUTH_TOKEN, spring.datasource.password - which is how a credential is named
    // in an environment and in a values file. `_` is a word character, so without the prefix
    // group there is no word boundary inside GITHUB_TOKEN and it was not matched at all.
    [GeneratedRegex(@"(?<key>\b(?:[A-Za-z0-9]+[_.-]+)*(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret)\b)(?<sep>\s*[=:]\s*)(?<value>""[^""]*""|'[^']*'|[^\s;,&""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex Bearer();

    // GitHub's five prefixes: personal (ghp_), OAuth (gho_), user-to-server (ghu_),
    // server-to-server (ghs_) and refresh (ghr_), and the fine-grained github_pat_.
    [GeneratedRegex(@"\b(?:ghp_|gho_|ghu_|ghs_|ghr_|github_pat_|sk-ant-|sk-|xox[bap]-|AKIA)[A-Za-z0-9_-]{8,}")]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^\s/@:]+:[^\s/@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();
}
