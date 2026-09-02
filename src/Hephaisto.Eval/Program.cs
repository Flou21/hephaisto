using Hephaisto.Eval;
using Hephaisto.Eval.Cli;

// The eval harness CLI. Five commands, and the split between them is the design - what each
// one NEEDS is the whole reason it is a separate verb:
//
//   record   needs a cluster, a database and money. Run rarely, on the dev cluster.
//   run      needs only the model. Run constantly, which is what makes experiments affordable.
//   export   needs a database and nothing else. It is how an incident the agent actually acted
//            on becomes a committed artifact - the one thing `run` can never produce, because
//            replay constructs an InvestigationRunner and no executor, no policy engine and no
//            state machine, so it has no executed action and no terminal state to record.
//   inspect  needs nothing. Reads a cassette so a fixture nobody can check by hand cannot exist.
//   redact   needs nothing. Re-scrubs transcripts when the rules change.
//
// Nothing here is reachable from the agent: the Dockerfile copies Core, ServiceDefaults and
// Agent only, so this project cannot grow the shipped image.

using var lifetime = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl-C cancels the investigation in flight so the loop unwinds and a partial
    // recording is still written. A second one is the runtime's, and kills the process.
    e.Cancel = !lifetime.IsCancellationRequested;
    lifetime.Cancel();
};

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(
        """
        hephaisto-eval - measure the agent's diagnosis accuracy repeatably

          record  --incident <guid> --fixture <c4> [--expect <text>] [--description <text>]
                  [--out cassettes] [--set Key=Value]
                  Runs a real investigation against the live cluster and records every tool
                  call. Needs a database, a cluster and an API key.

          run     [--cassettes <dir> | <cassette.json>...] [--repeats 3] [--label baseline]
                  [--no-judge] [--out results] [--transcripts <dir>] [--set Key=Value]
                  Replays the corpus and scores it. Needs only the model.
                  --transcripts also keeps what the run computed and normally discards: the
                  step trace, the findings, the evidence blobs and the grade. One file per
                  cassette, last pass wins. Those are what the demo stack is seeded from, so
                  it needs no model, no key and no cluster.

          export  --incident <guid> --id <c13-resolved> [--fixture c13] [--expect <text>]
                  [--description <text>] [--out <dir>] [--force] [--set Key=Value]
                  Snapshots a FINISHED incident out of the database into a transcript: the
                  transitions it actually made, the action it executed or was refused, and
                  the policy decision behind that. No model, no cluster, nothing computed.
                  Refuses an incident still in flight, one with no transitions, and one whose
                  evidence blobs the retention sweep has already taken.

          inspect <cassette.json>...
                  Validates a cassette and describes what it holds.

          redact  <transcripts/> | <transcript.json>...
                  Re-writes transcripts through the redactor and says which changed. Saving a
                  transcript always redacts, so this is only needed when the rules change.

        A cassette records one scenario's tool surface, its incident and every answer the
        cluster gave, so a prompt or budget change can be measured without a cluster and
        without a seeded fault.

        Experiment arms are `--set`, so an arm is one command line rather than an exported
        variable that outlives the run that wanted it:

          hephaisto-eval run --cassettes cassettes --repeats 3 --label baseline
          hephaisto-eval run --cassettes cassettes --repeats 3 --label steps-20 \
              --set Llm:Investigation:MaxSteps=20
        """);

    return 0;
}

try
{
    var parsed = EvalArguments.Parse(args.Skip(1).ToArray());

    return args[0] switch
    {
        "record" => await RecordCommand.RunAsync(parsed, lifetime.Token),
        "run" => await RunCommand.RunAsync(parsed, lifetime.Token),
        "export" => await ExportCommand.RunAsync(parsed, lifetime.Token),
        "inspect" => Inspect(parsed.Positional),
        "redact" => RedactCommand.Run(parsed.Positional),
        _ => Unknown(args[0]),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"unknown command '{command}'; try --help");
    return 2;
}

static int Inspect(IReadOnlyList<string> paths)
{
    if (paths.Count == 0)
    {
        Console.Error.WriteLine("inspect needs at least one cassette path");
        return 2;
    }

    var failed = 0;

    foreach (var path in paths)
    {
        try
        {
            var cassette = Cassette.Load(path);
            var toolset = new ReplayToolset(cassette);

            var declared = cassette.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var called = cassette.Calls.Select(c => c.ToolName).ToHashSet(StringComparer.Ordinal);

            Console.WriteLine($"{cassette.Id}  {cassette.Description}");
            Console.WriteLine($"  expected      {cassette.ExpectedRootCause}");
            Console.WriteLine($"  tools         {cassette.Tools.Count} declared, {called.Count} exercised");
            Console.WriteLine($"  calls         {cassette.Calls.Count}");

            if (cassette.Incident is { } incident)
            {
                Console.WriteLine(
                    $"  incident      {incident.Kind} {incident.Target.Namespace}/{incident.Target.Name}, "
                    + $"{incident.Signals.Count} signals");
            }
            else
            {
                // Not fatal, but it means a replay of this file composes an invented incident
                // card, so its numbers cannot be compared with a complete cassette's.
                Console.WriteLine("  incident      NOT RECORDED - replay will invent the incident card");
            }

            if (cassette.Origin is { } origin)
            {
                Console.WriteLine(
                    $"  recorded      {origin.RecordedAt:u} from investigation {origin.InvestigationId} "
                    + $"({origin.AgentVersion ?? "unknown version"}, {origin.ModelId ?? "unknown model"}"
                    + $"{(origin.AgentCommit is { } sha ? $", {sha}" : string.Empty)})");
            }

            // Whether the prompt fragments and runbook still say what they said when this was
            // recorded. Stale is a warning, never a refusal: measuring a rewritten runbook
            // against cassettes recorded before it is the experiment, not a mistake.
            if (PromptFingerprint.Describe(cassette) is { } freshness)
            {
                Console.WriteLine($"  {freshness}");
            }

            // A call to a tool the cassette never declared can never be replayed: the model is
            // not offered that tool, so the recording holds an answer to a question that can no
            // longer be asked. Worth saying out loud rather than discovering as a miss.
            var orphaned = called.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            if (orphaned.Count > 0)
            {
                Console.WriteLine($"  ORPHANED      {string.Join(", ", orphaned)} - recorded but not declared");
                failed++;
            }

            var unexercised = declared.Except(called, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            if (unexercised.Count > 0)
            {
                Console.WriteLine(
                    $"  no recording  {unexercised.Count} declared tools were never called; "
                    + "the model may ask and will be told so");
            }

            Console.WriteLine($"  replayable    {toolset.Functions.Count} functions rebuilt");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"{path}: {ex.Message}");
            failed++;
        }
    }

    return failed == 0 ? 0 : 1;
}
