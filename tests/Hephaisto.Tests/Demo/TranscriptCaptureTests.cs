using Hephaisto.Agent.Demo;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Demo;

/// <summary>
/// The committed corpus has the shape both renderers rest on.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Transcript"/>'s own remarks say the test suite should not grow to depend on a
/// transcript's <i>contents</i> - what one model said on one day is not a fixture. This depends
/// on their <i>shape</i>, which is a different thing and is the invariant two renderers and a
/// seeder read: a file either carries the history of an incident or it does not, and everything
/// downstream branches on that.
/// </para>
/// <para>
/// It is here because the alternative discriminators are all worse and one of them is tempting.
/// <c>incident.state</c> cannot be read directly: <c>Detected</c> is zero, which is also what
/// the serializer writes for a field nothing set, so a replayed transcript and a genuinely
/// just-detected incident are indistinguishable. The transitions are the discriminator, and
/// this asserts they agree with everything else the file says.
/// </para>
/// </remarks>
public class TranscriptCaptureTests
{
    public static TheoryData<string> Committed()
    {
        var data = new TheoryData<string>();

        foreach (var file in Directory.EnumerateFiles(TranscriptDirectory(), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(file);
        }

        return data;
    }

    [Fact]
    public void The_corpus_is_not_empty()
    {
        // Every assertion below is a Theory over the directory, so an empty one would make the
        // whole class vacuously green - the same trap demo-site/build.mjs refuses to build on.
        Committed().Should().NotBeEmpty("the demo surfaces are seeded from these files");
    }

    [Theory]
    [MemberData(nameof(Committed))]
    public void A_transcript_carries_a_history_exactly_when_it_was_captured_from_a_cluster(string path)
    {
        var t = Transcript.Load(path);
        var name = Path.GetFileName(path);

        if (t.Origin.Capture is TranscriptCapture.Cluster)
        {
            t.Incident.Events.Should().NotBeEmpty(
                $"{name} says it was exported from a cluster, so it must carry the transitions "
                + "the agent actually made - that is the whole reason export exists");

            t.Incident.State.Should().NotBe(IncidentState.Detected,
                $"{name} was exported after the incident settled");

            t.Incident.Events.OrderBy(e => e.At).Last().To.Should().Be(t.Incident.State,
                $"{name}'s last transition and its state column must agree; a log and a column "
                + "that disagree is a bug, not something to render");
        }
        else
        {
            t.Incident.Events.Should().BeEmpty(
                $"{name} is a replay, and `run --transcripts` constructs no state machine, so "
                + "any transitions in it were invented somewhere they should not have been");

            t.Incident.State.Should().Be(IncidentState.Detected,
                $"{name} is a replay and has no state to record");

            (t.Investigation.Plan?.Actions ?? []).Should().OnlyContain(a => a.ExecutedAt == null,
                $"{name} is a replay, and replay constructs no executor");
        }
    }

    private static string TranscriptDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);

        return Path.Combine(dir!.FullName, "src", "Hephaisto.Agent", "Demo", "transcripts");
    }
}
