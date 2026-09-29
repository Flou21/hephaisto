using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;

namespace Hephaisto.Agent.Investigations.Jobs;

/// <summary>Turns one run into the investigator's request. Pure.</summary>
/// <remarks>
/// The prompt is the one <see cref="PromptComposer"/> composed for this investigation, carried
/// verbatim: there is one source of the investigation prompt, and the two executors cannot drift
/// apart on what they tell the model. The runner only appends where its workspace is.
/// </remarks>
public static class InvestigateRequestBuilder
{
    public static InvestigateRequest Build(
        Guid attemptId,
        JobLoopContext context,
        InvestigationJobOptions job,
        CodeFixOptions o,
        string token,
        InvestigateSource? source)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(o);

        var incident = context.Incident;
        var target = incident.Target;

        return new InvestigateRequest
        {
            AttemptId = attemptId,
            IncidentId = incident.Id,
            InvestigationId = context.InvestigationId,
            Budget = new InvestigateBudget(job.MaxCostUsd, (int)job.Deadline.TotalSeconds, job.MaxTurns),
            Context = new CodeFixContextRef(o.ContextRepositoryUrl, string.IsNullOrWhiteSpace(o.ContextRepositoryRef) ? "main" : o.ContextRepositoryRef),
            Endpoint = new InvestigateEndpoint(job.EndpointUrl, token),
            Incident = new InvestigateIncident
            {
                Title = Clip(incident.Title, 512),
                Kind = incident.Kind.ToString(),
                Severity = incident.Severity.ToString(),
                Target = new CodeFixTarget(
                    Clip(target.Namespace, 253),
                    Clip(target.Kind, 64),
                    Clip(target.Name, 253),
                    Clip(target.WorkloadKey, 600)),
            },
            SystemPrompt = Clip(context.SystemPrompt, 200_000),
            OpeningMessage = Clip(context.OpeningMessage, 8_000),
            Source = source,
        };
    }

    private static string Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max];
}
