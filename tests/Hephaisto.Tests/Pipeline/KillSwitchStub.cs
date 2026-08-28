using Hephaisto.Agent.Safety;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

namespace Hephaisto.Tests.Pipeline;

/// <summary>
/// A kill switch that answers whatever the test tells it to, and records that it was asked.
/// </summary>
/// <remarks>
/// <para>
/// The first <see cref="IKillSwitch"/> double in the suite. It exists because the gates it
/// feeds are all inside <c>BackgroundService</c> loops, and a test that cannot say "the loop
/// has now seen this signal" has to fall back on sleeping, which is how a suite acquires
/// intermittent failures.
/// </para>
/// <para>
/// <see cref="Asked"/> completes on the first resolve, so a test can await the loop reaching
/// the gate instead of guessing how long that takes.
/// </para>
/// </remarks>
internal sealed class KillSwitchStub(AgentMode mode) : IKillSwitch
{
    private readonly TaskCompletionSource asked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes the first time anything asks this switch for the mode.</summary>
    public Task Asked => asked.Task;

    public int ResolveCount { get; private set; }

    public IReadOnlyList<ModeArm> ExternalArms => [];

    public ModeResolution External => Build(mode);

    public Task<ModeResolution> ResolveAsync(CancellationToken ct)
    {
        ResolveCount++;
        asked.TrySetResult();

        return Task.FromResult(Build(mode));
    }

    private static ModeResolution Build(AgentMode effective) => new()
    {
        Effective = effective,
        DecidedBy = "test",
        Arms = [],
        IsConstrained = false,
    };
}
