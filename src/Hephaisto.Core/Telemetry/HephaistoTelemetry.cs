namespace Hephaisto.Core.Telemetry;

/// <summary>
/// Names only. The ActivitySource and Meter instances live in the composition root so that
/// Core stays free of side effects, but the names are shared so a dashboard, an alert rule
/// and the code that emits the metric cannot drift apart.
/// </summary>
public static class HephaistoTelemetry
{
    public const string ActivitySourceName = "Hephaisto";

    public const string MeterName = "Hephaisto";

    /// <summary>Microsoft.Extensions.AI emits gen_ai.* spans under this name; subscribe to it too.</summary>
    public const string ExtensionsAiSourceName = "Microsoft.Extensions.AI";

    public static class Spans
    {
        public const string Incident = "hephaisto.incident";
        public const string Investigation = "hephaisto.investigation";
        public const string Plan = "hephaisto.plan";
        public const string PolicyEvaluate = "hephaisto.policy.evaluate";
        public const string ActionExecute = "hephaisto.action.execute";
        public const string Verification = "hephaisto.verification";
        public const string ToolPrefix = "hephaisto.tool.";

        /// <summary>One attempt to hand one message to one channel.</summary>
        public const string NotificationDeliver = "hephaisto.notification.deliver";
    }

    public static class Metrics
    {
        public const string SignalsReceived = "hephaisto.signals.received";
        public const string SignalsDropped = "hephaisto.signals.dropped";
        public const string IncidentsOpened = "hephaisto.incidents.opened";
        public const string IncidentsClosed = "hephaisto.incidents.closed";
        public const string IncidentsOpen = "hephaisto.incidents.open";

        /// <summary>Signal first seen to incident opened. The agent's MTTD.</summary>
        public const string DetectionLatency = "hephaisto.detection.latency";

        /// <summary>Incident opened to resolved. The agent's MTTR.</summary>
        public const string IncidentDuration = "hephaisto.incident.duration";

        public const string InvestigationDuration = "hephaisto.investigation.duration";
        public const string InvestigationSteps = "hephaisto.investigation.steps";
        public const string InvestigationTerminations = "hephaisto.investigation.terminations";

        public const string ToolCalls = "hephaisto.tool.calls";
        public const string ToolDuration = "hephaisto.tool.duration";

        public const string LlmTokens = "hephaisto.llm.tokens";
        public const string LlmCostUsd = "hephaisto.llm.cost_usd";

        /// <summary>Gauge, 0..1+. Drives HephaistoLlmBudgetWarning and ...Exhausted.</summary>
        public const string LlmBudgetUtilization = "hephaisto.llm.budget_utilization";

        public const string PolicyDecisions = "hephaisto.policy.decisions";
        public const string ActionsExecuted = "hephaisto.actions.executed";
        public const string ActionsRolledBack = "hephaisto.actions.rolled_back";
        public const string VerificationResult = "hephaisto.verification.result";

        /// <summary>Evidence rejected for failing the substring check. A rising count means prompt drift.</summary>
        public const string GroundingRejected = "hephaisto.grounding.rejected";

        public const string BudgetRemaining = "hephaisto.budget.remaining";
        public const string Mode = "hephaisto.mode";

        /// <summary>The only honest false-positive rate available. Built in from day one on purpose.</summary>
        public const string HumanFeedback = "hephaisto.human.feedback";

        /// <summary>
        /// Constant 1, carrying `version` and `commit` labels. Scraped as
        /// <c>hephaisto_build_info</c>.
        /// </summary>
        /// <remarks>
        /// The value is meaningless; the labels are the point. Joined against any other
        /// series it turns "this started failing at 14:20" into "this started failing when
        /// 0.0.2-main.0.44 rolled out", which is the first question asked in an incident and
        /// the one a dashboard otherwise cannot answer.
        /// </remarks>
        public const string BuildInfo = "hephaisto.build.info";

        /// <summary>
        /// Kubernetes watch reconnects. A watch that reconnects constantly is an agent that is
        /// intermittently blind, and nothing else reports that.
        /// </summary>
        /// <remarks>
        /// Emitted from a raw string literal in the Kubernetes layer until 2026-08-30
        /// (backlog #17), which is exactly the drift this file exists to prevent - the names
        /// are shared so a dashboard, an alert rule and the code that emits the metric cannot
        /// disagree, and one emitted from a literal is invisible to the other two by
        /// construction.
        /// </remarks>
        public const string KubernetesWatchReconnects = "hephaisto.kubernetes.watch_reconnects";

        /// <summary>Outbox rows written. Labelled by event and channel, both closed sets.</summary>
        public const string NotificationsEnqueued = "hephaisto.notifications.enqueued";

        /// <summary>
        /// How each outbox row ended: <c>delivered</c>, <c>failed</c> or <c>suppressed</c>.
        /// </summary>
        /// <remarks>
        /// <b>The <c>failed</c> series is the one that matters.</b> It counts the times nobody
        /// was told, which is the worst outcome this system produces and the only one an
        /// operator cannot discover by looking at the console - because the whole point of a
        /// notification is that they were not looking.
        /// </remarks>
        public const string NotificationsDelivered = "hephaisto.notifications.delivered";

        /// <summary>Enqueue to delivery, in seconds. How late the news was, not how slow the HTTP was.</summary>
        public const string NotificationLatency = "hephaisto.notification.latency";

        /// <summary>
        /// Rows still pending. A number that climbs and does not come down means an endpoint is
        /// down and the backlog is people who have not been told yet.
        /// </summary>
        public const string NotificationsPending = "hephaisto.notifications.pending";
    }
}
