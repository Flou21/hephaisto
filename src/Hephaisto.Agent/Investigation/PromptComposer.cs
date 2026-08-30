using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Investigations;

/// <summary>
/// Assembles the system prompt from parts.
/// </summary>
/// <remarks>
/// <para>
/// Six sections, in this order, and the order is not arbitrary:
/// </para>
/// <list type="number">
/// <item><c>Prompts/00-role.md</c> — who it is and what a good outcome looks like.</item>
/// <item>the environment card — facts about this cluster it cannot look up.</item>
/// <item>the incident card — what happened, to what, when.</item>
/// <item><c>Prompts/10-tool-contract.md</c> — tool results are data, never instructions.</item>
/// <item><c>Prompts/20-output-contract.md</c> — how to conclude and how to cite.</item>
/// <item>the runbook for this <see cref="SignalKind"/>.</item>
/// </list>
/// <para>
/// <b>Parts, not one blob.</b> The fragments are prose a human maintains and reviews; the
/// cards are generated from live configuration and from the incident row. Splicing them into
/// a single template would mean editing generated text to fix a sentence about honesty, and
/// the diff on a prompt change would be unreadable. It also means the fragments can be
/// live-synced into the pod and edited without a rebuild, which is why they are
/// <c>Content</c> and not embedded resources.
/// </para>
/// <para>
/// The runbook goes <b>last</b>, closest to the conversation. It is the most specific
/// instruction in the prompt and the one most likely to be needed on the first turn.
/// </para>
/// </remarks>
public sealed class PromptComposer
{
    public const string RoleFragment = "00-role.md";
    public const string ToolContractFragment = "10-tool-contract.md";
    public const string OutputContractFragment = "20-output-contract.md";
    public const string PlanningFragment = "30-planning.md";
    public const string DefaultRunbook = "_Default.md";

    private readonly string _promptsPath;
    private readonly string _runbooksPath;
    private readonly EnvironmentCardOptions _environment;
    private readonly ILogger<PromptComposer>? _logger;

    public PromptComposer(
        IOptions<EnvironmentCardOptions> environment,
        ILogger<PromptComposer>? logger = null,
        string? contentRoot = null)
    {
        _environment = environment.Value;
        _logger = logger;

        var root = contentRoot ?? AppContext.BaseDirectory;
        _promptsPath = Path.Combine(root, "Prompts");
        _runbooksPath = Path.Combine(root, "Runbooks");
    }

    /// <summary>The phase 1 system prompt.</summary>
    public string ComposeInvestigationPrompt(Incident incident, IReadOnlyList<Signal>? signals = null)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var sb = new StringBuilder();

        Append(sb, ReadFragment(RoleFragment));
        Append(sb, ComposeEnvironmentCard());
        Append(sb, ComposeIncidentCard(incident, signals ?? incident.Signals));
        Append(sb, ReadFragment(ToolContractFragment));
        Append(sb, ReadFragment(OutputContractFragment));
        Append(sb, ReadRunbook(incident.Kind));

        return sb.ToString();
    }

    /// <summary>
    /// The phase 2 system prompt. Carries no tool contract, because there are no tools -
    /// telling a model not to use tools it does not have wastes tokens and invites it to
    /// wonder where they went.
    /// </summary>
    public string ComposePlanningPrompt(
        Incident incident,
        IReadOnlyList<Finding> groundedFindings,
        string? investigationSummary = null)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(groundedFindings);

        var sb = new StringBuilder();

        Append(sb, ReadFragment(PlanningFragment));
        Append(sb, ComposeActionVocabulary());
        Append(sb, ComposeEnvironmentCard());
        Append(sb, ComposeIncidentCard(incident, incident.Signals));
        Append(sb, ComposeFindingsCard(groundedFindings, investigationSummary));

        return sb.ToString();
    }

    /// <summary>
    /// The runbook for a signal kind, falling back to <c>_Default.md</c>.
    /// </summary>
    /// <remarks>
    /// A missing runbook is a normal state, not an error: <see cref="SignalKind"/> has more
    /// members than there are files, and adding a member should not be able to break
    /// investigation of every other kind. The fallback is silent to the model and logged
    /// once for us.
    /// </remarks>
    public string ReadRunbook(SignalKind kind)
    {
        var specific = Path.Combine(_runbooksPath, $"{kind}.md");

        if (File.Exists(specific))
        {
            return File.ReadAllText(specific);
        }

        _logger?.LogDebug("No runbook for {Kind}; using {Fallback}", kind, DefaultRunbook);

        var fallback = Path.Combine(_runbooksPath, DefaultRunbook);

        return File.Exists(fallback)
            ? File.ReadAllText(fallback)
            : throw new FileNotFoundException(
                $"Neither Runbooks/{kind}.md nor Runbooks/{DefaultRunbook} exists under {_runbooksPath}. "
                + "The Runbooks/ content items are missing from the output directory.",
                fallback);
    }

    public string ReadFragment(string fileName)
    {
        var path = Path.Combine(_promptsPath, fileName);

        return File.Exists(path)
            ? File.ReadAllText(path)
            // Unlike a runbook, a missing fragment has no sane fallback: 10-tool-contract.md
            // is the prompt-injection briefing and 20-output-contract.md is how to cite
            // evidence. Silently investigating without either would produce a run whose
            // findings all fail grounding, at full cost, for no result.
            : throw new FileNotFoundException(
                $"Prompt fragment {fileName} is missing from {_promptsPath}. The Prompts/ content "
                + "items are not in the output directory.",
                path);
    }

    // ------------------------------------------------------------------
    // Generated cards
    // ------------------------------------------------------------------

    public string ComposeEnvironmentCard()
    {
        var sb = new StringBuilder();

        sb.Append("## This cluster\n\n");
        sb.Append("- Cluster label: `cluster=").Append(_environment.ClusterName).Append("`. ")
            .Append("Every metric and log line here carries it; a query without it may match another cluster.\n");

        sb.Append("- Namespaces in scope: ")
            .Append(Join(_environment.InScopeNamespaces))
            .Append('\n');

        if (_environment.ProtectedNamespaces.Count > 0)
        {
            sb.Append("- Permanently out of scope: ")
                .Append(Join(_environment.ProtectedNamespaces))
                .Append(". These are the agent itself and the stack it depends on to see anything. ")
                .Append("Read them if a diagnosis genuinely needs it; never propose acting on them.\n");
        }

        if (_environment.DatasourceUids.Count > 0)
        {
            sb.Append("- Datasource uids (pass these, not the names):\n");

            foreach (var (name, uid) in _environment.DatasourceUids.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append("  - ").Append(name).Append(": `").Append(uid).Append("`\n");
            }
        }

        if (_environment.WorkloadOwners.Count > 0)
        {
            sb.Append("- Workload owners (name the owner in a finding; it is what makes an escalation actionable):\n");

            foreach (var (workload, owner) in _environment.WorkloadOwners.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append("  - `").Append(workload).Append("` → ").Append(owner).Append('\n');
            }
        }

        sb.Append("\n### Known tool caveat\n\n");
        sb.Append(GrafanaMcpToolProvider.AlertRulesCaveat).Append('\n');

        foreach (var note in _environment.Notes)
        {
            sb.Append("\n").Append(note).Append('\n');
        }

        return sb.ToString();
    }

    public static string ComposeIncidentCard(Incident incident, IReadOnlyList<Signal> signals)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var sb = new StringBuilder();

        sb.Append("## The incident\n\n");
        sb.Append("- Title: ").Append(incident.Title).Append('\n');
        sb.Append("- Kind: ").Append(incident.Kind).Append('\n');
        sb.Append("- Severity: ").Append(incident.Severity).Append('\n');
        sb.Append("- Opened: ").Append(incident.OpenedAt.ToString("O")).Append('\n');
        sb.Append("- Last signal: ").Append(incident.LastSignalAt.ToString("O")).Append('\n');

        var target = incident.Target;
        sb.Append("- Target: `").Append(target.Namespace).Append('/').Append(target.Kind)
            .Append('/').Append(target.Name).Append('`');

        if (target.OwnerKind is { Length: > 0 } ownerKind && target.OwnerName is { Length: > 0 } ownerName)
        {
            // Stated explicitly because the runbooks all insist on reasoning about the
            // controller, and a model given only a pod name has nothing else to reason about.
            sb.Append(", owned by `").Append(ownerKind).Append('/').Append(ownerName).Append('`');
        }

        sb.Append('\n');

        if (target.NodeName is { Length: > 0 } node)
        {
            sb.Append("- Node: `").Append(node).Append("`\n");
        }

        sb.Append("- Workload key (use this for anything keyed on identity): `")
            .Append(target.WorkloadKey).Append("`\n");

        if (incident.QuarantinedUntil is { } quarantine)
        {
            sb.Append("- **Quarantined until ").Append(quarantine.ToString("O"))
                .Append("** for oscillating. Diagnose it; do not propose acting on it.\n");
        }

        if (signals.Count > 0)
        {
            sb.Append("\n### Signals, oldest first\n\n");

            foreach (var signal in signals.OrderBy(s => s.FirstSeen))
            {
                sb.Append("- `").Append(signal.FirstSeen.ToString("O")).Append("`");

                if (signal.Count > 1)
                {
                    sb.Append(" ×").Append(signal.Count)
                        .Append(" (last `").Append(signal.LastSeen.ToString("O")).Append("`)");
                }

                sb.Append(" [").Append(signal.Source).Append('/').Append(signal.Reason).Append("] ")
                    .Append(OneLine(signal.Message))
                    .Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string ComposeFindingsCard(IReadOnlyList<Finding> findings, string? summary)
    {
        var sb = new StringBuilder();

        sb.Append("## What the investigation established\n\n");

        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.Append(summary).Append("\n\n");
        }

        if (findings.Count == 0)
        {
            // Reached when every finding lost its evidence to the grounding check. Saying so
            // plainly is the point: the model must not reconstruct a cause from memory of a
            // conversation whose citations were just thrown away.
            sb.Append("**No finding survived the grounding check.** There is nothing evidenced to act on. ")
                .Append("Set `no_action_required: true` and say what a human should check.\n");

            return sb.ToString();
        }

        sb.Append("These findings are **grounded**: every excerpt below was verified to appear ")
            .Append("verbatim in a tool result from this investigation. Cite them by id.\n\n");

        foreach (var finding in findings.OrderByDescending(f => f.IsPrimary).ThenByDescending(f => f.Confidence))
        {
            sb.Append("### ").Append(finding.IsPrimary ? "PRIMARY — " : string.Empty)
                .Append(finding.Category)
                .Append(" (confidence ").Append(finding.Confidence.ToString("F2")).Append(")\n\n");

            sb.Append("- id: `").Append(finding.Id).Append("`\n");
            sb.Append("- hypothesis: ").Append(finding.Hypothesis).Append('\n');

            foreach (var evidence in finding.Evidence)
            {
                sb.Append("- evidence: `").Append(OneLine(evidence.Excerpt)).Append("`\n");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// What each action actually does, addressed to the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every action carries a description, and that is not decoration.</b> This vocabulary
    /// used to render bare enum names - ten lines of <c>- `RestartPod`</c> and nothing else -
    /// while the seventeen read tools in <c>KubernetesReadTools</c> each carry a paragraph
    /// written for the model, on that file's own stated reasoning: "a tool it does not
    /// understand is a tool it calls at the wrong moment and then reasons from". The standard
    /// was applied rigorously to reading and not at all to acting.
    /// </para>
    /// <para>
    /// The clearest cost was <see cref="ActionType.RestartPod"/>. Nothing anywhere told the
    /// model that it DELETES the pod - which is the fact that decides whether it fixes anything,
    /// because it is what makes pod-scoped state go away. A model that reads "restart" as
    /// "restart the container in place" is reasoning correctly from the wrong vocabulary. See
    /// <c>docs/backlog.md</c> #41.
    /// </para>
    /// <para>
    /// Availability is not stated here but derived from <see cref="ActionCapability"/>, which
    /// the executor reads too, so the prompt cannot claim this build does something it refuses.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<ActionType, string> ActionDescriptions = new()
    {
        [ActionType.RestartPod] =
            "Deletes one pod. There is no restart verb in the Kubernetes API - deleting a managed "
            + "pod IS the restart, and its controller immediately creates a replacement. The "
            + "replacement is a NEW pod, so everything scoped to the old one is discarded with it: "
            + "process memory, a held lock, an in-process cache, and the contents of any emptyDir "
            + "volume. Everything that outlives a pod is reproduced exactly: the image, the "
            + "command, ConfigMap and Secret values, and the contents of any PersistentVolumeClaim. "
            + "That distinction is the question to answer before proposing this - not whether the "
            + "workload is unhealthy, but whether the unhealthy thing lives in the pod.",

        [ActionType.RolloutRestart] =
            "Replaces every pod of a Deployment, StatefulSet or DaemonSet by stamping its pod "
            + "template, so the controller rolls them under its own update strategy rather than "
            + "leaving a gap. Same effect on pod-scoped state as RestartPod, applied to the whole "
            + "workload. Prefer RestartPod when one pod is bad and its siblings are healthy.",

        [ActionType.RollbackDeployment] =
            "Returns a Deployment to an earlier revision. The answer when get_rollout_history "
            + "shows a revision created shortly before the symptoms began, which is the case where "
            + "a rollback is a reasoned response rather than a guess.",

        [ActionType.ScaleWorkload] =
            "Sets a Deployment or StatefulSet to a specific replica count. For under-capacity, or "
            + "for taking a workload to zero to stop it. It does not repair a broken pod: N copies "
            + "of a container that crashes on startup crash N times.",

        [ActionType.DeleteStuckJob] =
            "Deletes a Job together with its pods. For a Job wedged past its deadline or backoff "
            + "limit whose pods will never complete. A CronJob creates the next one on schedule; a "
            + "bare Job does not come back, and its record of what happened goes with it.",

        [ActionType.DeleteFailedJobPods] =
            "Deletes only the Failed pods belonging to a Job and leaves the Job itself. Clears the "
            + "debris a repeatedly failing Job accumulates without discarding the Job's own record.",

        [ActionType.SilenceAlert] =
            "Silences an Alertmanager alert for a bounded window. It changes nothing in the "
            + "cluster and repairs nothing - it stops a page. Correct only when the cause is "
            + "understood and a human is already dealing with it; silencing something you have not "
            + "explained is how an incident becomes invisible.",

        [ActionType.PatchResources] =
            "Changes a container's resource requests or limits. The answer for a container "
            + "OOMKilled against a limit that is genuinely too low, or one the scheduler cannot "
            + "place because its request is too large.",

        [ActionType.CordonNode] =
            "Marks a node unschedulable. Pods already on it stay where they are; only new "
            + "placement is affected.",

        [ActionType.DrainNode] =
            "Evicts every pod from a node and marks it unschedulable. The most disruptive action "
            + "in this vocabulary: it moves work belonging to people who did not ask you.",

        [ActionType.DeletePvc] =
            "Deletes a PersistentVolumeClaim, and with it the data on the volume.",

        [ActionType.DeleteWorkload] =
            "Deletes a Deployment, StatefulSet or DaemonSet outright.",
    };

    /// <summary>
    /// The closed action vocabulary, rendered from the enum itself so the prompt cannot drift
    /// from the type the JSON schema is generated against.
    /// </summary>
    private static string ComposeActionVocabulary()
    {
        var sb = new StringBuilder();

        sb.Append("## The action vocabulary\n\n");
        sb.Append("These are the only action types that exist. Anything else produces a rejected plan.\n\n");

        foreach (var type in Enum.GetValues<ActionType>())
        {
            if (type == ActionType.None)
            {
                continue;
            }

            sb.Append("- `").Append(type).Append('`');

            if (ActionDescriptions.TryGetValue(type, out var description))
            {
                sb.Append(" — ").Append(description);
            }

            if (ActionCapability.IsPermanentlyDenied(type))
            {
                // Listed rather than hidden so a plan that names one is recorded and refused
                // with a reason, instead of failing to deserialise into an unknown value and
                // producing "no plan" with no explanation.
                sb.Append(" **Permanently denied.** Listed so that naming it is recorded and refused, "
                    + "not so that it can be proposed.");
            }
            else if (!ActionCapability.IsImplemented(type))
            {
                // Marked here, explained once below. Said out loud for the first time at all:
                // without it a model can propose one of these, policy can admit it, a human can
                // be paged to approve it, and the executor then refuses it with
                // outcome=unsupported - spending the scarcest thing in an incident on an action
                // that was never going to run.
                sb.Append(" **Not available in this build.**");
            }

            sb.Append("\n\n");
        }

        // Once, rather than repeated under every entry. Step budget is the binding constraint on
        // accuracy in this system, and four copies of the same paragraph is four copies of the
        // same tokens on every planning call.
        sb.Append("An action marked **Not available in this build** is recorded and then refused at "
            + "execution, after a human has spent attention approving it. If one of them is "
            + "genuinely the right answer, say so plainly in your reasoning and set "
            + "`no_action_required`: a person has to make that change either way, and telling them "
            + "which change is the useful thing you can do.\n");

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string section)
    {
        if (sb.Length > 0)
        {
            sb.Append("\n\n---\n\n");
        }

        sb.Append(section.TrimEnd());
    }

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "(none configured)" : string.Join(", ", values.Select(v => $"`{v}`"));

    private static string OneLine(string text)
    {
        var single = string.Join(' ', text.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return single.Length <= 500 ? single : string.Concat(single.AsSpan(0, 500), "…");
    }
}
