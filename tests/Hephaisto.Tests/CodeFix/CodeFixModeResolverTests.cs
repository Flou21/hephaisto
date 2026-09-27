using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

namespace Hephaisto.Tests.CodeFix;

/// <summary>
/// The code-fix precedence table. Silence is Off, the most restrictive arm wins, and the agent's
/// kill switch beats every arm here - by arm, not by effective mode.
/// </summary>
public sealed class CodeFixModeResolverTests
{
    private const string Stop = "configmap:killSwitch";
    private const string Latch = "db:agent_mode";

    private static ModeResolution Agent(params ModeArm[] arms) => ModeResolver.Resolve(arms);

    private static ModeResolution Observe => Agent(ModeArm.Declaring("env", AgentMode.Observe));

    private static CodeFixModeResolution Resolve(ModeResolution agent, params CodeFixArm[] arms) =>
        CodeFixModeResolver.Resolve(arms, agent, Stop, Latch);

    [Fact]
    public void Silence_IsOff()
    {
        var r = Resolve(Observe, CodeFixModeResolver.Parse("env", null), CodeFixModeResolver.Parse("cm", " "));

        r.Effective.Should().Be(CodeFixMode.Off);
        r.DecidedBy.Should().Be("default");
    }

    [Fact]
    public void MostRestrictiveArmWins()
    {
        var r = Resolve(Observe, CodeFixModeResolver.Parse("env", "Pr"), CodeFixModeResolver.Parse("cm", "plan"));

        r.Effective.Should().Be(CodeFixMode.Plan);
        r.DecidedBy.Should().Be("cm");
    }

    [Theory]
    [InlineData("2")]
    [InlineData("prr")]
    [InlineData("auto")]
    public void MalformedArm_ReadsAsOff(string raw)
    {
        var r = Resolve(Observe, CodeFixModeResolver.Parse("env", "pr"), CodeFixModeResolver.Parse("cm", raw));

        r.Effective.Should().Be(CodeFixMode.Off);
        r.Arms[1].Status.Should().Be(ModeArmStatus.Malformed);
    }

    [Fact]
    public void UnreadableArm_ReadsAsOff()
    {
        Resolve(Observe, CodeFixModeResolver.Parse("env", "pr"), CodeFixModeResolver.Unreadable("cm", "io"))
            .Effective.Should().Be(CodeFixMode.Off);
    }

    [Fact]
    public void AgentObserve_DoesNotConstrain()
    {
        Resolve(Observe, CodeFixModeResolver.Parse("env", "pr")).Effective.Should().Be(CodeFixMode.Pr);
    }

    [Fact]
    public void AgentOff_ForcesOff()
    {
        var r = Resolve(Agent(ModeArm.Declaring("env", AgentMode.Off)), CodeFixModeResolver.Parse("env", "pr"));

        r.Effective.Should().Be(CodeFixMode.Off);
        r.Declared.Should().Be(CodeFixMode.Pr);
    }

    [Fact]
    public void EmergencyStop_ForcesOff_EvenThoughTheAgentAxisSaysObserve()
    {
        // The stop parses as Observe on the agent axis, which this stage runs in happily. Reading
        // only the effective agent mode would leave coders running with the big red button pressed.
        var agent = Agent(
            ModeArm.Declaring("env", AgentMode.Auto),
            ModeResolver.ParseEmergencyStop(Stop, "yes"));

        agent.Effective.Should().Be(AgentMode.Observe);

        var r = Resolve(agent, CodeFixModeResolver.Parse("env", "pr"));

        r.Effective.Should().Be(CodeFixMode.Off);
        r.EmergencyStop.Should().BeTrue();
        r.DecidedBy.Should().Be(Stop);
    }

    [Fact]
    public void DisengagedStop_DoesNotConstrain()
    {
        var agent = Agent(ModeArm.Declaring("env", AgentMode.Auto), ModeResolver.ParseEmergencyStop(Stop, "false"));

        Resolve(agent, CodeFixModeResolver.Parse("env", "plan")).Effective.Should().Be(CodeFixMode.Plan);
    }

    [Fact]
    public void RunawayLatch_ForcesOff()
    {
        var agent = Agent(ModeArm.Declaring("env", AgentMode.Auto), ModeArm.Declaring(Latch, AgentMode.Observe, "runaway latch"));

        var r = Resolve(agent, CodeFixModeResolver.Parse("env", "plan"));

        r.Effective.Should().Be(CodeFixMode.Off);
        r.RunawayLatched.Should().BeTrue();
    }

    [Fact]
    public void UnreadableAgentArm_ForcesOff()
    {
        var agent = Agent(ModeArm.Declaring("env", AgentMode.Auto), ModeArm.Unreadable("configmap:mode", "gone"));

        Resolve(agent, CodeFixModeResolver.Parse("env", "plan")).Effective.Should().Be(CodeFixMode.Off);
    }
}
