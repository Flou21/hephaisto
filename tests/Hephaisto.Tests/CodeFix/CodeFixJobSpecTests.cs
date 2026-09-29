using k8s;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.CodeFix;

/// <summary>
/// The coder pod holds a shell and a model. Every property that makes that acceptable is a field in
/// this spec, so each one is asserted by name rather than trusted to a golden file alone.
/// </summary>
public sealed class CodeFixJobSpecTests
{
    private static readonly CodeFixAttempt Attempt = new()
    {
        Id = Guid.Parse("0192a6f0-0000-7000-8000-00000000abcd"),
        IncidentId = Guid.Parse("0192a6f0-0000-7000-8000-0000000000aa"),
        RepositoryUrl = "https://github.com/Flou21/hephaisto-fixture-dotnet",
        Branch = "hephaisto/codefix-00000000abcd",
    };

    private static CodeFixOptions Options() => new()
    {
        Image = "ghcr.io/flou21/hephaisto-coder:0.9.0",
        EgressProxyUrl = "http://hephaisto-coder-egress.hephaisto-coder.svc:3128",
    };

    [Theory]
    [InlineData(CodeFixPhase.Plan)]
    [InlineData(CodeFixPhase.Implement)]
    public void ThePodHasNoClusterIdentity_NoRoot_NoWritableImage_AndNoRetries(CodeFixPhase phase)
    {
        var job = CodeFixJobSpec.Job(Attempt, phase, Options());
        var pod = job.Spec.Template.Spec;
        var c = pod.Containers.Single();

        pod.AutomountServiceAccountToken.Should().BeFalse();
        pod.ServiceAccountName.Should().Be("hephaisto-coder");
        pod.RestartPolicy.Should().Be("Never");
        pod.EnableServiceLinks.Should().BeFalse();
        pod.SecurityContext.RunAsNonRoot.Should().BeTrue();
        pod.SecurityContext.RunAsUser.Should().Be(64198);
        pod.SecurityContext.SeccompProfile.Type.Should().Be("RuntimeDefault");
        c.SecurityContext.ReadOnlyRootFilesystem.Should().BeTrue();
        c.SecurityContext.AllowPrivilegeEscalation.Should().BeFalse();
        c.SecurityContext.Capabilities.Drop.Should().Equal("ALL");
        job.Spec.BackoffLimit.Should().Be(0);
        job.Spec.ActiveDeadlineSeconds.Should().Be(phase == CodeFixPhase.Plan ? 1800 : 3600);
        job.Spec.TtlSecondsAfterFinished.Should().Be(3600);
        job.Metadata.NamespaceProperty.Should().Be("hephaisto-coder");
    }

    [Fact]
    public void NoSecretValueEverAppearsInTheSpec()
    {
        var job = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, Options());
        var env = job.Spec.Template.Spec.Containers.Single().Env;

        env.Where(e => e.Name.Contains("TOKEN", StringComparison.Ordinal) || e.Name.Contains("KEY", StringComparison.Ordinal))
            .Should().OnlyContain(e => e.Value == null && e.ValueFrom.SecretKeyRef.Name == "hephaisto-codefix" && e.ValueFrom.SecretKeyRef.Optional == true);

        KubernetesJson.Serialize(job).Should().NotMatchRegex("ghp_|github_pat_|sk-ant-");
    }

    [Fact]
    public void NamesFitTheLabelLimit_AndTheBranchIsTheAssignedShape()
    {
        CodeFixJobSpec.JobName(Attempt.Id, CodeFixPhase.Implement).Length.Should().BeLessThanOrEqualTo(63);
        CodeFixJobSpec.ConfigMapName(Attempt.Id, CodeFixPhase.Implement).Length.Should().BeLessThanOrEqualTo(63);
        CodeFixJobSpec.BranchName(Attempt.Id).Should().MatchRegex("^hephaisto/codefix-[0-9a-f]{12}$");
    }

    [Fact]
    public void TwoAttemptsInTheSameMillisecond_GetDifferentBranches()
    {
        // Version-7 guids share their first twelve hex digits within a millisecond; the tail is random.
        var a = Guid.CreateVersion7(DateTimeOffset.UnixEpoch);
        var b = Guid.CreateVersion7(DateTimeOffset.UnixEpoch);

        CodeFixJobSpec.BranchName(a).Should().NotBe(CodeFixJobSpec.BranchName(b));
    }

    [Fact]
    public void TheRequestIsMountedReadOnly_AndOwnedByItsJob()
    {
        var job = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, Options());
        job.Metadata.Uid = "uid-1";

        var mount = job.Spec.Template.Spec.Containers.Single().VolumeMounts.Single(m => m.Name == "request");
        mount.MountPath.Should().Be("/work/in");
        mount.ReadOnlyProperty.Should().BeTrue();

        var cm = CodeFixJobSpec.RequestConfigMap(Attempt, CodeFixPhase.Plan, Options(), "{}", job);
        cm.Metadata.OwnerReferences.Single().Uid.Should().Be("uid-1");
        cm.Data.Should().ContainKey("request.json");
    }

    [Fact]
    public void TheProxyIsWiredWhenConfigured_AndAbsentOtherwise()
    {
        var withProxy = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, Options()).Spec.Template.Spec.Containers.Single().Env;
        withProxy.Should().Contain(e => e.Name == "HTTPS_PROXY" && e.Value == Options().EgressProxyUrl);

        var o = Options();
        o.EgressProxyUrl = string.Empty;
        CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, o).Spec.Template.Spec.Containers.Single().Env
            .Should().NotContain(e => e.Name == "HTTPS_PROXY");
    }

    [Fact]
    public void ThePinnedModel_ReachesTheCoder_AndNoPinLeavesTheCliDefault()
    {
        var o = Options();
        o.Model = "claude-haiku-4-5-20251001";

        CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, o).Spec.Template.Spec.Containers.Single().Env
            .Should().ContainSingle(e => e.Name == "CODEFIX_MODEL").Which.Value.Should().Be("claude-haiku-4-5-20251001");

        CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, Options()).Spec.Template.Spec.Containers.Single().Env
            .Should().NotContain(e => e.Name == "CODEFIX_MODEL");
    }

    [Fact]
    public void LabelsCarryTheAttempt_TheIncident_AndThePhase()
    {
        var labels = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, Options()).Spec.Template.Metadata.Labels;

        labels["app.kubernetes.io/name"].Should().Be("hephaisto-coder");
        labels["hephaisto.dev/attempt"].Should().Be(Attempt.Id.ToString());
        labels["hephaisto.dev/incident"].Should().Be(Attempt.IncidentId.ToString());
        labels["hephaisto.dev/phase"].Should().Be("implement");
    }

    [Theory]
    [InlineData(CodeFixPhase.Plan)]
    [InlineData(CodeFixPhase.Implement)]
    public void The_coder_is_handed_no_way_into_the_mcp_endpoint(CodeFixPhase phase)
    {
        // #157: the coder Job has no route to Hephaisto and gets none from the MCP endpoint - no
        // setting, no token, no Secret reference, no address of the port.
        var job = System.Text.Json.JsonSerializer.Serialize(CodeFixJobSpec.Job(Attempt, phase, Options()));

        job.Should().NotContain("Mcp__").And.NotContain("hephaisto-mcp").And.NotContain(":8083").And.NotContain("/mcp");
    }
}
