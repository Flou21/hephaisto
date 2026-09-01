using System.Text.Json;
using Hephaisto.Agent.Investigations;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// The boundary between "what a language model produced" and "what Hephaisto will consider
/// doing". Everything past this type is typed data the policy engine judges.
/// </summary>
public class ActionPlanDraftTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Action_types_round_trip_as_strings()
    {
        // The model emits a name, not an ordinal. An ordinal would silently remap the day
        // somebody inserts an enum member.
        var json = JsonSerializer.Serialize(
            new ActionDraft { Type = ActionType.RolloutRestart },
            Json);

        json.Should().Contain("\"RolloutRestart\"");

        JsonSerializer.Deserialize<ActionDraft>(json, Json)!.Type.Should().Be(ActionType.RolloutRestart);
    }

    [Fact]
    public void A_permanently_denied_type_deserialises_rather_than_throwing()
    {
        // DeletePvc is in the enum precisely so a plan naming it is recorded and refused with
        // a reason, instead of failing to deserialise into an unknown value and producing
        // "no plan" with no explanation.
        var draft = JsonSerializer.Deserialize<ActionDraft>(
            """{"type":"DeletePvc","namespace":"hephaisto-chaos","kind":"PersistentVolumeClaim","name":"data"}""",
            Json);

        draft!.Type.Should().Be(ActionType.DeletePvc);
    }

    [Fact]
    public void An_invented_action_type_fails_to_deserialise()
    {
        var act = () => JsonSerializer.Deserialize<ActionDraft>(
            """{"type":"RunArbitraryShellCommand"}""",
            Json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Mapping_leaves_the_decision_at_default_deny()
    {
        var finding = Guid.CreateVersion7();

        var draft = new ActionPlanDraft
        {
            Summary = "Restart it.",
            Actions =
            [
                new ActionDraft
                {
                    Type = ActionType.RolloutRestart,
                    Namespace = "hephaisto-chaos",
                    Kind = "Deployment",
                    Name = "api",
                    PredictedEffect = "Ready for five minutes.",
                    RollbackJson = """{"revision":4}""",
                    EvidenceFindingIds = [finding.ToString()],
                },
            ],
        };

        var plan = ActionPlanDraftMapper.TryToDomain(
            draft, Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);

        var action = plan.Actions.Should().ContainSingle().Subject;

        action.State.Should().Be(ActionState.Proposed);
        action.Decision.Should().Be(PolicyDecision.Deny);
        action.EvidenceFindingIds.Should().ContainSingle().Which.Should().Be(finding);
        action.Target.WorkloadKey.Should().Be("hephaisto-chaos/Deployment/api");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void Malformed_arguments_become_null_rather_than_reaching_the_executor(string arguments)
    {
        // These end up in a jsonb column and in front of the executor's parser. Rejecting
        // them here turns "the insert failed" into an action with no arguments, which the
        // policy engine handles as the ordinary case it is.
        var draft = new ActionPlanDraft
        {
            Actions =
            [
                new ActionDraft
                {
                    Type = ActionType.ScaleWorkload,
                    Namespace = "shop",
                    Kind = "Deployment",
                    Name = "checkout",
                    ArgumentsJson = arguments,
                    RollbackJson = arguments,
                },
            ],
        };

        var plan = ActionPlanDraftMapper.TryToDomain(
            draft, Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);

        plan.Actions[0].Arguments.Should().BeNull();

        // Absence of a rollback is meaningful: an action with none can never be executed
        // automatically, whatever its risk tier.
        plan.Actions[0].RollbackSpec.Should().BeNull();
    }

    [Fact]
    public void Well_formed_arguments_survive()
    {
        var draft = new ActionPlanDraft
        {
            Actions =
            [
                new ActionDraft
                {
                    Type = ActionType.ScaleWorkload,
                    Namespace = "shop",
                    Kind = "Deployment",
                    Name = "checkout",
                    ArgumentsJson = """{"replicas":3}""",
                },
            ],
        };

        var plan = ActionPlanDraftMapper.TryToDomain(
            draft, Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);

        plan.Actions[0].Arguments.Should().Contain("replicas");
    }

    [Theory]
    [InlineData(null, "Deployment", "checkout")]
    [InlineData("", "Deployment", "checkout")]
    [InlineData("   ", "Deployment", "checkout")]
    [InlineData("shop", null, "checkout")]
    [InlineData("shop", "", "checkout")]
    [InlineData("shop", "Deployment", null)]
    [InlineData("shop", "Deployment", "")]
    public void An_action_with_no_usable_target_is_dropped(string? ns, string? kind, string? name)
    {
        // These three fields are NOT NULL in agent_actions. The declared type says they
        // cannot be null, but System.Text.Json writes an explicit JSON null over the
        // string.Empty initialiser without consulting nullable annotations - so this is
        // reachable, and it used to fail the INSERT and take the whole investigation with
        // it (23502 on target_namespace).
        var draft = new ActionPlanDraft
        {
            Actions =
            [
                new ActionDraft
                {
                    Type = ActionType.RestartPod,
                    Namespace = ns!,
                    Kind = kind!,
                    Name = name!,
                },
            ],
        };

        var plan = ActionPlanDraftMapper.TryToDomain(
            draft, Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);

        plan.Actions.Should().BeEmpty();

        // And NOT reported as "nothing to do". The model believed remediation was needed;
        // it just failed to say what to act on. Zero actions with NoActionRequired false is
        // what escalates that to a human, which is the honest outcome.
        plan.NoActionRequired.Should().BeFalse();
    }

    [Fact]
    public void A_well_targeted_action_alongside_a_malformed_one_survives()
    {
        var draft = new ActionPlanDraft
        {
            Actions =
            [
                new ActionDraft { Type = ActionType.RestartPod, Namespace = null!, Kind = "Pod", Name = "a" },
                new ActionDraft { Type = ActionType.RestartPod, Namespace = "shop", Kind = "Pod", Name = "b" },
            ],
        };

        var plan = ActionPlanDraftMapper.TryToDomain(
            draft, Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);

        plan.Actions.Should().ContainSingle();
        plan.Actions[0].Target.Name.Should().Be("b");
    }

    [Fact]
    public void An_empty_action_list_maps_to_no_action_required()
    {
        var plan = ActionPlanDraftMapper.TryToDomain(
            new ActionPlanDraft { Summary = "Nothing to do." },
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            DateTimeOffset.UnixEpoch);

        plan.NoActionRequired.Should().BeTrue();
        plan.Actions.Should().BeEmpty();
    }

    // #72: a RestartPod recorded without an owner is permanently Inconclusive. The pod it
    // names is deleted by the action itself, and VerificationChecks has no health predicate
    // for a bare Pod - so the incident sits in Verifying forever. The incident already knows
    // the owner; the model cannot.
    private static ActionPlanDraft RestartDraft(string ns, string kind, string name) => new()
    {
        Summary = "restart it",
        Actions =
        [
            new ActionDraft
            {
                Type = ActionType.RestartPod,
                Namespace = ns,
                Kind = kind,
                Name = name,
                Risk = RiskTier.Low,
            },
        ],
    };

    [Fact]
    public void An_action_on_the_incident_target_inherits_its_owner()
    {
        var incidentTarget = new TargetRef
        {
            Namespace = "hephaisto-chaos",
            Kind = "Pod",
            Name = "c12-stale-lease-abc",
            OwnerKind = "Deployment",
            OwnerName = "c12-stale-lease",
        };

        var plan = ActionPlanDraftMapper.TryToDomain(
            RestartDraft("hephaisto-chaos", "Pod", "c12-stale-lease-abc"),
            Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, incidentTarget);

        var action = plan.Actions.Should().ContainSingle().Subject;

        action.Target.OwnerKind.Should().Be("Deployment");
        action.Target.OwnerName.Should().Be("c12-stale-lease");

        // And that is what verification keys on: the workload, not the pod that is about to
        // stop existing.
        action.Target.WorkloadKey.Should().Be("hephaisto-chaos/Deployment/c12-stale-lease");
    }

    [Fact]
    public void An_action_on_a_different_object_inherits_nothing()
    {
        // An owner copied onto an object it does not own is a worse record than none: it would
        // send verification to look at an unrelated workload and call the result an answer.
        var incidentTarget = new TargetRef
        {
            Namespace = "hephaisto-chaos",
            Kind = "Pod",
            Name = "c12-stale-lease-abc",
            OwnerKind = "Deployment",
            OwnerName = "c12-stale-lease",
        };

        var plan = ActionPlanDraftMapper.TryToDomain(
            RestartDraft("hephaisto-chaos", "Pod", "some-other-pod"),
            Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, incidentTarget);

        var action = plan.Actions.Should().ContainSingle().Subject;

        action.Target.OwnerKind.Should().BeNull();
        action.Target.OwnerName.Should().BeNull();
    }

    [Fact]
    public void With_no_incident_target_the_owner_is_simply_absent()
    {
        var plan = ActionPlanDraftMapper.TryToDomain(
            RestartDraft("hephaisto-chaos", "Pod", "c12-stale-lease-abc"),
            Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);

        plan.Actions.Should().ContainSingle().Which.Target.OwnerKind.Should().BeNull();
    }
}
