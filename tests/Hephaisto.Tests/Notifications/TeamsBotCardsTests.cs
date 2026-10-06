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
        // off - the default - no card may carry one, because nothing would answer it. Asked of the
        // incidents that draw the most with it on: one awaiting approval, and one escalated with
        // nothing found, which is where Reinvestigate goes.
        var waiting = Incident(state: IncidentState.AwaitingApproval, codeFix: CodeFixState.PrOpened, pr: "https://github.com/o/r/pull/7")
            with { AlertName = "ConsumerLagHigh", NoteExcerpt = "Check the upstream feed first." };
        var undiagnosed = Incident();

        undiagnosed.CanReinvestigate.Should().BeTrue("otherwise this no longer covers the retry");

        // An approver being mapped draws nothing by itself: the buttons are off.
        foreach (var links in new[] { Links, Links with { Closing = true } })
        {
            links.Actions.Should().BeFalse();

            var everything = string.Concat(
                TeamsBotCards.Board([waiting, undiagnosed], 2, links, Now).ToJsonString(),
                TeamsBotCards.Alert(waiting, links).ToJsonString(),
                TeamsBotCards.Alert(undiagnosed, links).ToJsonString(),
                TeamsBotCards.Superseded(waiting, links).ToJsonString());

            everything.Should().NotContain("Action.Submit").And.NotContain("Action.Execute").And.NotContain("Action.Http");
            everything.Should().NotContain("Input.", "an input belongs to a button that sends it somewhere");

            foreach (var incident in new[] { waiting, undiagnosed })
            {
                Actions(TeamsBotCards.Alert(incident, links)).Select(a => a.GetProperty("type").GetString())
                    .Should().OnlyContain(k => k == "Action.OpenUrl");
            }
        }
    }

    [Fact]
    public void With_the_actions_on_only_an_open_alert_carries_verbs_and_only_the_ones_its_state_allows()
    {
        var acting = Links with { Actions = true };
        var open = Incident(state: IncidentState.AwaitingApproval, codeFix: CodeFixState.PrOpened, pr: "https://github.com/o/r/pull/7");

        var executes = Executes(TeamsBotCards.Alert(open, acting));

        executes.Select(a => a.GetProperty("verb").GetString())
            .Should().BeEquivalentTo([TeamsBotVerbs.Acknowledge, TeamsBotVerbs.AssignToMe]);
        executes.Should().OnlyContain(a => a.GetProperty("data").GetProperty("incidentId").GetString() == open.Id.ToString());

        // Nothing else acts: not the board, not a superseded alert, not a closed one, and no
        // other kind of acting button anywhere - with every button the configuration can draw.
        var everyButton = acting with { Closing = true };

        var elsewhere = string.Concat(
            TeamsBotCards.Board([open, Incident()], 2, everyButton, Now).ToJsonString(),
            TeamsBotCards.Superseded(open, everyButton).ToJsonString(),
            TeamsBotCards.Superseded(Incident(), everyButton).ToJsonString(),
            TeamsBotCards.Alert(open with { State = IncidentState.Closed }, everyButton).ToJsonString(),
            TeamsBotCards.Alert(Incident(state: IncidentState.Expired), everyButton).ToJsonString(),
            TeamsBotCards.Alert(Incident(state: IncidentState.Resolved), everyButton).ToJsonString());

        elsewhere.Should().NotContain("Action.Execute").And.NotContain("Input.");
        TeamsBotCards.Alert(open, everyButton).ToJsonString().Should().NotContain("Action.Submit").And.NotContain("Action.Http");

        // Acknowledged once, it does not ask again; taking it over still can.
        Verbs(TeamsBotCards.Alert(open with { AcknowledgedBy = "oncall@example.com" }, acting))
            .Should().Equal(TeamsBotVerbs.AssignToMe);
    }

    [Theory]
    [InlineData(IncidentState.Escalated, false, true)]
    [InlineData(IncidentState.Escalated, true, false)]
    [InlineData(IncidentState.Investigating, false, false)]
    [InlineData(IncidentState.AwaitingApproval, false, false)]
    [InlineData(IncidentState.Expired, false, false)]
    [InlineData(IncidentState.Closed, false, false)]
    [InlineData(IncidentState.Resolved, false, false)]
    public void Reinvestigate_is_drawn_where_the_console_offers_the_retry_and_the_card_is_still_live(
        IncidentState state, bool diagnosed, bool drawn)
    {
        // The console (IncidentDetail.razor, CanReinvestigate; IncidentDetailRetryTests) offers the
        // retry for an Escalated or Expired incident with no primary finding in any investigation.
        // Expired is over, and a card for an incident that is over is edited once and then left -
        // so of those two only Escalated gets a button. A diagnosed one never does: the banner
        // beside it would say "no diagnosis was produced" above the diagnosis.
        var incident = Incident(state: state) with { Diagnosed = diagnosed };

        incident.CanReinvestigate.Should().Be(drawn);

        var verbs = Verbs(TeamsBotCards.Alert(incident, Links with { Actions = true }));

        (verbs.Contains(TeamsBotVerbs.Reinvestigate)).Should().Be(drawn);
    }

    [Fact]
    public void Close_is_a_card_that_asks_why_and_is_drawn_only_when_somebody_may_close()
    {
        var acting = Links with { Actions = true };
        var incident = Incident();

        // Nobody mapped to the approver role: every click would be refused, so nothing is drawn.
        Verbs(TeamsBotCards.Alert(incident, acting)).Should().NotContain(TeamsBotVerbs.Close);
        TeamsBotCards.Alert(incident, acting).ToJsonString().Should().NotContain("Input.");

        var close = Actions(TeamsBotCards.Alert(incident, acting with { Closing = true }))
            .Single(a => a.GetProperty("type").GetString() == "Action.ShowCard");

        close.GetProperty("title").GetString().Should().Be("Close");

        // Unfolding it sends nothing. The reason is asked for before the button that does.
        var input = close.GetProperty("card").GetProperty("body")[0];

        input.GetProperty("type").GetString().Should().Be("Input.Text");
        input.GetProperty("id").GetString().Should().Be(TeamsBotVerbs.ReasonInput).And.Be("reason");
        input.GetProperty("isRequired").GetBoolean().Should().BeTrue();
        input.GetProperty("errorMessage").GetString().Should().NotBeNullOrWhiteSpace("a required input without one fails silently");
        input.GetProperty("maxLength").GetInt32().Should().Be(TeamsBotCards.MaxReasonLength);

        var send = close.GetProperty("card").GetProperty("actions").EnumerateArray().Single();

        send.GetProperty("type").GetString().Should().Be("Action.Execute");
        send.GetProperty("verb").GetString().Should().Be(TeamsBotVerbs.Close);
        send.GetProperty("data").GetProperty("incidentId").GetString().Should().Be(incident.Id.ToString());

        // The card is the same for everybody: nothing in it names who may press the button.
        send.GetProperty("data").EnumerateObject().Select(p => p.Name).Should().Equal("incidentId");
    }

    [Fact]
    public void Every_verb_a_button_can_send_has_a_handler_and_every_handler_has_a_button()
    {
        // A button whose verb the route does not know would be a click that does nothing. The
        // handler answers exactly TeamsBotVerbs.All, so every verb drawn must be in it - and a
        // verb nothing draws would be a route nobody can reach from a card, which is a way in
        // nobody reviews. Drawn anywhere in the card: its own row, or the card a button unfolds.
        var everyButton = Links with { Actions = true, Closing = true };

        var drawn = new[]
            {
                Incident(),
                Incident(state: IncidentState.AwaitingApproval),
                Incident(state: IncidentState.Investigating),
                Incident() with { Diagnosed = true },
            }
            .SelectMany(i => Verbs(TeamsBotCards.Alert(i, everyButton)))
            .Distinct()
            .ToList();

        drawn.Should().NotBeEmpty().And.OnlyContain(v => TeamsBotVerbs.All.Contains(v));
        TeamsBotVerbs.All.Should().OnlyContain(v => drawn.Contains(v), "every verb the route answers is one a card can send");

        TeamsBotVerbs.All.Should().BeEquivalentTo(
            [TeamsBotVerbs.Acknowledge, TeamsBotVerbs.AssignToMe, TeamsBotVerbs.Reinvestigate, TeamsBotVerbs.Close]);
        TeamsBotVerbs.Approver.Should().BeEquivalentTo([TeamsBotVerbs.Close]).And.BeSubsetOf(TeamsBotVerbs.All);
    }

    [Fact]
    public void The_same_alert_is_drawn_wherever_it_is_rendered()
    {
        // Posted by the channel, compared by the reconciler, answered by a click: three places,
        // one set of links. If they differed the comparison would edit the card back every tick.
        var options = new Hephaisto.Core.Notifications.NotificationOptions { BaseUrl = "https://hephaisto.example/" };

        TeamsBotLinks.Alert(options, null).Should().BeEquivalentTo(new TeamsCardLinks { BaseUrl = "https://hephaisto.example/" });

        options.TeamsBot.Actions.Approvers.Add("5f0c0000-0000-0000-0000-000000000001");
        TeamsBotLinks.Alert(options, null).Closing.Should().BeFalse("an approver is mapped, and the buttons are off");

        options.TeamsBot.Actions.Enabled = true;
        TeamsBotLinks.Alert(options, null).Should().BeEquivalentTo(
            new TeamsCardLinks { BaseUrl = "https://hephaisto.example/", Actions = true, Closing = true });

        options.TeamsBot.Actions.Approvers.Clear();
        TeamsBotLinks.Alert(options, null).Should().BeEquivalentTo(
            new TeamsCardLinks { BaseUrl = "https://hephaisto.example/", Actions = true },
            "with nobody mapped, Close would be a button every click on which is refused");
    }

    [Fact]
    public void An_alert_carries_the_start_of_the_team_note_and_a_link_to_all_of_it()
    {
        // #145: what the team wrote about this alert, before the person opens anything.
        var incident = Incident() with { AlertName = "Consumer Lag/High", NoteExcerpt = "Check the upstream feed first." };

        var alert = TeamsBotCards.Alert(incident, Links);

        Card(alert).ToString().Should().Contain("Team note: Check the upstream feed first.");

        var link = Actions(alert).Single(a => a.GetProperty("title").GetString() == "Alert note");

        link.GetProperty("type").GetString().Should().Be("Action.OpenUrl");
        link.GetProperty("url").GetString().Should().Be("https://hephaisto.example/alerts/Consumer%20Lag%2FHigh");
    }

    [Fact]
    public void An_alert_without_a_note_carries_neither_line_nor_link()
    {
        var alert = TeamsBotCards.Alert(Incident() with { AlertName = "ConsumerLagHigh" }, Links);

        Card(alert).ToString().Should().NotContain("Team note");
        Actions(alert).Select(a => a.GetProperty("title").GetString()).Should().NotContain("Alert note");
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

    /// <summary>Every <c>Action.Execute</c> anywhere in the card, however deep a button sits.</summary>
    private static List<JsonElement> Executes(JsonObject activity)
    {
        var found = new List<JsonElement>();

        Walk(Card(activity));

        return found;

        void Walk(JsonElement node)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    if (node.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && type.GetString() == "Action.Execute")
                    {
                        found.Add(node);
                    }

                    foreach (var property in node.EnumerateObject())
                    {
                        Walk(property.Value);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                    {
                        Walk(item);
                    }

                    break;
            }
        }
    }

    private static List<string> Verbs(JsonObject activity) =>
        [.. Executes(activity).Select(a => a.GetProperty("verb").GetString()!)];
}
