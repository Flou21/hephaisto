using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Demo;

/// <summary>
/// Loads recorded investigations into an empty database so the console has something in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is real except the clock.</b> The incidents were opened by the real
/// ingest path against a real k3s cluster with real seeded faults; the investigations were run
/// by the real loop against a real model; the diagnoses, the evidence and the grades are what
/// that produced. What the seeder does is insert them and move the timestamps forward, and it
/// says so on every row it writes.
/// </para>
/// <para>
/// <b>Why the timestamps move.</b> Not cosmetics - correctness. <c>RetentionService</c> deletes
/// evidence blobs on <c>ExpiresAt &lt;= now OR CreatedAt &lt;= now - EvidenceBlobRetention</c>,
/// and both arms fire on a transcript recorded weeks ago: the first sweep after boot would
/// delete precisely the raw tool output that every "view raw" link on the page points at,
/// leaving a demo of the provenance chain with no provenance. So the whole graph is rebased,
/// preserving every interval, and the recording date is stated on the incident's timeline
/// instead of being implied by a stale timestamp.
/// </para>
/// <para>
/// <b>It cannot damage an installation.</b> It refuses on any database that already holds an
/// incident, so the worst case of setting <c>Demo:Seed</c> on a real agent by mistake is a log
/// line. It is still wrong to do, and <see cref="DemoOptions.Seed"/> says why.
/// </para>
/// </remarks>
internal sealed class DemoSeeder(
    IServiceScopeFactory scopes,
    IOptions<DemoOptions> options,
    IHostEnvironment environment,
    IClock clock,
    ILogger<DemoSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;

        if (!o.Seed)
        {
            return;
        }

        var directory = Path.IsPathRooted(o.TranscriptPath)
            ? o.TranscriptPath
            : Path.Combine(environment.ContentRootPath, o.TranscriptPath);

        if (!Directory.Exists(directory))
        {
            logger.LogWarning(
                "Demo:Seed is set but {Directory} does not exist, so the console will be empty.",
                directory);

            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();
        var embedder = scope.ServiceProvider.GetRequiredService<IncidentEmbedder>();

        if (await db.Incidents.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            // Idempotent across restarts, and the guard that makes this safe to ship in the
            // image at all.
            logger.LogInformation("Demo:Seed is set but the database already holds incidents; nothing seeded.");
            return;
        }

        var files = Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var seeded = 0;

        foreach (var file in files)
        {
            try
            {
                await SeedOneAsync(db, embedder, Transcript.Load(file), cancellationToken)
                    .ConfigureAwait(false);
                seeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One malformed transcript must not cost the other nine. The console is still
                // worth looking at with a gap in it.
                logger.LogError(ex, "Could not seed {File}; skipping it", file);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Seeded {Count} recorded investigation(s) from {Directory}. These are REPLAYS of "
            + "faults in another cluster, not live data; each incident's timeline says which "
            + "cassette and which model produced it.",
            seeded,
            directory);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Everything the seed does to a transcript before it touches the database: decide whether
    /// the file carries its own history, compose one if it does not, and rebase every clock.
    /// </summary>
    /// <remarks>
    /// Separated from <c>SeedOneAsync</c> so it can be asserted without a Postgres. This is the
    /// half that decides what the console will SAY about an incident, which is the half worth
    /// pinning: it silently claimed Escalated for every transcript until an exported one
    /// arrived that had actually resolved.
    /// </remarks>
    internal static void PrepareForSeed(Transcript transcript, DateTimeOffset now)
    {
        var incident = transcript.Incident;
        var investigation = transcript.Investigation;

        // The serialized incident carries its investigations, and the investigation is also
        // added explicitly below. Adding both would insert the graph twice.
        incident.Investigations.Clear();

        // Whether this file carries a state history is the whole question, and the transitions
        // ARE that history - so it is asked of the payload rather than of a flag. A cluster
        // export records what the agent actually did; a replay cannot, because
        // `run --transcripts` constructs an investigation runner and no state machine.
        var recorded = incident.Events.Count > 0;

        if (!recorded)
        {
            incident.Actions.Clear();
            incident.Events.Clear();
        }

        // Anchor the most recent moment in the transcript at "just now" and shift everything
        // else by the same amount, so every interval the page renders - time to diagnosis,
        // gaps between steps - survives exactly.
        var latest = Latest(incident, investigation, transcript.Blobs);
        var shift = now - latest;

        Rebase(incident, investigation, transcript.Blobs, shift);

        investigation.IncidentId = incident.Id;

        if (!recorded)
        {
            // In Observe mode a diagnosed incident goes to a human: nothing executes, so
            // nothing verifies, so nothing closes. That is the state these recordings actually
            // ended in, and it is COMPOSED here rather than read, because a replayed transcript
            // has no state to read - so it is labelled as composed on the first transition.
            incident.State = IncidentState.Escalated;

            // Not PolicyDenied. No policy engine ran on any of these, and saying one denied the
            // plan is a claim about a component that was never constructed. It read as harmless
            // until a genuinely denied cluster capture landed in the same list beside ten
            // replays wearing the same label.
            incident.EscalationReason = investigation.Plan is null || investigation.Plan.NoActionRequired
                ? EscalationReason.NoPlanProduced
                : EscalationReason.None;

            incident.Events.Add(new IncidentEvent
            {
                IncidentId = incident.Id,
                From = null,
                To = IncidentState.Detected,
                At = incident.OpenedAt,
                Reason = Provenance(transcript),
            });

            incident.Events.Add(new IncidentEvent
            {
                IncidentId = incident.Id,
                From = IncidentState.Detected,
                To = IncidentState.Investigating,
                At = investigation.StartedAt,
                Reason = $"Investigating with {investigation.ModelId}.",
            });

            incident.Events.Add(new IncidentEvent
            {
                IncidentId = incident.Id,
                From = IncidentState.Investigating,
                To = IncidentState.Escalated,
                At = investigation.CompletedAt ?? investigation.StartedAt,
                Reason = incident.EscalationReason is EscalationReason.NoPlanProduced
                    ? "Diagnosed, and no action was proposed. Escalated to a human."
                    : "Diagnosed, and a plan was proposed. Nothing executes in Observe mode.",
            });
        }

    }

    private async Task SeedOneAsync(
        HephaistoDbContext db,
        IncidentEmbedder embedder,
        Transcript transcript,
        CancellationToken ct)
    {
        var incident = transcript.Incident;
        var investigation = transcript.Investigation;

        PrepareForSeed(transcript, clock.UtcNow);

        db.Incidents.Add(incident);
        db.AddInvestigationGraph(investigation);
        db.EvidenceBlobs.AddRange(transcript.Blobs);

        // The digest feeds the search page. BuildAsync writes a null embedding when no
        // embedding provider is configured, which is the demo's normal state - so search works
        // on its lexical and trigram arms and honestly reports the vector arm as absent.
        var digest = await embedder.BuildAsync(
            new IncidentDigestInput
            {
                Incident = incident,
                PrimaryFinding = investigation.Findings.FirstOrDefault(f => f.IsPrimary),
            },
            existing: null,
            ct).ConfigureAwait(false);

        db.IncidentDigests.Add(digest);
    }

    /// <summary>
    /// The sentence that makes the row unmistakably a recording. It is the first entry in the
    /// incident's timeline, which the detail page renders in full.
    /// </summary>
    /// <remarks>
    /// <b>It states the grade whatever the grade was, and says when the run was unsound.</b>
    /// Replay serves a recorded tool trace to a live model, so a model that reaches for a call
    /// the recording does not contain gets a miss - and a scenario with a high miss rate
    /// produces a bad diagnosis for an instrument reason rather than a reasoning one
    /// (backlog #55). Showing that verdict without the caveat would blame the agent for the
    /// corpus; hiding the scenario entirely would quietly curate the demo up from the 8-of-10
    /// this project publishes. So it is shown, and labelled.
    /// </remarks>
    internal static string Provenance(Transcript t)
    {
        // A cluster export was never replayed, has no cassette behind it and no second model's
        // tool trace, so none of the sentence below is true of it. The structural caveat is
        // replay-specific too - there is no recording to miss against.
        if (t.Origin.Capture is TranscriptCapture.Cluster)
        {
            return $"DEMO DATA - LIVE CAPTURE, exported from the agent's own database after a "
                + $"real run on a real k3s cluster. Incident {t.Incident.Id.ToString()[..8]}, "
                + $"investigated by {t.Origin.ModelId} on {t.Origin.RecordedAt:yyyy-MM-dd}, "
                + $"agent {t.Origin.AgentVersion}. The state, the transitions and the policy "
                + "decision are what the agent did, not what this page composed. Timestamps are "
                + "shifted forward; nothing else is changed. Not part of the replayed cassette "
                + "corpus and not counted in its score.";
        }

        var caveat = t.Score.StructurallySound
            ? string.Empty
            : " NOTE: this replay was structurally unsound - the recorded tool trace did not "
            + "cover what the model asked for, so the verdict reflects the recording rather "
            + "than the reasoning.";

        return $"DEMO DATA - replayed from cassette {t.CassetteId}, recorded against a real k3s "
            + $"cluster. Investigated by {t.Origin.ModelId} on {t.Origin.RecordedAt:yyyy-MM-dd} "
            + $"against a tool trace from {t.Origin.RecordedAgainstModelId ?? "an earlier run"}, "
            + $"and graded {t.Score.Verdict} against the answer key. Timestamps are shifted "
            + $"forward; nothing else is changed.{caveat}";
    }

    internal static DateTimeOffset Latest(
        Incident incident,
        Investigation investigation,
        IReadOnlyList<EvidenceBlob> blobs)
    {
        var candidates = new List<DateTimeOffset>
        {
            incident.OpenedAt,
            incident.LastSignalAt,
            investigation.StartedAt,
            investigation.CompletedAt ?? investigation.StartedAt,
        };

        candidates.AddRange(incident.Signals.Select(s => s.LastSeen));
        candidates.AddRange(investigation.Steps.Select(s => s.At));
        candidates.AddRange(blobs.Select(b => b.CreatedAt));

        // A cluster export carries three clocks a replay never had, and all three run PAST
        // the investigation: an incident is resolved after the loop finishes, not during it.
        // Anchoring on the old set put the resolution and the execution in the future - a
        // console showing an action executed ten minutes from now.
        if (incident.ResolvedAt is { } resolved)
        {
            candidates.Add(resolved);
        }

        candidates.AddRange(incident.Events.Select(e => e.At));

        if (investigation.Plan is { } plan)
        {
            candidates.Add(plan.CreatedAt);
            candidates.AddRange(plan.Actions.Where(a => a.ApprovedAt is not null).Select(a => a.ApprovedAt!.Value));
            candidates.AddRange(plan.Actions.Where(a => a.ExecutedAt is not null).Select(a => a.ExecutedAt!.Value));

            // A verification runs AFTER the action it checks - up to fifteen minutes after - so
            // anchoring on the action alone would place the last check in the future and render
            // a completed incident as still waiting on one.
            candidates.AddRange(
                plan.Actions
                    .SelectMany(a => a.Verifications)
                    .Where(v => v.RanAt is not null)
                    .Select(v => v.RanAt!.Value));
        }

        return candidates.Max();
    }

    internal static void Rebase(
        Incident incident,
        Investigation investigation,
        IReadOnlyList<EvidenceBlob> blobs,
        TimeSpan shift)
    {
        incident.OpenedAt += shift;
        incident.LastSignalAt += shift;

        if (incident.ResolvedAt is { } resolved)
        {
            incident.ResolvedAt = resolved + shift;
        }

        foreach (var signal in incident.Signals)
        {
            signal.FirstSeen += shift;
            signal.LastSeen += shift;
        }

        investigation.StartedAt += shift;

        if (investigation.CompletedAt is { } completed)
        {
            investigation.CompletedAt = completed + shift;
        }

        foreach (var step in investigation.Steps)
        {
            step.At += shift;
        }

        // The transitions the timeline renders. Composed ones are built from already-shifted
        // fields, so this is a no-op for a replay and load-bearing for an export.
        foreach (var transition in incident.Events)
        {
            transition.At += shift;
        }

        if (investigation.Plan is { } plan)
        {
            // Unshifted until now, which was a latent bug for the ten as well: the plan
            // rendered as created weeks before the investigation that produced it.
            plan.CreatedAt += shift;

            foreach (var action in plan.Actions)
            {
                if (action.ApprovedAt is { } approved)
                {
                    action.ApprovedAt = approved + shift;
                }

                if (action.ExecutedAt is { } executed)
                {
                    action.ExecutedAt = executed + shift;
                }

                // Shifted for exactly the reason the plan is. Verifications began travelling in
                // transcripts in v0.7.0 (#96), and an unshifted one renders as a check that ran
                // weeks before the action it checks - which on the one page whose subject is
                // "the agent fixed it, and here is the proof" is worse than showing nothing.
                foreach (var verification in action.Verifications)
                {
                    verification.DueAt += shift;

                    if (verification.RanAt is { } ran)
                    {
                        verification.RanAt = ran + shift;
                    }
                }
            }
        }

        foreach (var blob in blobs)
        {
            // Both arms of the retention predicate, not just ExpiresAt: the age fallback would
            // delete these on the first sweep otherwise.
            blob.CreatedAt += shift;
            blob.ExpiresAt += shift;
        }
    }
}
