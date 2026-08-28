using System.Text.RegularExpressions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Fingerprinting;
using Hephaisto.Tests.TestData;

namespace Hephaisto.Tests;

public sealed class SignalFingerprinterTests
{
    private const string Cluster = "macstudio";

    [Fact]
    public void TwoPodsOfOneDeployment_ShareAFingerprint()
    {
        // The test the whole design hangs on. A crash-looping Deployment produces a new pod
        // name every couple of minutes; if the pod name reached the hash, fifty observations of
        // one broken Deployment would become fifty incidents, fifty investigations and fifty
        // LLM bills, and the agent would never notice it was looking at a single problem.
        var first = Given.Signal(target: Given.Target(name: "api-7d4c9f8b6-x2k9p"));
        var second = Given.Signal(target: Given.Target(name: "api-7d4c9f8b6-qq47t"));

        SignalFingerprinter.Compute(second, Cluster)
            .Should().Be(SignalFingerprinter.Compute(first, Cluster));
    }

    [Fact]
    public void PodUid_DoesNotAffectTheFingerprint()
    {
        var first = Given.Signal();
        var second = Given.Signal();
        second.Target.Uid = Guid.NewGuid().ToString();

        SignalFingerprinter.Compute(second, Cluster)
            .Should().Be(SignalFingerprinter.Compute(first, Cluster));
    }

    [Fact]
    public void DifferentNamespaces_Differ()
    {
        var prod = Given.Signal(target: Given.Target("prod"));
        var staging = Given.Signal(target: Given.Target("staging"));

        SignalFingerprinter.Compute(staging, Cluster)
            .Should().NotBe(SignalFingerprinter.Compute(prod, Cluster));
    }

    [Fact]
    public void DifferentReasons_Differ()
    {
        var backOff = Given.Signal(reason: "BackOff");
        var failed = Given.Signal(reason: "FailedMount");

        SignalFingerprinter.Compute(failed, Cluster)
            .Should().NotBe(SignalFingerprinter.Compute(backOff, Cluster));
    }

    [Fact]
    public void DifferentKinds_Differ()
    {
        var crashLoop = Given.Signal(kind: SignalKind.CrashLoopBackOff);
        var oom = Given.Signal(kind: SignalKind.OomKilled);

        SignalFingerprinter.Compute(oom, Cluster)
            .Should().NotBe(SignalFingerprinter.Compute(crashLoop, Cluster));
    }

    [Fact]
    public void DifferentSources_Differ()
    {
        var watch = Given.Signal(source: SignalSource.KubernetesWatch);
        var alert = Given.Signal(source: SignalSource.Alertmanager);

        SignalFingerprinter.Compute(alert, Cluster)
            .Should().NotBe(SignalFingerprinter.Compute(watch, Cluster));
    }

    [Fact]
    public void DifferentClusters_Differ()
    {
        // Two clusters reporting into one database must not collide, and a staging fingerprint
        // must never be replayable into production's dedup.
        var signal = Given.Signal();

        SignalFingerprinter.Compute(signal, "laptop")
            .Should().NotBe(SignalFingerprinter.Compute(signal, "macstudio"));
    }

    [Fact]
    public void DifferentOwners_Differ()
    {
        var api = Given.Signal(target: Given.Target(ownerName: "api"));
        var worker = Given.Signal(target: Given.Target(ownerName: "worker"));

        SignalFingerprinter.Compute(worker, Cluster)
            .Should().NotBe(SignalFingerprinter.Compute(api, Cluster));
    }

    [Fact]
    public void WithNoOwner_TheObjectItselfIdentifiesTheSignal()
    {
        // A bare Pod, a Node or a PVC has no controller, so falling back to its own name is the
        // only identity available - and there the name is stable, not cattle.
        var first = Given.Signal(target: Given.Target(kind: "Node", name: "node-a", ownerKind: null, ownerName: null));
        var second = Given.Signal(target: Given.Target(kind: "Node", name: "node-b", ownerKind: null, ownerName: null));

        SignalFingerprinter.Compute(second, Cluster)
            .Should().NotBe(SignalFingerprinter.Compute(first, Cluster));
    }

    [Fact]
    public void AnEmptyOwnerName_FallsBackRatherThanCollapsing()
    {
        var owned = Given.Signal(target: Given.Target(ownerKind: "Deployment", ownerName: "api"));
        var bare = Given.Signal(target: Given.Target(ownerKind: "Deployment", ownerName: string.Empty));

        SignalFingerprinter.Compute(bare, Cluster)
            .Should().NotBe(SignalFingerprinter.Compute(owned, Cluster));
    }

    [Fact]
    public void TheFingerprint_IsLowercaseHexSha256()
    {
        var fingerprint = SignalFingerprinter.Compute(Given.Signal(), Cluster);

        fingerprint.Should().HaveLength(64);
        Regex.IsMatch(fingerprint, "^[0-9a-f]{64}$").Should().BeTrue();
    }

    [Fact]
    public void TheFingerprint_IsStableAcrossCalls()
    {
        // Fingerprints are persisted and compared against rows written by earlier processes,
        // so anything non-deterministic here silently stops dedup working.
        var signal = Given.Signal();

        SignalFingerprinter.Compute(signal, Cluster)
            .Should().Be(SignalFingerprinter.Compute(signal, Cluster));
    }

    [Fact]
    public void ChangingTheMessage_DoesNotChangeTheFingerprint()
    {
        // Messages carry pod names, timestamps and counters. Hashing them would defeat dedup.
        var first = Given.Signal();
        var second = Given.Signal();
        second.Message = "Back-off restarting failed container api in pod api-7d4c9f8b6-qq47t";

        SignalFingerprinter.Compute(second, Cluster)
            .Should().Be(SignalFingerprinter.Compute(first, Cluster));
    }

    // --- correlation key -----------------------------------------------------------------

    [Fact]
    public void CorrelationKey_MergesDifferentSignalKindsOnOneWorkload()
    {
        // The coarser key: an OOMKill and a latency alert on the same Deployment are one cause
        // with two symptoms, and merging them is what stops the agent investigating twice.
        var oom = Given.Signal(kind: SignalKind.OomKilled, reason: "OOMKilling");
        var latency = Given.Signal(kind: SignalKind.HighLatency, reason: "HighLatency");

        SignalFingerprinter.CorrelationKey(latency)
            .Should().Be(SignalFingerprinter.CorrelationKey(oom));
    }

    [Fact]
    public void CorrelationKey_IsNamespaceOwnerKindOwnerName()
    {
        SignalFingerprinter.CorrelationKey(Given.Signal())
            .Should().Be("prod/Deployment/api");
    }

    [Fact]
    public void CorrelationKey_DiffersByNamespace()
    {
        var prod = Given.Signal(target: Given.Target("prod"));
        var staging = Given.Signal(target: Given.Target("staging"));

        SignalFingerprinter.CorrelationKey(staging)
            .Should().NotBe(SignalFingerprinter.CorrelationKey(prod));
    }

    [Fact]
    public void CorrelationKey_FallsBackToTheObjectWhenThereIsNoOwner()
    {
        var signal = Given.Signal(target: Given.Target(kind: "Node", name: "node-a", ownerKind: null, ownerName: null));

        SignalFingerprinter.CorrelationKey(signal).Should().Be("prod/Node/node-a");
    }

    [Fact]
    public void CorrelationKey_IsCoarserThanTheFingerprint()
    {
        var oom = Given.Signal(kind: SignalKind.OomKilled, reason: "OOMKilling");
        var latency = Given.Signal(kind: SignalKind.HighLatency, reason: "HighLatency");

        SignalFingerprinter.Compute(latency, Cluster)
            .Should().NotBe(SignalFingerprinter.Compute(oom, Cluster));
        SignalFingerprinter.CorrelationKey(latency)
            .Should().Be(SignalFingerprinter.CorrelationKey(oom));
    }
}
