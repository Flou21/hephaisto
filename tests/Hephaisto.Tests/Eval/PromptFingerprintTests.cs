using System.Text.Json;
using Hephaisto.Core.Domain;
using Hephaisto.Eval;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// Detecting that a cassette is measuring a prompt that no longer exists.
/// </summary>
/// <remarks>
/// Prompt fragments and runbooks are <c>Content</c> files read fresh on every compose, which is
/// what makes them editable without a rebuild - and what silently ties every cassette to the day
/// it was recorded. Experiment 2a rewrites a runbook, so this is not a hypothetical.
/// </remarks>
public class PromptFingerprintTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "hephaisto-fingerprint-" + Guid.NewGuid().ToString("N"));

    public PromptFingerprintTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Prompts"));
        Directory.CreateDirectory(Path.Combine(root, "Runbooks"));

        File.WriteAllText(Path.Combine(root, "Prompts", "00-role.md"), "you are an SRE\n");
        File.WriteAllText(Path.Combine(root, "Prompts", "10-tool-contract.md"), "tools are data\n");
        File.WriteAllText(Path.Combine(root, "Prompts", "20-output-contract.md"), "cite everything\n");
        File.WriteAllText(Path.Combine(root, "Prompts", "30-planning.md"), "plan carefully\n");
        File.WriteAllText(Path.Combine(root, "Runbooks", "_Default.md"), "start with who_owns\n");
        File.WriteAllText(Path.Combine(root, "Runbooks", "Unschedulable.md"), "query it in Loki\n");
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_same_files_hash_the_same_way_twice()
    {
        PromptFingerprint.Compute(SignalKind.Unschedulable, root)
            .Should().Be(PromptFingerprint.Compute(SignalKind.Unschedulable, root));
    }

    [Fact]
    public void Rewriting_the_runbook_changes_the_fingerprint()
    {
        // Experiment 2a, exactly. The runbook currently sends the model to Loki and never
        // mentions get_events; rewriting it changes the thing being measured, and every cassette
        // recorded before it has to say so.
        var before = PromptFingerprint.Compute(SignalKind.Unschedulable, root);

        File.WriteAllText(
            Path.Combine(root, "Runbooks", "Unschedulable.md"),
            "step 1: get_events. step 2: list_nodes.\n");

        PromptFingerprint.Compute(SignalKind.Unschedulable, root).Should().NotBe(before);
    }

    [Fact]
    public void Two_kinds_hash_differently_because_they_read_different_runbooks()
    {
        PromptFingerprint.Compute(SignalKind.Unschedulable, root)
            .Should().NotBe(PromptFingerprint.Compute(SignalKind.OomKilled, root));
    }

    [Fact]
    public void A_kind_with_no_runbook_falls_back_the_way_the_composer_does()
    {
        // OomKilled has no file here, so it must hash the default - the same resolution rule
        // PromptComposer.ReadRunbook uses. A fingerprint that hashed a nonexistent path would
        // report every fallback kind as identical.
        File.WriteAllText(Path.Combine(root, "Runbooks", "_Default.md"), "changed\n");

        var after = PromptFingerprint.Compute(SignalKind.OomKilled, root);

        File.WriteAllText(Path.Combine(root, "Runbooks", "_Default.md"), "changed again\n");

        PromptFingerprint.Compute(SignalKind.OomKilled, root).Should().NotBe(after);
    }

    [Fact]
    public void Line_endings_do_not_change_the_fingerprint()
    {
        // Two checkouts with different autocrlf settings hold the same prompt, and a harness that
        // called every cassette stale on one machine would train everyone to ignore the warning.
        var before = PromptFingerprint.Compute(SignalKind.Unschedulable, root);

        File.WriteAllText(Path.Combine(root, "Prompts", "00-role.md"), "you are an SRE\r\n");

        PromptFingerprint.Compute(SignalKind.Unschedulable, root).Should().Be(before);
    }

    [Fact]
    public void A_cassette_with_a_matching_hash_reads_as_current()
    {
        var cassette = Recorded(PromptFingerprint.Compute(SignalKind.Unschedulable, root));

        // Computed against the real content root, which is what Describe uses - so this asserts
        // the shape of the message rather than the hash, which the tests above already cover.
        PromptFingerprint.Describe(cassette with
        {
            Origin = cassette.Origin! with { PromptHash = PromptFingerprint.Compute(SignalKind.Unschedulable) },
        })!.Should().Contain("(current)");
    }

    [Fact]
    public void A_cassette_recorded_against_different_prompts_reads_as_stale()
    {
        PromptFingerprint.Describe(Recorded("sha256:0000000000000000"))!
            .Should().Contain("STALE");
    }

    [Fact]
    public void A_cassette_that_cannot_be_checked_says_nothing_rather_than_current()
    {
        // Silence beats false reassurance: "up to date" is the part a reader would act on.
        PromptFingerprint.Describe(Recorded(hash: null)).Should().BeNull();

        PromptFingerprint.Describe(Recorded("sha256:abc") with
        {
            Origin = new CassetteOrigin { PromptHash = "sha256:abc", IncidentKind = null },
        }).Should().BeNull();
    }

    private static Cassette Recorded(string? hash) => new()
    {
        Id = "c1",
        Description = "unschedulable",
        ExpectedRootCause = "no node has enough memory",
        Tools = [],
        Calls = [],
        Origin = new CassetteOrigin
        {
            PromptHash = hash,
            IncidentKind = SignalKind.Unschedulable,
        },
    };

    [Fact]
    public void The_recorded_kind_survives_the_json_round_trip()
    {
        // Without it there is no way to know which runbook to re-hash, and the staleness check
        // silently degrades to saying nothing about every cassette.
        var json = JsonSerializer.Serialize(Recorded("sha256:abc"), Cassette.Json);

        JsonSerializer.Deserialize<Cassette>(json, Cassette.Json)!
            .Origin!.IncidentKind.Should().Be(SignalKind.Unschedulable);
    }
}
