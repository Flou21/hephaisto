using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;
using Hephaisto.Core.Safety;

namespace Hephaisto.Tests.TestData;

/// <summary>
/// Baselines that are deliberately *permissive*: every one of these fixtures, left untouched,
/// produces an Allow. A test therefore reads as the single thing it changes - the one fact that
/// flips the verdict - instead of forty lines of setup in which the significant line is hidden.
/// </summary>
internal static class Given
{
    /// <summary>Fixed so that every duration in a test is arithmetic, not a race.</summary>
    public static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    public static readonly Guid FindingId = new("11111111-1111-1111-1111-111111111111");

    public static readonly Guid IncidentId = new("22222222-2222-2222-2222-222222222222");

    public static readonly Guid ActionId = new("33333333-3333-3333-3333-333333333333");

    public sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    public static FixedClock Clock() => new(Now);

    public static TargetRef Target(
        string ns = "prod",
        string kind = "Pod",
        string name = "api-7d4c9f8b6-x2k9p",
        string? ownerKind = "Deployment",
        string? ownerName = "api") =>
        new()
        {
            Namespace = ns,
            Kind = kind,
            Name = name,
            OwnerKind = ownerKind,
            OwnerName = ownerName,
        };

    public static Signal Signal(
        SignalSource source = SignalSource.KubernetesWatch,
        SignalKind kind = SignalKind.CrashLoopBackOff,
        string reason = "BackOff",
        TargetRef? target = null) =>
        new()
        {
            Source = source,
            Kind = kind,
            Reason = reason,
            Target = target ?? Target(),
            FirstSeen = Now,
            LastSeen = Now,
        };

    public static Incident Incident(IncidentState state = IncidentState.Detected) =>
        new()
        {
            Id = IncidentId,
            State = state,
            CorrelationKey = "prod/Deployment/api",
            Title = "api is crash looping",
            Kind = SignalKind.CrashLoopBackOff,
            Severity = Severity.Critical,
            Target = Target(),
            Mode = AgentMode.Auto,
            OpenedAt = Now,
            LastSignalAt = Now,
        };

    /// <summary>
    /// Everything the low-risk path needs and nothing it does not: the namespace is allowed,
    /// RestartPod is auto-enabled, and the caps are left at their production defaults.
    /// </summary>
    public static PolicyOptions Options() =>
        new()
        {
            AllowedNamespaces = ["prod", "staging"],
            AutoEnabledActionTypes =
            [
                ActionType.RestartPod,
                ActionType.RolloutRestart,
                ActionType.RollbackDeployment,
                ActionType.DeleteStuckJob,
                ActionType.DeleteFailedJobPods,
                ActionType.SilenceAlert,
            ],
        };

    /// <summary>
    /// Three healthy replicas, settled, on a revision old enough that a rollback is a guess.
    /// Tests that want an obvious rollback shorten <c>CurrentRevisionAge</c> explicitly, which
    /// keeps the interesting number visible at the call site.
    /// </summary>
    public static WorkloadFacts Workload() =>
        new()
        {
            Key = "prod/Deployment/api",
            Kind = "Deployment",
            DesiredReplicas = 3,
            ReadyReplicas = 3,
            UpdatedReplicas = 3,
            Generation = 7,
            ObservedGeneration = 7,
            YoungestPodAge = TimeSpan.FromMinutes(30),
            CurrentRevisionAge = TimeSpan.FromHours(6),
            PreviousRevisionHealthyFor = TimeSpan.FromDays(3),
        };

    public static ClusterFacts Facts() =>
        new()
        {
            Now = Now,
            Mode = AgentMode.Auto,
            Workload = Workload(),
            NamespaceLabels = Labels(("hephaisto.dev/destructive-actions-allowed", "true")),
        };

    public static ActionRequest Request(ActionType type = ActionType.RestartPod) =>
        new()
        {
            ActionId = ActionId,
            IncidentId = IncidentId,
            Type = type,
            Target = Target(),
            Risk = RiskTier.Low,
            AffectedPodCount = 1,
            HasRollbackSpec = true,
            GroundedFindingIds = [FindingId],
        };

    public static ActionOutcome Restart(double hoursAgo, bool reopened = true) =>
        new(Now.AddHours(-hoursAgo), ActionType.RestartPod, reopened);

    public static Dictionary<string, string> Labels(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}
