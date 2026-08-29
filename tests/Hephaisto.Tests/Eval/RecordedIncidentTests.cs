using Hephaisto.Agent.Investigations;
using Hephaisto.Core.Domain;
using Hephaisto.Eval;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// Whether a recorded incident reproduces the prompt the recording was made against.
/// </summary>
/// <remarks>
/// <see cref="A_rebuilt_incident_composes_a_byte_identical_incident_card"/> is the load-bearing
/// test. A cassette replaces the cluster, not the incident - and the incident card is a whole
/// section of every system prompt. If replay composed a thinner card than recording did, every
/// experiment would attribute part of the difference between two arms to the change under test
/// when it actually came from the prompt losing its signal list.
/// </remarks>
public class RecordedIncidentTests
{
    private static Incident Full()
    {
        var incident = new Incident
        {
            Title = "hephaisto-chaos/c7-configerror is not starting",
            Kind = SignalKind.ConfigError,
            Severity = Severity.Critical,
            OpenedAt = DateTimeOffset.Parse("2026-08-28T09:15:00Z", null),
            LastSignalAt = DateTimeOffset.Parse("2026-08-28T09:41:12Z", null),
            QuarantinedUntil = DateTimeOffset.Parse("2026-08-28T10:41:12Z", null),
            Target = new TargetRef
            {
                Namespace = "hephaisto-chaos",
                Kind = "Pod",
                Name = "c7-configerror-6b4d9-2xk8p",
                Uid = "not-in-the-card",
                OwnerKind = "Deployment",
                OwnerName = "c7-configerror",
                NodeName = "macstudio",
            },
        };

        incident.Signals.Add(new Signal
        {
            Source = SignalSource.KubernetesWatch,
            Kind = SignalKind.ConfigError,
            Reason = "Failed",
            Message = "Error: secret \"c7-database-credentials\" not found",
            FirstSeen = DateTimeOffset.Parse("2026-08-28T09:15:00Z", null),
            LastSeen = DateTimeOffset.Parse("2026-08-28T09:41:12Z", null),
            Count = 14,
            Fingerprint = "not-in-the-card",
            Labels = { ["also"] = "not-in-the-card" },
        });

        incident.Signals.Add(new Signal
        {
            Source = SignalSource.KubernetesWatch,
            Kind = SignalKind.ConfigError,
            Reason = "BackOff",
            Message = "Back-off restarting failed container",
            FirstSeen = DateTimeOffset.Parse("2026-08-28T09:16:30Z", null),
            LastSeen = DateTimeOffset.Parse("2026-08-28T09:40:00Z", null),
            Count = 1,
        });

        return incident;
    }

    [Fact]
    public void A_rebuilt_incident_composes_a_byte_identical_incident_card()
    {
        var original = Full();
        var rebuilt = RecordedIncident.From(original).ToIncident();

        PromptComposer.ComposeIncidentCard(rebuilt, rebuilt.Signals)
            .Should().Be(PromptComposer.ComposeIncidentCard(original, original.Signals));
    }

    [Fact]
    public void A_rebuilt_incident_survives_the_json_round_trip()
    {
        // The path a cassette actually takes. Serialising through the same options the file uses
        // is what proves the card is reproducible from disk and not just from memory.
        var original = Full();

        var json = System.Text.Json.JsonSerializer.Serialize(
            RecordedIncident.From(original), Cassette.Json);

        var rebuilt = System.Text.Json.JsonSerializer
            .Deserialize<RecordedIncident>(json, Cassette.Json)!
            .ToIncident();

        PromptComposer.ComposeIncidentCard(rebuilt, rebuilt.Signals)
            .Should().Be(PromptComposer.ComposeIncidentCard(original, original.Signals));
    }

    [Fact]
    public void The_card_carries_the_signals_that_matter_to_a_diagnosis()
    {
        // A guard on the test above rather than on the code: two empty cards would also be
        // identical, so assert the fixture is actually exercising the interesting part.
        var rebuilt = RecordedIncident.From(Full()).ToIncident();
        var card = PromptComposer.ComposeIncidentCard(rebuilt, rebuilt.Signals);

        card.Should().Contain("c7-database-credentials");
        card.Should().Contain("Deployment/c7-configerror");
        card.Should().Contain("Quarantined until");
        card.Should().Contain("14");
    }

    [Fact]
    public void Two_rebuilds_get_different_ids_so_concurrent_replays_cannot_collide()
    {
        var recorded = RecordedIncident.From(Full());

        recorded.ToIncident().Id.Should().NotBe(recorded.ToIncident().Id);
    }

    [Fact]
    public void An_incident_with_no_signals_rebuilds_cleanly()
    {
        var bare = new Incident
        {
            Title = "quiet",
            Kind = SignalKind.Unschedulable,
            Severity = Severity.Warning,
            OpenedAt = DateTimeOffset.UnixEpoch,
            LastSignalAt = DateTimeOffset.UnixEpoch,
            Target = new TargetRef { Namespace = "hephaisto-chaos", Kind = "Pod", Name = "c3-oom-1" },
        };

        var rebuilt = RecordedIncident.From(bare).ToIncident();

        rebuilt.Signals.Should().BeEmpty();
        PromptComposer.ComposeIncidentCard(rebuilt, rebuilt.Signals)
            .Should().Be(PromptComposer.ComposeIncidentCard(bare, bare.Signals));
    }
}
