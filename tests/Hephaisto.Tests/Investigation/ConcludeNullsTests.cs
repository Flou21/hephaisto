using Hephaisto.Agent.Investigations;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// A model's JSON null is "none", never an exception.
/// </summary>
/// <remarks>
/// Found on the v0.10.0-rc1 release gate: gpt-oss:120b concluded c5 with a finding whose
/// <c>evidence</c> was <c>null</c>. The null replaced the empty list the property starts as, the
/// mapper's foreach threw, and an investigation that had reached a conclusion escalated as
/// InvestigationFailed with "Object reference not set to an instance of an object". The pager
/// suite's stand-in never sends null, so nothing before the real model could have found it.
/// </remarks>
public sealed class ConcludeNullsTests
{
    [Fact]
    public void A_finding_with_null_evidence_is_a_finding_with_none()
    {
        var request = new ConcludeRequest
        {
            Summary = "c5's job fails on a missing secret",
            Findings = [new FindingDraft { Hypothesis = "missing secret", Confidence = 0.7, Primary = true, Evidence = null! }],
        };

        var findings = ConcludeMapper.ToFindings(request, Guid.NewGuid(), []);

        findings.Should().ContainSingle().Which.Evidence.Should().BeEmpty();
    }

    [Fact]
    public void Null_findings_null_drafts_and_null_citations_are_skipped()
    {
        ConcludeMapper.ToFindings(new ConcludeRequest { Findings = null! }, Guid.NewGuid(), []).Should().BeEmpty();

        var request = new ConcludeRequest
        {
            Findings =
            [
                null!,
                new FindingDraft
                {
                    Hypothesis = null!,
                    Primary = true,
                    Evidence = [null!, new EvidenceDraft { StepId = null!, Excerpt = null! }],
                },
            ],
        };

        var finding = ConcludeMapper.ToFindings(request, Guid.NewGuid(), []).Should().ContainSingle().Subject;
        finding.Hypothesis.Should().BeEmpty();
        finding.Evidence.Should().ContainSingle().Which.Excerpt.Should().BeEmpty();
    }
}
