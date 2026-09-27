using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.CodeFix;

/// <summary>
/// Turns an incident and its investigation into the request a coder receives. Pure apart from the
/// options it reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only grounded evidence crosses.</b> The coder sees the excerpts that survived the substring
/// check and nothing else - no raw evidence blobs, no ConfigMap dumps, no tool arguments. Everything
/// that did cross came out of a workload's logs, so the runner wraps it as untrusted data; this side
/// caps it and scrubs the obvious credentials, because a coder has no business holding a connection
/// string it happened to read in a stack trace.
/// </para>
/// </remarks>
public sealed partial class CodeFixRequestBuilder(IOptionsMonitor<CodeFixOptions> options)
{
    public const int MaxFindings = 10;
    public const int MaxEvidencePerFinding = 20;
    public const int MaxExcerptChars = 2048;
    public const int MaxHypothesisChars = 4000;
    public const int MaxSummaryChars = 8000;

    public CodeFixRequest Build(
        CodeFixAttempt attempt,
        CodeFixPhase phase,
        Incident incident,
        Investigation? investigation,
        string? image,
        string? rolloutRevision,
        CodeFixPlanResult? plan)
    {
        var o = options.CurrentValue;

        if (phase == CodeFixPhase.Implement && plan is null)
            throw new InvalidOperationException("an implement request needs the approved plan");

        var tools = investigation?.Steps.ToDictionary(s => s.Id, s => s.ToolName ?? "unknown") ?? [];

        var findings = (investigation?.Findings ?? [])
            .OrderByDescending(f => f.IsPrimary)
            .ThenByDescending(f => f.Confidence)
            .Take(MaxFindings)
            .Select(f => new CodeFixFinding
            {
                Id = f.Id,
                Primary = f.IsPrimary,
                Category = Cap(f.Category, 64),
                Confidence = Math.Clamp(f.Confidence, 0, 1),
                Hypothesis = Cap(Redact(f.Hypothesis), MaxHypothesisChars),
                Evidence = f.Evidence
                    .Take(MaxEvidencePerFinding)
                    .Select(e => new CodeFixEvidence(
                        e.StepId,
                        Cap(tools.GetValueOrDefault(e.StepId, "unknown"), 128),
                        Cap(Redact(e.Excerpt), MaxExcerptChars)))
                    .ToList(),
            })
            .ToList();

        var summary = investigation?.Plan?.Summary;

        return new CodeFixRequest
        {
            AttemptId = attempt.Id,
            IncidentId = incident.Id,
            Phase = phase == CodeFixPhase.Plan ? "plan" : "implement",
            Budget = new CodeFixBudget(
                phase == CodeFixPhase.Plan ? o.MaxCostUsdPerPlan : o.MaxCostUsdPerImplement,
                (int)(phase == CodeFixPhase.Plan ? o.PlanDeadline : o.ImplementDeadline).TotalSeconds),
            Repository = new CodeFixRepository(attempt.RepositoryUrl, attempt.DefaultBranch, BindingPath(o, attempt), attempt.Branch),
            Context = new CodeFixContextRef(o.ContextRepositoryUrl, o.ContextRepositoryRef),
            Incident = new CodeFixIncident
            {
                Title = Cap(incident.Title, 512),
                Kind = incident.Kind.ToString(),
                Severity = incident.Severity.ToString(),
                Target = new CodeFixTarget(
                    incident.Target.Namespace,
                    incident.Target.Kind,
                    incident.Target.Name,
                    Cap(incident.Target.WorkloadKey, 600)),
                Image = image is null ? null : Cap(image, 1024),
                RolloutRevision = rolloutRevision,
                EscalationReason = incident.EscalationReason.ToString(),
            },
            Findings = findings,
            InvestigationSummary = string.IsNullOrWhiteSpace(summary) ? null : Cap(Redact(summary), MaxSummaryChars),
            Plan = plan,
        };
    }

    private static string BindingPath(CodeFixOptions o, CodeFixAttempt attempt) =>
        o.BindingFor(attempt.Workload)?.Path ?? string.Empty;

    private static string Cap(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value.Length <= max)
            return value;

        // Never split a surrogate pair: a lone half is invalid UTF-8 on the wire.
        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..cut];
    }

    /// <summary>Scrubs the credential shapes that turn up in logs. Keeps the key, drops the value.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

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
