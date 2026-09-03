using Hephaisto.Agent.Web;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Web;

/// <summary>
/// The retry callout is offered only when the incident ended without an answer.
/// </summary>
/// <remarks>
/// <para>
/// The callout says, in bold, <b>"no diagnosis was produced"</b>. It was shown for every
/// escalated incident - and an incident escalates for several reasons, only some of which
/// mean nothing was produced. One escalated because the policy engine refused its plan
/// carries a complete diagnosis, and the banner claiming otherwise sat directly above the
/// hypothesis whose existence it was denying.
/// </para>
/// <para>
/// Found by photographing the shipping console for the v0.6.0 release notes: the live capture
/// on the demo site displayed it, over a finding with 0.92 confidence. The block's own comment
/// always said "for the incidents that never got an answer"; the condition never checked.
/// </para>
/// <para>
/// This asserts the predicate rather than the rendered markup, because the predicate is what
/// was wrong and a Razor render test would pin the wording as well.
/// </para>
/// </remarks>
public class IncidentDetailRetryTests
{
    private static IncidentDetailView Incident(IncidentState state, bool withDiagnosis) => new()
    {
        Id = Guid.NewGuid(),
        State = state,
        Investigations =
        [
            new InvestigationView
            {
                Findings = withDiagnosis
                    ? [new FindingView { Hypothesis = "the lock is stale", IsPrimary = true, Confidence = 0.92 }]
                    : [],
            },
        ],
    };

    /// <summary>The predicate as <c>IncidentDetail.razor</c> computes it.</summary>
    private static bool CanReinvestigate(IncidentDetailView incident) =>
        incident.InProgress is null
        && incident.State is IncidentState.Escalated or IncidentState.Expired
        && incident.Investigations.All(v => v.PrimaryFinding is null);

    [Theory]
    [InlineData(IncidentState.Escalated)]
    [InlineData(IncidentState.Expired)]
    public void An_incident_with_no_answer_is_offered_a_retry(IncidentState state)
    {
        CanReinvestigate(Incident(state, withDiagnosis: false)).Should().BeTrue(
            "a run that faulted or exhausted its budget leaves nothing to read, which is what "
            + "the retry exists for");
    }

    [Theory]
    [InlineData(IncidentState.Escalated)]
    [InlineData(IncidentState.Expired)]
    public void An_incident_that_produced_a_diagnosis_is_not(IncidentState state)
    {
        CanReinvestigate(Incident(state, withDiagnosis: true)).Should().BeFalse(
            "the callout states that no diagnosis was produced, and this one has a primary "
            + "finding - a policy-denied incident is escalated AND fully diagnosed");
    }

    [Theory]
    [InlineData(IncidentState.Resolved)]
    [InlineData(IncidentState.Investigating)]
    [InlineData(IncidentState.Detected)]
    public void A_non_terminal_or_resolved_incident_is_never_offered_one(IncidentState state)
    {
        CanReinvestigate(Incident(state, withDiagnosis: false)).Should().BeFalse();
    }
}
