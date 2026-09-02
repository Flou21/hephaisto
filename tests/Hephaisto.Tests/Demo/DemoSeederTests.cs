using Hephaisto.Agent.Demo;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Demo;

/// <summary>
/// The things about the demo seed that are not obvious, and that fail silently.
/// </summary>
public class DemoSeederTests
{
    private static Transcript Sample(bool sound = true, string verdict = "Correct")
    {
        var opened = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        return new Transcript
        {
            CassetteId = "c4",
            Description = "a bad image tag",
            ExpectedRootCause = "the tag does not exist",
            Incident = new Incident
            {
                OpenedAt = opened,
                LastSignalAt = opened.AddMinutes(5),
                Signals = [new Signal { FirstSeen = opened, LastSeen = opened.AddMinutes(5) }],
            },
            Investigation = new Investigation
            {
                StartedAt = opened.AddMinutes(1),
                CompletedAt = opened.AddMinutes(3),
                Steps = [new InvestigationStep { At = opened.AddMinutes(2) }],
            },
            Blobs =
            [
                new EvidenceBlob
                {
                    CreatedAt = opened.AddMinutes(2),
                    ExpiresAt = opened.AddMinutes(2).AddDays(30),
                },
            ],
            Score = new TranscriptGrade { Verdict = verdict, StructurallySound = sound },
            Origin = new TranscriptOrigin
            {
                ModelId = "gpt-oss:120b",
                RecordedAgainstModelId = "gemini-3.7-flash",
                RecordedAt = opened,
                AgentVersion = "0.6.0",
            },
        };
    }

    /// <summary>
    /// A finished incident as `hephaisto-eval export` writes it: a state that was reached, the
    /// transitions that reached it, and an action that was actually decided on.
    /// </summary>
    private static Transcript Captured(
        IncidentState state = IncidentState.Resolved,
        PolicyDecision decision = PolicyDecision.Allow)
    {
        var t = Sample();
        var opened = t.Incident.OpenedAt;

        t.Incident.State = state;
        t.Incident.EscalationReason = state is IncidentState.Escalated
            ? EscalationReason.PolicyDenied
            : EscalationReason.None;

        t.Incident.ResolvedAt = state is IncidentState.Resolved ? opened.AddMinutes(9) : null;

        t.Incident.Events.Add(new IncidentEvent
        {
            IncidentId = t.Incident.Id,
            From = null,
            To = IncidentState.Detected,
            At = opened,
            Reason = "detected",
        });

        t.Incident.Events.Add(new IncidentEvent
        {
            IncidentId = t.Incident.Id,
            From = IncidentState.Investigating,
            To = state,
            At = opened.AddMinutes(9),
            Reason = "the agent got there",
        });

        t.Investigation.Plan = new ActionPlan
        {
            CreatedAt = opened.AddMinutes(4),
            Actions =
            [
                new AgentAction
                {
                    Type = ActionType.RestartPod,
                    Target = new TargetRef { Namespace = "hephaisto-chaos", Kind = "Pod", Name = "c13-1" },
                    Risk = RiskTier.Low,
                    Decision = decision,
                    State = decision is PolicyDecision.Allow ? ActionState.Executed : ActionState.Denied,
                    ApprovedAt = decision is PolicyDecision.Allow ? opened.AddMinutes(5) : null,
                    ExecutedAt = decision is PolicyDecision.Allow ? opened.AddMinutes(6) : null,
                },
            ],
        };

        return t with { Origin = t.Origin with { Capture = TranscriptCapture.Cluster } };
    }

    /// <summary>
    /// This failure does not happen on the day it ships. RetentionService deletes blobs on
    /// `ExpiresAt &lt;= now OR CreatedAt &lt;= now - retention`, so a transcript recorded longer
    /// ago than the retention window loses its evidence on the first sweep after boot - leaving
    /// a demo of the provenance chain whose every "view raw" link 404s.
    /// </summary>
    [Fact]
    public void Rebasing_moves_both_arms_of_the_retention_predicate()
    {
        var t = Sample();
        var shift = TimeSpan.FromDays(400);
        var blob = t.Blobs[0];
        var createdBefore = blob.CreatedAt;
        var expiresBefore = blob.ExpiresAt;

        DemoSeeder.Rebase(t.Incident, t.Investigation, t.Blobs, shift);

        Assert.Equal(createdBefore + shift, blob.CreatedAt);
        Assert.Equal(expiresBefore + shift, blob.ExpiresAt);
    }

    [Fact]
    public void Rebasing_preserves_every_interval()
    {
        // The page renders time to diagnosis and the gaps between steps. Shifting fields
        // independently, or clamping any of them to "now", would invent those numbers.
        var t = Sample();
        var before = t.Investigation.CompletedAt!.Value - t.Incident.OpenedAt;

        DemoSeeder.Rebase(t.Incident, t.Investigation, t.Blobs, TimeSpan.FromDays(400));

        Assert.Equal(before, t.Investigation.CompletedAt!.Value - t.Incident.OpenedAt);
    }

    [Fact]
    public void The_latest_moment_considers_every_clock_in_the_graph()
    {
        // Anchoring on the incident alone would leave steps or blobs in the future.
        var t = Sample();

        var latest = DemoSeeder.Latest(t.Incident, t.Investigation, t.Blobs);

        Assert.Equal(t.Incident.LastSignalAt, latest);
    }

    [Fact]
    public void Every_seeded_row_says_it_is_a_recording()
    {
        var provenance = DemoSeeder.Provenance(Sample());

        Assert.Contains("DEMO DATA", provenance, StringComparison.Ordinal);
        Assert.Contains("c4", provenance, StringComparison.Ordinal);
        Assert.Contains("gpt-oss:120b", provenance, StringComparison.Ordinal);
    }

    /// <summary>
    /// Replay serves a recorded tool trace to a live model, so a model reaching for a call the
    /// recording lacks takes a miss, and a high miss rate produces a bad diagnosis for an
    /// instrument reason rather than a reasoning one - backlog #55. Showing the verdict without
    /// the caveat blames the agent for the corpus.
    /// </summary>
    [Fact]
    public void An_unsound_replay_says_so_rather_than_blaming_the_agent()
    {
        var unsound = DemoSeeder.Provenance(Sample(sound: false, verdict: "NoFinding"));
        var sound = DemoSeeder.Provenance(Sample());

        Assert.Contains("structurally unsound", unsound, StringComparison.Ordinal);
        Assert.DoesNotContain("structurally unsound", sound, StringComparison.Ordinal);
    }

    /// <summary>
    /// A recorded incident keeps the state it actually reached.
    /// </summary>
    /// <remarks>
    /// The seeder used to clear Actions and Events and then assert Escalated over the top,
    /// which was true of the ten replayed transcripts and false of anything exported from a
    /// cluster. A page that says the agent escalated when it resolved is worse than one that
    /// says nothing.
    /// </remarks>
    [Theory]
    [InlineData(IncidentState.Resolved)]
    [InlineData(IncidentState.Escalated)]
    public void A_recorded_incident_keeps_its_own_state_and_transitions(IncidentState state)
    {
        var t = Captured(state);

        DemoSeeder.PrepareForSeed(t, t.Incident.OpenedAt.AddMinutes(9));

        t.Incident.State.Should().Be(state);
        t.Incident.Events.Should().HaveCount(2, "the recorded transitions are not replaced by composed ones");
        t.Investigation.Plan!.Actions.Should().ContainSingle("a recorded action is not cleared away");
    }

    /// <summary>The regression guard for the ten replayed transcripts.</summary>
    [Fact]
    public void A_replayed_transcript_still_gets_the_three_synthesised_transitions()
    {
        var t = Sample();

        DemoSeeder.PrepareForSeed(t, t.Incident.OpenedAt.AddMinutes(5));

        t.Incident.State.Should().Be(IncidentState.Escalated);
        t.Incident.Events.Should().HaveCount(3);
        t.Incident.Events[0].Reason.Should().Contain("DEMO DATA");
    }

    /// <summary>
    /// A replay is never reported as policy-denied, because no policy engine ran on one.
    /// </summary>
    [Fact]
    public void A_replay_with_a_plan_is_not_reported_as_policy_denied()
    {
        var t = Sample();
        t.Investigation.Plan = new ActionPlan { CreatedAt = t.Incident.OpenedAt.AddMinutes(2) };

        DemoSeeder.PrepareForSeed(t, t.Incident.OpenedAt.AddMinutes(5));

        t.Incident.EscalationReason.Should().NotBe(EscalationReason.PolicyDenied,
            "the demo stack constructs no policy engine, so it cannot have denied anything - and "
            + "this label sat beside a genuinely denied cluster capture in the same list");
    }

    [Fact]
    public void The_latest_moment_considers_the_resolution_and_the_execution()
    {
        // Both run PAST the investigation: an incident resolves after the loop finishes. Anchor
        // on the old candidate set and the rebase puts them in the future.
        var t = Captured();

        var latest = DemoSeeder.Latest(t.Incident, t.Investigation, t.Blobs);

        latest.Should().Be(t.Incident.ResolvedAt!.Value);
        latest.Should().BeAfter(t.Investigation.CompletedAt!.Value);
    }

    [Fact]
    public void Rebasing_moves_the_transitions_and_the_execution_stamps()
    {
        var t = Captured();
        var shift = TimeSpan.FromDays(400);
        var action = t.Investigation.Plan!.Actions[0];

        var transitionBefore = t.Incident.Events[1].At;
        var executedBefore = action.ExecutedAt!.Value;
        var planBefore = t.Investigation.Plan.CreatedAt;

        DemoSeeder.Rebase(t.Incident, t.Investigation, t.Blobs, shift);

        t.Incident.Events[1].At.Should().Be(transitionBefore + shift);
        action.ExecutedAt.Should().Be(executedBefore + shift);
        t.Investigation.Plan.CreatedAt.Should().Be(planBefore + shift);
    }

    [Fact]
    public void A_live_capture_says_so_rather_than_claiming_a_cassette()
    {
        var provenance = DemoSeeder.Provenance(Captured());

        provenance.Should().Contain("LIVE CAPTURE");
        provenance.Should().NotContain("replayed from cassette");
        provenance.Should().Contain("not counted in its score",
            "the published accuracy figure is over the replayed corpus and must stay that way");
    }
}
