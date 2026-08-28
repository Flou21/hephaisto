using System.Collections.Concurrent;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Which investigations are running <i>right now</i>, and how far along they are.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing in the database can answer this.</b> <c>IncidentState.Investigating</c> is set
/// during triage, before the incident is even queued, and the investigation row is written
/// only when the whole thing finishes. So between those two moments the stored state says
/// "Investigating" for an incident that is being actively worked on, one that is sitting
/// behind two others in the queue, and one whose worker died - three very different
/// situations that a reader needs to tell apart.
/// </para>
/// <para>
/// That gap is felt immediately in practice: with <c>InvestigationWorker</c> running two at
/// a time and a queue thirty-two deep, most incidents marked Investigating are waiting, and
/// a console that shows them all identically looks like an agent doing nothing while it is
/// in fact busy and spending money.
/// </para>
/// <para>
/// In memory and deliberately not persisted. It describes this process's current work, so it
/// is correct for it to be empty after a restart - anything in it was killed by that restart
/// anyway. Registered as a singleton; the coordinator writes, the UI and the status endpoint
/// read.
/// </para>
/// </remarks>
public sealed class InvestigationTracker(IClock clock)
{
    private readonly ConcurrentDictionary<Guid, InProgressInvestigation> _running = new();

    public IReadOnlyCollection<InProgressInvestigation> Running => [.. _running.Values];

    public int RunningCount => _running.Count;

    public bool IsRunning(Guid incidentId) => _running.ContainsKey(incidentId);

    public InProgressInvestigation? For(Guid incidentId) =>
        _running.TryGetValue(incidentId, out var entry) ? entry : null;

    public IDisposable Begin(Guid incidentId, string model)
    {
        var entry = new InProgressInvestigation(incidentId, model, clock.UtcNow);

        _running[incidentId] = entry;

        return new Registration(this, incidentId);
    }

    /// <summary>
    /// Called as the loop progresses so a reader sees movement rather than a spinner.
    /// </summary>
    public void Report(
        Guid incidentId,
        int toolCalls,
        decimal costUsd,
        string? activity,
        IReadOnlyList<InvestigationStep> stepLog)
    {
        if (_running.TryGetValue(incidentId, out var entry))
        {
            entry.Update(toolCalls, costUsd, activity, stepLog);
        }
    }

    private void End(Guid incidentId) => _running.TryRemove(incidentId, out _);

    /// <remarks>
    /// Disposable rather than a matching End call, so an investigation that throws still
    /// deregisters. An entry that leaks stays "running" forever and is exactly the sort of
    /// stale reassurance this class exists to remove.
    /// </remarks>
    private sealed class Registration(InvestigationTracker tracker, Guid incidentId) : IDisposable
    {
        public void Dispose() => tracker.End(incidentId);
    }
}

public sealed class InProgressInvestigation(Guid incidentId, string model, DateTimeOffset startedAt)
{
    public Guid IncidentId { get; } = incidentId;

    public string Model { get; } = model;

    public DateTimeOffset StartedAt { get; } = startedAt;

    public int Steps => StepLog.Count;

    public int ToolCalls { get; private set; }

    public decimal CostUsd { get; private set; }

    /// <summary>
    /// The steps taken so far, in order.
    /// </summary>
    /// <remarks>
    /// A snapshot, not a live reference. The recorder mutates its list under a lock from the
    /// investigation's own thread while a request may be reading this one, and handing out
    /// the live list would make a page render race a tool call. The recorder's Steps property
    /// already copies, so this holds the copy it was given.
    ///
    /// Counters alone are not enough here: "16 steps, 9 tool calls" says the agent is busy
    /// and nothing about whether it is asking sensible questions, which is the thing a person
    /// watching an investigation actually wants to judge.
    /// </remarks>
    public IReadOnlyList<InvestigationStep> StepLog { get; private set; } = [];

    /// <summary>What it is doing, e.g. the tool it is waiting on. Null before the first turn.</summary>
    public string? Activity { get; private set; }

    internal void Update(
        int toolCalls,
        decimal costUsd,
        string? activity,
        IReadOnlyList<InvestigationStep> stepLog)
    {
        StepLog = stepLog;
        ToolCalls = toolCalls;
        CostUsd = costUsd;

        if (activity is not null)
        {
            Activity = activity;
        }
    }
}
