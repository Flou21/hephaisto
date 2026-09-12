using System.Reflection;

using Hephaisto.Agent.Persistence;
using Hephaisto.Core;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests;

/// <summary>
/// Three lists answer "is this incident still live", they do not all agree, and only two of them
/// are supposed to.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Incident.IsOpen"/> is the definition. <see cref="HephaistoDbContext.OpenStates"/>
/// duplicates it as data because a computed property cannot be translated into SQL, so those two
/// must match exactly - and nothing enforced that until this file. <c>IncidentStateMachine</c> has
/// a third, private array that is deliberately NARROWER: it is the legal-predecessor set for the
/// edges leaving the live part of the lifecycle, and an escalated incident cannot escalate again.
/// </para>
/// <para>
/// The comment on that third array claimed for four releases that it "mirrors
/// <see cref="Incident.IsOpen"/>". It did not - the two have disagreed about
/// <see cref="IncidentState.Escalated"/> the whole time. That is a harmless disagreement and a
/// dangerous comment, because adding a state means auditing all three and a reader who trusts the
/// comment updates the wrong one. Adding <see cref="IncidentState.Closed"/> was the first time
/// anyone had to.
/// </para>
/// <para>
/// These tests are written over <c>Enum.GetValues</c> rather than over a hand-listed set, so a new
/// member fails them until somebody decides what it means.
/// </para>
/// </remarks>
public sealed class IncidentStateDefinitionsAgreeTests
{
    private static readonly IncidentState[] All = Enum.GetValues<IncidentState>();

    /// <summary>The array EF queries on must agree with the property everything else reads.</summary>
    [Fact]
    public void The_queryable_open_set_matches_the_property_for_every_state()
    {
        foreach (var state in All)
        {
            var property = new Incident { State = state }.IsOpen;
            var queryable = HephaistoDbContext.OpenStates.Contains(state);

            queryable.Should().Be(
                property,
                $"HephaistoDbContext.OpenStates and Incident.IsOpen disagree about {state}, so a "
                + "query and the object it returns would tell an operator different things");
        }
    }

    /// <summary>
    /// The terminal states, named explicitly. This is the test that would have to change if
    /// somebody decided a closed incident was still open, which is the point.
    /// </summary>
    [Fact]
    public void Exactly_four_states_are_terminal()
    {
        All.Where(s => !new Incident { State = s }.IsOpen)
            .Should().BeEquivalentTo([
                IncidentState.Suppressed,
                IncidentState.Resolved,
                IncidentState.Expired,
                IncidentState.Closed,
            ]);
    }

    /// <summary>
    /// Escalated is open. The agent gave up; the cluster did not get better.
    /// </summary>
    /// <remarks>
    /// Pinned on its own because it is the surprising one and because it is the reason
    /// <see cref="IncidentState.Closed"/> had to be added: on an Observe install every incident
    /// escalates, so if Escalated were not open there would have been no problem to solve, and if
    /// it is open without a terminal exit the count climbs forever.
    /// </remarks>
    [Fact]
    public void An_escalated_incident_is_still_open()
    {
        new Incident { State = IncidentState.Escalated }.IsOpen.Should().BeTrue();
        HephaistoDbContext.OpenStates.Should().Contain(IncidentState.Escalated);
    }

    /// <summary>
    /// The state machine's array is narrower than the property, and specifically by Escalated.
    /// </summary>
    [Fact]
    public void The_state_machines_predecessor_set_is_the_open_set_minus_escalated()
    {
        var field = typeof(IncidentStateMachine)
            .GetField("OpenStates", BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull(
            "this test pins a private array by name; if it was renamed, re-point the test rather "
            + "than deleting it");

        var predecessors = (IncidentState[])field!.GetValue(null)!;

        predecessors.Should().BeEquivalentTo(
            HephaistoDbContext.OpenStates.Where(s => s != IncidentState.Escalated),
            "the difference between these two lists is Escalated and nothing else - any other "
            + "divergence means one of them was updated and the other forgotten");
    }

    /// <summary>
    /// A terminal state must never be a legal predecessor of the leaving edges.
    /// </summary>
    [Fact]
    public void No_terminal_state_can_be_left_again_through_the_open_edges()
    {
        var field = typeof(IncidentStateMachine)
            .GetField("OpenStates", BindingFlags.NonPublic | BindingFlags.Static);
        var predecessors = (IncidentState[])field!.GetValue(null)!;

        foreach (var terminal in All.Where(s => !new Incident { State = s }.IsOpen))
        {
            predecessors.Should().NotContain(
                terminal,
                $"{terminal} is terminal, so resolving, escalating or expiring out of it would "
                + "overwrite a fact that is already settled");
        }
    }

    /// <summary>
    /// The NAMES are the data, not the numbers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test used to assert the numbers, with a justification that was exactly
    /// backwards.</b> It said the enum was "persisted by value, so the numbers may not be
    /// rearranged". It is not: <c>HephaistoDbContext</c> applies an
    /// <c>EnumToStringConverter</c> to every enum property, and its own comment gives the
    /// reason - "renumbering an enum silently rewrites the meaning of history". The column is
    /// <c>text</c> and holds <c>'Escalated'</c>, not <c>8</c>.
    /// </para>
    /// <para>
    /// So renumbering is harmless and RENAMING is the breaking change - it orphans every row
    /// already written, and the failure is a read that throws long after the commit that caused
    /// it. That is what this pins. The JSON API serialises names too, for the same reason, so a
    /// rename breaks stored rows and published responses together.
    /// </para>
    /// <para>
    /// Found while seeding a test database by hand: rows written with <c>state = 8</c> were
    /// invisible to every query, because the column expects a name.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(IncidentState.Detected, "Detected")]
    [InlineData(IncidentState.Triaging, "Triaging")]
    [InlineData(IncidentState.Suppressed, "Suppressed")]
    [InlineData(IncidentState.Investigating, "Investigating")]
    [InlineData(IncidentState.AwaitingApproval, "AwaitingApproval")]
    [InlineData(IncidentState.Acting, "Acting")]
    [InlineData(IncidentState.Verifying, "Verifying")]
    [InlineData(IncidentState.Resolved, "Resolved")]
    [InlineData(IncidentState.Escalated, "Escalated")]
    [InlineData(IncidentState.Expired, "Expired")]
    [InlineData(IncidentState.Closed, "Closed")]
    public void The_persisted_names_are_fixed(IncidentState state, string name) =>
        state.ToString().Should().Be(name);

    /// <summary>Nothing was added without being pinned here.</summary>
    [Fact]
    public void Every_state_is_pinned_by_the_theory_above()
    {
        // Read through CustomAttributeData rather than InlineDataAttribute.GetData: on xunit.v3
        // that method requires a DisposalTracker the test has no way to supply, and the
        // constructor arguments are the thing being asserted on anyway.
        var pinned = typeof(IncidentStateDefinitionsAgreeTests)
            .GetMethod(nameof(The_persisted_names_are_fixed))!
            .GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(InlineDataAttribute))
            .Select(a => (IReadOnlyCollection<CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!)
            .Select(args => (IncidentState)args.First().Value!)
            .ToArray();

        pinned.Should().BeEquivalentTo(All);
    }
}
