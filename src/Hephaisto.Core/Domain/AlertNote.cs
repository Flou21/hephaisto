namespace Hephaisto.Core.Domain;

/// <summary>
/// What the people who get paged for one alert name have learned about it: what it means, what
/// to look at first, which dashboard. Backlog #145.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed by alert name, not by incident and not by kind.</b> A runbook belongs to a
/// <see cref="SignalKind"/> and ships in the image; feedback belongs to one incident. Neither is
/// where "the last three times this fired it was the upstream feed" can live - that is a fact
/// about a rule, written by the people who own the rule, and it has to outlive every incident it
/// explains.
/// </para>
/// <para>
/// Two parts, on purpose. The <see cref="Body"/> is curated and replaced as a whole, by someone
/// allowed to decide what the team believes. The <see cref="Entries"/> are appended by whoever
/// was on call - "restarted the consumer, it recovered" - and are never edited, so the history
/// of what was tried cannot be tidied away.
/// </para>
/// <para>
/// Everything here is written by people and then shown to the model. It is reference text from
/// the operators, not an instruction channel: the prompt says so where it is rendered.
/// </para>
/// </remarks>
public sealed class AlertNote
{
    /// <summary>The largest body kept. A note is a page, not a wiki.</summary>
    public const int MaxBodyLength = 16 * 1024;

    /// <summary>The largest single entry kept.</summary>
    public const int MaxEntryLength = 4 * 1024;

    /// <summary>The longest alert name accepted, which is well past any rule anybody writes.</summary>
    public const int MaxAlertNameLength = 200;

    /// <summary>The Alertmanager <c>alertname</c>. The key.</summary>
    public string AlertName { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string UpdatedBy { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public List<AlertNoteEntry> Entries { get; set; } = [];

    /// <summary>
    /// A name that can key a note, trimmed; or null when it cannot.
    /// </summary>
    /// <remarks>
    /// The same check for every door - the API, the console and the prompt's lookup - so a note
    /// saved under one spelling is found under it again. Control characters are refused rather
    /// than stripped: a name that needed cleaning is not one any rule produced.
    /// </remarks>
    public static string? NormaliseName(string? alertName)
    {
        var name = alertName?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > MaxAlertNameLength || name.Any(char.IsControl))
        {
            return null;
        }

        return name;
    }

    /// <summary>
    /// The alert name an incident is about: that of its oldest Alertmanager signal, or null for
    /// an incident no alert opened.
    /// </summary>
    /// <remarks>
    /// The <c>alertname</c> label when the signal carries one, else its reason, which the webhook
    /// sets to the same value. Oldest first because that is the alert that opened the incident;
    /// a later one attached by correlation is a different rule's opinion about the same fault.
    /// </remarks>
    public static string? AlertNameOf(IEnumerable<Signal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var first = signals
            .Where(s => s.Source == SignalSource.Alertmanager)
            .OrderBy(s => s.FirstSeen)
            .FirstOrDefault();

        if (first is null)
        {
            return null;
        }

        return NormaliseName(first.Labels.TryGetValue("alertname", out var name) ? name : first.Reason);
    }

    /// <summary>
    /// The first <paramref name="max"/> characters of the body on one line, for a card; null when
    /// there is no body.
    /// </summary>
    public static string? Excerpt(string? body, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var single = string.Join(' ', body.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return single.Length <= max ? single : string.Concat(single.AsSpan(0, max).TrimEnd(), "…");
    }

    /// <summary>Replaces the body, or throws when it is over the cap.</summary>
    public void SetBody(string? body, string actor, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var text = (body ?? string.Empty).Trim();

        if (text.Length > MaxBodyLength)
        {
            throw new ArgumentException(
                $"An alert note is at most {MaxBodyLength} characters; this one is {text.Length}.", nameof(body));
        }

        Body = text;
        UpdatedBy = actor.Trim();
        UpdatedAt = at;
    }

    /// <summary>Appends an entry, or throws when it is empty or over the cap.</summary>
    public AlertNoteEntry AddEntry(string? text, Guid? incidentId, string author, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        var trimmed = (text ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new ArgumentException("An entry says what was done; this one says nothing.", nameof(text));
        }

        if (trimmed.Length > MaxEntryLength)
        {
            throw new ArgumentException(
                $"An entry is at most {MaxEntryLength} characters; this one is {trimmed.Length}.", nameof(text));
        }

        var entry = new AlertNoteEntry
        {
            AlertName = AlertName,
            IncidentId = incidentId,
            Author = author.Trim(),
            Text = trimmed,
            CreatedAt = at,
        };

        Entries.Add(entry);

        return entry;
    }
}

/// <summary>One line of "what was done this time", appended and never edited.</summary>
public sealed class AlertNoteEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string AlertName { get; set; } = string.Empty;

    /// <summary>The incident it was written from, when it was. A pointer, not a constraint.</summary>
    public Guid? IncidentId { get; set; }

    public string Author { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Written through the MCP endpoint (#157): a model relayed it, whoever it says it is for. Such
    /// an entry is shown, and marked, but never handed to the investigator as what operators wrote.
    /// </summary>
    public bool RelayedByAgent { get; set; }
}
