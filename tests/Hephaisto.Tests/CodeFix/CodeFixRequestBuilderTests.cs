using System.Text.Json;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.CodeFix;

public sealed class CodeFixRequestBuilderTests
{
    private sealed class Monitor(CodeFixOptions value) : IOptionsMonitor<CodeFixOptions>
    {
        public CodeFixOptions CurrentValue => value;

        public CodeFixOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<CodeFixOptions, string?> listener) => null;
    }

    private static readonly CodeFixOptions Options = new()
    {
        ContextRepositoryUrl = "https://github.com/TrueRelevance/dev-context",
        Repositories = [new RepositoryBinding { Workload = "chaos/Deployment/shop-api", Url = "https://github.com/o/r", Path = "src" }],
    };

    private static (CodeFixAttempt, Incident, Investigation) World(string excerpt = "NullReferenceException at Shop.Api.Startup.Endpoints")
    {
        var step = new InvestigationStep { ToolName = "get_pod_logs" };
        var investigation = new Investigation
        {
            Steps = [step],
            Findings =
            [
                new Finding { Category = "config", Hypothesis = "secondary", Confidence = 0.4 },
                new Finding
                {
                    Category = "application",
                    Hypothesis = "startup dereferences a null list",
                    Confidence = 0.9,
                    IsPrimary = true,
                    Evidence = [new Evidence { StepId = step.Id, Excerpt = excerpt }],
                },
            ],
            Plan = new ActionPlan { Summary = "no action; code bug" },
        };

        var incident = new Incident
        {
            Title = "shop-api crash-looping",
            Kind = SignalKind.CrashLoopBackOff,
            EscalationReason = EscalationReason.NoPlanProduced,
            Target = new TargetRef { Namespace = "chaos", Kind = "Pod", Name = "shop-api-x", OwnerKind = "Deployment", OwnerName = "shop-api" },
        };

        var attempt = new CodeFixAttempt
        {
            IncidentId = incident.Id,
            Workload = "chaos/Deployment/shop-api",
            RepositoryUrl = "https://github.com/o/r",
            DefaultBranch = "main",
        };
        attempt.Branch = CodeFixJobSpec.BranchName(attempt.Id);

        return (attempt, incident, investigation);
    }

    private static CodeFixRequest Build(string excerpt = "NullReferenceException at Shop.Api.Startup.Endpoints")
    {
        var (a, i, inv) = World(excerpt);
        return new CodeFixRequestBuilder(new Monitor(Options)).Build(a, CodeFixPhase.Plan, i, inv, "ghcr.io/o/r:0123", "4", null);
    }

    [Fact]
    public void ThePrimaryFindingComesFirst_WithItsToolAndExcerpt()
    {
        var r = Build();

        r.Findings[0].Primary.Should().BeTrue();
        r.Findings[0].Evidence.Single().Tool.Should().Be("get_pod_logs");
        r.Repository.Path.Should().Be("src");
        r.Incident.Target.Workload.Should().Be("chaos/Deployment/shop-api");
        r.Incident.Image.Should().Be("ghcr.io/o/r:0123");
        r.InvestigationSummary.Should().Be("no action; code bug");
        r.Plan.Should().BeNull();
    }

    [Fact]
    public void ExcerptsAreCapped()
    {
        Build(new string('x', 10_000)).Findings[0].Evidence[0].Excerpt.Length.Should().Be(CodeFixRequestBuilder.MaxExcerptChars);
    }

    [Theory]
    [InlineData("connecting with password=hunter2 to db", "hunter2")]
    [InlineData("Authorization: Bearer " + "abcdefgh" + "ijklmnop", "abcdefghijklmnop")]
    [InlineData("token gh" + "p_0123456789abcdefghijklmnopqrstuv", "0123456789abcdef")]
    [InlineData("mongodb://root:s3cret@mongo:27017", "s3cret")]
    [InlineData("\"apiKey\": \"sk-" + "ant-api03-verysecret\"", "verysecret")]
    public void CredentialShapes_AreScrubbed_FromEvidence(string excerpt, string secret)
    {
        Build(excerpt).Findings[0].Evidence[0].Excerpt.Should().NotContain(secret).And.Contain("[redacted]");
    }

    [Fact]
    public void TheSerialisedRequest_MatchesTheContractShape()
    {
        var json = JsonSerializer.Serialize(Build(), CodeFixContract.Json);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("contract_version").GetString().Should().Be("1");
        doc.RootElement.GetProperty("phase").GetString().Should().Be("plan");
        doc.RootElement.GetProperty("repository").GetProperty("branch").GetString().Should().MatchRegex("^hephaisto/codefix-[0-9a-f]{12}$");
        doc.RootElement.GetProperty("plan").ValueKind.Should().Be(JsonValueKind.Null);
        json.Should().NotMatchRegex("(?i)\"[a-z_]*(token|secret|password)[a-z_]*\"\\s*:");
    }

    [Fact]
    public void AnImplementRequest_NeedsThePlan()
    {
        var (a, i, inv) = World();
        var act = () => new CodeFixRequestBuilder(new Monitor(Options)).Build(a, CodeFixPhase.Implement, i, inv, null, null, null);

        act.Should().Throw<InvalidOperationException>();
    }
}
