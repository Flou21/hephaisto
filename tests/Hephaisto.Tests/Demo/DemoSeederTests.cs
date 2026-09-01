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
}
