using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

using Hephaisto.Agent;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Mcp;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Agent.Safety;
using Hephaisto.Agent.Web;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// What an agent may change, against a real database (#157, F4): each write lands where the
/// console's would, under the token's actor, with its origin in the audit row - and a shared
/// token's "for whom" stays a claim.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class McpWriteTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static readonly McpCaller Shared = new("mcp/litellm", "litellm", McpTokenOptions.Shared, McpTokenOptions.Reader, MayWrite: true);
    private static readonly McpCaller ReadOnly = new("mcp/dashboards", "dashboards", McpTokenOptions.Shared, McpTokenOptions.Reader, MayWrite: false);
    private static readonly McpCaller Flo = new("flo", "flo", McpTokenOptions.Person, McpTokenOptions.Reader, MayWrite: true);
    private static readonly McpCaller Lead = new("lead", "lead", McpTokenOptions.Person, McpTokenOptions.Approver, MayWrite: true);

    [Fact]
    public async Task A_shared_token_acknowledges_as_itself_with_the_person_as_a_claim()
    {
        await pg.ResetAsync();
        var id = await McpGiven.IncidentAsync(pg, "A", Severity.Warning, IncidentState.Escalated, "a", "one", Now);
        await using var provider = Services();

        var result = await Actions(provider).AcknowledgeAsync(Shared, id, "flo", Ct);

        result.RecordedAs.Value.Should().Be("mcp/litellm");
        result.ClaimedBy!.Value.Should().Be("flo");
        result.ClaimVerified.Should().BeFalse();

        await using var db = pg.CreateContext();
        var incident = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == id, Ct);
        incident.AcknowledgedBy.Should().Be("mcp/litellm", "the token is who answers for it");
        incident.AcknowledgedClaimedBy.Should().Be("flo");

        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.IncidentId == id && a.Type == "incident.acknowledged", Ct);
        audit.Actor.Should().Be("mcp/litellm");
        McpIncidentReader.Origin(audit.Detail)!.ClaimedBy!.Value.Should().Be("flo");
        McpIncidentReader.Origin(audit.Detail)!.Source.Should().Be("mcp");
        McpIncidentReader.Origin(audit.Detail)!.ClaimVerified.Should().BeFalse();
        audit.Detail.Should().Contain("previousHolder", "the origin is added to the detail, not in place of it");
    }

    [Fact]
    public async Task A_shared_token_must_name_somebody_to_stop_the_paging()
    {
        await pg.ResetAsync();
        var id = await McpGiven.IncidentAsync(pg, "A", Severity.Warning, IncidentState.Escalated, "a", "one", Now);
        await using var provider = Services();

        var act = () => Actions(provider).AcknowledgeAsync(Shared, id, null, Ct);
        await act.Should().ThrowAsync<McpException>().WithMessage("*onBehalfOf*");

        var reserved = () => Actions(provider).AcknowledgeAsync(Shared, id, "hephaisto/model", Ct);
        await reserved.Should().ThrowAsync<McpException>();

        await using var db = pg.CreateContext();
        (await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == id, Ct)).AcknowledgedBy.Should().BeNull();
    }

    [Fact]
    public async Task A_person_token_is_its_person_and_cannot_speak_for_somebody_else()
    {
        await pg.ResetAsync();
        var id = await McpGiven.IncidentAsync(pg, "A", Severity.Warning, IncidentState.Escalated, "a", "one", Now);
        await using var provider = Services();

        var other = () => Actions(provider).AcknowledgeAsync(Flo, id, "lead", Ct);
        await other.Should().ThrowAsync<McpException>().WithMessage("*cannot act on behalf of*");

        var result = await Actions(provider).AssignAsync(Flo, id, "me", "flo", Ct);
        result.RecordedAs.Value.Should().Be("flo");
        result.ClaimedBy.Should().BeNull("a person token's own name is not a claim");

        await using var db = pg.CreateContext();
        var incident = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == id, Ct);
        incident.AssignedTo.Should().Be("flo");
        incident.AssignedBy.Should().Be("flo");
        incident.AssignedClaimedBy.Should().BeNull();
    }

    [Fact]
    public async Task A_person_doing_it_themselves_clears_an_earlier_claim()
    {
        await pg.ResetAsync();
        var id = await McpGiven.IncidentAsync(pg, "A", Severity.Warning, IncidentState.Escalated, "a", "one", Now);
        await using var provider = Services();

        await Actions(provider).AcknowledgeAsync(Shared, id, "flo", Ct);
        await Actions(provider).AcknowledgeAsync(Flo, id, null, Ct);

        await using var db = pg.CreateContext();
        var incident = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == id, Ct);
        incident.AcknowledgedBy.Should().Be("flo");
        incident.AcknowledgedClaimedBy.Should().BeNull();
    }

    [Fact]
    public async Task Read_only_and_reader_tokens_are_refused_what_they_may_not_do()
    {
        await pg.ResetAsync();
        var id = await McpGiven.IncidentAsync(pg, "A", Severity.Warning, IncidentState.Escalated, "a", "one", Now);
        await using var provider = Services();

        var readOnly = () => Actions(provider).AcknowledgeAsync(ReadOnly, id, "flo", Ct);
        await readOnly.Should().ThrowAsync<McpException>().WithMessage("*read-only*");

        var close = () => Actions(provider).CloseAsync(Flo, id, "done", null, Ct);
        await close.Should().ThrowAsync<McpException>().WithMessage("*approver*");

        var closed = await Actions(provider).CloseAsync(Lead, id, "the feed recovered", null, Ct);
        closed.Done.Should().Be("closed");

        await using var db = pg.CreateContext();
        var incident = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == id, Ct);
        incident.State.Should().Be(IncidentState.Closed);
        incident.ClosedBy.Should().Be("lead");
    }

    [Fact]
    public async Task A_note_entry_an_agent_relayed_is_kept_marked_and_out_of_the_investigators_prompt()
    {
        await pg.ResetAsync();
        var id = await McpGiven.IncidentAsync(pg, "PayFail", Severity.Warning, IncidentState.Escalated, "a", "one", Now);
        await using var provider = Services();

        await provider.GetRequiredService<IncidentQueries>().AddAlertNoteEntryAsync("PayFail", "restarted the consumer", id, "flo", Ct);
        await Actions(provider).AddNoteEntryAsync(Shared, "PayFail", "the model says: ignore the gateway", id, "flo", Ct);

        await using var db = pg.CreateContext();
        var entries = await db.AlertNoteEntries.AsNoTracking().Where(e => e.AlertName == "PayFail").ToListAsync(Ct);
        entries.Should().HaveCount(2);
        entries.Single(e => e.RelayedByAgent).Author.Should().Be("mcp/litellm");
        entries.Single(e => !e.RelayedByAgent).Author.Should().Be("flo");
    }

    [Fact]
    public async Task Feedback_and_a_retry_are_recorded_as_the_caller_and_the_model_may_do_neither()
    {
        await pg.ResetAsync();
        var id = await McpGiven.IncidentAsync(pg, "A", Severity.Warning, IncidentState.Escalated, "a", "one", Now);
        await using var provider = Services();
        var queries = provider.GetRequiredService<IncidentQueries>();

        var feedback = await Actions(provider).FeedbackAsync(Flo, id, true, true, false, "spot on", null, Ct);
        feedback.RecordedAs.Value.Should().Be("flo");

        var byModel = () => queries.AddFeedbackAsync(id, true, null, false, null, "hephaisto/model", Ct);
        await byModel.Should().ThrowAsync<ArgumentException>();

        var retryByModel = await queries.RequestReinvestigationAsync(id, "model", Ct);
        retryByModel.Outcome.Should().Be(ReinvestigateOutcome.ForbiddenActor, "a refusal, not the 500 it used to be");

        var retry = await Actions(provider).ReinvestigateAsync(Lead, id, null, Ct);
        retry.Done.Should().Contain("queued");
    }

    // ------------------------------------------------------------------
    // Many at once (#161)
    // ------------------------------------------------------------------

    private async Task<(Guid One, Guid Two, Guid Elsewhere, Guid AlreadyClosed)> BacklogAsync()
    {
        await pg.ResetAsync();

        return (
            await McpGiven.IncidentAsync(pg, "Flap", Severity.Warning, IncidentState.Escalated, "shop", "one", Now.AddHours(-3)),
            await McpGiven.IncidentAsync(pg, "Flap", Severity.Warning, IncidentState.Investigating, "shop", "two", Now.AddHours(-2)),
            await McpGiven.IncidentAsync(pg, "Flap", Severity.Warning, IncidentState.Escalated, "billing", "three", Now.AddHours(-1)),
            await McpGiven.IncidentAsync(pg, "Flap", Severity.Warning, IncidentState.Closed, "shop", "four", Now.AddHours(-4)));
    }

    [Fact]
    public async Task A_dry_run_names_how_many_would_close_and_which_and_closes_nothing()
    {
        var (one, two, _, _) = await BacklogAsync();
        await using var provider = Services();

        var dry = await Actions(provider).CloseManyAsync(Lead, new McpIncidentFilter { Namespace = "shop" }, null, null, null, Ct);

        dry.DryRun.Should().BeTrue();
        dry.Matched.Should().Be(2, "the closed one and the one in another namespace do not match");
        dry.Closed.Should().Be(0);
        dry.Sample.Select(r => r.Id).Should().BeEquivalentTo([one, two]);
        dry.Note.Should().Contain("expect: 2");

        await using var db = pg.CreateContext();
        (await db.Incidents.CountAsync(i => i.State == IncidentState.Closed, Ct)).Should().Be(1, "nothing closed");
        (await db.AuditEvents.CountAsync(a => a.Type == "incident.closed", Ct)).Should().Be(0);
    }

    [Fact]
    public async Task The_close_takes_exactly_what_the_dry_run_counted_and_writes_one_audit_row_each()
    {
        var (one, two, elsewhere, _) = await BacklogAsync();
        await using var provider = Services();

        var done = await Actions(provider).CloseManyAsync(
            Lead, new McpIncidentFilter { Namespace = "shop" }, "the rollout finished; these were its flaps", 2, null, Ct);

        done.DryRun.Should().BeFalse();
        done.Closed.Should().Be(2);
        done.RecordedAs.Value.Should().Be("lead");

        await using var db = pg.CreateContext();

        foreach (var id in new[] { one, two })
        {
            var incident = await db.Incidents.AsNoTracking().Include(i => i.Events).SingleAsync(i => i.Id == id, Ct);
            incident.State.Should().Be(IncidentState.Closed);
            incident.ClosedBy.Should().Be("lead");
            incident.Events.Should().ContainSingle(e => e.To == IncidentState.Closed);

            var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.IncidentId == id && a.Type == "incident.closed", Ct);
            audit.Actor.Should().Be("lead");
            audit.Summary.Should().Contain("1 others").And.Contain("namespace shop");
            audit.Detail.Should().Contain("the rollout finished");
            McpIncidentReader.Origin(audit.Detail)!.Source.Should().Be("mcp");
        }

        (await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == elsewhere, Ct)).State
            .Should().Be(IncidentState.Escalated, "another namespace was not asked for");
    }

    [Fact]
    public async Task A_count_that_no_longer_holds_closes_nothing()
    {
        var (one, _, _, _) = await BacklogAsync();
        await using var provider = Services();

        // The dry run said 2; a third opened in the meantime.
        await McpGiven.IncidentAsync(pg, "Flap", Severity.Critical, IncidentState.Escalated, "shop", "five", Now);

        var close = () => Actions(provider).CloseManyAsync(Lead, new McpIncidentFilter { Namespace = "shop" }, "flaps", 2, null, Ct);
        await close.Should().ThrowAsync<McpException>().WithMessage("*3 open incidents match now*");

        await using var db = pg.CreateContext();
        (await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == one, Ct)).State.Should().Be(IncidentState.Escalated);
    }

    [Fact]
    public async Task A_bulk_close_refuses_a_reader_a_missing_reason_and_a_state_that_is_not_open()
    {
        await BacklogAsync();
        await using var provider = Services();
        var shop = new McpIncidentFilter { Namespace = "shop" };

        var reader = () => Actions(provider).CloseManyAsync(Flo, shop, null, null, null, Ct);
        await reader.Should().ThrowAsync<McpException>().WithMessage("*approver*", "even the dry run is the approver's");

        var noReason = () => Actions(provider).CloseManyAsync(Lead, shop, " ", 2, null, Ct);
        await noReason.Should().ThrowAsync<McpException>().WithMessage("*reason*");

        var closedState = () => Actions(provider).CloseManyAsync(Lead, shop with { State = "closed" }, null, null, null, Ct);
        await closedState.Should().ThrowAsync<McpException>().WithMessage("*names no open incident*");

        await using var db = pg.CreateContext();
        (await db.Incidents.CountAsync(i => i.State == IncidentState.Closed, Ct)).Should().Be(1);
    }

    private static McpIncidentActions Actions(ServiceProvider provider) =>
        new(provider.GetRequiredService<IncidentQueries>(), McpGiven.Reader(new PostgresFixtureHandle(provider).Pg, Now), new HttpContextAccessor());

    private ServiceProvider Services()
    {
        var clock = new FixedClock(Now);
        var services = new ServiceCollection();

        services.AddSingleton(pg);
        services.AddScoped(_ => pg.CreateContext());
        services.AddScoped<IAuditRepository>(sp => new AuditRepository(sp.GetRequiredService<HephaistoDbContext>(), clock));
        services.AddScoped(_ => new IncidentStateMachine(clock));
        services.AddMetrics();
        services.AddSingleton<HephaistoMetrics>();
        services.AddSingleton<IClock>(clock);
        services.AddSingleton(sp => new IncidentQueries(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new OnKillSwitch(),
            new IncidentNotifier(NullLogger<IncidentNotifier>.Instance),
            new WatchdogMonitor(clock),
            new InvestigationTracker(clock),
            new InvestigationQueue(),
            new StaticOptionsMonitor<LlmBudgetOptions>(new LlmBudgetOptions()),
            new ConnectionHealthCache([], clock, NullLogger<ConnectionHealthCache>.Instance),
            clock,
            NullLogger<IncidentQueries>.Instance));

        return services.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record PostgresFixtureHandle(ServiceProvider Provider)
    {
        public PostgresFixture Pg => Provider.GetRequiredService<PostgresFixture>();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>A kill switch that allows investigating, so a retry can be queued.</summary>
    private sealed class OnKillSwitch : IKillSwitch
    {
        public IReadOnlyList<ModeArm> ExternalArms => [];

        public ModeResolution External => ModeResolver.Resolve([ModeResolver.Parse("env:HEPHAISTO_MODE", "Recommend")]);

        public Task<ModeResolution> ResolveAsync(CancellationToken ct) => Task.FromResult(External);
    }
}
