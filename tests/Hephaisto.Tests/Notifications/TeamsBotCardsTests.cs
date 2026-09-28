using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The two cards the bot keeps up to date. What is load-bearing is not the layout: it is that an
/// unchanged card compares as unchanged, that nothing in a card can need an inbound route, and
/// that a board which lists only some incidents says so.
/// </summary>
public sealed class TeamsBotCardsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly TeamsCardLinks Links = new()
    {
        BaseUrl = "https://hephaisto.example/",
        GrafanaUrl = "https://grafana.example",
    };

    [Fact]
    public void The_board_lists_each_incident_and_counts_all_of_them()
    {
        var card = Card(TeamsBotCards.Board([Incident(title: "api is crash looping"), Incident(title: "db is slow")], 2, Links, Now));

        card.GetProperty("body")[0].GetProperty("text").GetString().Should().Be("Hephaisto - open incidents (2)");
        card.ToString().Should().Contain("api is crash looping").And.Contain("db is slow");
        card.GetProperty("version").GetString().Should().Be("1.5");
    }

    [Fact]
    public void A_board_that_lists_only_some_says_how_many_it_left_out()
    {
        // A board that lists twenty of thirty and looks complete is worse than one that is
        // visibly partial.
        var card = Card(TeamsBotCards.Board([Incident()], 31, Links, Now));

        card.GetProperty("body")[0].GetProperty("text").GetString().Should().Contain("(31)");
        card.ToString().Should().Contain("and 30 more");
    }

    [Fact]
    public void An_empty_board_says_so_in_words()
    {
        var card = Card(TeamsBotCards.Board([], 0, Links, Now));

        card.ToString().Should().Contain("No open incidents.").And.NotContain("more, not shown");
    }

    [Fact]
    public void The_clock_moving_is_not_a_change()
    {
        // The board is compared every few seconds. If the timestamp counted, every comparison
        // would find a difference and the board would be edited constantly.
        var incidents = new[] { Incident() };

        var earlier = TeamsBotCards.Hash(TeamsBotCards.Board(incidents, 1, Links, updatedAt: null));
        var later = TeamsBotCards.Hash(TeamsBotCards.Board(incidents, 1, Links, updatedAt: null));

        later.Should().Be(earlier);

        TeamsBotCards.Board(incidents, 1, Links, Now).ToJsonString().Should().Contain("Updated 2026-09-28 12:00 UTC");
        TeamsBotCards.Board(incidents, 1, Links, updatedAt: null).ToJsonString().Should().NotContain("Updated");
    }

    [Fact]
    public void A_board_row_carries_no_relative_time()
    {
        // "Open for 20 min" would be different a minute later, which is an edit a minute.
        var row = TeamsBotCards.Board([Incident()], 1, Links, Now).ToJsonString();

        row.Should().Contain("since 2026-09-28 11:40 UTC");
        row.Should().NotContain(" min").And.NotContain(" ago");
    }

    [Fact]
    public void Any_visible_change_is_a_different_hash()
    {
        var before = TeamsBotCards.Hash(TeamsBotCards.Board([Incident()], 1, Links, null));

        TeamsBotCards.Hash(TeamsBotCards.Board([Incident(assignedTo: "flo")], 1, Links, null)).Should().NotBe(before);
        TeamsBotCards.Hash(TeamsBotCards.Board([Incident(state: IncidentState.AwaitingApproval)], 1, Links, null)).Should().NotBe(before);
        TeamsBotCards.Hash(TeamsBotCards.Board([Incident(codeFix: CodeFixState.PlanReady)], 1, Links, null)).Should().NotBe(before);
        TeamsBotCards.Hash(TeamsBotCards.Board([], 0, Links, null)).Should().NotBe(before);
    }

    [Fact]
    public void What_a_lock_screen_announces_is_not_part_of_the_hash()
    {
        // The announcement names the EVENT, the card shows the STATE. If it counted, the first
        // comparison after every alert would edit a card whose content had not changed.
        var card = TeamsBotCards.Alert(Incident(), Links);
        var before = TeamsBotCards.Hash(card);

        TeamsBotCards.Hash(TeamsBotCards.WithSummary(card, "Code fix ended without a PR: api")).Should().Be(before);
    }

    [Fact]
    public void With_the_actions_off_nothing_in_any_card_can_need_an_inbound_route()
    {
        // A button that acts means Microsoft calls this process. With Notifications:TeamsBot:Actions
        // off - the default - no card may carry one, because nothing would answer it.
        var incident = Incident(state: IncidentState.AwaitingApproval, codeFix: CodeFixState.PrOpened, pr: "https://github.com/o/r/pull/7");

        var everything = string.Concat(
            TeamsBotCards.Board([incident], 1, Links, Now).ToJsonString(),
            TeamsBotCards.Alert(incident, Links).ToJsonString(),
            TeamsBotCards.Superseded(incident, Links).ToJsonString());

        everything.Should().NotContain("Action.Submit").And.NotContain("Action.Execute").And.NotContain("Action.Http");

        var kinds = Actions(TeamsBotCards.Alert(incident, Links)).Select(a => a.GetProperty("type").GetString());

        kinds.Should().OnlyContain(k => k == "Action.OpenUrl");
    }

    [Fact]
    public void With_the_actions_on_only_an_open_alert_carries_the_two_verbs()
    {
        var acting = Links with { Actions = true };
        var open = Incident(state: IncidentState.AwaitingApproval, codeFix: CodeFixState.PrOpened, pr: "https://github.com/o/r/pull/7");

        var executes = Actions(TeamsBotCards.Alert(open, acting))
            .Where(a => a.GetProperty("type").GetString() == "Action.Execute")
            .ToList();

        executes.Select(a => a.GetProperty("verb").GetString())
            .Should().BeEquivalentTo([TeamsBotVerbs.Acknowledge, TeamsBotVerbs.AssignToMe]);
        executes.Should().OnlyContain(a => a.GetProperty("data").GetProperty("incidentId").GetString() == open.Id.ToString());

        // Nothing else acts: not the board, not a superseded alert, not a closed one, and no
        // other kind of acting button anywhere.
        var elsewhere = string.Concat(
            TeamsBotCards.Board([open], 1, acting, Now).ToJsonString(),
            TeamsBotCards.Superseded(open, acting).ToJsonString(),
            TeamsBotCards.Alert(open with { State = IncidentState.Closed }, acting).ToJsonString());

        elsewhere.Should().NotContain("Action.Execute");
        TeamsBotCards.Alert(open, acting).ToJsonString().Should().NotContain("Action.Submit").And.NotContain("Action.Http");

        // Acknowledged once, it does not ask again; taking it over still can.
        Actions(TeamsBotCards.Alert(open with { AcknowledgedBy = "oncall@example.com" }, acting))
            .Where(a => a.GetProperty("type").GetString() == "Action.Execute")
            .Select(a => a.GetProperty("verb").GetString())
            .Should().Equal(TeamsBotVerbs.AssignToMe);
    }

    [Fact]
    public void Every_verb_a_button_can_send_has_a_handler()
    {
        // A button whose verb the route does not know would be a click that does nothing. The
        // handler answers exactly TeamsBotVerbs.All, so every verb drawn must be in it.
        var acting = Links with { Actions = true };

        var drawn = Actions(TeamsBotCards.Alert(Incident(), acting))
            .Where(a => a.GetProperty("type").GetString() == "Action.Execute")
            .Select(a => a.GetProperty("verb").GetString());

        drawn.Should().NotBeEmpty().And.OnlyContain(v => TeamsBotVerbs.All.Contains(v!));
        TeamsBotVerbs.All.Should().BeEquivalentTo([TeamsBotVerbs.Acknowledge, TeamsBotVerbs.AssignToMe]);
    }

    [Fact]
    public void An_alert_is_announced_in_words_on_a_lock_screen()
    {
        // Without a summary Teams announces a card as "Sent a card".
        var activity = TeamsBotCards.Alert(Incident(title: "api is crash looping"), Links);

        activity["summary"]!.GetValue<string>().Should().Contain("Escalated").And.Contain("api is crash looping");
    }

    [Theory]
    [InlineData(IncidentState.Escalated, EscalationReason.NoPlanProduced, "Escalated")]
    [InlineData(IncidentState.Escalated, EscalationReason.RollbackPerformed, "Verification failed")]
    [InlineData(IncidentState.AwaitingApproval, EscalationReason.None, "Approval required")]
    [InlineData(IncidentState.Resolved, EscalationReason.None, "Resolved")]
    [InlineData(IncidentState.Expired, EscalationReason.None, "Expired")]
    [InlineData(IncidentState.Investigating, EscalationReason.None, "In progress - Investigating")]
    public void The_headline_says_the_state_in_words(IncidentState state, EscalationReason reason, string expected)
    {
        TeamsBotCards.Headline(Incident(state: state) with { EscalationReason = reason }).Should().Contain(expected);
    }

    [Fact]
    public void A_closed_incident_names_who_closed_it()
    {
        var closed = Incident(state: IncidentState.Closed) with { ClosedBy = "flo" };

        TeamsBotCards.Headline(closed).Should().Be("Closed by flo");
        Card(TeamsBotCards.Alert(closed, Links)).GetProperty("body")[0].GetProperty("color").GetString().Should().Be("Good");
    }

    [Fact]
    public void A_pr_comes_first_because_it_is_the_subject()
    {
        var incident = Incident(codeFix: CodeFixState.PrOpened, pr: "https://github.com/o/r/pull/7");

        var first = Actions(TeamsBotCards.Alert(incident, Links))[0];

        first.GetProperty("title").GetString().Should().Be("Open the Draft PR");
        first.GetProperty("url").GetString().Should().Be("https://github.com/o/r/pull/7");
        TeamsBotCards.Headline(incident).Should().Contain("Draft PR opened");
    }

    [Fact]
    public void A_plan_links_to_where_it_is_reviewed()
    {
        var incident = Incident(codeFix: CodeFixState.PlanReady);

        var link = Actions(TeamsBotCards.Alert(incident, Links))[0];

        link.GetProperty("title").GetString().Should().Be("Review the plan in Hephaisto");
        link.GetProperty("url").GetString().Should().Be($"https://hephaisto.example/incidents/{incident.Id}#codefix");
    }

    [Fact]
    public void An_alert_links_to_the_board_once_there_is_one()
    {
        var without = Actions(TeamsBotCards.Alert(Incident(), Links)).Select(a => a.GetProperty("title").GetString());
        var with = Actions(TeamsBotCards.Alert(Incident(), Links with { BoardUrl = "https://teams.microsoft.com/l/message/x/1" }))
            .Select(a => a.GetProperty("title").GetString());

        without.Should().NotContain("Open the board");
        with.Should().Contain("Open the board");
    }

    [Fact]
    public void A_card_with_no_links_carries_no_empty_action_bar()
    {
        var card = Card(TeamsBotCards.Alert(Incident(), new TeamsCardLinks()));

        card.TryGetProperty("actions", out _).Should().BeFalse();
    }

    [Fact]
    public void A_superseded_alert_is_one_line_that_points_below()
    {
        var card = Card(TeamsBotCards.Superseded(Incident(title: "api is crash looping"), Links));

        card.GetProperty("body").GetArrayLength().Should().Be(1);
        card.GetProperty("body")[0].GetProperty("text").GetString()
            .Should().Contain("Superseded").And.Contain("api is crash looping");
    }

    [Fact]
    public void A_board_too_large_to_send_lists_fewer_and_says_so()
    {
        // A count is not a limit. Forty rows with long titles, a PR and a resolution note come
        // to about 160 KB, and the largest board Teams was seen to accept was 114 KB.
        var incidents = Enumerable.Range(0, 40).Select(Worst).ToList();

        Encoding.Unicode.GetByteCount(TeamsBotCards.Board(incidents, 40, Links, Now).ToJsonString())
            .Should().BeGreaterThan(114 * 1024, "otherwise this test no longer describes the problem");

        var json = TeamsBotCards.BoardWithin(incidents, 40, Links, Now).ToJsonString();

        // Teams counts UTF-16.
        Encoding.Unicode.GetByteCount(json).Should().BeLessThanOrEqualTo(TeamsBotCards.MaxBoardBytes);
        json.Should().Contain("open incidents (40)").And.Contain(" more, not shown here");
        json.Should().Contain("cait-matching-service-0 after", "the first rows are the ones kept");
    }

    [Fact]
    public void The_default_board_fits_whatever_the_incidents_say()
    {
        // Twenty is the default. At its worst it has to be sent whole, or the default is wrong.
        var incidents = Enumerable.Range(0, 20).Select(Worst).ToList();

        var whole = TeamsBotCards.Board(incidents, 20, Links, Now).ToJsonString();

        Encoding.Unicode.GetByteCount(whole).Should().BeLessThanOrEqualTo(TeamsBotCards.MaxBoardBytes);
        TeamsBotCards.BoardWithin(incidents, 20, Links, Now).ToJsonString().Should().Be(whole);
    }

    [Fact]
    public void A_board_that_fits_is_not_trimmed()
    {
        var incidents = new[] { Incident(title: "one"), Incident(title: "two") };

        TeamsBotCards.BoardWithin(incidents, 2, Links, Now).ToJsonString()
            .Should().Be(TeamsBotCards.Board(incidents, 2, Links, Now).ToJsonString());
    }

    [Fact]
    public void A_long_resolution_note_is_cut_on_the_board()
    {
        var row = TeamsBotCards.Board([Incident() with { Summary = new string('x', 1000) }], 1, Links, Now).ToJsonString();

        row.Should().Contain(new string('x', 280) + "...").And.NotContain(new string('x', 281));
    }

    private static TeamsIncident Worst(int i) => Incident(
        title: $"CrashLoopBackOff on cait-matching-service-{i} after the rollout of a very long image tag",
        assignedTo: "somebody@true-relevance.example",
        codeFix: CodeFixState.PrOpened,
        pr: "https://github.com/TrueRelevance/CaitMatchingService/pull/1234") with
    {
        Summary = new string('x', 1000),
    };

    private static TeamsIncident Incident(
        string title = "api is crash looping",
        IncidentState state = IncidentState.Escalated,
        string? assignedTo = null,
        CodeFixState? codeFix = null,
        string? pr = null) => new()
        {
            Id = Guid.Parse("0192a6f0-0000-7000-8000-0000000000aa"),
            Title = title,
            Kind = SignalKind.CrashLoopBackOff,
            Severity = Severity.Critical,
            State = state,
            EscalationReason = state is IncidentState.Escalated ? EscalationReason.NoPlanProduced : EscalationReason.None,
            Target = "cait/Deployment/api",
            OpenedAt = Now.AddMinutes(-20),
            AssignedTo = assignedTo,
            CodeFix = codeFix,
            PullRequestUrl = pr,
        };

    private static JsonElement Card(JsonObject activity) =>
        JsonDocument.Parse(activity.ToJsonString()).RootElement.GetProperty("attachments")[0].GetProperty("content");

    private static List<JsonElement> Actions(JsonObject activity) =>
        Card(activity).TryGetProperty("actions", out var actions) ? [.. actions.EnumerateArray()] : [];
}
