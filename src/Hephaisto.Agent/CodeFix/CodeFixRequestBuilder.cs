using Microsoft.Extensions.Options;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

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
public sealed class CodeFixRequestBuilder(IOptionsMonitor<CodeFixOptions> options)
{
    public const int MaxFindings = 10;
    public const int MaxEvidencePerFinding = 20;
    public const int MaxExcerptChars = 2048;
    public const int MaxHypothesisChars = 4000;
    public const int MaxSummaryChars = 8000;

    /// <summary>GitHub's own limit for an issue's text, so an issue passes as it was written.</summary>
    public const int MaxIssueBodyChars = 65_536;

    public const int MaxIssueTitleChars = 512;

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
                Hypothesis = Cap(Redact(WithCodeRefs(f)), MaxHypothesisChars),
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
            Repository = new CodeFixRepository(attempt.RepositoryUrl, attempt.DefaultBranch, o.PathFor(attempt.Workload, attempt.RepositoryUrl), attempt.Branch),
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
                    Cap(string.IsNullOrEmpty(attempt.Workload) ? incident.Target.WorkloadKey : attempt.Workload, 600)),
                Image = image is null ? null : Cap(image, 1024),
                RolloutRevision = rolloutRevision,
                EscalationReason = incident.EscalationReason.ToString(),
            },
            Findings = findings,
            InvestigationSummary = string.IsNullOrWhiteSpace(summary) ? null : Cap(Redact(summary), MaxSummaryChars),
            Plan = plan,
        };
    }

    /// <summary>
    /// The request for a work item (contract version 2). The issue travels as it was when it was
    /// taken - <see cref="WorkItem.Body"/> is a snapshot, and nothing here asks GitHub - capped,
    /// and scrubbed of the credential shapes people paste into issues with their logs.
    /// </summary>
    /// <remarks>
    /// There is no image and no analysed commit: an issue names no running thing, so the runner
    /// plans on the default branch's HEAD. Comments are not passed on yet.
    /// </remarks>
    public CodeFixWorkItemRequest BuildForWorkItem(CodeFixAttempt attempt, CodeFixPhase phase, WorkItem item, CodeFixPlanResult? plan)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(item);

        var o = options.CurrentValue;

        if (phase == CodeFixPhase.Implement && plan is null)
            throw new InvalidOperationException("an implement request needs the approved plan");

        return new CodeFixWorkItemRequest
        {
            AttemptId = attempt.Id,
            Phase = phase == CodeFixPhase.Plan ? "plan" : "implement",
            Budget = new CodeFixBudget(
                phase == CodeFixPhase.Plan ? o.MaxCostUsdPerPlan : o.MaxCostUsdPerImplement,
                (int)(phase == CodeFixPhase.Plan ? o.PlanDeadline : o.ImplementDeadline).TotalSeconds),
            Repository = new CodeFixRepository(attempt.RepositoryUrl, attempt.DefaultBranch, o.PathFor(attempt.Workload, attempt.RepositoryUrl), attempt.Branch),
            Context = new CodeFixContextRef(o.ContextRepositoryUrl, o.ContextRepositoryRef),
            WorkItem = new CodeFixWorkItem
            {
                Source = item.Source,
                Repository = item.Repository,
                Number = item.Number,
                Url = Cap(item.Url, 512),
                Title = Cap(Redact(item.Title), MaxIssueTitleChars),
                Type = string.IsNullOrWhiteSpace(item.Type) ? null : Cap(item.Type, 64),
                Author = Cap(item.AuthorLogin, 64),
                Body = Cap(Redact(item.Body), MaxIssueBodyChars),
                Comments = [],
            },
            Plan = plan,
        };
    }

    /// <summary>
    /// The hypothesis, followed by where an investigator with source access said it points (v0.12.0
    /// F5), so the plan starts from the file and line. Inside the hypothesis rather than a new field:
    /// it is model-written text like the rest of it, and the contract stays as it is.
    /// </summary>
    internal static string WithCodeRefs(Finding f)
    {
        if (f.CodeRefs.Count == 0)
            return f.Hypothesis;

        var lines = f.CodeRefs.Take(10).Select(c =>
            $"- {c.Path}:{c.Line}{(c.EndLine is { } end ? $"-{end}" : string.Empty)}"
            + (c.Ref is { Length: > 0 } sha ? $" at {sha}" : string.Empty)
            + (c.Note is { Length: > 0 } note ? $" ({note})" : string.Empty));

        return $"{f.Hypothesis}\n\nWhere the investigator, reading the running revision, says it points:\n{string.Join("\n", lines)}";
    }

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
    /// <remarks>The patterns are <see cref="SecretRedactor"/>'s, shared with the MCP endpoint.</remarks>
    public static string Redact(string? text) => SecretRedactor.Redact(text);
}
