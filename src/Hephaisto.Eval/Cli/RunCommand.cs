using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Eval.Scoring;

namespace Hephaisto.Eval.Cli;

/// <summary>
/// Replays a corpus of cassettes and scores what came back.
/// </summary>
/// <remarks>
/// <para>
/// The command the experiments are run with. It needs no cluster, no chaos fixture and no
/// database - only the model, which is the thing under test. That is the whole point: one arm of
/// an experiment costs a few model calls and a minute instead of a kind cluster, a seeded fault,
/// twenty-five minutes and a single noisy sample.
/// </para>
/// <para>
/// <b>The score is a measurement, not a test result.</b> A run exits non-zero when the
/// <i>instrument</i> failed - a dangling citation, a category outside the contract, a miss rate
/// that says the model spent the run talking to the harness - and exits zero when the agent
/// simply did badly. Conflating the two would make a regression look like a broken harness and
/// a broken harness look like a regression.
/// </para>
/// </remarks>
internal static class RunCommand
{
    public static async Task<int> RunAsync(EvalArguments args, CancellationToken ct)
    {
        var paths = ResolvePaths(args);

        if (paths.Count == 0)
        {
            Console.Error.WriteLine("run needs --cassettes <dir> or one or more cassette paths");
            return 2;
        }

        var scenarios = new List<(Cassette Cassette, AnswerKey Key)>();

        foreach (var path in paths)
        {
            var cassette = Cassette.Load(path);
            var key = AnswerKey.ForCassette(cassette.Id);

            if (key is null)
            {
                // Refused before a single model call. Running a scenario that cannot be scored
                // spends money to produce a row that has to be thrown away.
                Console.Error.WriteLine(
                    $"{path}: no answer key for '{cassette.Id}'. "
                    + $"Known fixtures: {string.Join(", ", AnswerKey.All.Select(k => k.Fixture))}");

                return 2;
            }

            scenarios.Add((cassette, key));

            if (PromptFingerprint.Describe(cassette) is { } freshness && freshness.Contains("STALE", StringComparison.Ordinal))
            {
                // A warning, never a refusal. Measuring a rewritten runbook against cassettes
                // recorded before it is experiment 2a, not a mistake - but it must be visible,
                // because the same warning means "re-record" for every other kind of drift.
                Console.WriteLine($"WARNING  {cassette.Id}: {freshness}");
            }
        }

        var overrides = args.Multiple("set");
        var configuration = EvalHost.BuildConfiguration(overrides);
        var repeats = args.IntValue("repeats", 1);
        var label = args.Value("label") ?? "unlabelled";

        await using var services = EvalHost.BuildForReplay(configuration);

        // 180s, not 60. A local model reasons before it answers, and a judge call that times
        // out is recorded as a failed grade rather than a slow one - which reads as a scenario
        // nobody could grade instead of a judge nobody waited for.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

        var judge = args.Flag("no-judge") ? null : RootCauseJudgeFactory.FromEnvironment(http);

        if (judge is null && !args.Flag("no-judge"))
        {
            Console.WriteLine(
                "note     no judge configured; scoring deterministically only. Set "
                + "HEPHAISTO_GEMINI_API_KEY, or JUDGE_PROVIDER=openai with JUDGE_ENDPOINT "
                + "and JUDGE_MODEL");
        }

        var clock = services.GetRequiredService<IClock>();
        var startedAt = clock.UtcNow;
        var passes = new List<RunScore>();

        for (var pass = 1; pass <= repeats; pass++)
        {
            var scored = new List<ScenarioScore>();

            foreach (var (cassette, key) in scenarios)
            {
                var score = await ReplayAsync(services, cassette, key, judge, ct).ConfigureAwait(false);

                scored.Add(score);
                Console.WriteLine(Line(pass, score));
            }

            passes.Add(new RunScore { Label = $"{label} pass {pass}", Scenarios = scored });
        }

        var report = new RunReport
        {
            Label = label,
            StartedAt = startedAt,
            CompletedAt = clock.UtcNow,
            ModelId = services.GetRequiredService<IChatClientFactory>().InvestigationModelId,
            Overrides = overrides,
            Passes = passes,
        };

        Summarise(report);
        Write(report, args.Value("out") ?? "results");

        return report.Sound == report.Total ? 0 : 1;
    }

    private static IReadOnlyList<string> ResolvePaths(EvalArguments args)
    {
        var directory = args.Value("cassettes");

        if (directory is null)
        {
            return args.Positional;
        }

        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// One scenario: the production loop, with recorded tool output where the cluster would be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything except the tools is the real object, including the prompt composer - built here
    /// with the <b>cassette's</b> environment card rather than this machine's configuration.
    /// That card is rendered into every system prompt and is config-only in the agent, so
    /// replaying with local values would silently measure a different prompt from the one that
    /// was recorded.
    /// </para>
    /// <para>
    /// A fresh <c>InvestigationTracker</c> per scenario, because it is keyed by incident id and
    /// every replay of one cassette carries the same one.
    /// </para>
    /// </remarks>
    private static async Task<ScenarioScore> ReplayAsync(
        IServiceProvider services,
        Cassette cassette,
        AnswerKey key,
        IRootCauseJudge? judge,
        CancellationToken ct)
    {
        var replay = new ReplayToolset(cassette);

        WarnOnUnroutableTools(cassette, replay);

        var clock = services.GetRequiredService<IClock>();

        var runner = new InvestigationRunner(
            services.GetRequiredService<IChatClientFactory>(),
            new PromptComposer(
                Options.Create(cassette.Environment ?? new EnvironmentCardOptions()),
                services.GetRequiredService<ILogger<PromptComposer>>()),
            replay.FunctionsFor(ToolDeclaration.Kubernetes),
            new ReplayGrafanaToolProvider(replay),
            new NullGlobalLlmBudget(),
            new InvestigationTracker(clock),
            clock,
            services.GetRequiredService<IOptionsMonitor<LlmOptions>>(),
            services.GetRequiredService<IOptionsMonitor<InvestigationOptions>>(),
            services.GetRequiredService<ILogger<InvestigationRunner>>());

        var outcome = await runner.RunAsync(Rebuild(cassette, key), ct).ConfigureAwait(false);

        var judged = await JudgeAsync(judge, cassette, outcome.Investigation, ct).ConfigureAwait(false);

        return ScenarioScorer.Combine(cassette, key, outcome.Investigation, replay.Summarise(), judged);
    }

    private static async Task<JudgeVerdict?> JudgeAsync(
        IRootCauseJudge? judge,
        Cassette cassette,
        Investigation investigation,
        CancellationToken ct)
    {
        if (judge is null)
        {
            return null;
        }

        var primary = investigation.Findings.FirstOrDefault(f => f.IsPrimary);

        // Nothing to grade is not a question the judge can answer, and asking it anyway would
        // spend a call to be told so.
        return primary is null
            ? null
            : await judge.AskAsync(
                cassette.ExpectedRootCause,
                GeminiRootCauseJudge.Describe(primary),
                ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The incident a cassette was recorded from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rebuilt field-for-field from the recording, not approximated: the incident card is a whole
    /// section of the system prompt, and a replay that invented one would attribute the difference
    /// between two arms to the change under test when part of it was the card.
    /// </para>
    /// <para>
    /// The fallback exists only for cassettes recorded before the incident travelled with them. It
    /// is thin - a title, a kind and a target - and says so, because reading an experiment run on
    /// an invented prompt as a measurement is the mistake this whole file is trying to prevent.
    /// </para>
    /// </remarks>
    private static Incident Rebuild(Cassette cassette, AnswerKey key)
    {
        if (cassette.Incident is { } recorded)
        {
            return recorded.ToIncident();
        }

        Console.WriteLine(
            $"WARNING  {cassette.Id}: no recorded incident; the incident card is being invented "
            + "and this run is not comparable to one on a complete cassette");

        var at = cassette.Origin?.RecordedAt ?? DateTimeOffset.UnixEpoch;

        return new Incident
        {
            Title = cassette.Description,
            Kind = cassette.Origin?.IncidentKind ?? key.ExpectedKind,
            Severity = Severity.Critical,
            OpenedAt = at,
            LastSignalAt = at,
            Target = new TargetRef
            {
                Namespace = cassette.Environment?.InScopeNamespaces.FirstOrDefault() ?? "hephaisto-chaos",
                Kind = "Pod",
                Name = key.Fixture,
            },
        };
    }

    /// <summary>
    /// Says so when a cassette holds tools from a server replay cannot route back to the runner.
    /// </summary>
    /// <remarks>
    /// The runner takes Kubernetes tools by injection and Grafana tools by fetching them, so those
    /// are the only two seams. A declaration from anywhere else would be dropped from the surface
    /// silently, and every recorded call to it would come back as a miss that looks like the model
    /// changed its mind.
    /// </remarks>
    private static void WarnOnUnroutableTools(Cassette cassette, ReplayToolset replay)
    {
        var unroutable = replay.Servers
            .Where(s => s is not (ToolDeclaration.Kubernetes or ToolDeclaration.GrafanaMcp))
            .ToList();

        if (unroutable.Count > 0)
        {
            Console.WriteLine(
                $"WARNING  {cassette.Id}: tools declared by {string.Join(", ", unroutable)} "
                + "cannot be offered on replay");
        }
    }

    private static string Line(int pass, ScenarioScore score)
    {
        var verdict = score.Verdict switch
        {
            RootCauseVerdict.Correct => "correct   ",
            RootCauseVerdict.Incorrect => "incorrect ",
            _ => "no finding",
        };

        var soundness = score.StructurallySound ? string.Empty : "  UNSOUND";

        return $"  {pass}  {score.Fixture,-4} {verdict} {score.StepsUsed,3} steps  "
            + $"${score.CostUsd:F4}  {score.Replay}{soundness}";
    }

    private static void Summarise(RunReport report)
    {
        Console.WriteLine();

        foreach (var tally in report.ByFixture)
        {
            Console.WriteLine($"  {tally}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {report}");

        if (report.Sound < report.Total)
        {
            Console.WriteLine(
                $"  {report.Total - report.Sound} of {report.Total} attempts failed a structural "
                + "assertion; the instrument slipped, so read those verdicts with suspicion");
        }
    }

    private static void Write(RunReport report, string directory)
    {
        Directory.CreateDirectory(directory);

        var path = Path.Combine(
            directory,
            $"{report.Label.Replace(' ', '-')}-{report.StartedAt:yyyyMMdd-HHmmss}.json");

        File.WriteAllText(path, JsonSerializer.Serialize(report, Cassette.Json));

        Console.WriteLine($"  wrote         {path}");
    }
}
