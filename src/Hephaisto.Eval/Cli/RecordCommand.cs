using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Eval.Scoring;

namespace Hephaisto.Eval.Cli;

/// <summary>
/// Records one live investigation as a cassette.
/// </summary>
/// <remarks>
/// <para>
/// This is the only command that needs a cluster, a database and money, and it is the reason the
/// other one needs none of them. It runs the <b>real</b> investigation loop against a real
/// incident on a real cluster, with every tool wrapped so its declaration and its untruncated
/// answer are captured on the way through.
/// </para>
/// <para>
/// Recording by exporting the database instead was the original design and does not work. Tool
/// declarations are persisted nowhere; only 43 of 297 recorded tool calls carried an untruncated
/// blob; and arguments are stored post-redaction, where a parameter named <c>labelKey</c> has
/// already become <c>[redacted]</c> and could never match what a live model sends.
/// </para>
/// </remarks>
internal static class RecordCommand
{
    /// <summary>
    /// The signal count above which the incident card is worth a second look before recording.
    /// </summary>
    /// <remarks>
    /// The card renders every signal on the incident, so a long-running flapper produces a system
    /// prompt of mostly repetition - live incidents were found on the dev cluster carrying 579 and
    /// 423 signals. A cassette made from one measures the model's tolerance for a wall of
    /// near-identical lines, not its diagnostic skill, and the resemblance to a normal fixture is
    /// what makes it worth saying out loud.
    /// </remarks>
    private const int NoisyIncidentSignals = 100;

    public static async Task<int> RunAsync(EvalArguments args, CancellationToken ct)
    {
        var fixture = args.Value("fixture");
        var incidentId = args.Value("incident");

        if (fixture is null || incidentId is null)
        {
            Console.Error.WriteLine("record needs --incident <guid> and --fixture <c4>");
            return 2;
        }

        if (!Guid.TryParse(incidentId, out var id))
        {
            Console.Error.WriteLine($"--incident needs a guid, not '{incidentId}'");
            return 2;
        }

        var key = AnswerKey.ForCassette(fixture);
        var expected = args.Value("expect") ?? key?.ExpectedRootCause;

        if (expected is null)
        {
            // Recording a scenario with no answer is recording something that can never be
            // scored, which is an hour and a dollar spent on a file nobody can use.
            Console.Error.WriteLine(
                $"No answer key for fixture '{fixture}' and no --expect given. "
                + $"Known fixtures: {string.Join(", ", AnswerKey.All.Select(k => k.Fixture))}");

            return 2;
        }

        var configuration = EvalHost.BuildConfiguration(args.Multiple("set"));

        await using var services = EvalHost.BuildForRecording(configuration);
        await using var scope = services.CreateAsyncScope();

        var sp = scope.ServiceProvider;
        var incidents = sp.GetRequiredService<IIncidentRepository>();
        var incident = await incidents.GetWithDetailAsync(id, ct).ConfigureAwait(false);

        if (incident is null)
        {
            Console.Error.WriteLine($"No incident {id} in the database.");
            return 1;
        }

        Console.WriteLine($"recording {fixture} from incident {id}");
        Console.WriteLine($"  {incident.Kind} {incident.Target.Namespace}/{incident.Target.Name} - {incident.Title}");
        Console.WriteLine($"  {incident.Signals.Count} signals, state {incident.State}");

        if (incident.Signals.Count > NoisyIncidentSignals)
        {
            Console.WriteLine(
                $"  WARNING  {incident.Signals.Count} signals all render into the incident card; "
                + "this cassette measures prompt length as much as diagnosis");
        }

        var recorder = new RecordingToolset();
        var outcome = await InvestigateAsync(sp, incident, recorder, ct).ConfigureAwait(false);

        foreach (var warning in Warnings(recorder))
        {
            Console.WriteLine($"  WARNING  {warning}");
        }

        var cassette = new Cassette
        {
            Id = fixture,
            Description = args.Value("description") ?? incident.Title,
            ExpectedRootCause = expected,
            Incident = RecordedIncident.From(incident),
            Tools = recorder.Declarations,
            Calls = recorder.Calls,
            Environment = sp.GetRequiredService<IOptions<EnvironmentCardOptions>>().Value,
            Origin = new CassetteOrigin
            {
                InvestigationId = outcome.Investigation.Id,
                IncidentId = incident.Id,
                RecordedAt = sp.GetRequiredService<IClock>().UtcNow,
                AgentVersion = Assembly.GetAssembly(typeof(InvestigationRunner))?
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                ModelId = outcome.Investigation.ModelId,
                AgentCommit = GitCommit(),
                IncidentKind = incident.Kind,
                PromptHash = PromptFingerprint.Compute(incident.Kind),
            },
        };

        var directory = args.Value("out") ?? "cassettes";

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{fixture}.json");

        cassette.Save(path);

        Report(outcome.Investigation, key, path);

        // Zero recorded calls is a file that will replay as nothing but misses. It is written
        // anyway - looking at it is how you find out why the model asked nothing - but the exit
        // code says the recording failed, so a scripted sweep of eight fixtures cannot report
        // eight successes and leave eight useless files behind.
        return recorder.Calls.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// Runs the production loop with the tool surface wrapped, and nothing else changed.
    /// </summary>
    /// <remarks>
    /// The runner is constructed rather than resolved so the wrapping is visible in one place.
    /// Everything passed in is the object the agent would have used - the real chat client
    /// factory, the real prompt composer reading the real files, the real budgets - because a
    /// recording made against a substituted anything is a recording of the substitute.
    /// </remarks>
    private static async Task<InvestigationOutcome> InvestigateAsync(
        IServiceProvider sp,
        Incident incident,
        RecordingToolset recorder,
        CancellationToken ct)
    {
        var clusterTools = recorder.Wrap(
            sp.GetRequiredService<IEnumerable<AIFunction>>(),
            ToolDeclaration.Kubernetes);

        var grafana = new RecordingGrafanaToolProvider(
            sp.GetRequiredService<IGrafanaToolProvider>(),
            recorder);

        var runner = new InvestigationRunner(
            sp.GetRequiredService<IChatClientFactory>(),
            sp.GetRequiredService<PromptComposer>(),
            clusterTools,
            grafana,
            new NullGlobalLlmBudget(),
            sp.GetRequiredService<InvestigationTracker>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IOptionsMonitor<LlmOptions>>(),
            sp.GetRequiredService<IOptionsMonitor<InvestigationOptions>>(),
            sp.GetRequiredService<ILogger<InvestigationRunner>>());

        return await runner.RunAsync(incident, ct).ConfigureAwait(false);
    }

    private static IEnumerable<string> Warnings(RecordingToolset recorder)
    {
        if (recorder.Calls.Count == 0)
        {
            yield return "no tool calls were recorded; this cassette can only replay misses";
        }

        // Impossible from the live path, where the wrappers sit inside SafeToolDecorator and see
        // the model's own arguments. Seeing it means the recording came from persisted rows
        // instead, and every one of those calls would replay as a miss.
        if (recorder.RedactedArguments.Count > 0)
        {
            yield return
                $"redacted arguments recorded for {string.Join(", ", recorder.RedactedArguments)}; "
                + "these calls cannot replay";
        }
    }

    private static void Report(Investigation investigation, AnswerKey? key, string path)
    {
        Console.WriteLine(
            $"  {investigation.TerminationReason} after {investigation.StepsUsed} steps, "
            + $"{investigation.ToolCallsUsed} tool calls, ${investigation.CostUsd:F4}");

        var primary = investigation.Findings.FirstOrDefault(f => f.IsPrimary);

        Console.WriteLine(primary is null
            ? "  no primary finding"
            : $"  finding [{primary.Category}] {primary.Hypothesis}");

        // Graded immediately, because the useful question after a recording is not "did it save"
        // but "is this scenario one the agent can currently solve" - and the answer is the first
        // data point of the baseline.
        if (key is not null)
        {
            Console.WriteLine($"  verdict       {StructuralGrader.Grade(investigation, key).Verdict}");
        }

        Console.WriteLine($"  wrote         {path}");
    }

    /// <summary>
    /// The commit the prompts and tool surface came from, or null outside a checkout.
    /// </summary>
    /// <remarks>
    /// Shelled out rather than read from an assembly attribute because the thing that has to be
    /// pinned is the working tree - the prompt fragments and runbooks are <c>Content</c> files
    /// read off disk at compose time, so a rebuilt binary and an edited runbook are the same
    /// build with a different prompt. Null rather than a throw: a cassette recorded outside a
    /// checkout is worth slightly less, not worthless.
    /// </remarks>
    private static string? GitCommit()
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (git is null)
            {
                return null;
            }

            var output = git.StandardOutput.ReadToEnd().Trim();

            git.WaitForExit(TimeSpan.FromSeconds(5));

            return git.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
