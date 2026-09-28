using Hephaisto.Core.Domain;

namespace Hephaisto.Tests;

/// <summary>
/// The rules of an alert note (#145) that do not need a database: which names can key one, which
/// alert an incident is about, and the caps.
/// </summary>
public sealed class AlertNoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("ConsumerLagHigh", "ConsumerLagHigh")]
    [InlineData("  ConsumerLagHigh ", "ConsumerLagHigh")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("Bad\nName", null)]
    public void A_name_is_trimmed_and_one_that_needed_cleaning_is_refused(string? raw, string? expected) =>
        AlertNote.NormaliseName(raw).Should().Be(expected);

    [Fact]
    public void A_name_longer_than_any_rule_is_refused() =>
        AlertNote.NormaliseName(new string('a', AlertNote.MaxAlertNameLength + 1)).Should().BeNull();

    [Fact]
    public void An_incident_is_about_the_alert_that_opened_it()
    {
        // The oldest alert, not the newest: a later one attached by correlation is another
        // rule's opinion about the same fault.
        var signals = new[]
        {
            Alert("LaterRule", Now.AddMinutes(5)),
            Alert("FirstRule", Now),
            new Signal { Source = SignalSource.KubernetesWatch, Reason = "BackOff", FirstSeen = Now.AddMinutes(-5) },
        };

        AlertNote.AlertNameOf(signals).Should().Be("FirstRule");
    }

    [Fact]
    public void The_alertname_label_wins_over_the_reason()
    {
        var signal = Alert("FromReason", Now);
        signal.Labels["alertname"] = "FromLabel";

        AlertNote.AlertNameOf([signal]).Should().Be("FromLabel");
    }

    [Fact]
    public void An_incident_no_alert_opened_has_no_note() =>
        AlertNote.AlertNameOf([new Signal { Source = SignalSource.KubernetesWatch, Reason = "BackOff" }]).Should().BeNull();

    [Fact]
    public void A_body_over_the_cap_is_refused_and_the_old_one_kept()
    {
        var note = new AlertNote { AlertName = "A" };
        note.SetBody("kept", "operator-a", Now);

        var act = () => note.SetBody(new string('x', AlertNote.MaxBodyLength + 1), "operator-b", Now);

        act.Should().Throw<ArgumentException>();
        note.Body.Should().Be("kept");
        note.UpdatedBy.Should().Be("operator-a");
    }

    [Fact]
    public void An_entry_that_says_nothing_is_refused()
    {
        var note = new AlertNote { AlertName = "A" };

        note.Invoking(n => n.AddEntry("   ", null, "operator-a", Now)).Should().Throw<ArgumentException>();
        note.Invoking(n => n.AddEntry(new string('x', AlertNote.MaxEntryLength + 1), null, "operator-a", Now))
            .Should().Throw<ArgumentException>();
        note.Entries.Should().BeEmpty();
    }

    [Fact]
    public void An_entry_carries_its_author_incident_and_alert()
    {
        var note = new AlertNote { AlertName = "A" };
        var incident = Guid.CreateVersion7();

        var entry = note.AddEntry(" restarted the consumer ", incident, "operator-a", Now);

        entry.Text.Should().Be("restarted the consumer");
        entry.AlertName.Should().Be("A");
        entry.IncidentId.Should().Be(incident);
        entry.Author.Should().Be("operator-a");
        note.Entries.Should().ContainSingle();
    }

    [Fact]
    public void An_excerpt_is_one_line_and_cut_with_a_mark()
    {
        AlertNote.Excerpt("line one\nline two").Should().Be("line one line two");
        AlertNote.Excerpt(new string('x', 300), 200).Should().HaveLength(201).And.EndWith("…");
        AlertNote.Excerpt("   ").Should().BeNull();
    }

    private static Signal Alert(string name, DateTimeOffset at) => new()
    {
        Source = SignalSource.Alertmanager,
        Reason = name,
        FirstSeen = at,
    };
}
