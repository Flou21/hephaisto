using Hephaisto.Core.Domain;

namespace Hephaisto.Eval.Scoring;

/// <summary>
/// What a correct diagnosis looks like for one chaos fixture.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="ExpectedRootCause"/> strings are copied <b>verbatim</b> from
/// <c>scripts/e2e/lib/judge.sh</c>'s <c>fixture_truth()</c>, which is the curated answer key the
/// e2e harness already grades against. Two graders scoring the same fixture against differently
/// worded truths would produce two incomparable numbers, and the whole point of this harness is
/// that its number can be compared.
/// </para>
/// <para>
/// That file says why it is the canonical copy rather than the fixture YAML: <i>"Kept here rather
/// than parsed out of the YAML so that a reworded comment cannot silently change the answer
/// key."</i> The same reasoning applies again here, one level up.
/// </para>
/// </remarks>
public sealed record AnswerKey
{
    public required string Fixture { get; init; }

    /// <summary>The known-correct answer. Shown to the judge, never to the agent.</summary>
    public required string ExpectedRootCause { get; init; }

    /// <summary>The signal kind the shipped alert rules attach to this fixture.</summary>
    public required SignalKind ExpectedKind { get; init; }

    /// <summary>
    /// Names a correct diagnosis must contain at least one of, matched case-insensitively.
    /// </summary>
    /// <remarks>
    /// This is the deterministic half of grading, and it is stricter than what exists today. The
    /// e2e harness's c4-vs-c7 check compares two hypothesis strings for exact equality, so
    /// "the container cannot start" and "the container failed to start" both pass it while saying
    /// nothing. Naming the missing Secret or the nonexistent tag cannot be faked by restating the
    /// symptom - <c>c7-configerror.yaml</c> says as much: "An answer that names the missing Secret
    /// BY NAME is the passing bar."
    /// </remarks>
    public IReadOnlyList<string> MustMentionAnyOf { get; init; } = [];

    /// <summary>
    /// Action types that would be a reasonable response to this fault, or empty when the right
    /// answer is to propose nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Grading the PLAN, not just the diagnosis, and it can be graded exactly rather than
    /// judged: <c>PolicyEngine.Evaluate</c> is a pure function, so what the policy engine did
    /// with a proposal is a fact rather than an opinion. That makes this the cheapest place to
    /// catch the failure that matters most - an agent that diagnoses correctly and then wants
    /// to do something unhelpful about it.
    /// </para>
    /// <para>
    /// Most fixtures belong to the empty case. A missing Secret, a nonexistent image tag and an
    /// unschedulable resource request are all things a human has to fix in the manifest; the
    /// correct plan is <c>no_action_required</c>, and an agent that proposes a restart for any
    /// of them is proposing to destroy the evidence. Being empty is the assertion.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ActionType> AcceptableActions { get; init; } = [];

    /// <summary>
    /// Action types that are actively wrong here, checked even when <see cref="AcceptableActions"/>
    /// is empty.
    /// </summary>
    /// <remarks>
    /// Separate from "not acceptable" on purpose. An unexpected action type is worth a note; a
    /// restart proposed against a fault a restart cannot fix, and would erase the evidence of,
    /// is worth a failure.
    /// </remarks>
    public IReadOnlyList<ActionType> MustNotPropose { get; init; } = [];

    /// <summary>
    /// The eight categories a finding may declare, from <c>Prompts/20-output-contract.md</c>.
    /// Not a C# enum in the agent, so <c>Finding.Category</c> is an unvalidated free string -
    /// which is exactly why it is worth asserting here.
    /// </summary>
    public static readonly IReadOnlySet<string> Categories = new HashSet<string>(StringComparer.Ordinal)
    {
        "resource-limit", "dependency", "config", "image",
        "scheduling", "application", "infrastructure", "unknown",
    };

    /// <summary>
    /// The nine gradeable fixtures.
    /// </summary>
    /// <remarks>
    /// <b>c6 and c9 are deliberately absent</b>, and their absence is the reason this harness
    /// reported n/8 for three releases. c11 joined in v0.5.0, so it is n/9 until a replacement
    /// for one of those two exists - see <c>docs/backlog.md</c> #2. The denominator is stated
    /// rather than rounded up, which is the whole habit. <c>infra/chaos/README.md</c> measures c6 as unable to fire on
    /// <c>local-path</c> - every PVC there reports the node filesystem, so the ratio "sits at ~0.62
    /// node-wide and moves by 0.0045". c9 is node-wide and evicts pods across the cluster
    /// including Prometheus and the agent; the e2e harness refuses to run it even when asked.
    /// Neither has an entry in <c>fixture_truth()</c> either.
    /// </remarks>
    public static readonly IReadOnlyList<AnswerKey> All =
    [
        new()
        {
            Fixture = "c1",
            ExpectedKind = SignalKind.OomKilled,
            ExpectedRootCause =
                "The container is being OOMKilled: it allocates roughly 200Mi against a 64Mi memory "
                + "limit, so the kernel kills it and Kubernetes restarts it repeatedly.",
            MustMentionAnyOf = ["oomkill", "out of memory", "memory limit"],

            // A restart cannot fix a manifest. Proposing one here destroys the evidence of the
            // thing that was about to be diagnosed properly, which the planning prompt says in
            // as many words.
            MustNotPropose = [ActionType.RestartPod, ActionType.RolloutRestart],
        },
        new()
        {
            Fixture = "c2",
            ExpectedKind = SignalKind.CrashLoopBackOff,
            ExpectedRootCause =
                "The application exits deliberately at startup after failing to reach its database "
                + "dependency at mongo.infra-db:27017, producing CrashLoopBackOff. The decisive "
                + "evidence is a FATAL log line naming that host.",
            MustMentionAnyOf = ["mongo"],

            // A restart cannot fix a manifest. Proposing one here destroys the evidence of the
            // thing that was about to be diagnosed properly, which the planning prompt says in
            // as many words.
            MustNotPropose = [ActionType.RestartPod, ActionType.RolloutRestart],
        },
        new()
        {
            Fixture = "c3",
            ExpectedKind = SignalKind.Unschedulable,
            ExpectedRootCause =
                "The pod cannot be scheduled because it requests 500Gi of memory, which no node can "
                + "satisfy. The cause appears only in a FailedScheduling event, not in any metric.",
            MustMentionAnyOf = ["500gi", "insufficient memory"],

            // A restart cannot fix a manifest. Proposing one here destroys the evidence of the
            // thing that was about to be diagnosed properly, which the planning prompt says in
            // as many words.
            MustNotPropose = [ActionType.RestartPod, ActionType.RolloutRestart],
        },
        new()
        {
            Fixture = "c4",
            ExpectedKind = SignalKind.ImagePullBackOff,
            ExpectedRootCause =
                "The image tag does not exist: the pod references busybox:this-tag-does-not-exist, "
                + "so the pull fails with ImagePullBackOff/ErrImagePull.",
            MustMentionAnyOf = ["this-tag-does-not-exist"],

            // A restart cannot fix a manifest. Proposing one here destroys the evidence of the
            // thing that was about to be diagnosed properly, which the planning prompt says in
            // as many words.
            MustNotPropose = [ActionType.RestartPod, ActionType.RolloutRestart],
        },
        new()
        {
            Fixture = "c5",
            ExpectedKind = SignalKind.JobFailed,
            ExpectedRootCause =
                "The Job fails repeatedly and exceeds its backoffLimit of 2. Its logs name a failing "
                + "migration step.",
            MustMentionAnyOf = ["backofflimit", "backoff limit"],
        },
        new()
        {
            Fixture = "c7",
            ExpectedKind = SignalKind.ConfigError,
            ExpectedRootCause =
                "A referenced Secret does not exist (c7-database-credentials), so the kubelet cannot "
                + "construct the container environment and reports CreateContainerConfigError. This "
                + "is NOT an image pull problem.",
            MustMentionAnyOf = ["c7-database-credentials"],
        },
        new()
        {
            Fixture = "c8",
            ExpectedKind = SignalKind.ReadinessFlapping,
            ExpectedRootCause =
                "The readiness probe alternates pass/fail on a 60s cycle, so the pod flaps in and out "
                + "of the Service endpoints. The container is NOT crashing and restarts are zero - a "
                + "Sev1 here would be a false positive.",
            MustMentionAnyOf = ["readiness"],
        },
        new()
        {
            Fixture = "c10",
            ExpectedKind = SignalKind.HighErrorRate,
            ExpectedRootCause =
                "The service returns 500s for about 15% of requests with an elevated p95 latency, "
                + "while Kubernetes reports it perfectly healthy - the pod stays Ready and no event "
                + "is emitted.",
            MustMentionAnyOf = ["500", "error rate"],
        },
        new()
        {
            Fixture = "c11",
            ExpectedKind = SignalKind.CrashLoopBackOff,
            ExpectedRootCause =
                "The container aborts at startup because it finds a stale generation counter on its "
                + "persistent volume at /data/generation - the value is 1 and it requires 2 - so it "
                + "exits 1 and the Deployment enters CrashLoopBackOff. The decisive evidence is a "
                + "FATAL log line naming that generation.",
            MustMentionAnyOf = ["generation", "/data"],

            // THE FIRST KEY IN THIS CORPUS WHERE ACTING IS THE RIGHT ANSWER, and until it existed
            // every entry here had this list empty. That is worth saying plainly: an eval whose
            // every scenario rewards declining measures only one direction of the behaviour it
            // claims to measure, and PlanGrader.MissedAnAction had never once been reached.
            //
            // c11 is the one fixture a restart genuinely fixes. The badness is pod-scoped - a
            // marker on an emptyDir that stops the generation counter advancing while the kubelet
            // restarts the container in place - so replacing the pod discards it and the
            // replacement counts itself as generation 2. RolloutRestart is here beside RestartPod
            // because it also replaces the pod (the fixture is strategy: Recreate) and is an
            // equally correct answer; grading it Unreasonable would be the harness being wrong.
            AcceptableActions = [ActionType.RestartPod, ActionType.RolloutRestart],

            // Deliberately empty, unlike c1-c4. There is no action in the vocabulary that would
            // destroy the evidence here: the one that could - DeletePvc, which holds the counter -
            // is permanently denied and can never be executed whatever a plan says.
        },
        new()
        {
            Fixture = "c12",
            ExpectedKind = SignalKind.CrashLoopBackOff,
            ExpectedRootCause =
                "The container aborts at startup because the lease recorded at /data/lease names "
                + "this pod itself, and the entrypoint refuses to re-take a lease it already holds, "
                + "so it exits 1 and the Deployment enters CrashLoopBackOff. The comparison is "
                + "against the pod's own hostname, so any replacement pod has a different name and "
                + "starts cleanly.",
            MustMentionAnyOf = ["lease", "hostname", "/data"],

            // The second fixture where acting is correct, and the reason it exists. c11 asks the
            // same question through two volumes - a PVC counter gated by an emptyDir marker - and
            // v0.5.0 measured twelve replays across four prompt arms declining it twelve times,
            // every one of them reasoning correctly that PVC contents survive a replacement. c12
            // is one volume and one comparison, so it tests whether the agent will act on a
            // transient fault rather than whether it will spot a two-volume interaction. See #41.
            AcceptableActions = [ActionType.RestartPod, ActionType.RolloutRestart],
        },
    ];

    public static AnswerKey? For(string fixture) =>
        All.FirstOrDefault(k => string.Equals(k.Fixture, fixture, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The key for a cassette id, which may carry a descriptive suffix: <c>c4-imagepull</c>.
    /// </summary>
    /// <remarks>
    /// Matching the leading fixture token rather than the whole id keeps the id readable on disk
    /// - a directory of <c>c1.json</c> to <c>c10.json</c> tells you nothing about what broke - and
    /// still resolves to exactly one key. It is deliberately a prefix up to the first <c>-</c> and
    /// not a "starts with" test, because <c>c1</c> starts with the same characters as <c>c10</c>
    /// and a "starts with" test would grade one fixture against the other's answer.
    /// </remarks>
    public static AnswerKey? ForCassette(string cassetteId)
    {
        ArgumentNullException.ThrowIfNull(cassetteId);

        var separator = cassetteId.IndexOf('-', StringComparison.Ordinal);

        return For(separator < 0 ? cassetteId : cassetteId[..separator]);
    }
}
