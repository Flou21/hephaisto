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
                CodeRefs = [new CodeRef { Repository = "https://github.com/TrueRelevance/hephaisto", Ref = "c421c38", Path = "src/ConnectionHealthCache.cs", Line = 84 }],
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

        // Something was found, so the card offers no second attempt (#124).
        incidents[incidentId].Diagnosed.Should().BeTrue();
        incidents[incidentId].CanReinvestigate.Should().BeFalse();
    }

    [Fact]
    public async Task An_older_finding_still_counts_as_diagnosed_when_the_newest_run_found_nothing()
    {
        // The card shows the newest investigation; whether it offers another attempt is the
        // console's rule, which asks whether ANY investigation has a primary finding (#124).
        await pg.ResetAsync();
        var ct = TestContext.Current.CancellationToken;

        Guid incidentId;
        await using (var db = pg.CreateContext())
        {
            var incident = new Incident
            {
                CorrelationKey = $"cait/Deployment/ledger-{Guid.NewGuid():N}",
                Title = "ledger is crash looping",
                Kind = SignalKind.CrashLoopBackOff,
                Severity = Severity.Warning,
                State = IncidentState.Escalated,
                EscalationReason = EscalationReason.NoPlanProduced,
                Target = new TargetRef { Namespace = "cait", Kind = "Deployment", Name = "ledger" },
                OpenedAt = Now.AddHours(-2),
                LastSignalAt = Now.AddHours(-2),
            };
            db.Incidents.Add(incident);
            incidentId = incident.Id;

            var older = new Investigation
            {
                IncidentId = incident.Id,
                ModelId = "claude-opus-5-5",
                StartedAt = Now.AddMinutes(-30),
                CompletedAt = Now.AddMinutes(-29),
                TerminationReason = TerminationReason.Concluded,
                Executor = InvestigationExecutors.InProcess,
            };
            older.Findings.Add(new Finding
            {
                InvestigationId = older.Id,
                Category = "application",
                Hypothesis = "The ledger cannot reach its database.",
                Confidence = 0.7,
                IsPrimary = true,
            });
            db.Investigations.Add(older);

            db.Investigations.Add(new Investigation
            {
                IncidentId = incident.Id,
                ModelId = "claude-opus-5-5",
                StartedAt = Now.AddMinutes(-3),
                CompletedAt = Now,
                TerminationReason = TerminationReason.Concluded,
                Executor = InvestigationExecutors.InProcess,
            });

            await db.SaveChangesAsync(ct);
        }

        await using var read = pg.CreateContext();
        var incident2 = (await new TeamsBotIncidents(read).ByIdAsync([incidentId], ct))[incidentId];

        incident2.Diagnosis.Should().NotBeNull();
        incident2.Diagnosis!.Grounded.Should().BeFalse("the newest investigation is the one the card shows");
        incident2.Diagnosed.Should().BeTrue("an earlier one found something");
        incident2.CanReinvestigate.Should().BeFalse();
    }

    [Fact]
    public async Task Only_an_action_that_can_still_be_decided_is_what_a_card_offers()
    {
        // #124. The card of an incident awaiting approval names each waiting action above its
        // Approve and Deny. Not an action already decided, and not one that still says it is
        // waiting on an incident somebody closed - nobody can decide that one any more.
        await pg.ResetAsync();
        var ct = TestContext.Current.CancellationToken;

        Guid waitingId, closedId, pendingActionId;
        await using (var db = pg.CreateContext())
        {
            var waiting = new Incident
            {
                CorrelationKey = $"cait/Deployment/api-{Guid.NewGuid():N}",
                Title = "api is crash looping",
                Kind = SignalKind.CrashLoopBackOff,
                Severity = Severity.Critical,
                State = IncidentState.AwaitingApproval,
                Target = new TargetRef { Namespace = "cait", Kind = "Deployment", Name = "api" },
                OpenedAt = Now.AddMinutes(-10),
                LastSignalAt = Now.AddMinutes(-10),
            };
            var closed = new Incident
            {
                CorrelationKey = $"cait/Deployment/ledger-{Guid.NewGuid():N}",
                Title = "ledger is crash looping",
                Kind = SignalKind.CrashLoopBackOff,
                Severity = Severity.Warning,
                State = IncidentState.Closed,
                Target = new TargetRef { Namespace = "cait", Kind = "Deployment", Name = "ledger" },
                OpenedAt = Now.AddMinutes(-20),
                LastSignalAt = Now.AddMinutes(-20),
            };
            db.Incidents.AddRange(waiting, closed);
            waitingId = waiting.Id;
            closedId = closed.Id;

            var pending = new AgentAction
            {
                IncidentId = waiting.Id,
                Type = ActionType.ScaleWorkload,
                Target = new TargetRef { Namespace = "cait", Kind = "Deployment", Name = "api" },
                Arguments = """{"replicas":3}""",
                Risk = RiskTier.Medium,
                State = ActionState.AwaitingApproval,
            };
            pendingActionId = pending.Id;

            db.AgentActions.AddRange(
                pending,
                new AgentAction
                {
                    IncidentId = waiting.Id,
                    Type = ActionType.RestartPod,
                    Target = new TargetRef { Namespace = "cait", Kind = "Pod", Name = "api-7d9f" },
                    State = ActionState.Denied,
                },
                new AgentAction
                {
                    IncidentId = closed.Id,
                    Type = ActionType.RestartPod,
                    Target = new TargetRef { Namespace = "cait", Kind = "Pod", Name = "ledger-0" },
                    State = ActionState.AwaitingApproval,
                });

            await db.SaveChangesAsync(ct);
        }

        await using var read = pg.CreateContext();
        var reader = new TeamsBotIncidents(read);

        var asked = await reader.ByIdAsync([waitingId, closedId], ct, withPendingActions: true);

        var offered = asked[waitingId].PendingActions.Should().ContainSingle().Which;
        offered.Id.Should().Be(pendingActionId);
        offered.Type.Should().Be(ActionType.ScaleWorkload);
        offered.Risk.Should().Be(RiskTier.Medium);
        offered.Target.Should().Be("cait/Deployment/api");
        offered.Arguments.Should().Contain("replicas").And.Contain("3");
        asked[closedId].PendingActions.Should().BeEmpty("its incident is closed, so nothing can decide it");

        // Approvals off, the default: the actions are not read at all.
        var unasked = await reader.ByIdAsync([waitingId], ct);
        unasked[waitingId].PendingActions.Should().BeEmpty();
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

        var never = listed.Should().ContainSingle(i => i.Id == incidentId).Which;

        never.Diagnosis.Should().BeNull();

        // Escalated with nothing found: where the console offers the retry, the card does (#124).
        never.Diagnosed.Should().BeFalse();
        never.CanReinvestigate.Should().BeTrue();
    }
}
