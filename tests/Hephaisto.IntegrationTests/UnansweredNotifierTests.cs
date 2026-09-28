using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// The escalation steps against a real outbox (#142): a step fires once, an acknowledgement stops
/// it, and a reopen starts the clock again.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UnansweredNotifierTests(PostgresFixture pg) : IDisposable
{
    private static readonly DateTimeOffset Opened = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    private readonly MutableClock clock = new(Opened);

    private ServiceProvider? services;

    public void Dispose() => services?.Dispose();

    private static NotificationOptions Options() => new()
    {
        BaseUrl = "http://hephaisto.example",
        Routes =
        [
            new NotificationRoute
            {
                Name = "payments",
                Channel = "teamsBot",
                Events = [NotificationEvent.IncidentOpened],
                Matchers = [new LabelMatcher { Label = "team", Values = ["payments"] }],
                Recipients = ["oncall@example.com"],
                Steps = [new NotificationStep { After = TimeSpan.FromMinutes(20), Recipients = ["lead@example.com"] }],
            },
        ],
    };

    [Fact]
    public async Task A_step_fires_once_after_its_time()
    {
        await pg.ResetAsync();
        await SeedAsync();
        var notifier = Notifier();

        clock.Set(Opened.AddMinutes(10));
        (await notifier.TickAsync(Ct)).Should().Be(0);

        clock.Set(Opened.AddMinutes(21));
        (await notifier.TickAsync(Ct)).Should().Be(1);
        (await notifier.TickAsync(Ct)).Should().Be(0, "a step fires once per outage");

        await using var db = pg.CreateContext();
        var step = await db.NotificationDeliveries.SingleAsync(Ct);
        step.Event.Should().Be(NotificationEvent.IncidentUnanswered);
        step.Recipients.Should().Equal("lead@example.com");
        step.Step.Should().Be(0);
    }

    [Fact]
    public async Task An_acknowledgement_stops_it()
    {
        await pg.ResetAsync();
        await SeedAsync(acknowledgedBy: "oncall");

        clock.Set(Opened.AddHours(1));
        (await Notifier().TickAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task A_reopen_starts_the_clock_again()
    {
        await pg.ResetAsync();
        var id = await SeedAsync();
        var notifier = Notifier();

        clock.Set(Opened.AddMinutes(21));
        (await notifier.TickAsync(Ct)).Should().Be(1);

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents.FirstAsync(i => i.Id == id, Ct);
            incident.ReopenedAt = Opened.AddMinutes(30);
            await db.SaveChangesAsync(Ct);
        }

        clock.Set(Opened.AddMinutes(45));
        (await notifier.TickAsync(Ct)).Should().Be(0, "fifteen minutes since the reopen");

        clock.Set(Opened.AddMinutes(51));
        (await notifier.TickAsync(Ct)).Should().Be(1, "the reopened outage gets its own steps");
    }

    // --- plumbing ---------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UnansweredNotifier Notifier()
    {
        services = new ServiceCollection()
            .AddDbContext<HephaistoDbContext>(o => o.UseNpgsql(pg.ConnectionString, x => x.UseVector()))
            .AddSingleton<IClock>(clock)
            .AddScoped<IAuditRepository, AuditRepository>()
            .BuildServiceProvider();

        return new UnansweredNotifier(
            services.GetRequiredService<IServiceScopeFactory>(),
            clock,
            new StaticMonitor<NotificationOptions>(Options()),
            NullLogger<UnansweredNotifier>.Instance);
    }

    private async Task<Guid> SeedAsync(string? acknowledgedBy = null)
    {
        var incident = new Incident
        {
            Title = "payments feed stopped",
            Kind = SignalKind.Pipeline,
            Severity = Severity.Critical,
            State = IncidentState.Escalated,
            CorrelationKey = "c:shop/Alert/Feed",
            Target = new TargetRef { Namespace = "shop", Kind = "Alert", Name = "Feed" },
            Labels = new() { ["team"] = "payments" },
            OpenedAt = Opened,
            LastSignalAt = Opened,
            AcknowledgedBy = acknowledgedBy,
            AcknowledgedAt = acknowledgedBy is null ? null : Opened.AddMinutes(1),
        };

        await using var db = pg.CreateContext();
        db.Incidents.Add(incident);
        await db.SaveChangesAsync(Ct);
        return incident.Id;
    }

    private sealed class MutableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Set(DateTimeOffset at) => UtcNow = at;
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
