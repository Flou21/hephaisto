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

    [Fact]
    public void The_evidence_itself_survives()
    {
        // Redaction must not become editing. The pod spec is what the diagnosis cites.
        const string spec = "kind: Pod\nimage: busybox:this-tag-does-not-exist\npodIP: 10.42.0.9";

        var scrubbed = TranscriptRedactor.Scrub(spec);

        Assert.Contains("busybox:this-tag-does-not-exist", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("10.42.0.9", scrubbed, StringComparison.Ordinal);
    }
}
