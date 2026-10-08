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

    /// <summary>Every container of the pod, in the order it runs: the init containers, then the regular one.</summary>
    private static List<k8s.Models.V1Container> All(k8s.Models.V1Job job) =>
        [.. job.Spec.Template.Spec.InitContainers ?? [], .. job.Spec.Template.Spec.Containers];

    private static k8s.Models.V1Container Named(k8s.Models.V1Job job, string name) => All(job).Single(c => c.Name == name);

    private static List<string> KeysOf(k8s.Models.V1Container c) =>
        [.. c.Env.Where(e => e.ValueFrom?.SecretKeyRef != null).Select(e => e.Name)];

    [Theory]
    [InlineData(CodeFixPhase.Plan)]
    [InlineData(CodeFixPhase.Implement)]
    public void ThePodHasNoClusterIdentity_NoRoot_NoWritableImage_AndNoRetries(CodeFixPhase phase)
    {
        var job = CodeFixJobSpec.Job(Attempt, phase, Options());
        var pod = job.Spec.Template.Spec;

        pod.AutomountServiceAccountToken.Should().BeFalse();
        pod.ServiceAccountName.Should().Be("hephaisto-coder");
        pod.RestartPolicy.Should().Be("Never");
        pod.EnableServiceLinks.Should().BeFalse();
        pod.SecurityContext.RunAsNonRoot.Should().BeTrue();
        pod.SecurityContext.RunAsUser.Should().Be(64198);
        pod.SecurityContext.SeccompProfile.Type.Should().Be("RuntimeDefault");
        job.Spec.BackoffLimit.Should().Be(0);
        job.Spec.ActiveDeadlineSeconds.Should().Be(phase == CodeFixPhase.Plan ? 1800 : 3600);
        job.Spec.TtlSecondsAfterFinished.Should().Be(3600);
        job.Metadata.NamespaceProperty.Should().Be("hephaisto-coder");

        // A hardened main container beside a soft init container is a soft pod: every one of them.
        All(job).Should().HaveCount(phase == CodeFixPhase.Plan ? 2 : 3).And.AllSatisfy(c =>
        {
            c.Image.Should().Be(Options().Image);
            c.SecurityContext.ReadOnlyRootFilesystem.Should().BeTrue();
            c.SecurityContext.AllowPrivilegeEscalation.Should().BeFalse();
            c.SecurityContext.RunAsNonRoot.Should().BeTrue();
            c.SecurityContext.Privileged.Should().NotBe(true);
            c.SecurityContext.Capabilities.Drop.Should().Equal("ALL");
            c.SecurityContext.Capabilities.Add.Should().BeNullOrEmpty();
            c.Resources.Requests["memory"].ToString().Should().Be("2Gi");
            c.Resources.Limits["memory"].ToString().Should().Be("6Gi");
            c.Resources.Limits["cpu"].ToString().Should().Be("4");
        });
    }

    [Theory]
    [InlineData(CodeFixPhase.Plan)]
    [InlineData(CodeFixPhase.Implement)]
    public void NoSecretValueEverAppearsInTheSpec(CodeFixPhase phase)
    {
        var job = CodeFixJobSpec.Job(Attempt, phase, Options());

        All(job).SelectMany(c => c.Env)
            .Where(e => e.Name.Contains("TOKEN", StringComparison.Ordinal) || e.Name.Contains("KEY", StringComparison.Ordinal))
            .Should().NotBeEmpty().And
            .OnlyContain(e => e.Value == null && e.ValueFrom.SecretKeyRef.Name == "hephaisto-codefix" && e.ValueFrom.SecretKeyRef.Optional == true);

        KubernetesJson.Serialize(job).Should().NotMatchRegex("ghp_|github_pat_|sk-ant-");
    }

    // ---------------------------------------------------------------------------------------------
    // #116: which container holds what. The driver that held GITHUB_TOKEN and the model's shell
    // were one uid in one PID namespace, and a regex said the model could not read
    // /proc/<driver>/environ. Now the kernel says it: the token is in another container.

    [Fact]
    public void A_plan_is_prepare_then_coder_and_coder_is_the_one_that_prints()
    {
        var job = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, Options());
        var pod = job.Spec.Template.Spec;

        pod.InitContainers.Select(c => c.Name).Should().Equal("prepare");
        pod.Containers.Select(c => c.Name).Should().Equal("coder");
        All(job).Select(c => c.Env.Single(e => e.Name == "CODEFIX_ROLE").Value).Should().Equal("prepare", "coder");
        CodeFixJobSpec.ResultContainer(job).Should().Be("coder");
    }

    [Fact]
    public void An_implementation_is_prepare_then_coder_then_publish_and_coder_has_ended_before_publish_starts()
    {
        var job = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, Options());
        var pod = job.Spec.Template.Spec;

        // An init container has ended - every process of it - before the next container starts.
        // That, and not a wait loop in publish, is what keeps the model away from the push.
        pod.InitContainers.Select(c => c.Name).Should().Equal("prepare", "coder");
        pod.Containers.Select(c => c.Name).Should().Equal("publish");
        All(job).Select(c => c.Env.Single(e => e.Name == "CODEFIX_ROLE").Value).Should().Equal("prepare", "coder", "publish");
        All(job).Should().OnlyContain(c => c.RestartPolicy == null, "a restartable init container is a sidecar: it would still be running beside publish");
        CodeFixJobSpec.ResultContainer(job).Should().Be("publish");
    }

    [Theory]
    [InlineData(CodeFixPhase.Plan)]
    [InlineData(CodeFixPhase.Implement)]
    public void Each_container_is_handed_its_own_keys_and_no_others(CodeFixPhase phase)
    {
        var job = CodeFixJobSpec.Job(Attempt, phase, Options());

        var handed = All(job).ToDictionary(c => c.Name, KeysOf);

        handed["prepare"].Should().Equal("GITHUB_TOKEN", "NUGET_GITHUB_TOKEN");
        handed["coder"].Should().Equal("CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY");

        if (phase == CodeFixPhase.Implement)
            handed["publish"].Should().Equal(["GITHUB_TOKEN"], "a push and a PR ask no package feed");

        handed.Should().HaveCount(phase == CodeFixPhase.Plan ? 2 : 3);
    }

    [Theory]
    [InlineData(CodeFixPhase.Plan, "fake")]
    [InlineData(CodeFixPhase.Plan, "real")]
    [InlineData(CodeFixPhase.Implement, "fake")]
    [InlineData(CodeFixPhase.Implement, "real")]
    public void The_container_the_model_runs_in_has_no_git_or_NuGet_key_by_any_name(CodeFixPhase phase, string sdk)
    {
        var o = Options();
        o.Sdk = sdk;
        o.Gh = "shim";
        o.NugetCacheClaim = "hephaisto-coder-nuget";

        var coder = Named(CodeFixJobSpec.Job(Attempt, phase, o), "coder");

        coder.Env.Select(e => e.Name).Should().NotContain(
            ["GITHUB_TOKEN", "GH_TOKEN", "GH_ENTERPRISE_TOKEN", "NUGET_GITHUB_TOKEN", "CODEFIX_GIT_PASSWORD", "token", "username"]);
        coder.Env.Where(e => e.ValueFrom != null).Select(e => e.Name).Should().BeSubsetOf(CodeFixJobSpec.ModelSecretKeys);
        coder.EnvFrom.Should().BeNullOrEmpty("envFrom would hand over the whole Secret");
        coder.VolumeMounts.Should().NotContain(m => m.Name == "sealed");
    }

    [Theory]
    [InlineData(CodeFixPhase.Plan)]
    [InlineData(CodeFixPhase.Implement)]
    public void The_containers_that_hold_a_git_token_hold_no_model_credential(CodeFixPhase phase)
    {
        var job = CodeFixJobSpec.Job(Attempt, phase, Options());

        All(job).Where(c => c.Name != "coder").Should().NotBeEmpty().And.AllSatisfy(c =>
        {
            c.Env.Select(e => e.Name).Should().NotContain(["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"]);
            c.Env.Select(e => e.Name).Should().NotContain("CODEFIX_MODEL", "only the container that runs the model is told which");
            c.EnvFrom.Should().BeNullOrEmpty();
        });
    }

    [Theory]
    [InlineData(CodeFixPhase.Plan)]
    [InlineData(CodeFixPhase.Implement)]
    public void No_container_can_see_another_ones_processes(CodeFixPhase phase)
    {
        var job = CodeFixJobSpec.Job(Attempt, phase, Options());
        var pod = job.Spec.Template.Spec;

        // The whole of #116 rests on this being absent: with a shared process namespace every
        // container reads every other's /proc/<pid>/environ, and three containers are one again.
        pod.ShareProcessNamespace.Should().BeNull();
        pod.HostPID.Should().BeNull();
        pod.HostIPC.Should().BeNull();
        pod.HostNetwork.Should().BeNull();
        KubernetesJson.Serialize(job).Should().NotContain("shareProcessNamespace").And.NotContain("hostPID");
    }

    [Fact]
    public void What_prepare_decided_reaches_publish_on_a_volume_the_coder_container_never_mounts()
    {
        var job = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, Options());

        job.Spec.Template.Spec.Volumes.Single(v => v.Name == "sealed").EmptyDir.Should().NotBeNull();
        var prepare = Named(job, "prepare").VolumeMounts.Single(m => m.Name == "sealed");
        prepare.MountPath.Should().Be("/sealed");
        prepare.ReadOnlyProperty.Should().NotBe(true);
        var publish = Named(job, "publish").VolumeMounts.Single(m => m.Name == "sealed");
        publish.MountPath.Should().Be("/sealed");
        publish.ReadOnlyProperty.Should().BeTrue();
        Named(job, "coder").VolumeMounts.Select(m => m.Name).Should().BeEquivalentTo(["work", "tmp", "request"]);

        // a plan has no publish, and so nothing to seal for
        var plan = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, Options());
        plan.Spec.Template.Spec.Volumes.Should().NotContain(v => v.Name == "sealed");
    }

    [Fact]
    public void Every_container_has_a_tmp_of_its_own_and_they_all_share_the_workspace()
    {
        var job = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, Options());

        All(job).Select(c => c.VolumeMounts.Single(m => m.MountPath == "/tmp").Name)
            .Should().Equal("tmp-prepare", "tmp", "tmp-publish");
        All(job).Should().OnlyContain(c => c.VolumeMounts.Single(m => m.MountPath == "/work").Name == "work");
        job.Spec.Template.Spec.Volumes.Where(v => v.Name.StartsWith("tmp", StringComparison.Ordinal))
            .ToDictionary(v => v.Name, v => v.EmptyDir.SizeLimit.ToString())
            .Should().Equal(new Dictionary<string, string>
            {
                ["tmp-prepare"] = "1Gi",
                ["tmp"] = "1Gi",
                // publish clones the default branch into its /tmp: as large as the workspace may be
                ["tmp-publish"] = "8Gi",
            });
    }

    [Fact]
    public void The_package_cache_is_filled_by_prepare_read_only_for_the_model_and_absent_where_the_push_happens()
    {
        var o = Options();
        o.NugetCacheClaim = "hephaisto-coder-nuget";
        var job = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, o);

        job.Spec.Template.Spec.Volumes.Single(v => v.Name == "nuget").PersistentVolumeClaim.ClaimName.Should().Be("hephaisto-coder-nuget");
        Named(job, "prepare").VolumeMounts.Single(m => m.Name == "nuget").ReadOnlyProperty.Should().NotBe(true);
        var inCoder = Named(job, "coder").VolumeMounts.Single(m => m.Name == "nuget");
        inCoder.MountPath.Should().Be("/work/nuget/packages");
        inCoder.ReadOnlyProperty.Should().BeTrue(
            "the cache outlives the Job, and the next attempt's prepare restores from it beside both tokens");
        Named(job, "publish").VolumeMounts.Should().NotContain(m => m.Name == "nuget");

        CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, Options()).Spec.Template.Spec.Volumes
            .Should().NotContain(v => v.Name == "nuget", "without a claim the packages live in the workspace, which dies with the pod");
    }

    [Fact]
    public void The_result_is_read_from_the_container_the_Job_names_and_from_coder_when_it_names_none()
    {
        var plan = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, Options());
        var implement = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, Options());

        plan.Metadata.Annotations["hephaisto.dev/result-container"].Should().Be("coder");
        implement.Metadata.Annotations["hephaisto.dev/result-container"].Should().Be("publish");

        // A Job a v0.12 agent started is still running when v0.13 comes up: one container, coder.
        implement.Metadata.Annotations = null;
        CodeFixJobSpec.ResultContainer(implement).Should().Be("coder");

        // Never a name the spec did not write - not prepare, which prints nothing, and not a stranger.
        foreach (var bogus in new[] { "prepare", "", "sidecar", "PUBLISH" })
        {
            implement.Metadata.Annotations = new Dictionary<string, string> { ["hephaisto.dev/result-container"] = bogus };
            CodeFixJobSpec.ResultContainer(implement).Should().Be("coder");
        }
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
        var job = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, Options());
        job.Metadata.Uid = "uid-1";

        All(job).Should().AllSatisfy(c =>
        {
            var mount = c.VolumeMounts.Single(m => m.Name == "request");
            mount.MountPath.Should().Be("/work/in");
            mount.ReadOnlyProperty.Should().BeTrue();
            c.Env.Single(e => e.Name == "CODEFIX_REQUEST").Value.Should().Be("/work/in/request.json");
        });

        var cm = CodeFixJobSpec.RequestConfigMap(Attempt, CodeFixPhase.Plan, Options(), "{}", job);
        cm.Metadata.OwnerReferences.Single().Uid.Should().Be("uid-1");
        cm.Data.Should().ContainKey("request.json");
    }

    [Fact]
    public void TheProxyIsWiredWhenConfigured_AndAbsentOtherwise()
    {
        // One pod, one IP, one NetworkPolicy: a container without the proxy variables would go
        // direct and be refused. prepare and publish reach GitHub, coder the model's API.
        var withProxy = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, Options());
        All(withProxy).Should().HaveCount(3).And.AllSatisfy(c =>
        {
            c.Env.Should().Contain(e => e.Name == "HTTPS_PROXY" && e.Value == Options().EgressProxyUrl);
            c.Env.Should().Contain(e => e.Name == "https_proxy" && e.Value == Options().EgressProxyUrl);
            c.Env.Should().Contain(e => e.Name == "http_proxy" && e.Value == Options().EgressProxyUrl);
            c.Env.Should().Contain(e => e.Name == "NO_PROXY");
        });

        var o = Options();
        o.EgressProxyUrl = string.Empty;
        All(CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, o)).SelectMany(c => c.Env)
            .Should().NotContain(e => e.Name == "HTTPS_PROXY");
    }

    [Fact]
    public void ThePinnedModel_ReachesTheCoder_AndNoPinLeavesTheCliDefault()
    {
        var o = Options();
        o.Model = "claude-haiku-4-5-20251001";

        Named(CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, o), "coder").Env
            .Should().ContainSingle(e => e.Name == "CODEFIX_MODEL").Which.Value.Should().Be("claude-haiku-4-5-20251001");

        All(CodeFixJobSpec.Job(Attempt, CodeFixPhase.Plan, Options())).SelectMany(c => c.Env)
            .Should().NotContain(e => e.Name == "CODEFIX_MODEL");
    }

    [Fact]
    public void The_gh_shim_is_named_to_the_containers_that_run_gh()
    {
        var o = Options();
        o.Gh = "shim";
        o.Sdk = "fake";

        var job = CodeFixJobSpec.Job(Attempt, CodeFixPhase.Implement, o);

        // prepare lists open PRs and publish creates one; a scripted run reaches no GitHub.
        Named(job, "prepare").Env.Should().Contain(e => e.Name == "CODEFIX_GH" && e.Value == "shim");
        Named(job, "publish").Env.Should().Contain(e => e.Name == "CODEFIX_GH" && e.Value == "shim");
        All(job).Should().OnlyContain(c => c.Env.Single(e => e.Name == "CODEFIX_SDK").Value == "fake");
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

    [Theory]
    [InlineData(CodeFixPhase.Plan)]
    [InlineData(CodeFixPhase.Implement)]
    public void A_scripted_coder_is_handed_no_model_credential(CodeFixPhase phase)
    {
        var attempt = new CodeFixAttempt { IncidentId = Guid.NewGuid() };
        var fake = CodeFixJobSpec.Job(attempt, phase, new CodeFixOptions { Image = "coder:dev", Sdk = "fake" });
        var real = CodeFixJobSpec.Job(attempt, phase, new CodeFixOptions { Image = "coder:dev", Sdk = "real" });

        All(fake).SelectMany(KeysOf).Should().NotContain(CodeFixJobSpec.ModelSecretKeys,
            "the runner refuses fake mode beside a model credential, and the dev Secret holds a real one");
        KeysOf(Named(fake, "coder")).Should().BeEmpty("a scripted coder calls no model and pushes nothing itself");
        KeysOf(Named(fake, "prepare")).Should().Equal("GITHUB_TOKEN", "NUGET_GITHUB_TOKEN");

        All(real).SelectMany(KeysOf).Distinct().Should().BeEquivalentTo(CodeFixJobSpec.SecretKeys);
    }
}
