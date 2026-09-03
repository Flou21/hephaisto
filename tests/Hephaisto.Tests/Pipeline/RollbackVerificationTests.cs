using System.Text.Json;
using Hephaisto.Agent.Pipeline;

namespace Hephaisto.Tests.Pipeline;

/// <summary>
/// Reading back what the rollback executor recorded about itself.
/// </summary>
/// <remarks>
/// <para>
/// The predicate around this needs a cluster and belongs to the e2e harness. This is the part
/// that does not: <c>PostState</c> is a nullable free-form string that most action types fill
/// with a snapshot of the target, so the rollback predicate is reading a field that usually
/// contains something else entirely. Getting that wrong is silent - it produces Inconclusive,
/// which reads like a flaky cluster rather than a parsing bug.
/// </para>
/// <para>
/// The one thing this must never do is throw. A verification that throws is caught and reported
/// as Inconclusive, which on a rollback means an incident sits in Verifying rather than
/// resolving - the exact shape of the v0.6.0 bug where a jsonb write rolled back the transition
/// that had just succeeded.
/// </para>
/// </remarks>
public class RollbackVerificationTests
{
    [Fact]
    public void The_replica_set_the_executor_recorded_is_read_back()
    {
        var postState = JsonSerializer.Serialize(new
        {
            rolledBackTo = 2,
            rolledBackFrom = 3,
            replicaSet = "faulty-service-6d4cbbf8b9",
        });

        VerificationChecks.RolledBackToReplicaSet(postState).Should().Be("faulty-service-6d4cbbf8b9");
    }

    [Fact]
    public void A_snapshot_from_some_other_action_type_yields_nothing_rather_than_throwing()
    {
        // What PostState contains for every action type that does not record its own
        // after-state: a snapshot of the target object.
        var snapshot = JsonSerializer.Serialize(new
        {
            kind = "Deployment",
            replicas = 3,
            ready = 3,
        });

        VerificationChecks.RolledBackToReplicaSet(snapshot).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"a bare string\"")]
    [InlineData("{\"replicaSet\": null}")]
    [InlineData("{\"replicaSet\": 7}")]
    public void Anything_unusable_yields_null_and_never_throws(string? postState) =>
        VerificationChecks.RolledBackToReplicaSet(postState).Should().BeNull();
}
