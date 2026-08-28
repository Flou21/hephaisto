using System.Globalization;
using System.Text;
using Hephaisto.Core.Digest;
using Hephaisto.Tests.TestData;

namespace Hephaisto.Tests;

/// <summary>
/// The digester is the difference between an agent that can reason about a log and one that
/// drowns in it - and between a four-figure LLM bill per investigation and a sensible one.
/// </summary>
public sealed class LogDigesterTests
{
    private static LogDigestOptions Options() => new();

    private static string Join(IEnumerable<string> lines) => string.Join('\n', lines);

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    // --- normalisation and clustering --------------------------------------------------------

    [Fact]
    public void FiveHundredVaryingLines_CollapseIntoOneCluster()
    {
        // Every line differs - request id, peer address, duration - and every line says the
        // same thing. Handed the raw text a model attends to the bulk; handed "x500" it sees
        // one fact and has room left for the three lines that explain the fault.
        var raw = Join(Enumerable.Range(0, 500).Select(i =>
            $"2026-08-28T10:{i / 60 % 60:D2}:{i % 60:D2}Z request 550e8400-e29b-41d4-a716-{i:D12} " +
            $"from 10.0.0.{(i % 250) + 1} took {1000 + i}ms"));

        var digest = LogDigester.Digest(raw, Options());

        digest.OriginalLineCount.Should().Be(500);
        digest.Text.Should().Contain("x500 [");
        digest.Text.Split('\n').Count(l => l.StartsWith('x')).Should().Be(1);
    }

    [Theory]
    [InlineData("req 550e8400-e29b-41d4-a716-446655440000 done", "req 6ba7b810-9dad-11d1-80b4-00c04fd430c8 done")]
    [InlineData("peer 10.1.2.3 closed", "peer 192.168.9.44 closed")]
    [InlineData("upstream 10.1.2.3:8080 closed", "upstream 172.16.0.9:9090 closed")]
    [InlineData("handler took 1500ms", "handler took 42ms")]
    [InlineData("container a1b2c3d4e5f6 exited", "container ff00aa11bb22 exited")]
    [InlineData("offset 1234567 committed", "offset 89012345 committed")]
    public void LinesDifferingOnlyInVolatileTokens_Cluster(string first, string second)
    {
        var digest = LogDigester.Digest($"{first}\n{second}", Options());

        digest.Text.Should().Contain("x2 [");
    }

    [Fact]
    public void LinesThatGenuinelyDiffer_DoNotCluster()
    {
        var digest = LogDigester.Digest("started listening\nshutting down", Options());

        digest.Text.Should().NotContain("x2 [");
    }

    [Fact]
    public void AnsiEscapes_AreStripped()
    {
        var digest = LogDigester.Digest("\u001b[31mERROR\u001b[0m boom", Options());

        digest.Text.Should().Contain("ERROR boom");
        digest.Text.Should().NotContain("\u001b");
    }

    [Fact]
    public void LeadingTimestamps_BecomeTheClusterBounds()
    {
        var raw = Join([
            "2026-08-28T10:00:00Z upstream 10.0.0.1 refused connection",
            "2026-08-28T10:04:00Z upstream 10.0.0.9 refused connection",
        ]);

        var digest = LogDigester.Digest(raw, Options());

        digest.Text.Should().Contain("x2 [2026-08-28T10:00:00Z .. 2026-08-28T10:04:00Z]");
    }

    [Fact]
    public void ASingleOccurrence_IsNotAPattern()
    {
        var digest = LogDigester.Digest("only once", Options());

        digest.Text.Should().NotContain("-- repeated patterns --");
    }

    // --- the byte cap ------------------------------------------------------------------------

    [Fact]
    public void TheEightKilobyteCapHolds()
    {
        // The cap is a promise made to the context window, and a promise that only usually
        // holds is not one.
        var raw = Join(Enumerable.Range(0, 5000).Select(i =>
            $"worker {i} handled request {Guid.NewGuid()} in a line long enough to matter {new string('y', 120)}"));

        var digest = LogDigester.Digest(raw, Options());

        Bytes(digest.Text).Should().BeLessThanOrEqualTo(Options().MaxBytes);
        digest.Truncated.Should().BeTrue();
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void ASmallerCapIsAlsoHonoured(int maxBytes)
    {
        var raw = Join(Enumerable.Range(0, 2000).Select(i => $"line {i} {new string('z', 200)}"));

        var digest = LogDigester.Digest(raw, new LogDigestOptions { MaxBytes = maxBytes });

        Bytes(digest.Text).Should().BeLessThanOrEqualTo(maxBytes);
    }

    [Fact]
    public void FatalLinesSurviveEvenWhenTheDigestIsTruncated()
    {
        // Notable lines are allocated the byte budget first. A digest that dropped the panic to
        // make room for forty more lines of routine chatter would be worse than no digest.
        var lines = Enumerable
            .Range(0, 3000)
            .Select(i => $"worker {i} processed batch {new string('x', 200)}-{i}")
            .ToArray();
        lines[5] = "FATAL: database connection refused after 5 retries";

        var digest = LogDigester.Digest(Join(lines), Options());

        digest.Text.Should().Contain("FATAL: database connection refused after 5 retries");
        Bytes(digest.Text).Should().BeLessThanOrEqualTo(Options().MaxBytes);
        digest.Truncated.Should().BeTrue();
    }

    [Theory]
    [InlineData("panic: runtime error")]
    [InlineData("FATAL shutting down")]
    [InlineData("unhandled exception in handler")]
    [InlineData("container killed: OOM")]
    [InlineData("connection refused by upstream")]
    [InlineData("request Timeout after 30s")]
    [InlineData("unauthorized: token expired")]
    [InlineData("access denied for user")]
    public void EveryNotableKeywordIsRecognised(string notableLine)
    {
        // Deliberately broad matching: a false positive costs a few bytes, a false negative
        // costs the diagnosis.
        var lines = Enumerable.Range(0, 200).Select(i => $"quiet line {i:D3}").ToArray();
        lines[0] = notableLine;

        var digest = LogDigester.Digest(Join(lines), Options());

        digest.Text.Should().Contain(notableLine);
    }

    [Fact]
    public void ANotableLineBringsItsContext()
    {
        // The frames either side of a stack trace's first line are where the cause usually is.
        var lines = Enumerable.Range(0, 100).Select(i => $"ctx {i:D3}").ToArray();
        lines[50] = "panic: runtime error: index out of range";

        var digest = LogDigester.Digest(Join(lines), Options());

        digest.Text.Should().Contain("ctx 047").And.Contain("ctx 053");
        digest.Text.Should().NotContain("ctx 046");
        digest.Text.Should().NotContain("ctx 054");
    }

    // --- the tail ------------------------------------------------------------------------------

    [Fact]
    public void TheLastFortyLinesAreKeptVerbatim()
    {
        var raw = Join(Enumerable.Range(0, 200).Select(i => $"event number {i} occurred"));

        var digest = LogDigester.Digest(raw, Options());

        digest.Text.Should().Contain("event number 199 occurred");
        digest.Text.Should().Contain("event number 160 occurred");
        digest.Text.Should().NotContain("event number 159 occurred");
    }

    [Fact]
    public void ShorterThanFortyLines_IsKeptWhole()
    {
        var raw = Join(Enumerable.Range(0, 5).Select(i => $"step {i} of setup"));

        var digest = LogDigester.Digest(raw, Options());

        for (var i = 0; i < 5; i++)
        {
            digest.Text.Should().Contain($"step {i} of setup");
        }
    }

    // --- truncation bookkeeping -----------------------------------------------------------------

    [Fact]
    public void NothingOmitted_MeansNotTruncated()
    {
        var digest = LogDigester.Digest("alpha\nbravo\ncharlie\ndelta\necho", Options());

        digest.OriginalLineCount.Should().Be(5);
        digest.OmittedLineCount.Should().Be(0);
        digest.Truncated.Should().BeFalse();
        digest.Text.Should().NotContain("[truncated");
    }

    [Fact]
    public void CollapsedLinesCountAsOmitted()
    {
        // A cluster shows one exemplar; the other ninety-nine lines are genuinely not in the
        // digest, and the count has to say so rather than pretending the digest is complete.
        var raw = Join(Enumerable.Repeat("the same thing happened", 100));

        var digest = LogDigester.Digest(raw, Options());

        digest.OriginalLineCount.Should().Be(100);
        digest.Text.Should().Contain("x100 [");
        digest.OmittedLineCount.Should().Be(59, "40 lines of tail plus one exemplar are shown");
        digest.Truncated.Should().BeTrue();
        digest.Text.Should().Contain("[truncated: 59 of 100 lines omitted]");
    }

    [Fact]
    public void TheHeaderReportsTheOriginalSize()
    {
        var raw = Join(Enumerable.Range(0, 12).Select(i => $"line {i}"));

        var digest = LogDigester.Digest(raw, Options());

        digest.OriginalBytes.Should().Be(Bytes(raw));
        digest.Text.Should().StartWith($"log digest: 12 lines, {Bytes(raw)} bytes");
    }

    [Fact]
    public void EmptyInput_IsHandled()
    {
        var digest = LogDigester.Digest(string.Empty, Options());

        digest.OriginalLineCount.Should().Be(0);
        digest.OmittedLineCount.Should().Be(0);
        digest.Truncated.Should().BeFalse();
    }

    [Fact]
    public void ATrailingNewlineIsPunctuationNotALine()
    {
        LogDigester.Digest("alpha\nbravo\n", Options()).OriginalLineCount.Should().Be(2);
    }

    [Fact]
    public void CarriageReturnsAreNormalised()
    {
        var digest = LogDigester.Digest("alpha\r\nbravo\r\n", Options());

        digest.OriginalLineCount.Should().Be(2);
        digest.Text.Should().NotContain("\r");
    }

    // --- PromQL ---------------------------------------------------------------------------------

    [Fact]
    public void APromQlRangeIsDownsampledAndSummarised()
    {
        // A model handed 1000 raw samples reports the shape of whichever few it attended to.
        // Handed min, max, first, last and delta it reports the shape.
        var points = Enumerable
            .Range(0, 1000)
            .Select(i => new SeriesPoint(Given.Now.AddSeconds(i), i * 0.5))
            .ToList();

        var text = LogDigester.DigestPromQlRange("up{job=\"api\"}", points);

        text.Should().Contain("points: 1000 sampled to 51");
        text.Should().Contain("min=0 max=499.5 first=0 last=499.5 delta=+499.5");
        text.Split('\n').Count(l => l.StartsWith("2026", StringComparison.Ordinal)).Should().Be(51);
    }

    [Fact]
    public void TheMostRecentSampleIsNeverLostToTheStride()
    {
        // "How is it right now" is the question being asked, so the last point cannot fall off
        // the end of an uneven stride.
        var points = Enumerable
            .Range(0, 137)
            .Select(i => new SeriesPoint(Given.Now.AddSeconds(i), i))
            .ToList();

        var text = LogDigester.DigestPromQlRange("rate(errors[5m])", points);

        text.Should().Contain(Given.Now.AddSeconds(136).ToString("O", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AShortSeriesIsNotDownsampled()
    {
        var points = Enumerable.Range(0, 10).Select(i => new SeriesPoint(Given.Now.AddSeconds(i), i)).ToList();

        LogDigester.DigestPromQlRange("q", points).Should().Contain("points: 10 sampled to 10");
    }

    [Fact]
    public void AnEmptySeriesSaysSo()
    {
        LogDigester.DigestPromQlRange("q", []).Should().Contain("no data points");
    }

    // --- describe ---------------------------------------------------------------------------------

    [Fact]
    public void DescribeStripsManagedFieldsAndResourceVersion()
    {
        // managedFields alone is routinely larger than everything a human would look at.
        var raw = Join([
            "apiVersion: v1",
            "kind: Pod",
            "metadata:",
            "  name: api",
            "  resourceVersion: \"1284412\"",
            "  managedFields:",
            "  - apiVersion: v1",
            "    manager: kubectl-client-side-apply",
            "    operation: Apply",
            "  labels:",
            "    app: api",
            "status:",
            "  phase: Running",
        ]);

        var digest = LogDigester.DigestDescribe(raw);

        digest.Should().NotContain("managedFields");
        digest.Should().NotContain("kubectl-client-side-apply");
        digest.Should().NotContain("resourceVersion");
        digest.Should().Contain("app: api");
        digest.Should().Contain("phase: Running");
    }

    [Fact]
    public void DescribeDropsMostAnnotationsButKeepsTheRevision()
    {
        // last-applied-configuration is a second full copy of the spec, and the revision is
        // the one annotation that ever explains anything.
        var raw = Join([
            "Name:         api-7d4c9f8b6-x2k9p",
            "Namespace:    prod",
            "Annotations:  deployment.kubernetes.io/revision: 3",
            "              kubectl.kubernetes.io/last-applied-configuration:",
            "                {\"apiVersion\":\"apps/v1\",\"kind\":\"Deployment\"}",
            "Status:       Running",
        ]);

        var digest = LogDigester.DigestDescribe(raw);

        digest.Should().Contain("deployment.kubernetes.io/revision: 3");
        digest.Should().NotContain("last-applied-configuration");
        digest.Should().NotContain("apps/v1");
        digest.Should().Contain("Name:         api-7d4c9f8b6-x2k9p");
        digest.Should().Contain("Status:       Running");
    }

    [Fact]
    public void DescribeKeepsTheEventsTable()
    {
        // Events are the most valuable part of a describe; the strip has to degrade to
        // "kept too much", never to "dropped the Events".
        var raw = Join([
            "Name:  api",
            "Events:",
            "  Type     Reason     Age   Message",
            "  Warning  BackOff    2m    Back-off restarting failed container",
        ]);

        var digest = LogDigester.DigestDescribe(raw);

        digest.Should().Contain("Events:");
        digest.Should().Contain("BackOff");
        digest.Should().Contain("Back-off restarting failed container");
    }

    [Fact]
    public void DescribeKeepsHephaistoOwnAnnotations()
    {
        var raw = Join([
            "Annotations:  hephaisto.io/last-action: restart",
            "              example.com/noise: yes",
            "Status: Running",
        ]);

        var digest = LogDigester.DigestDescribe(raw);

        digest.Should().Contain("hephaisto.io/last-action");
        digest.Should().NotContain("example.com/noise");
    }

    // --- contract ------------------------------------------------------------------------------

    [Fact]
    public void DigestRejectsNullInput()
    {
        var act = () => LogDigester.Digest(null!, Options());

        act.Should().Throw<ArgumentNullException>();
    }
}
