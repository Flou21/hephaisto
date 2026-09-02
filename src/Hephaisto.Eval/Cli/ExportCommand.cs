using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Hephaisto.Agent.Demo;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Eval.Scoring;

namespace Hephaisto.Eval.Cli;

/// <summary>
/// Snapshots a finished incident out of the agent's database into a transcript.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because <c>run</c> can never produce what it produces.</b> Replay constructs
/// an <c>InvestigationRunner</c> and nothing else - no executor, no policy engine, no state
/// machine - so a replayed transcript structurally cannot contain an executed action, a policy
/// decision, or an incident that reached a terminal state. Those exist only where the agent
/// actually ran, which is a cluster and the database behind it.
/// </para>
/// <para>
/// So this is the cheapest command in the harness rather than the most expensive one: no model,
/// no cluster, no money, nothing computed. It reads rows the agent already wrote and reshapes
/// them into the artifact the demo surfaces already consume. The grade is the deterministic
/// structural grader over the same answer key, because that is free and reproducible; the plan
/// verdict is deliberately left unset, since <c>PlanGrader</c> SIMULATES what the policy engine
/// would decide and this artifact carries what it actually decided. A simulated verdict printed
/// beside a recorded one is a second opinion about a fact.
/// </para>
/// <para>
/// <b>Every refusal here is about not publishing something misleading.</b> An incident still in
/// flight has a state that has not settled, an incident with no transitions came from the wrong
/// database, and a transcript whose evidence blobs have been swept by the retention service is
/// a provenance chain with the provenance missing - which is the one thing a demo page exists
/// to show.
/// </para>
/// </remarks>
internal static partial class ExportCommand
{
    /// <summary>The states an incident can be exported from: the ones it stops in.</summary>
    /// <remarks>
    /// <c>Incident.IsOpen</c> is the wrong predicate and it is worth saying why, because it is
    /// the obvious one to reach for. <see cref="IncidentState.Escalated"/> counts as open - the
    /// incident is waiting for a human - and an escalation after a policy denial is exactly the
    /// artifact this command was written to capture.
    /// </remarks>
    private static readonly IReadOnlySet<IncidentState> Terminal = new HashSet<IncidentState>
    {
        IncidentState.Resolved,
        IncidentState.Escalated,
        IncidentState.Suppressed,
        IncidentState.Expired,
    };

    public static async Task<int> RunAsync(EvalArguments args, CancellationToken ct)
    {
        var incidentId = args.Value("incident");
        var id = args.Value("id");

        if (incidentId is null || id is null)
        {
            Console.Error.WriteLine("export needs --incident <guid> and --id <c13-resolved>");
            return 2;
        }

        if (!Guid.TryParse(incidentId, out var guid))
        {
            Console.Error.WriteLine($"--incident needs a guid, not '{incidentId}'");
            return 2;
        }

        // It becomes a filename and a URL path segment on a published site.
        if (!SafeId().IsMatch(id))
        {
            Console.Error.WriteLine(
                $"--id must be lowercase alphanumeric with dashes, not '{id}': it becomes both a "
                + "filename and a path segment on demo.hephaisto.dev");

            return 2;
        }

        var fixture = args.Value("fixture");
        var key = fixture is null ? null : AnswerKey.ForCassette(fixture);
        var expected = args.Value("expect") ?? key?.ExpectedRootCause;

        if (expected is null)
        {
            Console.Error.WriteLine(
                $"No answer key for fixture '{fixture ?? "(none given)"}' and no --expect given. "
                + $"Known fixtures: {string.Join(", ", AnswerKey.All.Select(k => k.Fixture))}");

            return 2;
        }

        var directory = args.Value("out") ?? Path.Combine("src", "Hephaisto.Agent", "Demo", "transcripts");
        var path = Path.Combine(directory, $"{id}.json");

        if (File.Exists(path) && !args.Flag("force"))
        {
            // These are committed artifacts on a public site. A second capture attempt
            // silently overwriting a published one is not a thing to discover from a diff.
            Console.Error.WriteLine($"{path} exists; pass --force to replace it");
            return 1;
        }

        var configuration = EvalHost.BuildConfiguration(args.Multiple("set"));

        await using var services = EvalHost.BuildForExport(configuration);
        await using var scope = services.CreateAsyncScope();

        var sp = scope.ServiceProvider;
        var incidents = sp.GetRequiredService<IIncidentRepository>();
        var db = sp.GetRequiredService<HephaistoDbContext>();

        var incident = await incidents.GetWithDetailAsync(guid, ct).ConfigureAwait(false);

        if (incident is null)
        {
            Console.Error.WriteLine($"No incident {guid} in the database.");
            return 1;
        }

        if (!Terminal.Contains(incident.State))
        {
            Console.Error.WriteLine(
                $"Incident {guid} is in {incident.State}, which is still in flight. Export it "
                + "when it settles - a page showing a state that never resolved is a page about "
                + "the capture rather than about the agent.");

            return 1;
        }

        if (incident.Events.Count == 0)
        {
            Console.Error.WriteLine(
                $"Incident {guid} carries no transitions. The recorded timeline is the whole "
                + "reason this command exists, so this is the wrong database or the wrong row.");

            return 1;
        }

        // The same load the console's own detail page does. Kept as tracked entities rather
        // than AsNoTracking: EF's identity map then makes plan.Actions and incident.Actions the
        // same instances, which is what lets the clear below drop a duplicate rather than data.
        var investigations = await db.Investigations
            .Where(v => v.IncidentId == guid)
            .Include(v => v.Steps)
            .Include(v => v.Findings)
                .ThenInclude(f => f.Evidence)
            .Include(v => v.Plan!)
                .ThenInclude(p => p.Actions)
            .AsSplitQuery()
            .OrderBy(v => v.StartedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var investigation = investigations.LastOrDefault(v => v.CompletedAt is not null);

        if (investigation is null)
        {
            Console.Error.WriteLine(
                $"Incident {guid} has no completed investigation ({investigations.Count} found).");

            return 1;
        }

        if (investigations.Count > 1)
        {
            Console.WriteLine(
                $"  {investigations.Count} investigations on this incident; exporting the last "
                + $"completed one, {investigation.Id}");
        }

        var blobIds = investigation.Steps
            .Select(s => s.RawBlobId)
            .Where(b => b is not null)
            .Select(b => b!.Value)
            .Distinct()
            .ToList();

        var blobs = blobIds.Count == 0
            ? []
            : await db.EvidenceBlobs
                .Where(b => blobIds.Contains(b.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);

        if (blobIds.Count > 0 && blobs.Count == 0)
        {
            Console.Error.WriteLine(
                $"None of {blobIds.Count} raw results survive - the retention sweep has taken "
                + "them. Every 'view raw' link on this page would 404, which is precisely the "
                + "provenance chain the page exists to show.");

            return 1;
        }

        if (blobs.Count < blobIds.Count)
        {
            Console.WriteLine($"  WARNING  {blobs.Count} of {blobIds.Count} raw results resolved");
        }

        // The actions travel on the plan, exactly once. Transcript.Json uses
        // ReferenceHandler.IgnoreCycles, which nulls CYCLES and not REPEATS - and these are two
        // disjoint paths from the root, so serializing both writes each action twice and it
        // reloads as two objects sharing one Guid. DemoSeeder then adds the plan's copy and
        // cascades the incident's, and Postgres refuses the duplicate key. Same reason the
        // seeder clears Incident.Investigations: one home per entity in this artifact.
        incident.Actions.Clear();
        incident.Investigations.Clear();

        var grade = StructuralGrader.Grade(investigation, key ?? AnswerKey.All[0]);

        Describe(incident, investigation);

        var transcript = new Transcript
        {
            CassetteId = id,
            Description = args.Value("description") ?? incident.Title,
            ExpectedRootCause = expected,
            Incident = incident,
            Investigation = investigation,
            Blobs = blobs,
            Score = new TranscriptGrade
            {
                Verdict = grade.Verdict.ToString(),

                // Left unset on purpose. PlanGrader answers "what WOULD the policy engine do";
                // this artifact carries what it DID, on the action rows below.
                PlanVerdict = null,
                Hypothesis = grade.Hypothesis,
                StructurallySound = grade.Assertions.All(a => a.Status is not EvalStatus.Fail),
                StepsUsed = investigation.StepsUsed,
                CostUsd = investigation.CostUsd,
                TerminationReason = investigation.TerminationReason.ToString(),
            },
            Origin = new TranscriptOrigin
            {
                ModelId = investigation.ModelId,

                // No cassette and no second model: this was not replayed against anything.
                RecordedAgainstModelId = null,
                RecordedAt = sp.GetRequiredService<IClock>().UtcNow,
                AgentVersion = typeof(Transcript).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? "unknown",
                Capture = TranscriptCapture.Cluster,
            },
        };

        Directory.CreateDirectory(directory);
        transcript.Save(path);

        Console.WriteLine();
        Console.WriteLine($"  transcript -> {path}");
        Console.WriteLine(
            "  read it before committing: preState/postState and decisionReasons are content no "
            + "replay has ever published, and the redactor only knows about IPv4.");

        return 0;
    }

    /// <summary>
    /// Prints what is about to be published, so it is reviewed rather than discovered.
    /// </summary>
    private static void Describe(Incident incident, Investigation investigation)
    {
        Console.WriteLine($"exporting incident {incident.Id}");
        Console.WriteLine($"  {incident.Kind} {incident.Target.Namespace}/{incident.Target.Name}");
        Console.WriteLine($"  state {incident.State}, escalation {incident.EscalationReason}");

        if (!string.IsNullOrWhiteSpace(incident.Resolution))
        {
            Console.WriteLine($"  resolution: {incident.Resolution}");
        }

        foreach (var e in incident.Events.OrderBy(e => e.At))
        {
            Console.WriteLine($"    {e.From?.ToString() ?? "-",-15} -> {e.To,-15} {e.Reason}");
        }

        var actions = investigation.Plan?.Actions ?? [];

        Console.WriteLine($"  {actions.Count} action(s) on the plan");

        foreach (var a in actions)
        {
            Console.WriteLine($"    {a.Type} -> {a.Decision}, state {a.State}");

            foreach (var reason in a.DecisionReasons)
            {
                Console.WriteLine($"      {reason}");
            }

            if (a.ExecutedAt is not null)
            {
                Console.WriteLine(
                    $"      executed {a.ExecutedAt:u} dryRun={a.DryRun} "
                    + $"by {a.ApprovedBy ?? "(nobody)"} ({a.ApprovalSource})");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SafeId();
}
