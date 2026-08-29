using System.Globalization;

namespace Hephaisto.Eval.Cli;

/// <summary>
/// A very small argument parser: <c>--name value</c>, <c>--name=value</c> and bare <c>--flag</c>.
/// </summary>
/// <remarks>
/// Hand-rolled rather than a library because the whole surface is three subcommands, and a
/// dependency here would be carried by a project that must never grow: <c>Hephaisto.Eval</c> is
/// dev-only and stays out of the pod image. It is a separate type from the commands so that
/// "did <c>--repeats</c> parse" is testable without a cluster, a database or a model.
/// </remarks>
internal sealed class EvalArguments
{
    private readonly List<(string Name, string? Value)> options;

    private EvalArguments(IReadOnlyList<string> positional, List<(string, string?)> options)
    {
        Positional = positional;
        this.options = options;
    }

    /// <summary>Arguments that were not attached to an option, in order.</summary>
    public IReadOnlyList<string> Positional { get; }

    public static EvalArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var positional = new List<string>();
        var parsed = new List<(string, string?)>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            var equals = name.IndexOf('=', StringComparison.Ordinal);

            if (equals >= 0)
            {
                parsed.Add((name[..equals], name[(equals + 1)..]));
                continue;
            }

            // A following token that itself looks like an option is the next option, not this
            // one's value - so `--no-judge --repeats 3` does not consume `--repeats` as a value.
            var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal);

            parsed.Add((name, hasValue ? args[++i] : null));
        }

        return new EvalArguments(positional, parsed);
    }

    /// <summary>The last value given for an option, or null when it was never given.</summary>
    public string? Value(string name) =>
        options.LastOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>True when the option was present at all, with or without a value.</summary>
    public bool Flag(string name) =>
        options.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every value given for an option that may be repeated, such as <c>--set</c>.</summary>
    public IReadOnlyList<string> Multiple(string name) =>
    [
        .. options
            .Where(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) && o.Value is not null)
            .Select(o => o.Value!)
    ];

    /// <summary>
    /// An integer option, falling back when absent and throwing when present but unreadable.
    /// </summary>
    /// <remarks>
    /// Loud on a bad value on purpose. Silently falling back would turn <c>--repeats thre</c> into
    /// a single-repeat run reported as if three had been asked for, and the whole reason repeats
    /// exist is that one sample of a language model is noise.
    /// </remarks>
    public int IntValue(string name, int fallback)
    {
        var raw = Value(name);

        if (raw is null)
        {
            return fallback;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"--{name} needs a whole number, not '{raw}'");
    }
}
