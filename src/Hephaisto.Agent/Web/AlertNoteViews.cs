namespace Hephaisto.Agent.Web;

/// <summary>
/// The note kept for one alert name (#145), as the API and the console show it.
/// </summary>
/// <remarks>
/// A name nobody has written about yet is answered with <see cref="Exists"/> false and an empty
/// body rather than a 404: "nothing is known about this alert" is the ordinary state, and the
/// console's first act on it is to write something.
/// </remarks>
public sealed record AlertNoteView
{
    public string AlertName { get; init; } = string.Empty;

    public bool Exists { get; init; }

    public string Body { get; init; } = string.Empty;

    public string? UpdatedBy { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Newest first, capped at <see cref="IncidentQueries.MaxAlertNoteEntriesShown"/>.</summary>
    public IReadOnlyList<AlertNoteEntryView> Entries { get; init; } = [];

    /// <summary>How many entries exist in all, which may be more than are listed.</summary>
    public int EntryCount { get; init; }
}

public sealed record AlertNoteEntryView
{
    public Guid Id { get; init; }

    public Guid? IncidentId { get; init; }

    public string Author { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>What happened to a request to change a note.</summary>
public enum AlertNoteOutcome
{
    Applied = 0,

    /// <summary>An unusable alert name, an empty entry, or text over its cap.</summary>
    Invalid = 1,

    /// <summary>A model identity tried to write as a person.</summary>
    ForbiddenActor = 2,
}

public sealed record AlertNoteResult
{
    public AlertNoteOutcome Outcome { get; init; }

    public string? Detail { get; init; }

    public AlertNoteView? Note { get; init; }
}

/// <summary>The body of <c>PUT /api/alerts/{name}/note</c>.</summary>
public sealed record SaveAlertNoteRequest
{
    public string? Body { get; init; }

    /// <summary>Who is writing it. Ignored when the request is authenticated.</summary>
    public string? UpdatedBy { get; init; }
}

/// <summary>The body of <c>POST /api/alerts/{name}/note/entries</c>.</summary>
public sealed record AddAlertNoteEntryRequest
{
    public string? Text { get; init; }

    /// <summary>The incident this was written from, when it was.</summary>
    public Guid? IncidentId { get; init; }

    /// <summary>Who is writing it. Ignored when the request is authenticated.</summary>
    public string? Author { get; init; }
}
