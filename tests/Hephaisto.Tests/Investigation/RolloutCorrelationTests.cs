using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// The sentence the incident card carries when a rollout preceded an incident.
/// </summary>
/// <remarks>
/// The roadmap's "change correlation", phrased as the sentence it exists to produce: "this
/// started 4 minutes after the rollout of x:sha". The reading side needs a cluster and belongs
/// to the e2e harness; the wording and the windowing do not.
/// </remarks>
public class RolloutCorrelationTests
{
    private static RolloutCorrelation Rollout(
        TimeSpan openedAfter, TimeSpan? previous = null, string? images = "faulty-service:bad") =>
        new()
        {
            Revision = 4,
            IncidentOpenedAfter = openedAfter,
            PreviousRevisionLastedFor = previous,
            Images = images,
        };

    [Fact]
    public void It_names_the_revision_the_gap_and_the_image()
    {
        var described = Rollout(TimeSpan.FromMinutes(4), previous: TimeSpan.FromHours(3)).Describe();

        described.Should().Contain("revision 4");
        described.Should().Contain("4m before this incident opened");
        described.Should().Contain("faulty-service:bad");
        described.Should().Contain("live for 3h");
    }

    [Fact]
    public void A_first_ever_revision_says_nothing_about_a_previous_one()
    {
        // There is nothing to roll back TO, and inventing a duration would be the kind of
        // confident-sounding fabrication the grounding rules exist to prevent.
        var described = Rollout(TimeSpan.FromMinutes(2), previous: null).Describe();

        described.Should().NotContain("previous revision");
    }

    [Fact]
    public void A_revision_with_no_readable_image_still_describes_the_timing()
    {
        var described = Rollout(TimeSpan.FromMinutes(2), images: null).Describe();

        described.Should().Contain("revision 4").And.Contain("2m");
        described.Should().NotContain("running ``");
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(240, "4m")]
    [InlineData(7200, "2h")]
    public void The_gap_is_rendered_at_a_useful_scale(int seconds, string expected) =>
        Rollout(TimeSpan.FromSeconds(seconds)).Describe().Should().Contain($"{expected} before");

    [Fact]
    public void The_relevance_window_is_wider_than_the_policy_gate_that_permits_acting()
    {
        // Deliberate, and the difference is the point: this decides whether the model is TOLD
        // about a rollout, and the policy gate decides whether it may act on one unattended.
        // Being told about a rollout it may not roll back is fine and often correct - the right
        // answer may be to escalate naming the revision.
        RolloutCorrelation.RelevanceWindow
            .Should().BeGreaterThan(new Hephaisto.Core.Policy.PolicyOptions().RollbackFreshRevisionWindow);
    }
}
