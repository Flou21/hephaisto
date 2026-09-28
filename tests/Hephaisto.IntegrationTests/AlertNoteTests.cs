using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Agent.Safety;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// Alert notes against a real database (#145).
/// </summary>
/// <remarks>
/// The caps and framing are pinned in memory. What needs Postgres is that a note is found again
/// under the name it was saved with, that entries come back newest first and are counted past the
/// cap, and that the audit trail records both kinds of write.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AlertNoteTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_name_nobody_wrote_about_is_an_empty_note_not_a_missing_one()
    {
        await pg.ResetAsync();
        await using var provider = Services(Now);

        var note = await Queries(provider, Now).GetAlertNoteAsync("ConsumerLagHigh", Ct);

        note.Should().NotBeNull();
        note!.Exists.Should().BeFalse();
        note.Body.Should().BeEmpty();
        note.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_saved_body_is_read_back_and_audited()
    {
        await pg.ResetAsync();
        await using var provider = Services(Now);
        var queries = Queries(provider, Now);

        var saved = await queries.SaveAlertNoteBodyAsync(" ConsumerLagHigh ", "Check the upstream feed first.", "operator-a", Ct);

        saved.Outcome.Should().Be(AlertNoteOutcome.Applied);

        var note = await queries.GetAlertNoteAsync("ConsumerLagHigh", Ct);

        note!.Exists.Should().BeTrue();
        note.Body.Should().Be("Check the upstream feed first.");
        note.UpdatedBy.Should().Be("operator-a");
        note.UpdatedAt.Should().BeCloseTo(Now, TimeSpan.FromSeconds(1));

        // A second save replaces the body and keeps what it said before in the trail.
        await queries.SaveAlertNoteBodyAsync("ConsumerLagHigh", "Check the feed, then the consumer.", "operator-b", Ct);

        await using var db = pg.CreateContext();

        var audits = await db.AuditEvents.AsNoTracking()
            .Where(a => a.Type == "alert-note.updated")
            .OrderBy(a => a.Id)
            .ToListAsync(Ct);

        audits.Should().HaveCount(2);
        audits[1].Actor.Should().Be("operator-b");
        audits[1].Detail.Should().Contain("Check the upstream feed first.");
    }

    [Fact]
    public async Task An_entry_creates_the_note_it_hangs_from_and_comes_back_newest_first()
    {
        await pg.ResetAsync();
        await using var provider = Services(Now);

        var incident = Guid.CreateVersion7();

        await Queries(provider, Now).AddAlertNoteEntryAsync("ConsumerLagHigh", "restarted the consumer", incident, "operator-a", Ct);
        await Queries(provider, Now.AddHours(1)).AddAlertNoteEntryAsync("ConsumerLagHigh", "the feed was late", null, "operator-b", Ct);

        var note = await Queries(provider, Now).GetAlertNoteAsync("ConsumerLagHigh", Ct);

        note!.Exists.Should().BeTrue();
        note.Body.Should().BeEmpty();
        note.EntryCount.Should().Be(2);
        note.Entries.Select(e => e.Text).Should().Equal("the feed was late", "restarted the consumer");
        note.Entries[1].IncidentId.Should().Be(incident);

        await using var db = pg.CreateContext();

        (await db.AuditEvents.CountAsync(a => a.Type == "alert-note.entry-added" && a.IncidentId == incident, Ct))
            .Should().Be(1);
    }

    [Fact]
    public async Task Entries_past_the_cap_are_counted_not_listed()
    {
        await pg.ResetAsync();
        await using var provider = Services(Now);

        for (var i = 0; i < IncidentQueries.MaxAlertNoteEntriesShown + 3; i++)
        {
            await Queries(provider, Now.AddMinutes(i)).AddAlertNoteEntryAsync("Busy", $"entry {i}", null, "operator-a", Ct);
        }

        var note = await Queries(provider, Now).GetAlertNoteAsync("Busy", Ct);

        note!.EntryCount.Should().Be(IncidentQueries.MaxAlertNoteEntriesShown + 3);
        note.Entries.Should().HaveCount(IncidentQueries.MaxAlertNoteEntriesShown);
        note.Entries[0].Text.Should().Be($"entry {IncidentQueries.MaxAlertNoteEntriesShown + 2}");
    }

    [Fact]
    public async Task The_agent_cannot_write_as_a_person_and_nothing_is_saved()
    {
        await pg.ResetAsync();
        await using var provider = Services(Now);

        var result = await Queries(provider, Now).AddAlertNoteEntryAsync("ConsumerLagHigh", "all fine", null, "hephaisto/auto", Ct);

        result.Outcome.Should().Be(AlertNoteOutcome.ForbiddenActor);

        await using var db = pg.CreateContext();

        (await db.AlertNotes.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task A_body_over_the_cap_is_refused_and_nothing_is_saved()
    {
        await pg.ResetAsync();
        await using var provider = Services(Now);

        var result = await Queries(provider, Now)
            .SaveAlertNoteBodyAsync("ConsumerLagHigh", new string('x', AlertNote.MaxBodyLength + 1), "operator-a", Ct);

        result.Outcome.Should().Be(AlertNoteOutcome.Invalid);

        await using var db = pg.CreateContext();

        (await db.AlertNotes.CountAsync(Ct)).Should().Be(0);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider Services(DateTimeOffset now)
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => pg.CreateContext());
        services.AddScoped<IAuditRepository>(sp => new AuditRepository(sp.GetRequiredService<HephaistoDbContext>(), new FixedClock(now)));

        return services.BuildServiceProvider();
    }

    private static IncidentQueries Queries(ServiceProvider provider, DateTimeOffset now)
    {
        var clock = new FixedClock(now);

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

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>The notes never consult the kill switch; this exists only to satisfy the constructor.</summary>
    private sealed class NoKillSwitch : IKillSwitch
    {
        public IReadOnlyList<ModeArm> ExternalArms => [];

        public ModeResolution External => throw new NotSupportedException();

        public Task<ModeResolution> ResolveAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
