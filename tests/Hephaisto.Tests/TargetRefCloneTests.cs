using Hephaisto.Core.Domain;

namespace Hephaisto.Tests;

/// <summary>
/// <see cref="TargetRef"/> is an EF Core owned type on both Signal and Incident, so the same
/// instance must never be attached to both. The failure mode is nasty: it throws at
/// SaveChanges rather than at the assignment, and it takes down ingest for EVERY signal, not
/// just the one that shared an instance.
/// </summary>
public class TargetRefCloneTests
{
    [Fact]
    public void Clone_copies_every_field()
    {
        var original = new TargetRef
        {
            Namespace = "hephaisto-chaos",
            Kind = "Pod",
            Name = "c2-crashloop-abc",
            Uid = "uid-1",
            OwnerKind = "Deployment",
            OwnerName = "c2-crashloop",
            NodeName = "lima-rancher-desktop",
        };

        var copy = original.Clone();

        copy.Should().BeEquivalentTo(original, "a missed field silently loses target identity");
    }

    [Fact]
    public void Clone_returns_a_different_instance()
    {
        var original = new TargetRef { Namespace = "a", Kind = "Pod", Name = "b" };

        original.Clone().Should().NotBeSameAs(original);
    }

    [Fact]
    public void Clone_does_not_alias_the_original()
    {
        var original = new TargetRef { Namespace = "a", Kind = "Pod", Name = "b" };
        var copy = original.Clone();

        copy.Name = "changed";

        original.Name.Should().Be("b");
    }

    /// <summary>WorkloadKey drives cooldowns and oscillation detection, so it must survive a copy.</summary>
    [Fact]
    public void Clone_preserves_the_workload_key()
    {
        var original = new TargetRef
        {
            Namespace = "hephaisto-chaos",
            Kind = "Pod",
            Name = "pod-xyz",
            OwnerKind = "Deployment",
            OwnerName = "app",
        };

        original.Clone().WorkloadKey.Should().Be(original.WorkloadKey);
    }
}
