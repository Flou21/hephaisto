using System.Text.RegularExpressions;

namespace Hephaisto.Agent.Demo;

/// <summary>
/// Removes addresses from a transcript before it becomes a committed artifact.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <c>cassettes/</c> is deliberately untracked, and the reason
/// is in <c>.gitignore</c>: <c>SafeToolDecorator</c> redacts tool <i>arguments</i>, not tool
/// <i>results</i>, so raw <c>describe_pod</c> and <c>get_pod_logs</c> output carries whatever
/// the cluster had in it. A transcript carries the same evidence blobs, and unlike a cassette
/// it is meant to be committed and published - so the same class of content needs a decision
/// rather than an assumption.
/// </para>
/// <para>
/// <b>What is removed, and why only this.</b> IPv4 addresses, which are the only thing in the
/// recorded corpus that describes somebody's network rather than the fault under study. Nothing
/// else is touched: the pod specs, the events, the container statuses and the log lines are the
/// evidence the diagnosis cites, and a transcript whose evidence had been edited would be a
/// mock-up wearing the costume of a recording.
/// </para>
/// <para>
/// <b>It is safe to do because no diagnosis depends on it.</b> Every fixture in the corpus is a
/// workload-level fault - a bad image tag, a memory limit, a missing Secret reference - and not
/// one of the answer keys turns on an address. If a networking fixture is ever added, this
/// becomes a real trade-off and the right answer is probably to leave that scenario out of the
/// published set rather than to weaken this.
/// </para>
/// </remarks>
public static partial class TranscriptRedactor
{
    /// <summary>What replaces an address, chosen to be obviously deliberate rather than plausible.</summary>
    public const string Placeholder = "0.0.0.0";

    /// <summary>
    /// Rewrites every evidence blob and step result in place.
    /// </summary>
    public static Transcript Redact(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        foreach (var blob in transcript.Blobs)
        {
            blob.Content = Scrub(blob.Content);
        }

        foreach (var step in transcript.Investigation.Steps)
        {
            if (step.ResultDigest is { } digest)
            {
                step.ResultDigest = Scrub(digest);
            }

            if (step.Arguments is { } arguments)
            {
                step.Arguments = Scrub(arguments);
            }
        }

        return transcript;
    }

    /// <summary>
    /// Replaces dotted quads. Guarded on each octet being 0-255 so version strings and
    /// decimals - <c>1.28.4</c>, a duration of <c>10.350197</c> - are left alone.
    /// </summary>
    internal static string Scrub(string text) =>
        IpV4().Replace(text, Placeholder);

    [GeneratedRegex(
        @"\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b")]
    private static partial Regex IpV4();
}
