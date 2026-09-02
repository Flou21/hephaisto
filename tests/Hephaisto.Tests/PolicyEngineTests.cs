using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;
using Hephaisto.Tests.TestData;

namespace Hephaisto.Tests;

/// <summary>
/// These tests are the safety argument. Every rule the policy engine enforces is a promise
/// about what the agent will never do, and a promise with no test behind it is a comment.
/// </summary>
public sealed class PolicyEngineTests
{
    // --- default deny -------------------------------------------------------------------

    [Fact]
    public void EmptyOptions_DenyEverything()
    {
        // An unconfigured PolicyOptions must be inert. If a ConfigMap fails to bind, the agent
        // has to fall silent rather than inherit whatever the defaults happen to permit.
        var result = PolicyEngine.Evaluate(Given.Request(), Given.Facts(), new PolicyOptions());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*not in the allowed namespaces*");
    }

    [Fact]
    public void DefaultDecision_IsDeny()
    {
        // The enum's zero value carries the fail-safe direction; a default-constructed result
        // must never be an accidental allow.
        default(PolicyDecision).Should().Be(PolicyDecision.Deny);
    }

    [Fact]
    public void BaselineFixture_Allows()
    {
        // Proves the fixtures are permissive, so every deny below is caused by the one fact
        // the test changed and not by leftover setup.
        var result = PolicyEngine.Evaluate(Given.Request(), Given.Facts(), Given.Options());

        result.Decision.Should().Be(PolicyDecision.Allow);
        result.Reasons.Should().NotBeEmpty();
        result.DowngradedFrom.Should().BeNull();
    }

    // --- hard denials -------------------------------------------------------------------

    [Theory]
    [InlineData(ActionType.DeletePvc)]
    [InlineData(ActionType.DeleteWorkload)]
    public void PermanentlyDeniedActionTypes_AreNeverApprovable(ActionType type)
    {
        var result = PolicyEngine.Evaluate(Given.Request(type), Given.Facts(), Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*permanently denied*");
    }

    [Theory]
    [InlineData(ActionType.DeletePvc)]
    [InlineData(ActionType.DeleteWorkload)]
    public void PermanentlyDeniedActionTypes_OutrankRollback(ActionType type)
    {
        var request = Given.Request(type) with { IsRollback = true };

        PolicyEngine.Evaluate(request, Given.Facts(), Given.Options())
            .Decision.Should().Be(PolicyDecision.Deny);
    }

    [Fact]
    public void AllowlistedNamespace_StillDenies_WithoutTheNamespacesOwnOptIn()
    {
        // The allowlist is the operator's authority; the label is the namespace owner's. One
        // without the other is half a decision. This is the check the RBAC manifests have
        // described as "a second, independent confirmation" since before any code read it.
        var facts = Given.Facts() with { NamespaceLabels = Given.Labels() };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*does not carry hephaisto.dev/destructive-actions-allowed=true*");
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("1")]
    public void NamespaceOptIn_RequiresLiterallyTrue(string value)
    {
        // "yes" and "1" are the plausible near-misses. Accepting them would mean the label
        // silently means something different from what the manifests document.
        var facts = Given.Facts() with
        {
            NamespaceLabels = Given.Labels(("hephaisto.dev/destructive-actions-allowed", value)),
        };

        PolicyEngine.Evaluate(Given.Request(), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Deny);
    }

    [Fact]
    public void NamespaceOptIn_IsCaseInsensitiveOnTheValue()
    {
        var facts = Given.Facts() with
        {
            NamespaceLabels = Given.Labels(("hephaisto.dev/destructive-actions-allowed", "True")),
        };

        PolicyEngine.Evaluate(Given.Request(), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    [Fact]
    public void NamespaceOptIn_BeatsRollback()
    {
        // Same reasoning as ProtectedNamespace_BeatsRollback: undoing an action in a namespace
        // is itself an action in that namespace.
        var facts = Given.Facts() with { NamespaceLabels = Given.Labels() };
        var request = Given.Request() with { IsRollback = true };

        PolicyEngine.Evaluate(request, facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Deny);
    }

    [Fact]
    public void NamespaceOptIn_CanBeDisabledByOperatorsWhoDoNotLabelNamespaces()
    {
        // An escape hatch that fails CLOSED unless deliberately emptied: the default is the
        // label, and turning the check off is a visible configuration act.
        var options = Given.Options();
        options.RequiredNamespaceLabel = string.Empty;

        var facts = Given.Facts() with { NamespaceLabels = Given.Labels() };

        PolicyEngine.Evaluate(Given.Request(), facts, options)
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    [Fact]
    public void NamespaceLabels_AreNotSatisfiedByTheSameLabelOnTheTarget()
    {
        // The two label sets are separate fields precisely so a workload cannot opt its own
        // namespace in. Merging them would make this pass, which is the bug being prevented.
        var facts = Given.Facts() with
        {
            NamespaceLabels = Given.Labels(),
            TargetLabels = Given.Labels(("hephaisto.dev/destructive-actions-allowed", "true")),
        };

        PolicyEngine.Evaluate(Given.Request(), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Deny);
    }

    [Theory]
    [InlineData("kube-system")]
    [InlineData("hephaisto")]
    [InlineData("hephaisto-obs")]
    public void ProtectedNamespace_Denies(string ns)
    {
        var options = Given.Options();
        options.AllowedNamespaces.Add(ns);

        var request = Given.Request() with { Target = Given.Target(ns) };

        var result = PolicyEngine.Evaluate(request, Given.Facts(), options);

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*is protected*");
    }

    [Fact]
    public void ProtectedNamespace_BeatsRollback()
    {
        // The one that matters most: "I am only undoing something" must not become a way into
        // kube-system, because the undo of a bad action there is itself an action there.
        var options = Given.Options();
        options.AllowedNamespaces.Add("kube-system");

        var request = Given.Request() with { Target = Given.Target("kube-system"), IsRollback = true };

        var result = PolicyEngine.Evaluate(request, Given.Facts(), options);

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*is protected*");
    }

    [Fact]
    public void NamespaceNotOnAllowlist_Denies()
    {
        var request = Given.Request() with { Target = Given.Target("someone-elses-team") };

        var result = PolicyEngine.Evaluate(request, Given.Facts(), Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*not in the allowed namespaces*");
    }

    [Fact]
    public void ProtectedLabelOnTarget_Denies()
    {
        var facts = Given.Facts() with { TargetLabels = Given.Labels(("hephaisto.dev/protected", "true")) };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*protected label*");
    }

    [Fact]
    public void ProtectedLabelWithDifferentValue_DoesNotDeny()
    {
        // The label is a key/value opt-out, not a key presence check: `protected=false` means
        // someone considered it and said no.
        var facts = Given.Facts() with { TargetLabels = Given.Labels(("hephaisto.dev/protected", "false")) };

        PolicyEngine.Evaluate(Given.Request(), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    // --- mode ---------------------------------------------------------------------------

    [Fact]
    public void ModeOff_Denies()
    {
        var result = PolicyEngine.Evaluate(Given.Request(), Given.Facts() with { Mode = AgentMode.Off }, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*agent is off*");
    }

    [Fact]
    public void ModeObserve_Denies()
    {
        var result = PolicyEngine.Evaluate(
            Given.Request(),
            Given.Facts() with { Mode = AgentMode.Observe },
            Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().Contain("agent is in observe mode");
    }

    [Fact]
    public void ModeDryRun_DoesNotDenyButNeedsApproval()
    {
        // DryRun exercises the whole executor with dryRun=All, so it must get past the mode
        // gate - but it is still not Auto, so nothing runs unattended.
        var result = PolicyEngine.Evaluate(
            Given.Request(),
            Given.Facts() with { Mode = AgentMode.DryRun },
            Given.Options());

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.DowngradedFrom.Should().Be(PolicyDecision.Allow);
    }

    // --- quarantine, grounding ------------------------------------------------------------

    [Fact]
    public void ActiveQuarantine_Denies()
    {
        var facts = Given.Facts() with { QuarantinedUntil = Given.Now.AddHours(4) };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*quarantined*");
    }

    [Fact]
    public void ExpiredQuarantine_DoesNotDeny()
    {
        var facts = Given.Facts() with { QuarantinedUntil = Given.Now.AddHours(-1) };

        PolicyEngine.Evaluate(Given.Request(), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    [Fact]
    public void NoGroundedFinding_Denies()
    {
        var request = Given.Request() with { GroundedFindingIds = [] };

        var result = PolicyEngine.Evaluate(request, Given.Facts(), Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().Contain("no grounded finding justifies this action");
    }

    [Fact]
    public void NoGroundedFinding_IsFineForARollback()
    {
        // Undoing does not need a fresh diagnosis. Requiring one would mean a rollback is
        // blocked exactly when the investigation that produced the bad action was wrong.
        var request = Given.Request() with { GroundedFindingIds = [], IsRollback = true };

        PolicyEngine.Evaluate(request, Given.Facts(), Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    // --- stability ------------------------------------------------------------------------

    [Fact]
    public void RolloutInFlightByGeneration_Denies()
    {
        var facts = Given.Facts() with { Workload = Given.Workload() with { Generation = 8, ObservedGeneration = 7 } };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().Contain("a rollout is in flight; do not fight a human deploy");
    }

    [Fact]
    public void RolloutInFlightByUpdatedReplicas_Denies()
    {
        var facts = Given.Facts() with { Workload = Given.Workload() with { UpdatedReplicas = 1 } };

        PolicyEngine.Evaluate(Given.Request(), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Deny);
    }

    [Fact]
    public void PodYoungerThanMinimum_Denies()
    {
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with { YoungestPodAge = TimeSpan.FromSeconds(10) },
        };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*fair chance to become healthy*");
    }

    [Fact]
    public void MaintenanceWindow_Denies()
    {
        var facts = Given.Facts() with { InMaintenanceWindow = true };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*maintenance window*");
    }

    [Fact]
    public void ClusterWideUnhealthiness_Denies()
    {
        var facts = Given.Facts() with { ClusterUnhealthyFraction = 0.6 };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*cluster-wide event, not a pod-level problem*");
    }

    [Fact]
    public void UnhealthinessAtTheCeiling_DoesNotDeny()
    {
        var facts = Given.Facts() with { ClusterUnhealthyFraction = 0.3 };

        PolicyEngine.Evaluate(Given.Request(), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    // --- blast radius ---------------------------------------------------------------------

    [Fact]
    public void TooManyAffectedPods_Denies()
    {
        var options = Given.Options();
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with { DesiredReplicas = 40, ReadyReplicas = 40, UpdatedReplicas = 40 },
        };
        var request = Given.Request(ActionType.RolloutRestart) with { AffectedPodCount = 11 };

        var result = PolicyEngine.Evaluate(request, facts, options);

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*exceeds the maximum of 10*");
    }

    [Fact]
    public void TooLargeAFractionOfTheWorkload_Denies()
    {
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with { DesiredReplicas = 4, ReadyReplicas = 4, UpdatedReplicas = 4 },
        };
        var request = Given.Request(ActionType.RolloutRestart) with { AffectedPodCount = 3 };

        var result = PolicyEngine.Evaluate(request, facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*of the workload exceeds*");
    }

    [Fact]
    public void HalfOfAWorkload_IsWithinTheFractionCap()
    {
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with { DesiredReplicas = 4, ReadyReplicas = 4, UpdatedReplicas = 4 },
        };
        var request = Given.Request(ActionType.RolloutRestart) with { AffectedPodCount = 2 };

        PolicyEngine.Evaluate(request, facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    // --- not the last one standing ----------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    public void RestartingTheLastReadyReplica_Denies(int desired, int ready)
    {
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with
            {
                DesiredReplicas = desired,
                ReadyReplicas = ready,
                UpdatedReplicas = desired,
            },
        };

        var result = PolicyEngine.Evaluate(Given.Request(ActionType.RestartPod), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*last Ready replica*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AWorkloadWithNothingReady_CanBeRestarted(int desired)
    {
        // This case used to deny, and the denial said "this would restart the last Ready
        // replica (0 ready of N desired)" - a sentence that cannot be true. Nothing is Ready,
        // so there is no last one to protect: the workload is already down, a restart cannot
        // degrade it, and it is the only thing that might help.
        //
        // Not a corner case, which is why it is a theory over both shapes. A crash-looping pod
        // is BY DEFINITION not Ready, so while this denied, RestartPod - the single action type
        // v0.2.0 promotes to auto - could never fire on the fault it exists for. It was found
        // by running the acceptance test and watching the agent propose exactly the right thing
        // and be refused.
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with
            {
                DesiredReplicas = desired,
                ReadyReplicas = 0,
                UpdatedReplicas = desired,
            },
        };

        PolicyEngine.Evaluate(Given.Request(ActionType.RestartPod), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    [Fact]
    public void TheLastReadyReplicaRule_StillProtectsAServiceThatIsStillServing()
    {
        // The other side of the same change, and the one that must not regress: one Ready
        // replica out of three is a degraded service, and restarting it makes it a down one.
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with
            {
                DesiredReplicas = 3,
                ReadyReplicas = 1,
                UpdatedReplicas = 3,
            },
        };

        var result = PolicyEngine.Evaluate(Given.Request(ActionType.RestartPod), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*last Ready replica*");
    }

    [Fact]
    public void SingleReplicaEscapeHatchLabel_Allows()
    {
        var options = Given.Options();
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with { DesiredReplicas = 1, ReadyReplicas = 1, UpdatedReplicas = 1 },
            TargetLabels = Given.Labels((options.AllowSingleReplicaRestartLabel, "true")),
        };

        PolicyEngine.Evaluate(Given.Request(ActionType.RestartPod), facts, options)
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    [Fact]
    public void SingleReplicaEscapeHatchSetToFalse_StillDenies()
    {
        var options = Given.Options();
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with { DesiredReplicas = 1, ReadyReplicas = 1, UpdatedReplicas = 1 },
            TargetLabels = Given.Labels((options.AllowSingleReplicaRestartLabel, "false")),
        };

        PolicyEngine.Evaluate(Given.Request(ActionType.RestartPod), facts, options)
            .Decision.Should().Be(PolicyDecision.Deny);
    }

    [Fact]
    public void LastReplicaRule_AppliesOnlyToRestartPod()
    {
        // Deleting a Job's failed pods on a single-replica workload takes nothing down. This
        // used to be asserted with SilenceAlert, which is no longer allow-eligible at all -
        // see NeverAutoEnabled below - so it would now pass for the wrong reason.
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with { DesiredReplicas = 1, ReadyReplicas = 1, UpdatedReplicas = 1 },
        };

        PolicyEngine.Evaluate(Given.Request(ActionType.DeleteFailedJobPods), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    // --- cooldown -------------------------------------------------------------------------

    [Fact]
    public void WithinWorkloadCooldown_Denies()
    {
        var facts = Given.Facts() with { LastActionOnWorkloadAt = Given.Now.AddMinutes(-5) };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*cooldown active*");
    }

    [Fact]
    public void AfterWorkloadCooldown_DoesNotDeny()
    {
        var facts = Given.Facts() with { LastActionOnWorkloadAt = Given.Now.AddMinutes(-16) };

        PolicyEngine.Evaluate(Given.Request(), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    [Fact]
    public void CooldownAppliesToRollbacksToo()
    {
        // A rollback bypasses budgets, not the cooldown: the cooldown exists to let the
        // cluster settle, and an undo disturbs it just as much as the original action did.
        var facts = Given.Facts() with { LastActionOnWorkloadAt = Given.Now.AddMinutes(-1) };
        var request = Given.Request() with { IsRollback = true };

        PolicyEngine.Evaluate(request, facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Deny);
    }

    // --- risk routing ---------------------------------------------------------------------

    [Theory]
    [InlineData(ActionType.RestartPod)]
    [InlineData(ActionType.DeleteStuckJob)]
    [InlineData(ActionType.DeleteFailedJobPods)]
    public void LowRiskActionTypes_AreAllowEligible(ActionType type)
    {
        var facts = Given.Facts() with
        {
            // Sized so the last-replica rule is out of the way for RestartPod.
            Workload = Given.Workload() with { DesiredReplicas = 3, ReadyReplicas = 3 },
        };

        PolicyEngine.Evaluate(Given.Request(type), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    /// <summary>
    /// <see cref="ActionType.SilenceAlert"/> can never be executed unattended, whatever the
    /// operator configures.
    /// </summary>
    /// <remarks>
    /// It was in the low-risk set until v0.3.0, and it satisfies every word of that set's
    /// description - cheap, reversible, single-object. The reason it does not belong there is
    /// not about risk of damage, it is about what failure looks like: every other action fails
    /// VISIBLY when it is wrong, and a wrong silence fails by making the cluster look quiet.
    ///
    /// This asserts the promotion path cannot reach it. An operator who puts SilenceAlert in
    /// autoEnabledActionTypes - which the chart's schema happily permits, because the list is
    /// the full ActionType enum - still gets RequireApproval, because eligibility is decided
    /// before the autonomy gate is consulted.
    /// </remarks>
    [Fact]
    public void SilenceAlert_AlwaysRequiresApproval_EvenWhenAutoEnabled()
    {
        var options = Given.Options();
        options.AutoEnabledActionTypes.Add(ActionType.SilenceAlert);

        var facts = Given.Facts() with
        {
            Mode = AgentMode.Auto,
            Workload = Given.Workload() with { DesiredReplicas = 3, ReadyReplicas = 3 },
        };

        var result = PolicyEngine.Evaluate(Given.Request(ActionType.SilenceAlert), facts, options);

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.Reasons.Should().Contain(r => r.Contains("stops a human being told", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ActionType.ScaleWorkload)]
    [InlineData(ActionType.PatchResources)]
    [InlineData(ActionType.CordonNode)]
    [InlineData(ActionType.DrainNode)]
    public void HigherRiskActionTypes_AlwaysRequireApproval(ActionType type)
    {
        var options = Given.Options();
        // Even explicitly auto-enabled, these must not become automatic.
        options.AutoEnabledActionTypes.Add(type);

        var result = PolicyEngine.Evaluate(Given.Request(type), Given.Facts(), options);

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.DowngradedFrom.Should().BeNull("this was never allow-eligible, so nothing was downgraded");
    }

    [Fact]
    public void RolloutRestartWithinTheUnattendedLimit_Allows()
    {
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with { DesiredReplicas = 40, ReadyReplicas = 40, UpdatedReplicas = 40 },
        };
        var request = Given.Request(ActionType.RolloutRestart) with { AffectedPodCount = 10 };

        PolicyEngine.Evaluate(request, facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    [Fact]
    public void RolloutRestartAboveTheUnattendedLimit_RequiresApproval()
    {
        var options = Given.Options();
        options.MaxPodsPerAction = 50;

        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with { DesiredReplicas = 40, ReadyReplicas = 40, UpdatedReplicas = 40 },
        };
        var request = Given.Request(ActionType.RolloutRestart) with { AffectedPodCount = 11 };

        var result = PolicyEngine.Evaluate(request, facts, options);

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.DowngradedFrom.Should().BeNull();
    }

    [Fact]
    public void RollbackOfAFreshRevisionToAProvenOne_Allows()
    {
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with
            {
                CurrentRevisionAge = TimeSpan.FromMinutes(10),
                PreviousRevisionHealthyFor = TimeSpan.FromDays(2),
            },
        };

        PolicyEngine.Evaluate(Given.Request(ActionType.RollbackDeployment), facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    [Fact]
    public void RollbackOfAnOldRevision_RequiresApproval()
    {
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with { CurrentRevisionAge = TimeSpan.FromHours(9) },
        };

        var result = PolicyEngine.Evaluate(Given.Request(ActionType.RollbackDeployment), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.Reasons.Should().ContainMatch("*not a fresh deploy*");
    }

    [Fact]
    public void RollbackToARevisionWithNoTrackRecord_RequiresApproval()
    {
        var facts = Given.Facts() with
        {
            Workload = Given.Workload() with
            {
                CurrentRevisionAge = TimeSpan.FromMinutes(10),
                PreviousRevisionHealthyFor = TimeSpan.FromMinutes(4),
            },
        };

        var result = PolicyEngine.Evaluate(Given.Request(ActionType.RollbackDeployment), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.Reasons.Should().ContainMatch("*no proven healthy history*");
    }

    [Fact]
    public void ActionTypeNone_Denies()
    {
        // An unroutable type must fall to Deny, so a new ActionType added without a rule is
        // inert until someone writes one.
        var result = PolicyEngine.Evaluate(Given.Request(ActionType.None), Given.Facts(), Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().ContainMatch("*no routing rule*");
    }

    // --- autonomy gate --------------------------------------------------------------------

    [Fact]
    public void ActionTypeNotAutoEnabled_DowngradesToApproval()
    {
        var options = Given.Options();
        options.AutoEnabledActionTypes.Remove(ActionType.RestartPod);

        var result = PolicyEngine.Evaluate(Given.Request(), Given.Facts(), options);

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.DowngradedFrom.Should().Be(PolicyDecision.Allow);
        result.Reasons.Should().ContainMatch("*not in the auto-enabled set*");
    }

    [Fact]
    public void NoAutoEnabledTypesAtAll_DowngradesEverything()
    {
        var options = Given.Options();
        options.AutoEnabledActionTypes.Clear();

        PolicyEngine.Evaluate(Given.Request(), Given.Facts(), options)
            .Decision.Should().Be(PolicyDecision.RequireApproval);
    }

    // --- budgets --------------------------------------------------------------------------

    [Fact]
    public void ExhaustedIncidentBudget_DowngradesRatherThanDenies()
    {
        // Downgrade, not deny: an exhausted budget must stop the agent acting alone, not stop
        // the on-call engineer from clicking approve during an outage.
        var facts = Given.Facts() with { ActionsOnIncident = 3 };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.DowngradedFrom.Should().Be(PolicyDecision.Allow);
        result.Reasons.Should().ContainMatch("*incident action budget exhausted*");
    }

    [Fact]
    public void ExhaustedWorkloadHourlyBudget_Downgrades()
    {
        var facts = Given.Facts() with { RecentActionsOnWorkload = 2 };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.Reasons.Should().ContainMatch("*workload hourly action budget exhausted*");
    }

    [Fact]
    public void ExhaustedClusterHourlyBudget_Downgrades()
    {
        var facts = Given.Facts() with { ActionsClusterWideLastHour = 10 };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.Reasons.Should().ContainMatch("*cluster hourly action budget exhausted*");
    }

    [Fact]
    public void ExhaustedClusterDailyBudget_Downgrades()
    {
        var facts = Given.Facts() with { ActionsClusterWideLastDay = 20 };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.Reasons.Should().ContainMatch("*cluster daily action budget exhausted*");
    }

    [Fact]
    public void Rollback_BypassesEveryBudget()
    {
        // You must always be able to undo, and being at the cap is exactly the situation in
        // which something needs taking back.
        var facts = Given.Facts() with
        {
            ActionsOnIncident = 99,
            RecentActionsOnWorkload = 99,
            ActionsClusterWideLastHour = 99,
            ActionsClusterWideLastDay = 99,
        };
        var request = Given.Request() with { IsRollback = true };

        var result = PolicyEngine.Evaluate(request, facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Allow);
        result.Reasons.Should().ContainMatch("*budget checks bypassed*");
    }

    [Fact]
    public void Rollback_DoesNotBypassTheAllowlist()
    {
        var facts = Given.Facts() with { ActionsOnIncident = 99 };
        var request = Given.Request() with { IsRollback = true, Target = Given.Target("not-allowed") };

        PolicyEngine.Evaluate(request, facts, Given.Options())
            .Decision.Should().Be(PolicyDecision.Deny);
    }

    // --- rollback spec --------------------------------------------------------------------

    [Fact]
    public void ActionWithoutARollbackSpec_IsNeverAllowed()
    {
        // RolloutRestart rather than the default RestartPod: a pod delete is exempt, because
        // it has no inverse to specify. See SelfHealingTypes_* below.
        var request = Given.Request(ActionType.RolloutRestart) with { HasRollbackSpec = false };

        var result = PolicyEngine.Evaluate(request, Given.Facts(), Given.Options());

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.DowngradedFrom.Should().Be(PolicyDecision.Allow);
        result.Reasons.Should().ContainMatch("*no rollback spec*");
    }

    [Theory]
    [InlineData(ActionType.DeleteStuckJob)]
    [InlineData(ActionType.SilenceAlert)]
    [InlineData(ActionType.RolloutRestart)]
    public void NoRollbackSpec_DowngradesEveryAllowEligibleType(ActionType type)
    {
        var request = Given.Request(type) with { HasRollbackSpec = false };

        PolicyEngine.Evaluate(request, Given.Facts(), Given.Options())
            .Decision.Should().NotBe(PolicyDecision.Allow);
    }

    [Theory]
    [InlineData(ActionType.RestartPod)]
    [InlineData(ActionType.DeleteFailedJobPods)]
    public void SelfHealingTypes_AreAllowedWithoutARollbackSpec(ActionType type)
    {
        // Deleting a pod is not undone, it is reconciled: the controller recreates it, which
        // is the entire mechanism of "restart a pod". There is no prior state to return to, so
        // requiring a rollback spec would ask the model for a fiction - and would make
        // RestartPod, the action v0.2.0 exists to automate, permanently RequireApproval.
        var request = Given.Request(type) with { HasRollbackSpec = false };

        PolicyEngine.Evaluate(request, Given.Facts(), Given.Options())
            .Decision.Should().Be(PolicyDecision.Allow);
    }

    [Fact]
    public void TheSelfHealingExemption_IsExactlyTwoTypesWide()
    {
        // Pins the width. The exemption weakens gate 14, so it earns a test that fails when
        // someone widens it - adding a type whose effects a controller does NOT reconcile
        // would let an unrevertable action run unattended, and nothing else would notice.
        var exempt = new[] { ActionType.RestartPod, ActionType.DeleteFailedJobPods };

        foreach (var type in Enum.GetValues<ActionType>().Except(exempt))
        {
            var request = Given.Request(type) with { HasRollbackSpec = false };

            PolicyEngine.Evaluate(request, Given.Facts(), Given.Options())
                .Decision.Should().NotBe(
                    PolicyDecision.Allow,
                    $"{type} is not self-healing, so it needs a rollback spec to run unattended");
        }
    }

    // --- reason accumulation ---------------------------------------------------------------

    [Fact]
    public void MultipleViolations_AreAllReported()
    {
        // One reason out of four is a misleading post-mortem, so denials accumulate rather
        // than short-circuiting on the first rule that fires.
        var request = Given.Request() with
        {
            Target = Given.Target("kube-system"),
            GroundedFindingIds = [],
        };
        var facts = Given.Facts() with { Mode = AgentMode.Observe, InMaintenanceWindow = true };

        var result = PolicyEngine.Evaluate(request, facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Reasons.Should().HaveCountGreaterThanOrEqualTo(4);
        result.Reasons.Should().ContainMatch("*is protected*");
        result.Reasons.Should().ContainMatch("*not in the allowed namespaces*");
        result.Reasons.Should().Contain("agent is in observe mode");
        result.Reasons.Should().ContainMatch("*maintenance window*");
    }

    [Fact]
    public void HardDenial_IsReportedFirst()
    {
        var request = Given.Request(ActionType.DeletePvc) with { GroundedFindingIds = [] };

        var result = PolicyEngine.Evaluate(request, Given.Facts(), Given.Options());

        result.Reasons[0].Should().Match("*permanently denied*");
    }

    [Fact]
    public void Evaluate_IsDeterministic()
    {
        var request = Given.Request();
        var facts = Given.Facts();
        var options = Given.Options();

        var first = PolicyEngine.Evaluate(request, facts, options);
        var second = PolicyEngine.Evaluate(request, facts, options);

        second.Decision.Should().Be(first.Decision);
        second.Reasons.Should().Equal(first.Reasons);
    }

    [Fact]
    public void Evaluate_RejectsNullArguments()
    {
        var act = () => PolicyEngine.Evaluate(Given.Request(), Given.Facts(), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // --- reason codes ---------------------------------------------------------------------

    /// <summary>
    /// Every gate reports itself as a closed code, so the per-gate breakdown is answerable from
    /// metrics without prose in a label.
    /// </summary>
    /// <remarks>
    /// backlog #40. The label used to be the verdict's first reason, and those are sentences
    /// written for a person - "workload is quarantined until 2026-08-30T12:34:56.789Z" - so it
    /// was unbounded series on a counter that fires for every proposed action (backlog #12).
    /// Taking the prose out was right and cost the breakdown; this is the breakdown back, as an
    /// enum carried BESIDE each sentence rather than parsed out of it. Deriving it from the
    /// string would silently start answering wrongly the moment somebody improved a sentence.
    /// </remarks>
    [Fact]
    public void A_protected_namespace_reports_itself()
    {
        var options = Given.Options();
        var request = Given.Request() with
        {
            Target = new TargetRef { Namespace = "kube-system", Kind = "Pod", Name = "x" },
        };

        PolicyEngine.Evaluate(request, Given.Facts(), options)
            .Codes.Should().Contain(PolicyReasonCode.ProtectedNamespace);
    }

    [Fact]
    public void An_unlisted_namespace_reports_itself()
    {
        var request = Given.Request() with
        {
            Target = new TargetRef { Namespace = "somewhere-else", Kind = "Pod", Name = "x" },
        };

        PolicyEngine.Evaluate(request, Given.Facts(), Given.Options())
            .Codes.Should().Contain(PolicyReasonCode.NamespaceNotAllowed);
    }

    [Fact]
    public void A_quarantine_reports_itself()
    {
        var facts = Given.Facts() with { QuarantinedUntil = Given.Now.AddHours(4) };

        PolicyEngine.Evaluate(Given.Request(), facts, Given.Options())
            .Codes.Should().Contain(PolicyReasonCode.Quarantined);
    }

    [Fact]
    public void An_ungrounded_action_reports_itself()
    {
        var request = Given.Request() with { GroundedFindingIds = [] };

        PolicyEngine.Evaluate(request, Given.Facts(), Given.Options())
            .Codes.Should().Contain(PolicyReasonCode.Ungrounded);
    }

    [Fact]
    public void Observe_mode_reports_itself_separately_from_off()
    {
        // The two are distinct codes for the reason they are distinct sentences: Observe is the
        // normal operating mode, and "denied because the agent is off" would read as a fault.
        PolicyEngine.Evaluate(Given.Request(), Given.Facts() with { Mode = AgentMode.Observe }, Given.Options())
            .Codes.Should().Contain(PolicyReasonCode.ObserveMode);

        PolicyEngine.Evaluate(Given.Request(), Given.Facts() with { Mode = AgentMode.Off }, Given.Options())
            .Codes.Should().Contain(PolicyReasonCode.AgentOff);
    }

    [Fact]
    public void An_action_that_always_needs_a_human_reports_why_it_was_not_eligible()
    {
        var facts = Given.Facts() with
        {
            Mode = AgentMode.Auto,
            Workload = Given.Workload() with { DesiredReplicas = 3, ReadyReplicas = 3 },
        };

        var result = PolicyEngine.Evaluate(Given.Request(ActionType.ScaleWorkload), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.PrimaryCode.Should().Be(PolicyReasonCode.NotAllowEligible);
    }

    [Fact]
    public void A_downgrade_reports_which_gate_downgraded_it()
    {
        // Allow-eligible, but the type is not promoted. Distinct from NotAllowEligible: one is
        // a property of the action, the other of the operator's configuration, and telling them
        // apart is most of what the breakdown is for.
        var facts = Given.Facts() with
        {
            Mode = AgentMode.Auto,
            Workload = Given.Workload() with { DesiredReplicas = 3, ReadyReplicas = 3 },
        };

        var options = Given.Options();
        options.AutoEnabledActionTypes.Clear();

        var result = PolicyEngine.Evaluate(Given.Request(), facts, options);

        result.Decision.Should().Be(PolicyDecision.RequireApproval);
        result.DowngradedFrom.Should().Be(PolicyDecision.Allow);
        result.Codes.Should().Contain(PolicyReasonCode.TypeNotAutoEnabled);
    }

    /// <summary>
    /// The structural invariant: a denial has exactly one code per reason, and none of them is
    /// <see cref="PolicyReasonCode.None"/>.
    /// </summary>
    /// <remarks>
    /// This is what catches the next bare <c>denials.Add(...)</c>. The codes are carried in a
    /// parallel list, so a site that adds a sentence without a code produces a mismatch here
    /// rather than a metric that quietly attributes the denial to whichever gate happened to
    /// fire alongside it.
    /// </remarks>
    [Fact]
    public void Every_denial_carries_exactly_one_code_and_never_None()
    {
        var facts = Given.Facts() with
        {
            Mode = AgentMode.Off,
            QuarantinedUntil = Given.Now.AddHours(4),
            InMaintenanceWindow = true,
        };

        var request = Given.Request() with
        {
            Target = new TargetRef { Namespace = "kube-system", Kind = "Pod", Name = "x" },
            GroundedFindingIds = [],
        };

        var result = PolicyEngine.Evaluate(request, facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Deny);
        result.Codes.Should().HaveCount(result.Reasons.Count);
        result.Codes.Should().NotContain(PolicyReasonCode.None);
        result.Codes.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_clean_allow_attributes_itself_to_no_gate()
    {
        var facts = Given.Facts() with
        {
            Mode = AgentMode.Auto,
            Workload = Given.Workload() with { DesiredReplicas = 3, ReadyReplicas = 3 },
        };

        var result = PolicyEngine.Evaluate(Given.Request(), facts, Given.Options());

        result.Decision.Should().Be(PolicyDecision.Allow);
        result.PrimaryCode.Should().Be(PolicyReasonCode.None);
    }
}
