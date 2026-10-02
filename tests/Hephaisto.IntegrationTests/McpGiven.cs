using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Mcp;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Agent.Safety;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Hephaisto.Core.Safety;

namespace Hephaisto.IntegrationTests;

/// <summary>What the MCP tests start from: incidents as ingest would leave them, and a reader over them.</summary>
internal static class McpGiven
{
    public static async Task<Guid> IncidentAsync(
        PostgresFixture pg,
        string alertName,
        Severity severity,
        IncidentState state,
        string ns,
        string workload,
        DateTimeOffset openedAt,
        string? assignedTo = null,
        string? acknowledgedBy = null,
        string? title = null,
        Dictionary<string, string>? annotations = null,
        Guid? id = null)
    {
        await using var db = pg.CreateContext();

        var incident = new Incident
        {
            Id = id ?? Guid.CreateVersion7(),
            Title = title ?? $"{alertName} on {workload}",
            CorrelationKey = $"{ns}/{workload}/{alertName}/{Guid.NewGuid():N}",
            Kind = SignalKind.Unknown,
            Severity = severity,
            State = state,
            Target = new TargetRef { Cluster = "dev", Namespace = ns, Kind = "Deployment", Name = workload },
            OpenedAt = openedAt,
            LastSignalAt = openedAt,
            AlertName = alertName,
            AssignedTo = assignedTo,
            AcknowledgedBy = acknowledgedBy,
            ClosedAt = state == IncidentState.Closed ? openedAt.AddMinutes(5) : null,
            ClosedBy = state == IncidentState.Closed ? "operator-a" : null,
            ResolvedAt = state == IncidentState.Resolved ? openedAt.AddMinutes(5) : null,
        };

        incident.Signals.Add(new Signal
        {
            Fingerprint = Guid.NewGuid().ToString("N"),
            Source = SignalSource.Alertmanager,
            Kind = SignalKind.Unknown,
            Target = new TargetRef { Cluster = "dev", Namespace = ns, Kind = "Deployment", Name = workload },
            Severity = severity,
            Reason = alertName,
            Message = "pager suite",
            FirstSeen = openedAt,
            LastSeen = openedAt,
            Status = SignalStatus.Firing,
            Labels = new() { ["alertname"] = alertName, ["namespace"] = ns },
            Annotations = annotations ?? new() { ["description"] = $"{alertName} fired" },
        });

        db.Incidents.Add(incident);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return incident.Id;
    }

    /// <summary>The console's read model over the test database, with nothing running.</summary>
    public static IncidentQueries Queries(PostgresFixture pg, DateTimeOffset now)
    {
        var clock = new FixedClock(now);
        var services = new ServiceCollection();
        services.AddScoped(_ => pg.CreateContext());
        var provider = services.BuildServiceProvider();

        return new IncidentQueries(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new NoKillSwitch(),
            new IncidentNotifier(NullLogger<IncidentNotifier>.Instance),
            new WatchdogMonitor(clock),
            new InvestigationTracker(clock),
            new InvestigationQueue(),
            new StaticOptionsMonitor<LlmBudgetOptions>(new LlmBudgetOptions()),
            new ConnectionHealthCache([], clock, NullLogger<ConnectionHealthCache>.Instance),
            clock,
            NullLogger<IncidentQueries>.Instance);
    }

    public static McpIncidentReader Reader(PostgresFixture pg, DateTimeOffset now, CodeFixMode codeFixMode = CodeFixMode.Off)
    {
        var clock = new FixedClock(now);
        var queries = Queries(pg, now);

        var db = pg.CreateContext();

        var codeFixes = new CodeFixQueries(
            db,
            new FixedSwitch(CodeFixModeResolver.Resolve(
                [CodeFixModeResolver.Parse("env:CodeFix__Mode", codeFixMode.ToString())],
                ModeResolver.Resolve([ModeResolver.Parse("env:HEPHAISTO_MODE", "Recommend")]),
                "configmap:killSwitch",
                "db:agent_mode")),
            new StaticOptionsMonitor<CodeFixOptions>(new CodeFixOptions()),
            new StaticOptionsMonitor<AuthOptions>(new AuthOptions()));

        return new McpIncidentReader(
            db,
            queries,
            codeFixes,
            new ConnectionHealthCache([], clock, NullLogger<ConnectionHealthCache>.Instance),
            new InvestigationTracker(clock),
            new StaticOptionsMonitor<NotificationOptions>(new NotificationOptions { BaseUrl = "https://console.example" }));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FixedSwitch(CodeFixModeResolution resolution) : ICodeFixSwitch
    {
        public Task<CodeFixModeResolution> ResolveAsync(CancellationToken ct) => Task.FromResult(resolution);
    }

    private sealed class NoKillSwitch : IKillSwitch
    {
        public IReadOnlyList<ModeArm> ExternalArms => [];

        public ModeResolution External => throw new NotSupportedException();

        public Task<ModeResolution> ResolveAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
