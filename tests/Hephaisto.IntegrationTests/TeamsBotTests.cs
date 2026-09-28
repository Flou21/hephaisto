using System.Net;
using System.Text.Json.Nodes;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// The board and the personal alerts, against a real database and a Teams that records what it
/// was asked to do.
/// </summary>
/// <remarks>
/// What needs Postgres is everything that makes this channel different from the others: a
/// message is posted once and then edited for as long as its incident lives, and the id that
/// makes editing possible is a row. The properties below are the ones a channel full of stale
/// or duplicated cards would violate.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class TeamsBotTests(PostgresFixture pg)
{
    private const string Channel = "19:abc@thread.tacv2";
    private const string Flo = "flo@true-relevance.example";
    private const string Dev = "dev@true-relevance.example";

    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ the board

    [Fact]
    public async Task The_board_is_posted_once_and_then_left_alone_while_nothing_changes()
    {
        await pg.ResetAsync();
        await SeedAsync();

        var teams = new RecordingTeams();

        await TickAsync(teams);
        await TickAsync(teams);
        await TickAsync(teams);

        teams.Calls.Should().Equal("post-channel");

        await using var db = pg.CreateContext();
        var board = await db.TeamsBotMessages.SingleAsync(TestContext.Current.CancellationToken);

        board.Kind.Should().Be(TeamsBotMessageKind.Board);
        board.State.Should().Be(TeamsBotMessageState.Live);
        board.Recipient.Should().Be(Channel);
        board.ActivityId.Should().Be("1");
    }

    [Fact]
    public async Task A_change_that_announces_nothing_still_edits_the_board()
    {
        // Assigning an incident is not a notification. It has to reach the board anyway.
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();
        await TickAsync(teams);

        await ChangeAsync(id, i => i.AssignedTo = "flo");
        await TickAsync(teams);

        teams.Calls.Should().Equal("post-channel", "edit 1");
        teams.Last.Should().Contain("assigned to flo");
    }

    [Fact]
    public async Task A_closed_incident_leaves_the_board_by_being_edited_out()
    {
        await pg.ResetAsync();
        var kept = await SeedAsync(title: "db is slow");
        var closed = await SeedAsync(title: "api is crash looping");

        var teams = new RecordingTeams();
        await TickAsync(teams);

        teams.Last.Should().Contain("open incidents (2)").And.Contain("api is crash looping");

        await ChangeAsync(closed, i =>
        {
            i.State = IncidentState.Closed;
            i.ClosedBy = "flo";
            i.ClosedAt = Now;
        });

        await TickAsync(teams);

        teams.Calls.Should().Equal("post-channel", "edit 1");
        teams.Last.Should().Contain("open incidents (1)").And.Contain("db is slow").And.NotContain("api is crash looping");
        kept.Should().NotBe(closed);
    }

    [Fact]
    public async Task With_nothing_open_the_board_says_so_rather_than_going_away()
    {
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();
        await TickAsync(teams);

        await ChangeAsync(id, i => i.State = IncidentState.Resolved);
        await TickAsync(teams);

        teams.Calls.Should().Equal("post-channel", "edit 1");
        teams.Last.Should().Contain("No open incidents.");
    }

    [Fact]
    public async Task A_board_that_could_not_be_posted_is_tried_again()
    {
        await pg.ResetAsync();
        await SeedAsync();

        var teams = new RecordingTeams { Refuse = HttpStatusCode.BadGateway };
        await TickAsync(teams);

        await using (var db = pg.CreateContext())
        {
            (await db.TeamsBotMessages.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        }

        teams.Refuse = null;
        await TickAsync(teams);

        teams.Calls.Should().Equal("post-channel", "post-channel");

        await using (var db = pg.CreateContext())
        {
            (await db.TeamsBotMessages.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        }
    }

    [Fact]
    public async Task An_edit_that_failed_is_still_different_on_the_next_tick()
    {
        // There is no queue of pending edits. A failed one is simply found again.
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();
        await TickAsync(teams);

        await ChangeAsync(id, i => i.AssignedTo = "flo");

        teams.Refuse = HttpStatusCode.TooManyRequests;
        await TickAsync(teams);

        teams.Refuse = null;
        await TickAsync(teams);
        await TickAsync(teams);

        teams.Calls.Should().Equal("post-channel", "edit 1", "edit 1");
    }

    [Fact]
    public async Task A_board_somebody_removed_by_hand_is_replaced_by_a_new_one()
    {
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();
        await TickAsync(teams);

        await ChangeAsync(id, i => i.AssignedTo = "flo");

        teams.Refuse = HttpStatusCode.NotFound;
        await TickAsync(teams);

        teams.Refuse = null;
        await TickAsync(teams);

        teams.Calls.Should().Equal("post-channel", "edit 1", "post-channel");

        await using var db = pg.CreateContext();
        var boards = await db.TeamsBotMessages.OrderBy(m => m.CreatedAt).ThenBy(m => m.ActivityId)
            .ToListAsync(TestContext.Current.CancellationToken);

        boards.Select(b => b.State).Should().BeEquivalentTo(
            [TeamsBotMessageState.Final, TeamsBotMessageState.Live]);
    }

    // ------------------------------------------------------------------ alerts

    [Fact]
    public async Task An_alert_reaches_every_recipient_in_their_own_chat()
    {
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();

        var result = await SendAsync(teams, Escalated(id), Flo, Dev);

        result.Disposition.Should().Be(DeliveryDisposition.Delivered);

        teams.Calls.Should().Equal(
            $"find {Flo}", "open-chat 29:flo", "send a:29:flo",
            $"find {Dev}", "open-chat 29:dev", "send a:29:dev");

        teams.Last.Should().Contain("Escalated").And.Contain("api is crash looping");

        await using var db = pg.CreateContext();
        var alerts = await db.TeamsBotMessages.ToListAsync(TestContext.Current.CancellationToken);

        alerts.Should().HaveCount(2).And.OnlyContain(a =>
            a.Kind == TeamsBotMessageKind.Alert && a.State == TeamsBotMessageState.Live && a.IncidentId == id);
    }

    [Fact]
    public async Task An_alert_is_not_edited_just_because_it_was_compared()
    {
        // The card shows the state and the lock screen announces the event. If the two were
        // hashed together, the first comparison after every alert would edit a card whose
        // content had not changed - and every alert would arrive marked "Edited".
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();
        await TickAsync(teams);
        await SendAsync(teams, Escalated(id, NotificationEvent.CodeFixFailed), Flo);

        teams.Calls.Clear();

        await TickAsync(teams);
        await TickAsync(teams);

        teams.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retried_delivery_does_not_tell_the_same_person_twice()
    {
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams { RefuseFor = "a:29:dev" };
        var message = Escalated(id);

        (await SendAsync(teams, message, Flo, Dev)).Disposition.Should().Be(DeliveryDisposition.Retryable);

        teams.RefuseFor = null;
        teams.Calls.Clear();

        (await SendAsync(teams, message, Flo, Dev)).Disposition.Should().Be(DeliveryDisposition.Delivered);

        // Flo was reached the first time and is not contacted again.
        teams.Calls.Should().Equal($"find {Dev}", "open-chat 29:dev", "send a:29:dev");
    }

    [Fact]
    public async Task Somebody_who_is_not_in_the_team_does_not_stop_the_others_being_told()
    {
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();

        var result = await SendAsync(teams, Escalated(id), "stranger@elsewhere.example", Flo);

        result.Disposition.Should().Be(DeliveryDisposition.Delivered);
        teams.Calls.Should().Contain("send a:29:flo").And.NotContain(c => c.Contains("stranger", StringComparison.Ordinal) && c.StartsWith("send", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_nobody_reachable_the_delivery_fails_loudly()
    {
        await pg.ResetAsync();
        var id = await SeedAsync();

        var result = await SendAsync(new RecordingTeams(), Escalated(id), "stranger@elsewhere.example");

        result.Disposition.Should().Be(DeliveryDisposition.Permanent);
        result.Detail.Should().Contain("stranger@elsewhere.example");
    }

    [Fact]
    public async Task With_no_recipients_configured_the_delivery_fails_loudly()
    {
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();

        var result = await SendAsync(teams, Escalated(id));

        result.Disposition.Should().Be(DeliveryDisposition.Permanent);
        result.Detail.Should().Contain("Recipients");
        teams.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_chat_with_a_person_is_looked_up_once()
    {
        await pg.ResetAsync();
        var first = await SeedAsync();
        var second = await SeedAsync(title: "db is slow");

        var teams = new RecordingTeams();

        await SendAsync(teams, Escalated(first), Flo);
        teams.Calls.Clear();

        await SendAsync(teams, Escalated(second), Flo);

        teams.Calls.Should().Equal("send a:29:flo");
    }

    [Fact]
    public async Task An_alert_follows_its_incident_to_the_end_and_then_stops()
    {
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();
        await TickAsync(teams);
        await SendAsync(teams, Escalated(id), Flo);

        await ChangeAsync(id, i =>
        {
            i.State = IncidentState.Closed;
            i.ClosedBy = "flo";
            i.ClosedAt = Now;
        });

        teams.Calls.Clear();

        await TickAsync(teams);

        teams.Calls.Should().Equal("edit 1", "edit 2");
        teams.Last.Should().Contain("Closed by flo");

        teams.Calls.Clear();

        await TickAsync(teams);
        await TickAsync(teams);

        teams.Calls.Should().BeEmpty();

        await using var db = pg.CreateContext();

        (await db.TeamsBotMessages.SingleAsync(m => m.Kind == TeamsBotMessageKind.Alert, TestContext.Current.CancellationToken))
            .State.Should().Be(TeamsBotMessageState.Final);
    }

    [Fact]
    public async Task A_second_alert_is_a_new_message_and_the_first_is_shrunk_not_deleted()
    {
        // An edit rings no phone, so something new that needs a person is a new message. The
        // older card cannot be deleted without leaving a "deleted" line behind.
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();
        await TickAsync(teams);

        await SendAsync(teams, Escalated(id), Flo);
        await SendAsync(teams, Escalated(id, NotificationEvent.CodeFixPlanReady), Flo);

        teams.Calls.Clear();

        await TickAsync(teams);

        teams.Calls.Should().Equal("edit 2");
        teams.Last.Should().Contain("Superseded by a newer alert below");

        await using var db = pg.CreateContext();
        var alerts = await db.TeamsBotMessages
            .Where(m => m.Kind == TeamsBotMessageKind.Alert)
            .OrderBy(m => m.ActivityId)
            .ToListAsync(TestContext.Current.CancellationToken);

        alerts.Select(a => (a.ActivityId, a.State)).Should().Equal(
            ("2", TeamsBotMessageState.Final),
            ("3", TeamsBotMessageState.Live));
    }

    [Fact]
    public async Task An_alert_that_can_no_longer_be_edited_is_left_as_it_is()
    {
        // The app was removed for this person. Trying every tick for ever is how a reconciler
        // becomes a source of load rather than of truth.
        await pg.ResetAsync();
        var id = await SeedAsync();

        var teams = new RecordingTeams();
        await SendAsync(teams, Escalated(id), Flo);

        await ChangeAsync(id, i => i.AssignedTo = "flo");

        teams.RefuseFor = "a:29:flo";
        teams.RefuseForStatus = HttpStatusCode.Forbidden;
        teams.Calls.Clear();

        await TickAsync(teams);
        await TickAsync(teams);

        teams.Calls.Where(c => c == "edit 1").Should().HaveCount(1);

        await using var db = pg.CreateContext();
        var alert = await db.TeamsBotMessages.SingleAsync(m => m.Kind == TeamsBotMessageKind.Alert, TestContext.Current.CancellationToken);

        alert.State.Should().Be(TeamsBotMessageState.Final);
        alert.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task An_alert_about_the_agent_is_posted_and_never_followed()
    {
        await pg.ResetAsync();

        var teams = new RecordingTeams();

        var message = new NotificationMessage
        {
            DeliveryId = Guid.CreateVersion7(),
            Snapshot = new NotificationSnapshot
            {
                Event = NotificationEvent.ModeChanged,
                Title = "runaway latch cleared",
                Severity = Severity.Critical,
                At = Now,
            },
        };

        (await SendAsync(teams, message, Flo)).Disposition.Should().Be(DeliveryDisposition.Delivered);
        teams.Last.Should().Contain("Autonomy re-armed");

        teams.Calls.Clear();
        await TickAsync(teams);

        teams.Calls.Should().Equal("post-channel");
    }

    // ------------------------------------------------------------------ plumbing

    private static NotificationMessage Escalated(
        Guid incidentId,
        NotificationEvent @event = NotificationEvent.IncidentEscalated) => new()
    {
        DeliveryId = Guid.CreateVersion7(),
        Snapshot = new NotificationSnapshot
        {
            Event = @event,
            IncidentId = incidentId,
            CorrelationKey = "cait/Deployment/api",
            Title = "api is crash looping",
            Severity = Severity.Critical,
            State = IncidentState.Escalated,
            Namespace = "cait",
            At = Now,
        },
    };

    private async Task TickAsync(RecordingTeams teams)
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => pg.CreateContext());
        services.AddScoped<TeamsBotIncidents>();
        services.AddSingleton<ITeamsBotClient>(teams);

        await using var provider = services.BuildServiceProvider();

        using var reconciler = new TeamsBoardReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(Now),
            Options(),
            NullLogger<TeamsBoardReconciler>.Instance);

        await reconciler.TickAsync(TestContext.Current.CancellationToken);
    }

    private async Task<DeliveryResult> SendAsync(
        RecordingTeams teams,
        NotificationMessage message,
        params string[] recipients)
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => pg.CreateContext());
        services.AddScoped<TeamsBotIncidents>();

        await using var provider = services.BuildServiceProvider();

        var channel = new TeamsBotNotificationChannel(
            teams,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(Now),
            Options(recipients),
            NullLogger<TeamsBotNotificationChannel>.Instance);

        return await channel.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private static StaticOptions Options(params string[] recipients) => new(new NotificationOptions
    {
        BaseUrl = "https://hephaisto.example",
        TeamsBot = new TeamsBotOptions
        {
            TenantId = "tenant-1",
            AppId = "app-1",
            ClientSecret = "not-a-real-secret",
            ChannelId = Channel,
            TeamId = "11111111-0000-0000-0000-000000000000",
            Recipients = [.. recipients],
        },
    });

    private async Task<Guid> SeedAsync(string title = "api is crash looping")
    {
        await using var db = pg.CreateContext();

        var incident = new Incident
        {
            CorrelationKey = $"cait/Deployment/api-{Guid.NewGuid():N}",
            Title = title,
            Kind = SignalKind.CrashLoopBackOff,
            Severity = Severity.Critical,
            State = IncidentState.Escalated,
            EscalationReason = EscalationReason.NoPlanProduced,
            Target = new TargetRef { Namespace = "cait", Kind = "Deployment", Name = "api" },
            OpenedAt = Now.AddMinutes(-20),
            LastSignalAt = Now,
        };

        db.Incidents.Add(incident);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return incident.Id;
    }

    private async Task ChangeAsync(Guid id, Action<Incident> change)
    {
        await using var db = pg.CreateContext();

        var incident = await db.Incidents.FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);
        change(incident);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A Teams that does what it is asked and writes it down. It has no way to delete, because
    /// <see cref="ITeamsBotClient"/> has none.
    /// </summary>
    private sealed class RecordingTeams : ITeamsBotClient
    {
        private int next;

        public List<string> Calls { get; } = [];

        /// <summary>The card most recently posted or edited, serialised.</summary>
        public string Last { get; private set; } = string.Empty;

        /// <summary>Refuse every call with this status.</summary>
        public HttpStatusCode? Refuse { get; set; }

        /// <summary>Refuse calls into this conversation only.</summary>
        public string? RefuseFor { get; set; }

        public HttpStatusCode RefuseForStatus { get; set; } = HttpStatusCode.BadGateway;

        public Task<TeamsBotResult<TeamsPosted>> PostToChannelAsync(JsonObject activity, CancellationToken ct)
        {
            Calls.Add("post-channel");

            if (Refuse is { } status)
            {
                return Task.FromResult(new TeamsBotResult<TeamsPosted>(status, default, "refused"));
            }

            Last = activity.ToJsonString();
            var id = (++next).ToString(System.Globalization.CultureInfo.InvariantCulture);

            return Task.FromResult(new TeamsBotResult<TeamsPosted>(
                HttpStatusCode.Created,
                new TeamsPosted($"{Channel};messageid={id}", id),
                null));
        }

        public Task<TeamsBotResult<string>> FindMemberAsync(string email, CancellationToken ct)
        {
            Calls.Add($"find {email}");

            if (Refuse is { } status)
            {
                return Task.FromResult(new TeamsBotResult<string>(status, null, "refused"));
            }

            return Task.FromResult(email.EndsWith("@true-relevance.example", StringComparison.Ordinal)
                ? new TeamsBotResult<string>(HttpStatusCode.OK, $"29:{email.Split('@')[0]}", null)
                : new TeamsBotResult<string>(HttpStatusCode.OK, null, $"{email} is not a member of the team"));
        }

        public Task<TeamsBotResult<string>> OpenChatAsync(string userId, CancellationToken ct)
        {
            Calls.Add($"open-chat {userId}");

            return Task.FromResult(Refuse is { } status
                ? new TeamsBotResult<string>(status, null, "refused")
                : new TeamsBotResult<string>(HttpStatusCode.Created, $"a:{userId}", null));
        }

        public Task<TeamsBotResult<string>> SendAsync(string conversationId, JsonObject activity, CancellationToken ct)
        {
            Calls.Add($"send {conversationId}");

            if (Refused(conversationId) is { } status)
            {
                return Task.FromResult(new TeamsBotResult<string>(status, null, "refused"));
            }

            Last = activity.ToJsonString();

            return Task.FromResult(new TeamsBotResult<string>(
                HttpStatusCode.Created,
                (++next).ToString(System.Globalization.CultureInfo.InvariantCulture),
                null));
        }

        public Task<TeamsBotResult<bool>> UpdateAsync(
            string conversationId,
            string activityId,
            JsonObject activity,
            CancellationToken ct)
        {
            Calls.Add($"edit {activityId}");

            if (Refused(conversationId) is { } status)
            {
                return Task.FromResult(new TeamsBotResult<bool>(status, false, "refused"));
            }

            Last = activity.ToJsonString();

            return Task.FromResult(new TeamsBotResult<bool>(HttpStatusCode.OK, true, null));
        }

        private HttpStatusCode? Refused(string conversationId) =>
            Refuse ?? (RefuseFor == conversationId ? RefuseForStatus : null);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StaticOptions(NotificationOptions value) : IOptionsMonitor<NotificationOptions>
    {
        public NotificationOptions CurrentValue => value;

        public NotificationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<NotificationOptions, string?> listener) => null;
    }
}
