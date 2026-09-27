using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hephaisto.Agent.CodeFix.Contract;

namespace Hephaisto.Agent.CodeFix;

/// <summary>What came out of a coder's log.</summary>
public sealed record CodeFixParse<T>(T? Result, string? Json, string? Violation)
    where T : class
{
    public bool Ok => Result is not null;

    public static CodeFixParse<T> Violated(string why) => new(null, null, why);
}

/// <summary>
/// Reads the framed result block off the end of a coder pod's log. Pure: a string in, a verdict out.
/// </summary>
/// <remarks>
/// <para>
/// The coder has no credential for Hephaisto and no way to call it, so its answer travels the one
/// channel Hephaisto can already read: its own log. That log also carries everything the agent
/// printed while reading attacker-influenceable production evidence, so the parser trusts the
/// framing and nothing else:
/// </para>
/// <list type="bullet">
/// <item>the LAST <c>BEGIN</c>/<c>END</c> pair wins - an earlier block, or one echoed by a tool, is
/// not the answer;</item>
/// <item>the declared byte count and sha256 must match the payload exactly, so a block cut by log
/// rotation or stitched together by an echo is refused rather than half-read;</item>
/// <item>unknown members are refused, sizes are capped, and the attempt id must be the one this Job
/// was launched for - a pod printing another attempt's result changes nothing.</item>
/// </list>
/// </remarks>
public static partial class CodeFixResultParser
{
    public const int MaxPayloadBytes = 512 * 1024;

    private const string End = "---HEPHAISTO-RESULT-END---";

    [GeneratedRegex(@"^---HEPHAISTO-RESULT-BEGIN sha256=(?<sha>[0-9a-f]{64}) bytes=(?<bytes>\d{1,7})---$", RegexOptions.Multiline)]
    private static partial Regex BeginLine();

    public static CodeFixParse<CodeFixPlanResult> ParsePlan(string? log, Guid attemptId) =>
        Parse<CodeFixPlanResult>(log, attemptId, "plan", r => (r.AttemptId, r.Phase));

    public static CodeFixParse<CodeFixImplementResult> ParseImplement(string? log, Guid attemptId) =>
        Parse<CodeFixImplementResult>(log, attemptId, "implement", r => (r.AttemptId, r.Phase));

    private static CodeFixParse<T> Parse<T>(string? log, Guid attemptId, string phase, Func<T, (Guid, string)> identity)
        where T : class
    {
        if (string.IsNullOrEmpty(log))
            return CodeFixParse<T>.Violated("the coder's log is empty; no result block");

        var begins = BeginLine().Matches(log);

        if (begins.Count == 0)
            return CodeFixParse<T>.Violated("no result block in the coder's log");

        var begin = begins[^1];
        var payloadStart = begin.Index + begin.Length;

        // The payload is exactly one line after the BEGIN line.
        if (payloadStart < log.Length && log[payloadStart] == '\r')
            payloadStart++;

        if (payloadStart >= log.Length || log[payloadStart] != '\n')
            return CodeFixParse<T>.Violated("the result block has no payload line");

        payloadStart++;
        var payloadEnd = log.IndexOf('\n', payloadStart);

        if (payloadEnd < 0)
            return CodeFixParse<T>.Violated("the result block is truncated (no END marker)");

        var payload = log[payloadStart..payloadEnd].TrimEnd('\r');
        var rest = log[(payloadEnd + 1)..];
        var endLine = rest.Split('\n', 2)[0].TrimEnd('\r');

        if (endLine != End)
            return CodeFixParse<T>.Violated("the result block is not terminated by the END marker");

        var bytes = Encoding.UTF8.GetBytes(payload);
        var declaredBytes = int.Parse(begin.Groups["bytes"].Value, System.Globalization.CultureInfo.InvariantCulture);

        if (bytes.Length > MaxPayloadBytes)
            return CodeFixParse<T>.Violated($"the result is {bytes.Length} bytes; the cap is {MaxPayloadBytes}");

        if (bytes.Length != declaredBytes)
            return CodeFixParse<T>.Violated($"the result declares {declaredBytes} bytes and carries {bytes.Length}");

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (!string.Equals(sha, begin.Groups["sha"].Value, StringComparison.Ordinal))
            return CodeFixParse<T>.Violated("the result's sha256 does not match its payload");

        T? result;

        try
        {
            result = JsonSerializer.Deserialize<T>(payload, CodeFixContract.Json);
        }
        catch (JsonException ex)
        {
            return CodeFixParse<T>.Violated($"the result does not match the v{CodeFixContract.Version} contract: {ex.Message}");
        }

        if (result is null)
            return CodeFixParse<T>.Violated("the result is null");

        var (id, reportedPhase) = identity(result);

        if (id != attemptId)
            return CodeFixParse<T>.Violated($"the result names attempt {id}, not {attemptId}");

        if (!string.Equals(reportedPhase, phase, StringComparison.Ordinal))
            return CodeFixParse<T>.Violated($"the result is for phase '{reportedPhase}', not '{phase}'");

        return new CodeFixParse<T>(result, payload, null);
    }

    /// <summary>Plan outcomes the contract allows.</summary>
    public static readonly IReadOnlySet<string> PlanOutcomes =
        new HashSet<string>(StringComparer.Ordinal) { "planned", "not_a_code_problem", "insufficient_context", "failed" };

    public static readonly IReadOnlySet<string> ImplementOutcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        "pr_opened", "already_exists", "no_changes", "build_failed", "tests_failed", "policy_diff", "failed",
    };

    /// <summary>
    /// The checks Hephaisto makes itself before believing a PR exists. The runner is trusted to
    /// report, not to decide: a PR on another repository, from another branch, or on a red build is
    /// refused however confidently it is described.
    /// </summary>
    public static string? CheckImplementPostConditions(
        CodeFixImplementResult result,
        string assignedBranch,
        string repositoryUrl,
        IReadOnlyCollection<string> allowedHosts,
        bool requireGreenBuild)
    {
        if (!ImplementOutcomes.Contains(result.Outcome))
            return $"unknown outcome '{result.Outcome}'";

        if (result.Outcome is not ("pr_opened" or "already_exists"))
            return null;

        if (!string.Equals(result.Branch, assignedBranch, StringComparison.Ordinal))
            return $"the PR is from '{result.Branch}', not the assigned branch '{assignedBranch}'";

        if (!Uri.TryCreate(result.PrUrl, UriKind.Absolute, out var pr))
            return "the PR URL is not an absolute URL";

        if (!allowedHosts.Contains(pr.Host, StringComparer.OrdinalIgnoreCase))
            return $"the PR is on host '{pr.Host}', which is not allowed";

        if (RepositoryPath(repositoryUrl) is not { } repo
            || !pr.AbsolutePath.StartsWith(repo + "/pull/", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repoUri)
            || !string.Equals(repoUri.Host, pr.Host, StringComparison.OrdinalIgnoreCase))
        {
            return $"the PR '{result.PrUrl}' is not on the mapped repository '{repositoryUrl}'";
        }

        if (requireGreenBuild && result.Outcome == "pr_opened" && !(result.BuildPassed && result.TestsPassed))
            return "a PR was opened on a build or test run that was not green";

        return null;
    }

    /// <summary><c>/owner/repo</c> of a repository URL, without <c>.git</c>.</summary>
    public static string? RepositoryPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var path = uri.AbsolutePath.TrimEnd('/');

        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    }

    /// <summary>Frames a payload exactly as the runner does. For tests and the dev forger in the e2e.</summary>
    public static string Frame(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        return $"---HEPHAISTO-RESULT-BEGIN sha256={sha} bytes={bytes.Length}---\n{json}\n{End}\n";
    }
}
