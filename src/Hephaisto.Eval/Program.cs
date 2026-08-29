using Hephaisto.Eval;

// The eval harness CLI. One command today - `inspect` - because a cassette that cannot be read
// and checked by hand is a fixture nobody will trust. `run` follows, once scenarios can be
// scored; adding it here before it can score anything would be a subcommand that lies.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(
        """
        hephaisto-eval - replay recorded incidents against recorded tool output

          inspect <cassette.json>...   validate a cassette and describe what it holds

        A cassette records one scenario's tool surface and every answer it gave, so a prompt
        or budget change can be measured without a cluster and without a seeded fault.
        """);

    return 0;
}

switch (args[0])
{
    case "inspect":
        return Inspect(args.Skip(1).ToArray());

    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'; try --help");
        return 2;
}

static int Inspect(string[] paths)
{
    if (paths.Length == 0)
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

            if (cassette.Origin is { } origin)
            {
                Console.WriteLine(
                    $"  recorded      {origin.RecordedAt:u} from investigation {origin.InvestigationId} "
                    + $"({origin.AgentVersion ?? "unknown version"}, {origin.ModelId ?? "unknown model"})");
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
