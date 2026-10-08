using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.CodeFix;

/// <summary>
/// Where the code of an issue's repository is cloned from, what its Job is labelled with, and
/// what Hephaisto checks before it believes a pull request for it exists. An issue names
/// <c>owner/repo</c>; everything else here is derived, and each derivation is pinned.
/// </summary>
public sealed class WorkItemRepositoryTests
{
    private const string InCluster = "http://coder-git.hephaisto-coder.svc/Flou21/hephaisto-fixture-dotnet.git";

    /// <summary>The dev cluster's list: one repository, mapped twice, by two fixture branches.</summary>
    private static CodeFixOptions Dev() => new()
    {
        Repositories =
        [
            new RepositoryBinding { Workload = "shop/Deployment/other", Url = "https://github.com/Flou21/another", DefaultBranch = "trunk" },
            new RepositoryBinding { Workload = "hephaisto-chaos/Deployment/shop-api", Url = InCluster, DefaultBranch = "fixture/c15-null-deref", Path = "src/Shop.Api" },
            new RepositoryBinding { Workload = "hephaisto-chaos/Deployment/catalog-api", Url = InCluster, DefaultBranch = "fixture/c19-injection" },
        ],
    };

    [Fact]
    public void TheFirstEntryWhoseUrlNamesTheRepository_GivesUrlBranchAndPath()
    {
        var binding = Dev().BindingForRepository("Flou21/hephaisto-fixture-dotnet");

        binding.Should().NotBeNull();
        binding!.Url.Should().Be(InCluster, "the URL is the entry's: on a dev cluster that is the in-cluster git server, not github.com");
        binding.DefaultBranch.Should().Be("fixture/c15-null-deref", "the first of two entries for one repository");
        binding.Path.Should().Be("src/Shop.Api");
        binding.Host.Should().Be("coder-git.hephaisto-coder.svc");
    }

    [Theory]
    [InlineData("https://github.com/octo/shop")]
    [InlineData("https://github.com/octo/shop.git")]
    [InlineData("https://github.com/octo/shop/")]
    [InlineData("https://github.com/Octo/Shop.GIT")]
    [InlineData("http://git.internal/mirrors/octo/shop.git")]
    public void AnEntryNamesARepository_ByTheEndOfItsPath_WhateverTheCaseOrTheHost(string url)
    {
        var options = new CodeFixOptions { Repositories = [new RepositoryBinding { Workload = "a/Deployment/b", Url = url }] };

        options.BindingForRepository("octo/shop").Should().NotBeNull();
        options.BindingForRepository("OCTO/SHOP").Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://github.com/octo/shop-api")]
    [InlineData("https://github.com/not-octo/shop")]
    [InlineData("https://github.com/shop")]
    [InlineData("https://github.com/octo/shop/tree/main")]
    [InlineData("not a url")]
    public void AnEntryForAnotherRepository_IsNotBorrowed(string url)
    {
        var options = new CodeFixOptions { Repositories = [new RepositoryBinding { Workload = "a/Deployment/b", Url = url }] };

        options.BindingForRepository("octo/shop").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("  ")]
    public void NoRepository_MatchesNoEntry(string ownerRepo) => Dev().BindingForRepository(ownerRepo).Should().BeNull();

    [Fact]
    public void WithNoEntry_TheRepositoryIsOnGitHub_OnTheBranchGitHubNames()
    {
        Dev().BindingForRepository("octo/shop").Should().BeNull();

        var asked = CodeFixOptions.GitHubRepository("octo/shop", "develop");

        asked.Url.Should().Be("https://github.com/octo/shop");
        asked.DefaultBranch.Should().Be("develop");
        asked.Host.Should().Be("github.com");
        asked.Path.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WhenGitHubNamesNoBranch_ItIsMain(string? reported) =>
        CodeFixOptions.GitHubRepository("octo/shop", reported).DefaultBranch.Should().Be("main");

    [Fact]
    public void AHostThatIsNotAllowed_RefusesTheWorkItem_WhicheverWayTheUrlWasFound()
    {
        var facts = new CodeFixFacts { Mode = CodeFixMode.Pr, AgentMode = AgentMode.Observe, EmergencyStop = false, RunawayLatched = false };
        var options = new CodeFixEligibilityOptions { AllowedRepositoryHosts = ["github.com"], MaxAttemptsPerRepositoryPerDay = 3, MaxConcurrentJobs = 1, MaxCostUsdPerDay = 50 };

        CodeFixVerdict Judge(RepositoryBinding binding) => CodeFixEligibility.EvaluateWorkItem(
            new WorkItemCandidate { Repository = "Flou21/hephaisto-fixture-dotnet", Taken = true, RepositoryListed = true, Binding = binding }, facts, options);

        Judge(Dev().BindingForRepository("Flou21/hephaisto-fixture-dotnet")!).Codes.Should().Equal(CodeFixReasonCode.RepositoryHostNotAllowed);
        Judge(CodeFixOptions.GitHubRepository("Flou21/hephaisto-fixture-dotnet", null)).Eligible.Should().BeTrue();
    }

    [Fact]
    public void ThePathOfAnAttempt_IsItsWorkloadsEntry_OrForAWorkItemTheFirstWithItsUrl()
    {
        var o = Dev();

        o.PathFor("hephaisto-chaos/Deployment/catalog-api", InCluster).Should().BeEmpty("an incident's attempt reads its own workload's entry");
        o.PathFor(string.Empty, InCluster).Should().Be("src/Shop.Api", "a work item's has no workload and reads the first entry with its URL");
        o.PathFor(string.Empty, "https://github.com/octo/shop").Should().BeEmpty();
    }

    // --- the Job ----------------------------------------------------------------------------

    [Fact]
    public void TheJobOfAWorkItemsAttempt_IsLabelledWithTheWorkItem_AndNoIncident()
    {
        var attempt = new CodeFixAttempt { WorkItemId = Guid.CreateVersion7(), RepositoryUrl = InCluster };
        attempt.Branch = CodeFixJobSpec.BranchName(attempt.Id);

        var job = CodeFixJobSpec.Job(attempt, CodeFixPhase.Plan, new CodeFixOptions { Image = "hephaisto/coder:test" });
        var labels = job.Spec.Template.Metadata.Labels;

        labels[CodeFixJobSpec.AttemptLabel].Should().Be(attempt.Id.ToString());
        labels[CodeFixJobSpec.PhaseLabel].Should().Be("plan");
        labels[CodeFixJobSpec.WorkItemLabel].Should().Be(attempt.WorkItemId.ToString());
        labels.Should().NotContainKey(CodeFixJobSpec.IncidentLabel, "an empty label value would select every Job that has none");
        job.Metadata.Labels.Should().Equal(labels);
        job.Metadata.Name.Should().Be(CodeFixJobSpec.JobName(attempt.Id, CodeFixPhase.Plan));
    }

    [Fact]
    public void TheJobOfAnIncidentsAttempt_IsLabelledAsItAlwaysWas()
    {
        var attempt = new CodeFixAttempt { IncidentId = Guid.CreateVersion7(), Workload = "a/Deployment/b", RepositoryUrl = InCluster };

        CodeFixJobSpec.Job(attempt, CodeFixPhase.Implement, new CodeFixOptions { Image = "hephaisto/coder:test" }).Metadata.Labels.Keys
            .Should().Equal("app.kubernetes.io/name", "app.kubernetes.io/managed-by", CodeFixJobSpec.AttemptLabel, CodeFixJobSpec.IncidentLabel, CodeFixJobSpec.PhaseLabel);
    }

    // --- the lifecycle ----------------------------------------------------------------------

    [Fact]
    public void AWorkItemsAttempt_WalksTheSameEdges_AndIsRefusedTheSameOnes()
    {
        // The state machine has never known what an attempt is for, and still does not: the same
        // edges, the same human-only door, for a row whose subject is a work item.
        var machine = new CodeFixStateMachine(Hephaisto.Tests.TestData.Given.Clock());
        var attempt = new CodeFixAttempt { WorkItemId = Guid.CreateVersion7(), RepositoryUrl = InCluster };

        attempt.State.Should().Be(CodeFixState.Eligible);
        machine.BeginPlanning(attempt, "codefix-x-plan");
        machine.PlanReady(attempt);

        var machineApproves = () => machine.Approve(attempt, "hephaisto/system", ApprovalSource.Api);
        machineApproves.Should().Throw<ArgumentException>().WithMessage("*human act*");
        attempt.State.Should().Be(CodeFixState.PlanReady);

        machine.Approve(attempt, "maintainer", ApprovalSource.Api);
        machine.BeginImplementing(attempt, "codefix-x-impl");
        machine.PrOpened(attempt, "https://github.com/octo/shop/pull/7", 7);

        attempt.State.Should().Be(CodeFixState.PrOpened);
        attempt.ApprovedBy.Should().Be("maintainer");

        var again = () => machine.Cancel(attempt, "too late");
        again.Should().Throw<InvalidOperationException>("a pull request that is open is not an open attempt");
    }

    // --- the result -------------------------------------------------------------------------

    private static CodeFixImplementResult Opened(string url, string branch = "hephaisto/codefix-0192a6f00000", bool build = true, bool tests = true) => new()
    {
        AttemptId = Guid.Parse("0192a6f0-0000-7000-8000-000000000001"),
        Outcome = "pr_opened",
        Branch = branch,
        PrUrl = url,
        PrNumber = 7,
        BaseCommit = null,
        Files = ["src/Shop.Api/Startup/Endpoints.cs"],
        BuildPassed = build,
        TestsPassed = tests,
        LogTail = string.Empty,
        Deviations = [],
        CostUsd = 1,
        SessionId = null,
        Error = null,
        DeniedToolCalls = [],
    };

    /// <summary>
    /// The post-conditions as the coordinator applies them to a work item's attempt: its branch,
    /// its repository URL - however that was found - and the install's hosts.
    /// </summary>
    private static string? Check(CodeFixImplementResult result, RepositoryBinding repository, params string[] hosts) =>
        CodeFixResultParser.CheckImplementPostConditions(result, "hephaisto/codefix-0192a6f00000", repository.Url, hosts, requireGreenBuild: true);

    [Fact]
    public void APullRequestOnTheIssuesOwnRepository_FromTheAssignedBranch_IsBelieved()
    {
        var github = CodeFixOptions.GitHubRepository("octo/shop", "main");

        Check(Opened("https://github.com/octo/shop/pull/7"), github, "github.com").Should().BeNull();

        // The dev cluster: the entry's URL is the in-cluster server's, and so is the pull request's.
        var dev = Dev().BindingForRepository("Flou21/hephaisto-fixture-dotnet")!;

        Check(Opened("http://coder-git.hephaisto-coder.svc/Flou21/hephaisto-fixture-dotnet/pull/1"), dev, "coder-git.hephaisto-coder.svc", "github.com")
            .Should().BeNull();
    }

    [Fact]
    public void APullRequestSomewhereElse_IsRefused_HoweverTheIssueWasWorded()
    {
        var github = CodeFixOptions.GitHubRepository("octo/shop", "main");

        Check(Opened("https://github.com/octo/another/pull/7"), github, "github.com").Should().Contain("not on the mapped repository");
        Check(Opened("https://github.com/octo/shop-api/pull/7"), github, "github.com").Should().Contain("not on the mapped repository");
        Check(Opened("https://evil.example/octo/shop/pull/7"), github, "github.com").Should().Contain("not allowed");
        Check(Opened("https://github.com/octo/shop/pull/7", branch: "main"), github, "github.com").Should().Contain("assigned branch");
        Check(Opened("https://github.com/octo/shop/pull/7", build: false), github, "github.com").Should().Contain("not green");

        // A repository that names github.com while the pull request is on the in-cluster server.
        Check(Opened("http://coder-git.hephaisto-coder.svc/octo/shop/pull/7"), github, "coder-git.hephaisto-coder.svc", "github.com")
            .Should().Contain("not on the mapped repository");
    }

    [Fact]
    public void AnOutcomeThatOpenedNothing_HasNothingToCheck()
    {
        var github = CodeFixOptions.GitHubRepository("octo/shop", "main");

        Check(Opened("https://evil.example/x") with { Outcome = "tests_failed", PrUrl = null }, github, "github.com").Should().BeNull();
        Check(Opened("https://github.com/octo/shop/pull/7") with { Outcome = "merged" }, github, "github.com").Should().Contain("unknown outcome");
    }
}
