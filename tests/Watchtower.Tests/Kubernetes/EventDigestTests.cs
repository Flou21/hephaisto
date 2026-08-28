using k8s.Models;

using Watchtower.Agent.Kubernetes;

namespace Watchtower.Tests.Kubernetes;

public class EventDigestTests
{
    private static readonly DateTimeOffset Now = K8sFixtures.Now;

    /// <summary>
    /// The whole point: one crash-looping Deployment emits the same BackOff line once per pod
    /// per retry, and handed raw that repetition is what the model attends to.
    /// </summary>
    [Fact]
    public void Identical_reason_and_message_collapse_into_one_row_with_a_summed_count()
    {
        var groups = EventDigest.Dedupe(
        [
            Event("BackOff", "Back-off restarting failed container", count: 100, pod: "api-a"),
            Event("BackOff", "Back-off restarting failed container", count: 80, pod: "api-b"),
            Event("BackOff", "Back-off restarting failed container", count: 34, pod: "api-c"),
        ]);

        groups.Should().HaveCount(1);
        groups[0].Count.Should().Be(214);
        groups[0].DistinctObjects.Should().Be(3);
    }

    [Fact]
    public void Different_messages_stay_separate_rows()
    {
        var groups = EventDigest.Dedupe(
        [
            Event("BackOff", "Back-off restarting failed container"),
            Event("FailedScheduling", "0/1 nodes are available: 1 Insufficient memory."),
        ]);

        groups.Should().HaveCount(2);
        groups.Select(g => g.Reason).Should().Contain(["BackOff", "FailedScheduling"]);
    }

    [Fact]
    public void The_time_range_spans_every_occurrence_in_the_group()
    {
        var groups = EventDigest.Dedupe(
        [
            Event("BackOff", "same", at: Now.AddMinutes(-26)),
            Event("BackOff", "same", at: Now.AddMinutes(-1)),
        ]);

        groups[0].FirstSeen.Should().Be(Now.AddMinutes(-26));
        groups[0].LastSeen.Should().Be(Now.AddMinutes(-1));
    }

    [Fact]
    public void Rows_are_ordered_newest_first()
    {
        var groups = EventDigest.Dedupe(
        [
            Event("Old", "a", at: Now.AddHours(-1)),
            Event("Recent", "b", at: Now.AddMinutes(-1)),
        ]);

        groups[0].Reason.Should().Be("Recent");
    }

    [Fact]
    public void An_empty_input_produces_no_rows()
    {
        EventDigest.Dedupe([]).Should().BeEmpty();
    }

    private static Corev1Event Event(
        string reason,
        string message,
        int count = 1,
        string pod = "api-a",
        DateTimeOffset? at = null) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = $"{pod}.{reason}", Uid = Guid.NewGuid().ToString() },
            Type = "Warning",
            Reason = reason,
            Message = message,
            Count = count,
            LastTimestamp = (at ?? Now).UtcDateTime,
            InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = pod },
        };
}

public class TextTableTests
{
    /// <summary>
    /// An empty result must be a sentence. "[]" reads as a broken tool, and the model's next
    /// move is to call it again with different arguments instead of treating the emptiness as
    /// the finding it usually is.
    /// </summary>
    [Fact]
    public void An_empty_table_renders_the_explanation_rather_than_an_empty_structure()
    {
        var rendered = TextTable.Render(["name"], [], "no pods in namespace prod");

        rendered.Should().Be("no pods in namespace prod");
    }

    [Fact]
    public void Rows_beyond_the_cap_are_replaced_by_a_count()
    {
        var rows = Enumerable.Range(0, 10)
            .Select(i => (IReadOnlyList<string?>)new string?[] { $"pod-{i}" })
            .ToList();

        var rendered = TextTable.Render(["name"], rows, "none", maxRows: 3);

        rendered.Should().Contain("pod-0").And.NotContain("pod-9");
        rendered.Should().Contain("7 more rows not shown (10 total)");
    }

    [Fact]
    public void Null_cells_render_as_a_dash_rather_than_as_nothing()
    {
        var rendered = TextTable.Render(
            ["name", "node"],
            [new string?[] { "api-a", null }],
            "none");

        rendered.Should().Contain("-");
    }

    [Fact]
    public void Age_is_rendered_in_the_compact_kubernetes_form()
    {
        TextTable.Age(K8sFixtures.Now.AddMinutes(-17), K8sFixtures.Now).Should().Be("17m");
        TextTable.Age(K8sFixtures.Now.AddHours(-3), K8sFixtures.Now).Should().Be("3h0m");
        TextTable.Age(K8sFixtures.Now.AddDays(-3).AddHours(-4), K8sFixtures.Now).Should().Be("3d4h");
    }
}
