using System.Text.Json.Serialization;

namespace Hephaisto.Eval.Scoring;

/// <summary>Where the coder's read-only plan put the fix.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CodeFixPlanVerdict>))]
public enum CodeFixPlanVerdict
{
    /// <summary>
    /// Names an expected file, touches nothing forbidden, and states the mechanism.
    /// </summary>
    RightLocation = 0,

    /// <summary>A plan was made, and it points somewhere else or for the wrong reason.</summary>
    WrongLocation = 1,

    /// <summary>
    /// No file was proposed - <c>not_a_code_problem</c>, <c>insufficient_context</c>, a failed
    /// run, or no result at all. Counted in the denominator, for the reason
    /// <see cref="RootCauseVerdict.NoFinding"/> is: a coder that plans less must not score higher.
    /// </summary>
    NoPlan = 2,
}

/// <summary>
/// Grades a code-fix plan's location, deterministically, from its <c>files</c> and
/// <c>root_cause</c> alone.
/// </summary>
/// <remarks>
/// <para>
/// The plan phase is the cheap one - read-only, no build, no PR - and this is what makes it worth
/// running on its own and in repeats: whether the plan points at the right code is a fact about
/// two fields, not an opinion, so it needs no second model and no repository.
/// </para>
/// <para>
/// <b>Right location means the right file for the right reason.</b> A plan naming
/// <c>Startup/Endpoints.cs</c> because the stack trace does, while explaining the crash as a log
/// format problem, has found the file and not the fault - so the root cause has to mention one of
/// <see cref="CodeFixAnswerKey.RootCauseMustMentionAnyOf"/> as well. And a plan that ALSO touches
/// <c>deploy/**</c> is proposing the wrong fix beside the right one; it is graded wrong, because
/// approving it would ship both.
/// </para>
/// <para>
/// Whether the fix is correct is not this grader's question. That is the diff and the tests on
/// the PR head, against the same key.
/// </para>
/// </remarks>
public static class CodeFixPlanGrader
{
    public static CodeFixPlanVerdict Grade(
        CodeFixAnswerKey key, IReadOnlyList<string>? files, string? rootCause) =>
        Grade(key, files, rootCause, out _);

    public static CodeFixPlanVerdict Grade(
        CodeFixAnswerKey key, IReadOnlyList<string>? files, string? rootCause, out string reason)
    {
        ArgumentNullException.ThrowIfNull(key);

        var proposed = (files ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();

        if (proposed.Count == 0)
        {
            reason = "the plan proposes no file to change";
            return CodeFixPlanVerdict.NoPlan;
        }

        var forbidden = proposed.Where(key.IsForbiddenPath).ToList();
        if (forbidden.Count > 0)
        {
            reason = $"the plan touches {string.Join(", ", forbidden)}, which "
                + $"{key.Fixture}'s key forbids ({string.Join(", ", key.MustNotTouch)})";
            return CodeFixPlanVerdict.WrongLocation;
        }

        if (!proposed.Any(key.IsExpectedFile))
        {
            reason = $"the plan names {string.Join(", ", proposed)} and none of "
                + string.Join(", ", key.ExpectedFilesAnyOf);
            return CodeFixPlanVerdict.WrongLocation;
        }

        var cause = rootCause ?? string.Empty;
        var mentioned = key.RootCauseMustMentionAnyOf
            .FirstOrDefault(t => cause.Contains(t, StringComparison.OrdinalIgnoreCase));

        if (mentioned is null)
        {
            reason = "the plan names the right file, and its root cause states none of "
                + string.Join(", ", key.RootCauseMustMentionAnyOf.Select(t => $"'{t}'"));
            return CodeFixPlanVerdict.WrongLocation;
        }

        reason = $"names {proposed.First(key.IsExpectedFile)} and mentions '{mentioned}'";
        return CodeFixPlanVerdict.RightLocation;
    }
}
