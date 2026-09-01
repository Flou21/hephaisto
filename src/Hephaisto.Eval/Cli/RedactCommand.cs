using Hephaisto.Agent.Demo;

namespace Hephaisto.Eval.Cli;

/// <summary>
/// Re-writes transcripts through the redactor.
/// </summary>
/// <remarks>
/// <para>
/// <c>Transcript.Save</c> redacts, so this is a load-and-save. It exists because the redaction
/// rules can change - backlog #81 says in as many words that the current rule is sound for the
/// current corpus and would need revisiting for a fixture whose diagnosis turns on an address -
/// and when they do, the committed corpus has to be brought forward without spending a model
/// run to regenerate content that is already correct.
/// </para>
/// <para>
/// It is deliberately not part of <c>run</c>. Re-redacting is a decision about published
/// artifacts and should appear in a diff on its own, rather than arriving as a side effect of
/// measuring something.
/// </para>
/// </remarks>
public static class RedactCommand
{
    public static int Run(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var files = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.GetFiles(path, "*.json").OrderBy(f => f, StringComparer.Ordinal));
            }
            else
            {
                files.Add(path);
            }
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("redact needs a transcript directory or one or more transcript paths");
            return 2;
        }

        foreach (var file in files)
        {
            var before = File.ReadAllText(file);
            Transcript.Load(file).Save(file);
            var after = File.ReadAllText(file);

            Console.WriteLine(before == after
                ? $"  unchanged  {file}"
                : $"  REDACTED   {file}");
        }

        return 0;
    }
}
