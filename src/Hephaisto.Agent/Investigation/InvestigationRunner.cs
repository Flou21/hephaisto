using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Investigations;

/// <summary>
/// Everything one pass of the loop produced. Inert: the caller persists it and the policy
/// engine judges it. Nothing here has executed anything.
/// </summary>
public sealed record InvestigationOutcome
{
    public required Investigation Investigation { get; init; }

    /// <summary>Untruncated tool output, to be saved alongside the steps.</summary>
    public required IReadOnlyList<EvidenceBlob> Blobs { get; init; }

    /// <summary>The plan, after grounding. Null when none was produced or it was rejected.</summary>
    public ActionPlan? Plan { get; init; }

    /// <summary>What phase 2 emitted before mapping, kept for the audit trail.</summary>
    public ActionPlanDraft? Draft { get; init; }

    public IReadOnlyList<GroundingRejection> Rejections { get; init; } = [];

    /// <summary>
    /// Set when this incident must go to a human. Null means the caller decides from the
    /// plan and the policy engine.
    /// </summary>
    public EscalationReason? Escalation { get; init; }
}

/// <summary>
/// The three-phase loop: investigate with read-only tools, verify the citations, then plan
/// with no tools at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The LLM never holds a mutating tool handle.</b> Phase 1's tools are read-only and every
/// one of them passes through <see cref="SafeToolDecorator"/>. Phase 2 is a separate model
/// call whose client has no function-invocation link in its chain and whose output is
/// constrained to a JSON schema over a closed <see cref="ActionType"/> vocabulary. Phase 3 -
/// which is not in this file, and whose absence here is the point - is pure C# over that
/// typed result.
/// </para>
/// <para>
/// So the worst a prompt injection in a log line can do is produce a <i>plan</i>. It then
/// meets a deterministic, default-deny policy engine. It cannot reach the Kubernetes API,
/// because at no point in this file does anything holding a Kubernetes write handle exist.
/// </para>
/// <para>
/// The split also sidesteps Gemini's historical "tools XOR responseSchema" restriction, which
/// is a convenient second reason for a decision that was already correct on security grounds.
/// </para>
/// </remarks>
public sealed class InvestigationRunner(
    IChatClientFactory clients,
    PromptComposer prompts,
    IEnumerable<AIFunction> clusterTools,
    IGrafanaToolProvider grafana,
    IGlobalLlmBudget globalBudget,
    Pipeline.InvestigationTracker tracker,
    IClock clock,
    IOptionsMonitor<LlmOptions> llmOptions,
    IOptionsMonitor<InvestigationOptions> options,
    ILogger<InvestigationRunner> logger)
{
    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    /// <summary>The model this runner investigates with, for callers that report on a run.</summary>
    public string ModelId => clients.InvestigationModelId;

    public async Task<InvestigationOutcome> RunAsync(Incident incident, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var opts = options.CurrentValue;
        var llm = llmOptions.CurrentValue;

        var investigation = new Investigation
        {
            IncidentId = incident.Id,
            ModelId = clients.InvestigationModelId,
            StartedAt = clock.UtcNow,
        };

        var global = await globalBudget.CheckAsync(incident.Id, ct).ConfigureAwait(false);

        if (!global.Allowed)
        {
            // Nothing was spent, so no per-investigation budget was breached; Cancelled is
            // the honest reason. The escalation is what carries "we ran out of money", and
            // an incident that visibly escalated for budget is a legible outcome where
            // silence would not be.
            logger.LogWarning("Not investigating incident {IncidentId}: {Reason}", incident.Id, global.Reason);

            investigation.TerminationReason = TerminationReason.Cancelled;
            investigation.CompletedAt = clock.UtcNow;
            investigation.Error = global.Reason;

            return new InvestigationOutcome
            {
                Investigation = investigation,
                Blobs = [],
                Escalation = EscalationReason.BudgetExhausted,
            };
        }

        using var activity = LlmInstrumentation.Source.StartActivity(
            HephaistoTelemetry.Spans.Investigation,
            ActivityKind.Internal);

        activity?.SetTag("investigation.id", investigation.Id);
        activity?.SetTag("incident.id", incident.Id);
        activity?.SetTag("signal.kind", incident.Kind.ToString());
        activity?.SetTag("k8s.namespace", incident.Target.Namespace);
        activity?.SetTag("workload", incident.Target.WorkloadKey);
        activity?.SetTag("gen_ai.request.model", clients.InvestigationModelId);
        activity?.SetTag("budget.steps", llm.Investigation.MaxSteps);
        activity?.SetTag("budget.usd", llm.Investigation.MaxCostUsd);

        // The join key between the database and Tempo: an investigation row links straight to
        // its trace, and a trace links back to the row that explains it.
        investigation.TraceId = activity?.TraceId.ToString();

        var recorder = new InvestigationRecorder(
            investigation.Id,
            clock,
            opts.EvidenceBlobRetention,
            (rec, activity) => tracker.Report(
                incident.Id, rec.ToolCallCount, rec.TotalCostUsd, activity, rec.Steps));
        var budget = new InvestigationBudget(llm.Investigation, clock);
        var conclusion = new ConclusionHolder();

        var termination = TerminationReason.Faulted;
        string? error = null;

        try
        {
            termination = await InvestigateAsync(
                incident, opts, llm, recorder, budget, conclusion, ct).ConfigureAwait(false);
        }
        catch (BudgetExhaustedException ex)
        {
            logger.LogInformation("Investigation {Id} stopped: {Message}", investigation.Id, ex.Message);
            termination = ex.Reason;
        }
        catch (OperationCanceledException) when (clock.UtcNow >= budget.Deadline)
        {
            termination = TerminationReason.WallClockExhausted;
        }
        catch (OperationCanceledException)
        {
            termination = TerminationReason.Cancelled;
        }
        catch (Exception ex)
        {
            // Recorded, not retried blindly. A loop that failed once on a malformed tool
            // schema will fail again on the next incident, and a silent retry turns one
            // visible defect into a doubled bill.
            logger.LogError(ex, "Investigation {Id} faulted", investigation.Id);
            termination = TerminationReason.Faulted;
            error = ex.Message;
        }

        var steps = recorder.Steps;

        investigation.TerminationReason = termination;
        investigation.Steps = [.. steps];
        investigation.StepsUsed = budget.Steps;
        investigation.ToolCallsUsed = recorder.ToolCallCount;
        investigation.InputTokens = recorder.TotalInputTokens;
        investigation.OutputTokens = recorder.TotalOutputTokens;
        investigation.CostUsd = recorder.TotalCostUsd;
        investigation.Error = error;

        activity?.SetTag("investigation.termination", termination.ToString());
        activity?.SetTag("investigation.steps", investigation.StepsUsed);
        activity?.SetTag("investigation.tool_calls", investigation.ToolCallsUsed);
        activity?.SetTag("investigation.cost_usd", investigation.CostUsd);

        LlmInstrumentation.Terminations.Add(1, new TagList { { "reason", termination.ToString() } });
        LlmInstrumentation.InvestigationDuration.Record(
            (clock.UtcNow - investigation.StartedAt).TotalMilliseconds,
            new TagList { { "signal.kind", incident.Kind.ToString() } });

        // ---- grounding, between the phases ----

        var claimed = conclusion.Value is { } request
            ? ConcludeMapper.ToFindings(request, investigation.Id, steps)
            : [];

        var grounding = GroundingVerifier.Verify(investigation.Id, steps, claimed);
        RecordRejections(grounding.Rejections);

        investigation.Findings = [.. grounding.Findings];
        investigation.Confidence = grounding.Findings.FirstOrDefault(f => f.IsPrimary)?.Confidence;

        activity?.SetTag("investigation.findings", grounding.Findings.Count);
        activity?.SetTag("grounding.rejected", grounding.Rejections.Count);

        investigation.CompletedAt = clock.UtcNow;

        var escalation = Escalate(termination, claimed, grounding, investigation, opts);

        if (escalation is not null || termination != TerminationReason.Concluded)
        {
            return new InvestigationOutcome
            {
                Investigation = investigation,
                Blobs = recorder.Blobs,
                Rejections = grounding.Rejections,
                Escalation = escalation,
            };
        }

        // ---- phase 2 ----

        var (draft, plan, planRejections) = await PlanAsync(
            incident, investigation, recorder, grounding.Findings, conclusion.Value?.Summary, opts, ct)
            .ConfigureAwait(false);

        var allRejections = planRejections.Count == 0
            ? grounding.Rejections
            : [.. grounding.Rejections, .. planRejections];

        investigation.Plan = plan;
        investigation.CostUsd = recorder.TotalCostUsd;
        investigation.InputTokens = recorder.TotalInputTokens;
        investigation.OutputTokens = recorder.TotalOutputTokens;
        investigation.Steps = [.. recorder.Steps];
        investigation.CompletedAt = clock.UtcNow;

        return new InvestigationOutcome
        {
            Investigation = investigation,
            Blobs = recorder.Blobs,
            Plan = plan,
            Draft = draft,
            Rejections = allRejections,
            Escalation = plan is null && draft is not null ? EscalationReason.GroundingRejected : null,
        };
    }

    // ------------------------------------------------------------------
    // Phase 1
    // ------------------------------------------------------------------

    private async Task<TerminationReason> InvestigateAsync(
        Incident incident,
        InvestigationOptions opts,
        LlmOptions llm,
        InvestigationRecorder recorder,
        InvestigationBudget budget,
        ConclusionHolder conclusion,
        CancellationToken ct)
    {
        var tools = await BuildToolsAsync(llm, budget, recorder, conclusion, ct).ConfigureAwait(false);

        using var chat = clients.CreateInvestigationClient(budget, recorder, incident.Id);

        var chatOptions = new ChatOptions
        {
            Tools = [.. tools],
            ToolMode = ChatToolMode.Auto,
            Temperature = (float)llm.Temperature,
            MaxOutputTokens = llm.MaxOutputTokens,
            AllowMultipleToolCalls = true,
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, prompts.ComposeInvestigationPrompt(incident)),
            new(ChatRole.User, opts.OpeningMessage),
        };

        // The wall-clock budget is enforced in two places: here, so a single very slow
        // provider call cannot outlive the deadline, and in EnsureCanStartStep, so a run
        // whose deadline passed between turns stops with the right reason rather than
        // starting one more.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(llm.Investigation.MaxWallClock);

        var idleTurns = 0;

        try
        {
            for (var turn = 0; turn < opts.MaxOuterTurns; turn++)
            {
                var toolCallsBefore = recorder.ToolCallCount;

                var response = await chat.GetResponseAsync(messages, chatOptions, deadline.Token)
                    .ConfigureAwait(false);

                messages.AddMessages(response);

                if (conclusion.Value is not null)
                {
                    return TerminationReason.Concluded;
                }

                // Counted from the recorder rather than by scanning the response, because
                // FunctionInvokingChatClient has already executed and folded away the tool calls
                // by the time we see it, and what it chooses to leave in Messages is an
                // implementation detail. The recorder saw every invocation.
                if (recorder.ToolCallCount > toolCallsBefore)
                {
                    idleTurns = 0;
                    continue;
                }

                if (++idleTurns >= llm.Investigation.MaxConsecutiveNoToolTurns)
                {
                    // Two turns of narration with no query and no conclusion. Nudging again
                    // costs a full turn's tokens to re-read the whole transcript and has already
                    // failed once.
                    logger.LogInformation(
                        "Investigation stalled after {Turns} turns with no tool call", idleTurns);

                    return TerminationReason.Stalled;
                }

                messages.Add(new ChatMessage(ChatRole.User, opts.StallNudge));
            }

            return TerminationReason.Stalled;
        }
        catch (BudgetExhaustedException ex)
            when (conclusion.Value is null && budget.TryGrantConcludingStep())
        {
            // The last word. Running out of budget is not a reason to throw away what was
            // already learned - and until this existed, it was exactly that. Every run that
            // survived the provider spent all twelve steps asking and had none left to
            // answer with, so it returned nothing.
            //
            // Tools are stripped to `conclude` alone rather than merely asking nicely for a
            // conclusion. On the reserved step the model gets one chance, and a model that
            // answers a "please conclude" by calling one more diagnostic tool would spend it
            // and leave the run exactly where it was.
            logger.LogInformation(
                "Investigation budget reached ({Reason}); spending the reserved step on a "
                + "conclusion rather than discarding the run.",
                ex.Reason);

            messages.Add(new ChatMessage(ChatRole.User, opts.FinalConclusionNudge));

            var concludeOnly = new ChatOptions
            {
                Tools = [.. tools.Where(t => t.Name == "conclude")],
                ToolMode = ChatToolMode.Auto,
                Temperature = (float)llm.Temperature,
                MaxOutputTokens = llm.MaxOutputTokens,
            };

            try
            {
                await chat.GetResponseAsync(messages, concludeOnly, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception finalEx)
            {
                // Never let the rescue attempt replace the real termination reason. The run
                // ended because its budget ran out; that this last call also failed is a
                // detail, and reporting it as the cause would hide the actual constraint.
                logger.LogWarning(
                    finalEx,
                    "The reserved concluding step failed. Reporting the original budget "
                    + "termination.");
            }

            return conclusion.Value is not null ? TerminationReason.Concluded : ex.Reason;
        }
    }

    private async Task<List<AIFunction>> BuildToolsAsync(
        LlmOptions llm,
        InvestigationBudget budget,
        InvestigationRecorder recorder,
        ConclusionHolder conclusion,
        CancellationToken ct)
    {
        var tools = new List<AIFunction>();

        // Kubernetes tools arrive from DI as plain AIFunctions. This layer deliberately knows
        // nothing about how they are implemented - it depends on the abstraction so that the
        // stream that owns them can change them freely.
        tools.AddRange(SafeToolDecorator.WrapAll(
            clusterTools, "kubernetes", llm.Tools, budget, recorder));

        var grafanaTools = await grafana.GetToolsAsync(ct).ConfigureAwait(false);

        if (grafanaTools.Count > 0)
        {
            tools.AddRange(SafeToolDecorator.WrapAll(
                grafanaTools, "grafana-mcp", llm.Tools, budget, recorder));
        }

        // Wrapped like everything else - the span, the argument redaction and the step record
        // are wanted here too - but deliberately with no budget. A run whose tool budget is
        // exhausted is told to conclude with what it has, and a `conclude` that the same
        // budget then refuses would leave it no way to say anything at all.
        tools.Add(new SafeToolDecorator(
            CreateConcludeTool(conclusion), "internal", llm.Tools, budget: null, recorder));

        return tools;
    }

    /// <summary>
    /// The virtual <c>conclude</c> tool. Calling it reaches nothing: it writes into this
    /// run's own state and returns an acknowledgement.
    /// </summary>
    private static AIFunction CreateConcludeTool(ConclusionHolder holder) =>
        AIFunctionFactory.Create(
            (ConcludeRequest request) =>
            {
                holder.Value = request;

                return "Conclusion recorded. Your citations are now checked against what the tools "
                    + "actually returned; any that do not match are discarded. Stop here.";
            },
            "conclude",
            "Ends the investigation and records your findings. Call this when you have enough to "
            + "state a cause, or enough to be sure you cannot. Do not simply stop talking.");

    // ------------------------------------------------------------------
    // Phase 2
    // ------------------------------------------------------------------

    private async Task<(ActionPlanDraft? Draft, ActionPlan? Plan, IReadOnlyList<GroundingRejection> Rejections)>
        PlanAsync(
            Incident incident,
            Investigation investigation,
            InvestigationRecorder recorder,
            IReadOnlyList<Finding> findings,
            string? summary,
            InvestigationOptions opts,
            CancellationToken ct)
    {
        using var activity = LlmInstrumentation.Source.StartActivity(
            HephaistoTelemetry.Spans.Plan,
            ActivityKind.Internal);

        activity?.SetTag("investigation.id", investigation.Id);
        activity?.SetTag("gen_ai.request.model", clients.PlanningModelId);

        var planBudget = new InvestigationBudget(
            new InvestigationBudgetOptions
            {
                MaxSteps = 2,
                MaxToolCalls = 0,
                MaxWallClock = opts.PlanningTimeout,
                MaxInputTokens = opts.PlanningMaxInputTokens,
                MaxCostUsd = opts.PlanningCostUsd,
            },
            clock);

        try
        {
            using var chat = clients.CreatePlanningClient(planBudget, recorder, incident.Id);

            var chatOptions = new ChatOptions
            {
                // No Tools, and the client has no function-invocation link anyway. Two
                // independent reasons phase 2 cannot call anything, because one of them is a
                // property of a config object that somebody could edit by accident.
                ResponseFormat = ChatResponseFormat.ForJsonSchema<ActionPlanDraft>(PlanJson),
                Temperature = 0f,
            };

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, prompts.ComposePlanningPrompt(incident, findings, summary)),
                new(ChatRole.User, "Decide whether anything should be done. Respond only with the JSON structure."),
            };

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(opts.PlanningTimeout);

            var response = await chat.GetResponseAsync(messages, chatOptions, deadline.Token)
                .ConfigureAwait(false);

            var draft = Deserialise(response.Text);

            if (draft is null)
            {
                logger.LogWarning("Planning produced no parseable plan for investigation {Id}", investigation.Id);
                activity?.SetStatus(ActivityStatusCode.Error, "unparseable plan");

                return (null, null, []);
            }

            var verdict = GroundingVerifier.VerifyPlan(draft, findings);
            RecordRejections(verdict.Rejections);

            if (!verdict.Accepted)
            {
                // Whole-plan rejection, not per-action. An action justified by a finding that
                // turned out to be invented says this investigation's reasoning is not
                // trustworthy, and the right response is a human, not a tidied-up plan.
                logger.LogWarning(
                    "Plan for investigation {Id} rejected: {Reasons}",
                    investigation.Id,
                    string.Join("; ", verdict.Rejections.Select(r => r.Detail)));

                activity?.SetStatus(ActivityStatusCode.Error, "grounding rejected");

                return (draft, null, verdict.Rejections);
            }

            var plan = ActionPlanDraftMapper.TryToDomain(draft, investigation.Id, incident.Id, clock.UtcNow);

            // TryToDomain drops actions the model gave no usable target. Say so: an action
            // that silently vanishes between the model proposing it and a human reading the
            // plan is indistinguishable from one that was never proposed.
            var dropped = draft.Actions.Count - plan.Actions.Count;
            if (dropped > 0)
            {
                logger.LogWarning(
                    "Dropped {Dropped} of {Total} proposed actions for investigation {Id}: "
                    + "the model gave no namespace, kind or name.",
                    dropped, draft.Actions.Count, investigation.Id);
            }

            activity?.SetTag("plan.actions_dropped", dropped);
            activity?.SetTag("plan.action_count", plan.Actions.Count);
            activity?.SetTag(
                "plan.max_risk",
                plan.Actions.Count == 0 ? "none" : plan.Actions.Max(a => a.Risk).ToString());
            activity?.SetTag("plan.no_action_required", plan.NoActionRequired);

            return (draft, plan, verdict.Rejections);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
        {
            // A failed plan is not a failed investigation. The findings are grounded and
            // worth showing to a human regardless of whether a plan came out of them.
            logger.LogError(ex, "Planning failed for investigation {Id}", investigation.Id);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            return (null, null, []);
        }
    }

    /// <summary>
    /// Gemini honours the response schema, but a fenced code block still turns up
    /// occasionally - usually when the model also decided to explain itself. Stripping it is
    /// two lines; not stripping it throws away an entire investigation's work.
    /// </summary>
    public static ActionPlanDraft? Deserialise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var json = text.Trim();

        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('\n');
            var end = json.LastIndexOf("```", StringComparison.Ordinal);

            if (start > 0 && end > start)
            {
                json = json[(start + 1)..end].Trim();
            }
        }

        try
        {
            return JsonSerializer.Deserialize<ActionPlanDraft>(json, PlanJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Maps how the loop ended onto whether a human is needed.
    /// </summary>
    /// <remarks>
    /// <b>A budget hit escalates; it never resolves.</b> Running out of steps, time or money
    /// is not a green light - it is the agent being interrupted mid-thought, and whatever it
    /// had reached at that moment was not what it considered its answer. Treating an
    /// exhausted budget as a conclusion is how an agent ends up acting on half an
    /// investigation.
    /// </remarks>
    private static EscalationReason? Escalate(
        TerminationReason termination,
        IReadOnlyList<Finding> claimed,
        GroundingResult grounding,
        Investigation investigation,
        InvestigationOptions opts) => termination switch
        {
            TerminationReason.StepBudgetExhausted
                or TerminationReason.ToolCallBudgetExhausted
                or TerminationReason.WallClockExhausted
                or TerminationReason.TokenBudgetExhausted
                or TerminationReason.CostBudgetExhausted => EscalationReason.BudgetExhausted,

            TerminationReason.Faulted => EscalationReason.InvestigationFailed,

            TerminationReason.Stalled or TerminationReason.Cancelled => EscalationReason.NoPlanProduced,

            // Concluded, but every citation failed: the model reported a cause it cannot
            // show. That is the case the whole grounding mechanism exists for, and it is
            // worth a human's attention rather than a quiet "no findings".
            _ when claimed.Count > 0 && !grounding.HasGroundedFindings => EscalationReason.GroundingRejected,

            _ when grounding.Findings.Count == 0 => EscalationReason.NoPlanProduced,

            _ when investigation.Confidence < opts.MinConfidenceForPlan => EscalationReason.LowConfidence,

            _ => null,
        };

    private static void RecordRejections(IReadOnlyList<GroundingRejection> rejections)
    {
        foreach (var rejection in rejections)
        {
            LlmInstrumentation.GroundingRejected.Add(
                1,
                new TagList { { "reason", rejection.Reason.ToString() } });
        }
    }

    /// <summary>
    /// A class rather than a captured local so the write from inside the tool invocation is
    /// visible to the loop: <c>FunctionInvokingChatClient</c> may invoke tools on a different
    /// thread, and it may invoke several concurrently.
    /// </summary>
    private sealed class ConclusionHolder
    {
        private ConcludeRequest? _value;

        public ConcludeRequest? Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }
    }
}
