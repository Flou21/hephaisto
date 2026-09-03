using Hephaisto.Agent.Demo;

namespace Hephaisto.Tests.Demo;

/// <summary>
/// A transcript is a committed, published artifact carrying raw tool output.
/// </summary>
/// <remarks>
/// <c>cassettes/</c> is untracked because tool <i>results</i> are never redacted - only
/// arguments are - so raw describe and log output carries whatever the cluster had in it. A
/// transcript holds the same blobs and is meant to be published, so the addresses come out.
/// </remarks>
public class TranscriptRedactorTests
{
    [Theory]
    [InlineData("podIP: 10.42.0.79", "podIP: 0.0.0.0")]
    [InlineData("hostIP: 192.168.5.15\nnodeName: x", "hostIP: 0.0.0.0\nnodeName: x")]
    [InlineData("connect to 172.16.3.4:8080 failed", "connect to 0.0.0.0:8080 failed")]
    public void Addresses_are_removed(string input, string expected) =>
        Assert.Equal(expected, TranscriptRedactor.Scrub(input));

    /// <summary>
    /// The reason the pattern checks each octet rather than matching three dots. Both of these
    /// appear in the real corpus - a Kubernetes version and a step duration - and neither is an
    /// address.
    /// </summary>
    [Theory]
    [InlineData("v1.28.4")]
    [InlineData("took 10.350197 seconds")]
    [InlineData("999.999.999.999")]
    public void Things_that_merely_contain_dots_are_left_alone(string input) =>
        Assert.Equal(input, TranscriptRedactor.Scrub(input));

    /// <summary>
    /// The regression that made this run over the whole document. The first version walked the
    /// blobs and the step results - the obvious places - and missed Incident.Target.NodeName,
    /// where a Prometheus alert had put an address:port. A field list has to be re-derived
    /// every time the schema grows; scrubbing the rendered JSON cannot go out of date.
    /// </summary>
    [Fact]
    public void A_field_nobody_thought_of_is_still_redacted()
    {
        const string json = """
            {"incident":{"target":{"nodeName":"10.42.0.128:8080"}},"blobs":[]}
            """;

        var scrubbed = TranscriptRedactor.RedactJson(json);

        Assert.DoesNotContain("10.42.0.128", scrubbed, StringComparison.Ordinal);
        Assert.Contains("\"nodeName\":\"0.0.0.0:8080\"", scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The address that reached a published page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found while building the static demo site, which renders these transcripts to HTML: an
    /// address appeared in the output that the redactor had reported nothing to do about. The
    /// cause is that this runs over a SERIALIZED document, so a newline inside an evidence blob
    /// is the two characters <c>\</c> and <c>n</c> - and a tool result whose next line began
    /// with an address serializes as <c>...\n10.42.0.68</c>. The old pattern anchored on
    /// <c>\b</c>, and there is no word boundary between <c>n</c> and <c>1</c>.
    /// </para>
    /// <para>
    /// This is the second bug of exactly this shape. The first was a field list that missed a
    /// field; scrubbing the whole document fixed that and introduced this, because scrubbing a
    /// serialized document means the escapes are part of the text being matched.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_address_immediately_after_an_escaped_newline_is_still_redacted()
    {
        // Exactly the shape found in c8.json: a kubectl-style table inside an evidence blob.
        const string json = """
            {"blobs":[{"content":"address     state\n----------  -----\n10.42.0.68  ready"}]}
            """;

        var scrubbed = TranscriptRedactor.RedactJson(json);

        Assert.DoesNotContain("10.42.0.68", scrubbed, StringComparison.Ordinal);
        Assert.Contains("0.0.0.0  ready", scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same <c>\b</c> was wrong at the other end too, and this is the more insidious half:
    /// it did not fail to redact, it redacted something that was not an address. In
    /// <c>v1.2.3.4.5</c> it matched <c>2.3.4.5</c> and rewrote a version string, which is
    /// editing the evidence rather than protecting it.
    /// </summary>
    [Theory]
    [InlineData("v1.2.3.4.5")]
    [InlineData("chart 0.6.0.1.2")]
    public void A_longer_dotted_run_is_not_an_address(string input) =>
        Assert.Equal(input, TranscriptRedactor.Scrub(input));

    [Fact]
    public void The_evidence_itself_survives()
    {
        // Redaction must not become editing. The pod spec is what the diagnosis cites.
        const string spec = "kind: Pod\nimage: busybox:this-tag-does-not-exist\npodIP: 10.42.0.9";

        var scrubbed = TranscriptRedactor.Scrub(spec);

        Assert.Contains("busybox:this-tag-does-not-exist", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("10.42.0.9", scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// An address immediately after an escaped quote is still an address.
    /// </summary>
    /// <remarks>
    /// This is backlog #84 a second time, and the shape is identical. That entry replaced
    /// <c>\b</c> because a newline reaches this function as the two characters <c>\</c> and
    /// <c>n</c>, and <c>n</c> to <c>1</c> is not a word boundary. The replacement lookbehind
    /// then lost to <c>\u0022</c>, which the serializer writes for a quote inside an evidence
    /// blob and which ends in the digit <c>2</c>.
    ///
    /// It was found by exporting a real incident: Prometheus alert JSON nested in a blob came
    /// out as <c>\u0022instance\u0022: \u002210.244.0.5:8080\u0022</c>, and the pod IP
    /// survived 66 times into a file whose entire purpose is to be published.
    /// </remarks>
    [Theory]
    [InlineData("\\u0022instance\\u0022: \\u002210.244.0.5:8080\\u0022")]
    [InlineData("\\u002210.244.0.5\\u0022")]
    [InlineData("\\n10.42.0.68")]
    [InlineData("prefix \\u0022 10.244.0.5")]
    public void An_address_after_an_escape_sequence_is_still_redacted(string escaped)
    {
        var scrubbed = TranscriptRedactor.Scrub(escaped);

        scrubbed.Should().NotContain("10.244.0.5");
        scrubbed.Should().NotContain("10.42.0.68");
        scrubbed.Should().Contain(TranscriptRedactor.Placeholder);
    }

    /// <summary>
    /// The widened boundary does not start eating version strings.
    /// </summary>
    /// <remarks>
    /// The alternation only adds ONE new place a match may begin - immediately after a complete
    /// <c>\uXXXX</c> escape. Everywhere else the old "not a digit and not a dot" still applies,
    /// which is what keeps <c>v1.2.3.4.5</c> intact.
    /// </remarks>
    [Theory]
    [InlineData("v1.2.3.4.5")]
    [InlineData("chart 0.6.0.1.2")]
    [InlineData("took 10.350197 seconds")]
    [InlineData("kubernetes 1.36.4")]
    public void The_widened_boundary_still_leaves_numbers_alone(string text)
    {
        TranscriptRedactor.Scrub(text).Should().Be(text);
    }
}
