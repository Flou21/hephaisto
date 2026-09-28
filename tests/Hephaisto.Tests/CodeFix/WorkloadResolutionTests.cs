using Hephaisto.Agent.CodeFix;

namespace Hephaisto.Tests.CodeFix;

/// <summary>
/// An alert names the pod; the repository mapping names the Deployment. When the pod an alert
/// named is already gone, its ReplicaSet - the pod name without its random suffix - still leads
/// to the owner.
/// </summary>
public sealed class WorkloadResolutionTests
{
    [Theory]
    [InlineData("shop-api-556d7fb5c6-2jwcd", "shop-api-556d7fb5c6")]
    [InlineData("cait-matching-service-7d9f8b6c4-x2k9p", "cait-matching-service-7d9f8b6c4")]
    public void ADeploymentPodName_LeadsToItsReplicaSet(string pod, string replicaSet) =>
        KubernetesWorkloadImageReader.ReplicaSetNameOf(pod).Should().Be(replicaSet);

    [Theory]
    [InlineData("hephaisto-postgres-0")]
    [InlineData("coder-git")]
    [InlineData("job-28745120-abcde")]
    public void AnythingElse_IsNotGuessed(string pod) =>
        KubernetesWorkloadImageReader.ReplicaSetNameOf(pod).Should().BeNull();
}
