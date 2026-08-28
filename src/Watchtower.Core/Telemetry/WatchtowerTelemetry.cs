namespace Watchtower.Core.Telemetry;

/// <summary>
/// Names only. The ActivitySource and Meter instances live in the composition root so that
/// Core stays free of side effects, but the names are shared so a dashboard, an alert rule
/// and the code that emits the metric cannot drift apart.
/// </summary>
public static class WatchtowerTelemetry
{
    public const string ActivitySourceName = "Watchtower";

    public const string MeterName = "Watchtower";

    /// <summary>Microsoft.Extensions.AI emits gen_ai.* spans under this name; subscribe to it too.</summary>
    public const string ExtensionsAiSourceName = "Microsoft.Extensions.AI";

    public static class Spans
    {
        public const string Incident = "watchtower.incident";
        public const string Investigation = "watchtower.investigation";
        public const string Plan = "watchtower.plan";
        public const string PolicyEvaluate = "watchtower.policy.evaluate";
        public const string ActionExecute = "watchtower.action.execute";
        public const string Verification = "watchtower.verification";
        public const string ToolPrefix = "watchtower.tool.";
    }

    public static class Metrics
    {
        public const string SignalsReceived = "watchtower.signals.received";
        public const string SignalsDropped = "watchtower.signals.dropped";
        public const string IncidentsOpened = "watchtower.incidents.opened";
        public const string IncidentsClosed = "watchtower.incidents.closed";
        public const string IncidentsOpen = "watchtower.incidents.open";

        /// <summary>Signal first seen to incident opened. The agent's MTTD.</summary>
        public const string DetectionLatency = "watchtower.detection.latency";

        /// <summary>Incident opened to resolved. The agent's MTTR.</summary>
        public const string IncidentDuration = "watchtower.incident.duration";

        public const string InvestigationDuration = "watchtower.investigation.duration";
        public const string InvestigationSteps = "watchtower.investigation.steps";
        public const string InvestigationTerminations = "watchtower.investigation.terminations";

        public const string ToolCalls = "watchtower.tool.calls";
        public const string ToolDuration = "watchtower.tool.duration";

        public const string LlmTokens = "watchtower.llm.tokens";
        public const string LlmCostUsd = "watchtower.llm.cost_usd";

        /// <summary>Gauge, 0..1+. Drives WatchtowerLlmBudgetWarning and ...Exhausted.</summary>
        public const string LlmBudgetUtilization = "watchtower.llm.budget_utilization";

        public const string PolicyDecisions = "watchtower.policy.decisions";
        public const string ActionsExecuted = "watchtower.actions.executed";
        public const string ActionsRolledBack = "watchtower.actions.rolled_back";
        public const string VerificationResult = "watchtower.verification.result";

        /// <summary>Evidence rejected for failing the substring check. A rising count means prompt drift.</summary>
        public const string GroundingRejected = "watchtower.grounding.rejected";

        public const string BudgetRemaining = "watchtower.budget.remaining";
        public const string Mode = "watchtower.mode";

        /// <summary>The only honest false-positive rate available. Built in from day one on purpose.</summary>
        public const string HumanFeedback = "watchtower.human.feedback";
    }
}
