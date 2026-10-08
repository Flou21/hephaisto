namespace Hephaisto.Core.Domain;

/// <summary>
/// Where a piece of work stands. Three states, because that is all a hand-over has: Hephaisto
/// has it, it is finished, or it was taken back.
/// </summary>
public enum WorkItemState
{
    /// <summary>Assigned to Hephaisto and not finished. At most one per issue.</summary>
    Taken = 0,

    /// <summary>Its pull request was merged.</summary>
    Done = 1,

    /// <summary>Taken back: the issue was closed, or Hephaisto is no longer an assignee.</summary>
    Cancelled = 2,
}

/// <summary>
/// A piece of work somebody handed to Hephaisto: an issue assigned to its account (v0.14.0).
/// </summary>
/// <remarks>
/// <para>
/// The second way in. An <see cref="Incident"/> is opened by an alert; a work item is opened by a
/// person, on purpose, and what it asks for is whatever the issue says. It is its own entity
/// rather than a kind of incident for the reason a <see cref="CodeFixAttempt"/> is not a state of
/// one: it has a different lifecycle, and nothing about it is a workload being unhealthy.
/// </para>
/// <para>
/// <b><see cref="Body"/> is a snapshot, taken once.</b> The issue's text goes to a model as data.
/// Somebody who can edit the issue after a plan was approved must not be able to change what the
/// approved plan is read against, so a later edit is not copied here - and the same issue handed
/// over a second time, after a cancel, is a new row with its own snapshot.
/// </para>
/// </remarks>
public sealed class WorkItem
{
    /// <summary>The one source there is. A column, so that a second one is a value and not a migration.</summary>
    public const string GitHubSource = "github";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Source { get; set; } = GitHubSource;

    /// <summary><c>owner/repo</c>, as the install lists it.</summary>
    public string Repository { get; set; } = string.Empty;

    public int Number { get; set; }

    /// <summary>GitHub's global id for the issue. Survives a rename or a transfer of the repository.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>The page a person opens.</summary>
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The issue's type where the repository has types, else the first of the labels
    /// <c>bug</c>, <c>enhancement</c>, <c>feature</c> it carries. Null when it says neither.
    /// </summary>
    public string? Type { get; set; }

    public string AuthorLogin { get; set; } = string.Empty;

    /// <summary>The author's account number. A login can be renamed and taken by somebody else; this cannot.</summary>
    public long AuthorId { get; set; }

    /// <summary>The issue's text when it was taken. Never updated - see the remarks.</summary>
    public string Body { get; set; } = string.Empty;

    public List<string> Labels { get; set; } = [];

    public WorkItemState State { get; set; } = WorkItemState.Taken;

    /// <summary>Why it was cancelled, in a sentence. Null while taken.</summary>
    public string? StateReason { get; set; }

    public DateTimeOffset TakenAt { get; set; }

    /// <summary>When it stopped being <see cref="WorkItemState.Taken"/>.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The one comment Hephaisto keeps up to date on the issue. GitHub's id, beyond 32 bits.</summary>
    public long? StatusCommentId { get; set; }

    /// <summary>
    /// SHA-256 of the status comment's text as it was last written. What makes "edit only when
    /// the text changed" true across a restart: the comparison is with this, never with a read
    /// of the comment.
    /// </summary>
    public string? StatusCommentDigest { get; set; }

    /// <summary>
    /// The reason codes of the last answer "no plan now", comma-separated, while there is no
    /// attempt. Asked again on every pass; recorded - and said on the issue - only when the
    /// codes are different ones. Null once an attempt exists.
    /// </summary>
    public string? DeclineCodes { get; set; }

    /// <summary>The same answer as a sentence, as it was when the codes last changed.</summary>
    public string? DeclineReason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsOpen => State == WorkItemState.Taken;
}
