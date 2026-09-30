using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Investigations;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// What the cards read about an incident's investigation, against a real database: the newest
/// investigation, its primary finding with its first citations and its code references, and the
/// plan's summary - in a fixed number of queries whatever the number of incidents.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TeamsBotDiagnosisTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_newest_investigation_is_the_diagnosis_on_the_card()
    {
        await pg.ResetAsync();
        var ct = TestContext.Current.CancellationToken;

        Guid incidentId;
        await using (var db = pg.CreateContext())
        {
            var incident = new Incident
            {
                CorrelationKey = $"hephaisto/Deployment/hephaisto-{Guid.NewGuid():N}",
                Title = "CrashLoopBackOff on hephaisto",
                Kind = SignalKind.CrashLoopBackOff,
                Severity = Severity.Critical,
                State = IncidentState.Escalated,
                EscalationReason = EscalationReason.NoPlanProduced,
                Target = new TargetRef { Namespace = "hephaisto", Kind = "Deployment", Name = "hephaisto" },
                OpenedAt = Now.AddHours(-9),
                LastSignalAt = Now.AddHours(-9),
            };
            db.Incidents.Add(incident);
            incidentId = incident.Id;

            // The older one grounded nothing; it must not be what the card shows.
            db.Investigations.Add(new Investigation
            {
                IncidentId = incident.Id,
                ModelId = "true-relevance",
                StartedAt = Now.AddMinutes(-9),
                CompletedAt = Now.AddMinutes(-8),
                TerminationReason = TerminationReason.Concluded,
                Executor = InvestigationExecutors.JobFallback,
            });

            var newest = new Investigation
            {
                IncidentId = incident.Id,
                ModelId = "claude-opus-5-5",
                StartedAt = Now.AddMinutes(-3),
                CompletedAt = Now,
                TerminationReason = TerminationReason.Concluded,
                Executor = InvestigationExecutors.Job,
            };
            var step = new InvestigationStep
            {
                InvestigationId = newest.Id,
                Ordinal = 1,
                Kind = StepKind.ToolCall,
                ToolName = "query_loki_logs",
                ToolServer = "grafana-mcp",
                ResultDigest = "at OidcProbe.ProbeAsync",
                At = Now,
            };
            var finding = new Finding
            {
                InvestigationId = newest.Id,
                Category = "application",
                Hypothesis = "ConnectionHealthCache lets a probe timeout stop the host.",
                Confidence = 0.85,
                IsPrimary = true,
                CodeRefs = [new CodeRef { Repository = "https://github.com/Flou21/hephaisto", Ref = "c421c38", Path = "src/ConnectionHealthCache.cs", Line = 84 }],
            };
            finding.Evidence.Add(new Evidence { FindingId = finding.Id, StepId = step.Id, Excerpt = "at OidcProbe.ProbeAsync" });
            newest.Steps.Add(step);
            newest.Findings.Add(finding);
            newest.Plan = new ActionPlan
            {
                InvestigationId = newest.Id,
                Summary = "A code fix is needed; nothing in the cluster to do.",
                NoActionRequired = true,
            };
            db.Investigations.Add(newest);

            await db.SaveChangesAsync(ct);
        }

        await using var read = pg.CreateContext();
        var incidents = await new TeamsBotIncidents(read).ByIdAsync([incidentId], ct);

        var diagnosis = incidents[incidentId].Diagnosis;
        diagnosis.Should().NotBeNull();
        diagnosis!.Grounded.Should().BeTrue();
        diagnosis.Hypothesis.Should().Be("ConnectionHealthCache lets a probe timeout stop the host.");
        diagnosis.Confidence.Should().Be(0.85);
        diagnosis.Executor.Should().Be(InvestigationExecutors.Job);
        diagnosis.ModelId.Should().Be("claude-opus-5-5");
        diagnosis.Summary.Should().Be("A code fix is needed; nothing in the cluster to do.");
        diagnosis.Evidence.Should().Equal("at OidcProbe.ProbeAsync");
        diagnosis.CodeRefs.Should().ContainSingle().Which.Line.Should().Be(84);
    }

    [Fact]
    public async Task An_incident_never_investigated_has_no_diagnosis()
    {
        await pg.ResetAsync();
        var ct = TestContext.Current.CancellationToken;

        Guid incidentId;
        await using (var db = pg.CreateContext())
        {
            var incident = new Incident
            {
                CorrelationKey = $"cait/Deployment/api-{Guid.NewGuid():N}",
                Title = "api is crash looping",
                Kind = SignalKind.CrashLoopBackOff,
                Severity = Severity.Warning,
                State = IncidentState.Escalated,
                EscalationReason = EscalationReason.NotInvestigated,
                Target = new TargetRef { Namespace = "cait", Kind = "Deployment", Name = "api" },
                OpenedAt = Now,
                LastSignalAt = Now,
            };
            db.Incidents.Add(incident);
            incidentId = incident.Id;
            await db.SaveChangesAsync(ct);
        }

        await using var read = pg.CreateContext();
        var (listed, _) = await new TeamsBotIncidents(read).OpenAsync(10, ct);

        listed.Should().ContainSingle(i => i.Id == incidentId).Which.Diagnosis.Should().BeNull();
    }
}
