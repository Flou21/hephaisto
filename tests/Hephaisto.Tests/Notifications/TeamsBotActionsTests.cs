using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The one route Microsoft calls (#124). What is load-bearing is that every refusal happens
/// before anything changes: a token for another bot, a key endorsed for another channel, an
/// activity from another tenant or service, and a clicker who is not in the team.
/// </summary>
public sealed class TeamsBotActionsTests
{
    private const string AppId = "00000000-0000-0000-0000-0000000000d2";
    private const string Tenant = "00000000-0000-0000-0000-0000000000d1";
    private const string Issuer = "https://api.botframework.com";
    private const string ServiceUrl = "https://smba.example/teams/";
    private const string ObjectId = "5f0c0000-0000-0000-0000-000000000001";

    private static readonly Guid IncidentId = Guid.Parse("0192a6f0-0000-7000-8000-0000000000aa");
    private static readonly Guid ActionId = Guid.Parse("0192a6f0-0000-7000-8000-0000000000ac");

    private static readonly TeamsMember Member = new("29:oncall", ObjectId, "On Call", "oncall@example.com", "oncall@example.com");

    // ---------------------------------------------------------------------------------------
    // The token
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_for_this_bot_from_the_bot_framework_is_valid()
    {
        using var key = RSA.Create(2048);
        var token = Sign(key, "msteams-key", audience: AppId, issuer: Issuer);

        var result = await Validate(token, key, "msteams-key");

        result.IsValid.Should().BeTrue(result.Exception?.Message);
    }

    [Theory]
    [InlineData("another-bot", Issuer, "a token issued for another bot")]
    [InlineData(AppId, "https://login.example.com", "a token from another issuer")]
    public async Task A_token_for_something_else_is_refused(string audience, string issuer, string because)
    {
        using var key = RSA.Create(2048);
        var token = Sign(key, "msteams-key", audience, issuer);

        var result = await Validate(token, key, "msteams-key");

        result.IsValid.Should().BeFalse(because);
    }

    [Fact]
    public async Task A_token_signed_by_a_key_nobody_published_is_refused()
    {
        using var published = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var token = Sign(other, "msteams-key", AppId, Issuer);

        var result = await Validate(token, published, "msteams-key");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Only_a_key_endorsed_for_teams_may_sign_a_click()
    {
        // The key set is shared by every Bot Framework channel. A generic JWT library passes a
        // token signed by any of them; the endorsement is the check that makes it Teams.
        var keys = new JsonWebKeySet("""
            {"keys":[
              {"kty":"RSA","kid":"teams","n":"AQAB","e":"AQAB","endorsements":["msteams","skype"]},
              {"kty":"RSA","kid":"webchat","n":"AQAB","e":"AQAB","endorsements":["webchat"]},
              {"kty":"RSA","kid":"bare","n":"AQAB","e":"AQAB"}
            ]}
            """);

        BotFrameworkTokens.IsEndorsed(keys, "teams", "msteams").Should().BeTrue();
        BotFrameworkTokens.IsEndorsed(keys, "webchat", "msteams").Should().BeFalse("endorsed for another channel");
        BotFrameworkTokens.IsEndorsed(keys, "bare", "msteams").Should().BeFalse("endorsed for nothing is not endorsed for Teams");
        BotFrameworkTokens.IsEndorsed(keys, "unknown", "msteams").Should().BeFalse("a key id nobody published");
        BotFrameworkTokens.IsEndorsed(keys, null, "msteams").Should().BeFalse("a token without a key id");
        BotFrameworkTokens.IsEndorsed(null, "teams", "msteams").Should().BeFalse("no key set at all");
    }

    // ---------------------------------------------------------------------------------------
    // The click
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_click_by_a_member_acknowledges_as_the_roster_names_them_and_shows_the_card()
    {
        var (handler, target, _) = Handler();

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Acknowledge, displayName: "Somebody Else"), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.card.adaptive");

        // The display name in the click is the sender's to choose. The roster's is not.
        await target.Received(1).AcknowledgeAsync(IncidentId, "oncall@example.com", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Every_verb_a_button_can_send_changes_the_incident()
    {
        // Over TeamsBotVerbs.All itself, not a list beside it: a verb added there without a
        // handler would otherwise be a button that answers "not something a button here can do".
        TeamsBotVerbs.All.Should().NotBeEmpty();

        foreach (var verb in TeamsBotVerbs.All)
        {
            var (handler, target, _) = Handler(approvers: [ObjectId], approvals: true);

            var answer = await handler.HandleAsync(
                Caller(),
                Click(verb, reason: "the rollout finished", actionId: ActionId.ToString()),
                TestContext.Current.CancellationToken);

            answer.Status.Should().Be(200, verb);
            answer.Body!["statusCode"]!.GetValue<int>().Should().Be(200, verb);
            answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.card.adaptive", verb);
            target.ReceivedCalls().Should().Contain(c => c.GetMethodInfo().Name != nameof(ITeamsActionTarget.CardAsync), verb);
        }
    }

    [Fact]
    public async Task Assign_to_me_assigns_to_the_clicker()
    {
        var (handler, target, _) = Handler();

        await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.AssignToMe), TestContext.Current.CancellationToken);

        await target.Received(1).AssignToAsync(IncidentId, "oncall@example.com", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_token_for_another_service_is_refused_before_anything_changes()
    {
        var (handler, target, members) = Handler();

        var answer = await handler.HandleAsync(Caller(serviceUrl: "https://elsewhere.example/"), Click(TeamsBotVerbs.Acknowledge), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        target.ReceivedCalls().Should().BeEmpty();
        members.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_click_from_another_tenant_is_refused_before_anything_changes()
    {
        var (handler, target, members) = Handler();

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Acknowledge, tenant: "another-tenant"), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        target.ReceivedCalls().Should().BeEmpty();
        members.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_click_by_somebody_outside_the_team_is_refused()
    {
        var (handler, target, _) = Handler(member: new TeamsBotResult<TeamsMember>(HttpStatusCode.OK, null, "not a member"));

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Acknowledge), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_roster_nobody_could_read_is_not_a_yes()
    {
        var (handler, target, _) = Handler(member: new TeamsBotResult<TeamsMember>(HttpStatusCode.BadGateway, null, "down"));

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Acknowledge), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(503);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_click_without_an_object_id_is_refused()
    {
        var (handler, target, _) = Handler();
        var click = Click(TeamsBotVerbs.Acknowledge);
        click["from"]!.AsObject().Remove("aadObjectId");

        var answer = await handler.HandleAsync(Caller(), click, TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("reopen")]
    [InlineData("decide")]
    [InlineData("Approve")]
    [InlineData("Close")]
    [InlineData("")]
    public async Task A_verb_no_button_sends_changes_nothing(string verb)
    {
        // An approver with approvals on, so that what refuses these is the verb and nothing else.
        var (handler, target, _) = Handler(approvers: [ObjectId], approvals: true);

        var answer = await handler.HandleAsync(Caller(), Click(verb), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.error");
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Anything_that_is_not_a_click_changes_nothing()
    {
        var (handler, target, members) = Handler();
        var install = Click(TeamsBotVerbs.Acknowledge);
        install["type"] = "conversationUpdate";

        var answer = await handler.HandleAsync(Caller(), install, TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body.Should().BeNull();
        target.ReceivedCalls().Should().BeEmpty();
        members.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_closed_incident_says_nothing_changed()
    {
        var (handler, target, _) = Handler();
        target.AcknowledgeAsync(default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(
            new LifecycleResult { Outcome = LifecycleOutcome.IllegalState, Detail = "already closed" });
        target.CardAsync(default, TestContext.Current.CancellationToken).ReturnsForAnyArgs((JsonObject?)null);

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Acknowledge), TestContext.Current.CancellationToken);

        answer.Body!["value"]!.GetValue<string>().Should().Contain("Nothing changed").And.Contain("already closed");
    }

    // ---------------------------------------------------------------------------------------
    // Reinvestigate: read-level, like the console
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Any_member_may_ask_for_another_investigation_and_is_recorded_as_the_roster_names_them()
    {
        // Nobody is mapped to the approver role here, and it does not matter: asking for another
        // attempt carries no approver policy in the console either.
        var (handler, target, _) = Handler();

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Reinvestigate, displayName: "Somebody Else"), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.card.adaptive");
        await target.Received(1).ReinvestigateAsync(IncidentId, "oncall@example.com", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ReinvestigateOutcome.AlreadyRunning, "Nothing changed", "already running")]
    [InlineData(ReinvestigateOutcome.Disabled, "Nothing changed", "switched off")]
    [InlineData(ReinvestigateOutcome.IllegalState, "Nothing changed", "the detail")]
    [InlineData(ReinvestigateOutcome.QueueFull, "Not started yet", "the detail")]
    [InlineData(ReinvestigateOutcome.NotFound, "no longer exists", "incident")]
    public async Task Each_way_a_reinvestigation_does_not_start_is_said_in_words(
        ReinvestigateOutcome outcome, string says, string and)
    {
        var (handler, target, _) = Handler();
        target.ReinvestigateAsync(default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(new ReinvestigateResult
        {
            Outcome = outcome,
            Detail = outcome is ReinvestigateOutcome.AlreadyRunning ? "An investigation is already running for this incident." : "the detail",
        });

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Reinvestigate), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.activity.message");
        answer.Body!["value"]!.GetValue<string>().Should().Contain(says).And.Contain(and);

        // A message, never the card: a refreshed card would read as "it worked".
        await target.DidNotReceiveWithAnyArgs().CardAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_saturated_queue_is_not_reported_as_nothing_changed()
    {
        // The one outcome where something did change: the incident is marked Investigating and
        // nothing is working it yet. "Nothing changed" would be untrue.
        var (handler, target, _) = Handler();
        target.ReinvestigateAsync(default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(
            new ReinvestigateResult { Outcome = ReinvestigateOutcome.QueueFull, Detail = "The investigation queue is saturated." });

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Reinvestigate), TestContext.Current.CancellationToken);

        answer.Body!["value"]!.GetValue<string>().Should().Contain("saturated").And.NotContain("Nothing changed");
    }

    [Fact]
    public async Task A_reinvestigation_the_console_would_forbid_the_actor_is_refused()
    {
        var (handler, target, _) = Handler();
        target.ReinvestigateAsync(default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(
            new ReinvestigateResult { Outcome = ReinvestigateOutcome.ForbiddenActor, Detail = "may not" });

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Reinvestigate), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        answer.Body.Should().BeNull();
    }

    [Fact]
    public void Every_reinvestigate_outcome_has_an_answer_of_its_own()
    {
        // The theory above names them by hand. A new outcome must be decided, not defaulted into
        // the 403 that only a forbidden actor should get.
        Enum.GetValues<ReinvestigateOutcome>().Should().BeEquivalentTo(
        [
            ReinvestigateOutcome.Queued,
            ReinvestigateOutcome.NotFound,
            ReinvestigateOutcome.AlreadyRunning,
            ReinvestigateOutcome.IllegalState,
            ReinvestigateOutcome.QueueFull,
            ReinvestigateOutcome.Disabled,
            ReinvestigateOutcome.ForbiddenActor,
        ]);
    }

    // ---------------------------------------------------------------------------------------
    // Close: an approver's, as in the console
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_approvers_click_closes_with_the_reason_from_the_card_as_the_roster_names_them()
    {
        var (handler, target, log) = Recording(approvers: [ObjectId]);

        var answer = await handler.HandleAsync(
            Caller(),
            Click(TeamsBotVerbs.Close, displayName: "Somebody Else", reason: "  the rollout finished  "),
            TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.card.adaptive");
        await target.Received(1).CloseAsync(IncidentId, "oncall@example.com", "the rollout finished", Arg.Any<CancellationToken>());
        log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task The_approver_map_is_matched_whatever_case_the_id_was_written_in()
    {
        var (handler, target, _) = Handler(approvers: [" " + ObjectId.ToUpperInvariant() + " "]);

        await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Close, reason: "done"), TestContext.Current.CancellationToken);

        await target.ReceivedWithAnyArgs(1).CloseAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("5f0c0000-0000-0000-0000-0000000000ff")]
    public async Task A_member_who_is_not_an_approver_cannot_close_and_is_told_why(string? somebodyElse)
    {
        // In the team, so the route believes who they are - and not on the list, or the list is
        // empty, which is the default and means nobody.
        var (handler, target, log) = Recording(approvers: somebodyElse is null ? [] : [somebodyElse]);

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Close, reason: "done"), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200, "the person has to be able to read why");
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.activity.message");
        answer.Body!["value"]!.GetValue<string>().Should()
            .Contain("approver role")
            .And.Contain("notifications.teamsBot.actions.approvers")
            .And.Contain("Nothing was changed");

        target.ReceivedCalls().Should().BeEmpty();
        log.Warnings.Should().ContainSingle().Which.Should().Contain("oncall@example.com").And.Contain("close");
    }

    [Fact]
    public async Task Being_on_the_approver_list_does_not_replace_being_in_the_team()
    {
        var (handler, target, _) = Handler(
            member: new TeamsBotResult<TeamsMember>(HttpStatusCode.OK, null, "not a member"),
            approvers: [ObjectId]);

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Close, reason: "done"), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u202e \u0007")]
    public async Task A_close_without_a_reason_changes_nothing_and_says_so(string? reason)
    {
        // The card requires it; a card is not the only thing that can send an invoke.
        var (handler, target, log) = Recording(approvers: [ObjectId]);

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Close, reason: reason), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.activity.message");
        answer.Body!["value"]!.GetValue<string>().Should().Contain("Give a reason").And.Contain("Nothing was changed");
        target.ReceivedCalls().Should().BeEmpty();
        log.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task A_reason_is_cut_to_what_the_card_would_have_allowed()
    {
        var (handler, target, _) = Handler(approvers: [ObjectId]);

        await handler.HandleAsync(
            Caller(),
            Click(TeamsBotVerbs.Close, reason: new string('x', TeamsBotCards.MaxReasonLength + 200)),
            TestContext.Current.CancellationToken);

        await target.Received(1).CloseAsync(IncidentId, "oncall@example.com", new string('x', TeamsBotCards.MaxReasonLength), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Closing_what_is_already_closed_says_nothing_changed()
    {
        var (handler, target, _) = Handler(approvers: [ObjectId]);
        target.CloseAsync(default, default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(
            new LifecycleResult { Outcome = LifecycleOutcome.IllegalState, Detail = "already closed" });
        target.CardAsync(default, TestContext.Current.CancellationToken).ReturnsForAnyArgs((JsonObject?)null);

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Close, reason: "done"), TestContext.Current.CancellationToken);

        answer.Body!["value"]!.GetValue<string>().Should().Contain("Nothing changed").And.Contain("already closed");
    }

    [Fact]
    public void Nobody_is_an_approver_until_somebody_is_named()
    {
        var actions = new TeamsBotActionsOptions();

        actions.Approvers.Should().BeEmpty();
        actions.IsApprover(ObjectId).Should().BeFalse();
        actions.IsApprover(null).Should().BeFalse();
        actions.IsApprover(" ").Should().BeFalse();

        actions.Approvers.Add(ObjectId);
        actions.IsApprover(ObjectId.ToUpperInvariant()).Should().BeTrue();
        actions.IsApprover("5f0c0000-0000-0000-0000-0000000000ff").Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------
    // Approve and deny: an approver's, and only when switched on
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_approvers_approve_reaches_the_approval_and_only_the_approval()
    {
        var (handler, target, log) = Recording(approvers: [ObjectId], approvals: true);

        var answer = await handler.HandleAsync(
            Caller(),
            Click(TeamsBotVerbs.Approve, displayName: "Somebody Else", actionId: ActionId.ToString()),
            TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.card.adaptive");
        await target.Received(1).ApproveAsync(IncidentId, ActionId, "oncall@example.com", Arg.Any<CancellationToken>());
        await target.DidNotReceiveWithAnyArgs().DenyAsync(default, default, default!, TestContext.Current.CancellationToken);
        log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task An_approvers_deny_reaches_the_denial_and_never_the_approval()
    {
        var (handler, target, _) = Handler(approvers: [ObjectId], approvals: true);

        // A field that says "approve" beside the verb "deny" is not a thing the route reads: the
        // verb is the decision, so nothing in the data can turn a denial into an approval.
        var click = Click(TeamsBotVerbs.Deny, actionId: ActionId.ToString());
        click["value"]!["action"]!["data"]!["approve"] = true;

        var answer = await handler.HandleAsync(Caller(), click, TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        await target.Received(1).DenyAsync(IncidentId, ActionId, "oncall@example.com", Arg.Any<CancellationToken>());
        await target.DidNotReceiveWithAnyArgs().ApproveAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(TeamsBotVerbs.Approve)]
    [InlineData(TeamsBotVerbs.Deny)]
    public async Task With_approvals_off_a_forged_click_is_refused_even_from_an_approver(string verb)
    {
        // The default. No card draws the buttons then, and a card is not the only thing that can
        // send an invoke - so the route refuses on its own.
        var (handler, target, log) = Recording(approvers: [ObjectId]);

        var answer = await handler.HandleAsync(Caller(), Click(verb, actionId: ActionId.ToString()), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.activity.message");
        answer.Body!["value"]!.GetValue<string>().Should()
            .Contain("switched off")
            .And.Contain("notifications.teamsBot.actions.approvals.enabled")
            .And.Contain("Nothing was changed");
        target.ReceivedCalls().Should().BeEmpty();
        log.Warnings.Should().ContainSingle().Which.Should().Contain("oncall@example.com").And.Contain(verb);
    }

    [Theory]
    [InlineData(TeamsBotVerbs.Approve, null)]
    [InlineData(TeamsBotVerbs.Deny, null)]
    [InlineData(TeamsBotVerbs.Approve, "5f0c0000-0000-0000-0000-0000000000ff")]
    [InlineData(TeamsBotVerbs.Deny, "5f0c0000-0000-0000-0000-0000000000ff")]
    public async Task A_member_who_is_not_an_approver_cannot_decide_an_action(string verb, string? somebodyElse)
    {
        var (handler, target, log) = Recording(approvers: somebodyElse is null ? [] : [somebodyElse], approvals: true);

        var answer = await handler.HandleAsync(Caller(), Click(verb, actionId: ActionId.ToString()), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["value"]!.GetValue<string>().Should()
            .Contain("approver role")
            .And.Contain("notifications.teamsBot.actions.approvers")
            .And.Contain("Nothing was changed");
        target.ReceivedCalls().Should().BeEmpty();
        log.Warnings.Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task A_decision_that_names_no_action_changes_nothing(string? actionId)
    {
        var (handler, target, log) = Recording(approvers: [ObjectId], approvals: true);

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Approve, actionId: actionId), TestContext.Current.CancellationToken);

        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.error");
        answer.Body!["value"]!["message"]!.GetValue<string>().Should().Contain("named no action");
        target.ReceivedCalls().Should().BeEmpty();
        log.Warnings.Should().ContainSingle();
    }

    [Theory]
    [InlineData(TeamsBotVerbs.Approve)]
    [InlineData(TeamsBotVerbs.Deny)]
    public async Task A_click_on_a_stale_card_is_told_it_was_already_decided(string verb)
    {
        // A card stays where it was posted. Three people can press Approve on the same action;
        // the second and third must be told, and must not see a card that reads as "done".
        var (handler, target, _) = Handler(approvers: [ObjectId], approvals: true);
        var stale = new ApprovalResult { Outcome = ApprovalOutcome.NotAwaitingApproval, Detail = "This action is Executed, not awaiting approval." };
        target.ApproveAsync(default, default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(stale);
        target.DenyAsync(default, default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(stale);

        var answer = await handler.HandleAsync(Caller(), Click(verb, actionId: ActionId.ToString()), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.activity.message");
        answer.Body!["value"]!.GetValue<string>().Should()
            .Contain("Already decided")
            .And.Contain("This action is Executed")
            .And.Contain("Nothing was changed");
        await target.DidNotReceiveWithAnyArgs().CardAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_action_that_no_longer_exists_says_so()
    {
        var (handler, target, _) = Handler(approvers: [ObjectId], approvals: true);
        target.ApproveAsync(default, default, default!, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(new ApprovalResult { Outcome = ApprovalOutcome.NotFound });

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Approve, actionId: ActionId.ToString()), TestContext.Current.CancellationToken);

        answer.Body!["value"]!.GetValue<string>().Should().Contain("no longer exists").And.Contain("Nothing was changed");
    }

    [Fact]
    public async Task An_approval_that_was_recorded_and_then_did_not_run_is_not_called_nothing()
    {
        // Admission refused it, or the API call failed - after the row saying who approved was
        // committed. "Nothing was changed" would be untrue, and "approved" alone would be too.
        var (handler, target, _) = Handler(approvers: [ObjectId], approvals: true);
        target.ApproveAsync(default, default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(
            new ApprovalResult { Outcome = ApprovalOutcome.ExecutionRefused, Detail = "the action budget for this workload is spent" });

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Approve, actionId: ActionId.ToString()), TestContext.Current.CancellationToken);

        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.activity.message");
        answer.Body!["value"]!.GetValue<string>().Should()
            .Contain("approval is recorded")
            .And.Contain("did not run")
            .And.Contain("budget")
            .And.NotContain("Nothing was changed");
    }

    [Fact]
    public async Task A_dry_run_is_said_to_be_one_when_there_is_no_card_to_show_it()
    {
        var (handler, target, _) = Handler(approvers: [ObjectId], approvals: true);
        target.ApproveAsync(default, default, default!, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(new ApprovalResult { Outcome = ApprovalOutcome.Executed, DryRun = true });
        target.CardAsync(default, TestContext.Current.CancellationToken).ReturnsForAnyArgs((JsonObject?)null);

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Approve, actionId: ActionId.ToString()), TestContext.Current.CancellationToken);

        answer.Body!["value"]!.GetValue<string>().Should().Contain("dry run");
    }

    [Fact]
    public async Task A_decision_the_console_would_forbid_the_actor_is_refused()
    {
        var (handler, target, _) = Handler(approvers: [ObjectId], approvals: true);
        target.ApproveAsync(default, default, default!, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(new ApprovalResult { Outcome = ApprovalOutcome.ForbiddenActor, Detail = "a model identity" });

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Approve, actionId: ActionId.ToString()), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        answer.Body.Should().BeNull();
    }

    [Fact]
    public void Every_approval_outcome_has_an_answer_of_its_own()
    {
        // A new outcome must be decided, not defaulted into the 403 only a forbidden actor gets.
        Enum.GetValues<ApprovalOutcome>().Should().BeEquivalentTo(
        [
            ApprovalOutcome.Executed,
            ApprovalOutcome.Denied,
            ApprovalOutcome.NotFound,
            ApprovalOutcome.NotAwaitingApproval,
            ApprovalOutcome.ExecutionRefused,
            ApprovalOutcome.ForbiddenActor,
        ]);
    }

    [Fact]
    public void An_approval_from_a_card_is_recorded_as_having_come_from_teams()
    {
        // The source is stored by name. Renaming the member would orphan every row written with it.
        Hephaisto.Core.Domain.ApprovalSource.Teams.ToString().Should().Be("Teams");
        ((int)Hephaisto.Core.Domain.ApprovalSource.Teams).Should().Be(5);

        Enum.GetNames<Hephaisto.Core.Domain.ApprovalSource>().Should().Equal("NotApplicable", "Ui", "Api", "Auto", "Oidc", "Teams");
    }

    // ---------------------------------------------------------------------------------------
    // Startup
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Off_by_default_registers_nothing()
    {
        new TeamsBotActionsOptions().Enabled.Should().BeFalse();

        var services = new ServiceCollection();
        services.AddHephaistoTeamsBotActions(Config());

        services.Should().NotContain(d => d.ServiceType == typeof(TeamsBotActionHandler));
    }

    [Fact]
    public void Buttons_without_a_bot_refuse_to_start()
    {
        var act = () => new ServiceCollection().AddHephaistoTeamsBotActions(Config(("Notifications:TeamsBot:Actions:Enabled", "true")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not configured*");
    }

    [Theory]
    [InlineData("8080", "0")]
    [InlineData("8081", "8081")]
    public void The_actions_port_must_be_its_own(string port, string webhookPort)
    {
        var act = () => new ServiceCollection().AddHephaistoTeamsBotActions(Config(
            [.. Bot(), ("Notifications:TeamsBot:Actions:Enabled", "true"),
             ("Notifications:TeamsBot:Actions:Port", port), ("Web:WebhookPort", webhookPort)]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*port of its own*");
    }

    [Theory]
    [InlineData("oncall@example.com")]
    [InlineData("On Call")]
    [InlineData("{5f0c0000-0000-0000-0000-000000000001}")]
    [InlineData("5f0c0000000000000000000000000001")]
    [InlineData("")]
    public void An_approver_that_is_not_an_object_id_refuses_to_start(string approver)
    {
        // A click is matched by from.aadObjectId. An address or a name would map nobody, and a
        // list that names nobody looks like one that works until somebody has to close something.
        var act = () => new ServiceCollection().AddHephaistoTeamsBotActions(Config(
            [.. Bot(), ("Notifications:TeamsBot:Actions:Enabled", "true"),
             ("Notifications:TeamsBot:Actions:Approvers:0", ObjectId),
             ("Notifications:TeamsBot:Actions:Approvers:1", approver)]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Approvers*object id*");
    }

    [Fact]
    public void Approvers_are_read_from_the_configuration_the_chart_writes()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var config = Config(
            [.. Bot(), ("Notifications:TeamsBot:Actions:Enabled", "true"),
             ("Notifications:TeamsBot:Actions:Approvers:0", ObjectId),
             ("Notifications:TeamsBot:Actions:Approvers:1", "5F0C0000-0000-0000-0000-0000000000FF")]);

        services.AddHephaistoTeamsBotActions(config);
        services.Should().Contain(d => d.ServiceType == typeof(TeamsBotActionHandler));

        var bound = config.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>()!.TeamsBot.Actions;

        bound.Approvers.Should().HaveCount(2);
        bound.IsApprover(ObjectId).Should().BeTrue();
        bound.IsApprover("5f0c0000-0000-0000-0000-0000000000ff").Should().BeTrue();
    }

    [Fact]
    public void Approvals_are_off_until_asked_for()
    {
        new TeamsBotActionsOptions().Approvals.Enabled.Should().BeFalse();
        new TeamsBotActionsOptions { Enabled = true, Approvers = [ObjectId] }.Approvals.Enabled.Should().BeFalse(
            "the buttons and an approver do not turn approvals on");
    }

    [Fact]
    public void Approvals_without_the_buttons_refuse_to_start()
    {
        // Checked before the early return for "actions off", which is where a switch that reads
        // as on and does nothing would otherwise hide.
        var act = () => new ServiceCollection().AddHephaistoTeamsBotActions(Config(
            [.. Bot(), ("Notifications:TeamsBot:Actions:Approvals:Enabled", "true"),
             ("Notifications:TeamsBot:Actions:Approvers:0", ObjectId)]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Approvals:Enabled*Actions:Enabled*nothing for a click to reach*");
    }

    [Fact]
    public void Approvals_with_nobody_mapped_refuse_to_start()
    {
        var act = () => new ServiceCollection().AddHephaistoTeamsBotActions(Config(
            [.. Bot(), ("Notifications:TeamsBot:Actions:Enabled", "true"),
             ("Notifications:TeamsBot:Actions:Approvals:Enabled", "true")]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Approvals:Enabled*Approvers*empty*");
    }

    [Fact]
    public void Approvals_with_the_buttons_and_an_approver_start()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var config = Config(
            [.. Bot(), ("Notifications:TeamsBot:Actions:Enabled", "true"),
             ("Notifications:TeamsBot:Actions:Approvals:Enabled", "true"),
             ("Notifications:TeamsBot:Actions:Approvers:0", ObjectId)]);

        services.AddHephaistoTeamsBotActions(config);

        services.Should().Contain(d => d.ServiceType == typeof(TeamsBotActionHandler));
        config.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>()!
            .TeamsBot.Actions.Approvals.Enabled.Should().BeTrue();
    }

    [Fact]
    public void A_configured_bot_with_its_own_port_registers_the_route()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHephaistoTeamsBotActions(Config([.. Bot(), ("Notifications:TeamsBot:Actions:Enabled", "true")]));

        services.Should().Contain(d => d.ServiceType == typeof(TeamsBotActionHandler));
    }

    // ---------------------------------------------------------------------------------------

    private static (TeamsBotActionHandler Handler, ITeamsActionTarget Target, ITeamsMemberDirectory Members) Handler(
        TeamsBotResult<TeamsMember>? member = null,
        string[]? approvers = null,
        bool approvals = false)
    {
        var (handler, target, members, _) = Build(member, approvers, approvals);

        return (handler, target, members);
    }

    /// <summary>The same handler, with what it logged: a refusal is one Warning line.</summary>
    private static (TeamsBotActionHandler Handler, ITeamsActionTarget Target, RecordingLogger Log) Recording(
        string[]? approvers = null,
        bool approvals = false)
    {
        var (handler, target, _, log) = Build(null, approvers, approvals);

        return (handler, target, log);
    }

    private static (TeamsBotActionHandler, ITeamsActionTarget, ITeamsMemberDirectory, RecordingLogger) Build(
        TeamsBotResult<TeamsMember>? member,
        string[]? approvers,
        bool approvals)
    {
        var options = Substitute.For<IOptionsMonitor<NotificationOptions>>();
        options.CurrentValue.Returns(new NotificationOptions
        {
            TeamsBot = new TeamsBotOptions
            {
                TenantId = Tenant,
                AppId = AppId,
                Actions = new TeamsBotActionsOptions
                {
                    Enabled = true,
                    Approvers = [.. approvers ?? []],
                    Approvals = new TeamsBotApprovalsOptions { Enabled = approvals },
                },
            },
        });

        var members = Substitute.For<ITeamsMemberDirectory>();
        members.FindAsync(default!, default).ReturnsForAnyArgs(member ?? new TeamsBotResult<TeamsMember>(HttpStatusCode.OK, Member, null));

        var target = Substitute.For<ITeamsActionTarget>();
        target.AcknowledgeAsync(default, default!, default).ReturnsForAnyArgs(new LifecycleResult { Outcome = LifecycleOutcome.Applied });
        target.AssignToAsync(default, default!, default).ReturnsForAnyArgs(new LifecycleResult { Outcome = LifecycleOutcome.Applied });
        target.ReinvestigateAsync(default, default!, default).ReturnsForAnyArgs(new ReinvestigateResult { Outcome = ReinvestigateOutcome.Queued });
        target.CloseAsync(default, default!, default!, default).ReturnsForAnyArgs(new LifecycleResult { Outcome = LifecycleOutcome.Applied });
        target.ApproveAsync(default, default, default!, default).ReturnsForAnyArgs(new ApprovalResult { Outcome = ApprovalOutcome.Executed });
        target.DenyAsync(default, default, default!, default).ReturnsForAnyArgs(new ApprovalResult { Outcome = ApprovalOutcome.Denied });
        target.CardAsync(default, default).ReturnsForAnyArgs(new JsonObject { ["type"] = "AdaptiveCard" });

        var log = new RecordingLogger();

        return (new TeamsBotActionHandler(options, members, target, log), target, members, log);
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<TeamsBotActionHandler>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private static ClaimsPrincipal Caller(string serviceUrl = ServiceUrl) =>
        new(new ClaimsIdentity([new Claim(BotFrameworkTokens.ServiceUrlClaim, serviceUrl)], BotFrameworkTokens.Scheme));

    /// <summary>
    /// A click as Teams sends it. <paramref name="reason"/> is what the Close card's input held:
    /// Teams merges an input into the button's data under the input's id.
    /// <paramref name="actionId"/> is what an Approve or Deny button carries.
    /// </summary>
    private static JsonObject Click(
        string verb,
        string tenant = Tenant,
        string displayName = "On Call",
        string? reason = null,
        string? actionId = null)
    {
        var data = new JsonObject { ["incidentId"] = IncidentId.ToString() };

        if (reason is not null)
        {
            data[TeamsBotVerbs.ReasonInput] = reason;
        }

        if (actionId is not null)
        {
            data[TeamsBotVerbs.ActionId] = actionId;
        }

        return new JsonObject
        {
            ["type"] = "invoke",
            ["name"] = "adaptiveCard/action",
            ["channelId"] = "msteams",
            ["serviceUrl"] = ServiceUrl.TrimEnd('/'),
            ["from"] = new JsonObject { ["id"] = "29:oncall", ["aadObjectId"] = ObjectId, ["name"] = displayName },
            ["conversation"] = new JsonObject { ["id"] = "a:oncall" },
            ["channelData"] = new JsonObject { ["tenant"] = new JsonObject { ["id"] = tenant } },
            ["value"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "Action.Execute",
                    ["verb"] = verb,
                    ["data"] = data,
                },
            },
        };
    }

    private static (string, string?)[] Bot() =>
    [
        ("Notifications:TeamsBot:TenantId", Tenant),
        ("Notifications:TeamsBot:AppId", AppId),
        ("Notifications:TeamsBot:ClientSecret", "not-a-secret"),
        ("Notifications:TeamsBot:ChannelId", "19:c@thread.tacv2"),
    ];

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static string Sign(RSA key, string kid, string audience, string issuer)
    {
        var handler = new JsonWebTokenHandler();

        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Claims = new Dictionary<string, object> { [BotFrameworkTokens.ServiceUrlClaim] = ServiceUrl },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        });
    }

    private static Task<TokenValidationResult> Validate(string token, RSA published, string kid)
    {
        var parameters = BotFrameworkTokens.Parameters(AppId, Issuer);
        parameters.IssuerSigningKey = new RsaSecurityKey(published.ExportParameters(false)) { KeyId = kid };

        return new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
    }
}
