using System.Diagnostics.Metrics;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Telemetry;
using Hephaisto.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hephaisto.Tests;

/// <summary>
/// The budget gauge must be absent before it is known, and sticky when it cannot be refreshed.
/// </summary>
/// <remarks>
/// <c>hephaisto.llm.budget_utilization</c> spent months declared, documented, alerted on by two
/// rules and drawn by two dashboard panels while no instrument existed to emit it. Nothing
/// failed, because an unemitted gauge reads as "no data" - which on a budget panel is
/// indistinguishable from "nothing has been spent".
///
/// These tests cover the two decisions in <see cref="BudgetUtilizationSnapshot"/> that are easy
/// to get backwards, and whose failure mode in both directions is a number that looks fine.
/// </remarks>
public class BudgetGaugeTests
{
    /// <summary>
    /// Before the first poll the gauge must emit nothing rather than zero.
    /// </summary>
    /// <remarks>
    /// Zero is a claim - "no budget consumed" - and after a restart part-way through an
    /// expensive incident it is a false one. It would also cross the alert thresholds in the
    /// safe direction, so a budget that was actually exhausted would appear healthy for as long
    /// as the process took to do its first read. Absent is honest: <c>max by (scope)</c> over
    /// an absent series returns nothing, and the rules treat that as not firing.
    /// </remarks>
    [Fact]
    public void Nothing_is_published_before_the_first_poll() =>
        new BudgetUtilizationSnapshot().Measure().Should().BeEmpty(
            "zero would claim no spend at the exact moment the value is unknown");

    /// <summary>One series per window, labelled with the argument that produced it.</summary>
    /// <remarks>
    /// The scope values are <see cref="LlmBudgetService"/>'s own window constants rather than a
    /// parallel vocabulary, so a label on a series is exactly the string you would pass to
    /// <c>GetUtilizationAsync</c> to reproduce it. The dashboard's spec table claimed
    /// <c>incident/daily/monthly</c>, which named no window that exists.
    /// </remarks>
    [Fact]
    public void Each_window_is_published_under_its_own_scope()
    {
        var snapshot = new BudgetUtilizationSnapshot();
        snapshot.Set(0.1, 0.2, 0.3, 2.40, 18.00);

        snapshot.Measure()
            .Select(m => (Scope: m.Tags.ToArray().Single().Value, m.Value))
            .Should().BeEquivalentTo(new[]
            {
                (Scope: (object?)LlmBudgetService.WindowHourTokens, Value: 0.1),
                (Scope: LlmBudgetService.WindowHourCost, Value: 0.2),
                (Scope: LlmBudgetService.WindowDayCost, Value: 0.3),
            });
    }

    /// <summary>
    /// Utilization above 1.0 is reported as it is, not clamped.
    /// </summary>
    /// <remarks>
    /// A window sitting at 1.4 is a different fact from one sitting at 1.0 - it says the cap was
    /// crossed by an in-flight investigation that was allowed to finish, and how badly. Clamping
    /// would make an overrun indistinguishable from a cap reached exactly, which is the one
    /// comparison worth having.
    /// </remarks>
    [Fact]
    public void An_overrun_is_reported_rather_than_clamped()
    {
        var snapshot = new BudgetUtilizationSnapshot();
        snapshot.Set(0.0, 1.4, 0.0, 0.0, 20.00);

        snapshot.Measure().Select(m => m.Value).Should().Contain(1.4);
    }

    /// <summary>
    /// A failed poll keeps the previous values rather than resetting them.
    /// </summary>
    /// <remarks>
    /// The publisher swallows its exceptions and does not call <c>Set</c>, so a database blip
    /// shows on a panel as a flat line rather than as a budget that emptied itself. Publishing
    /// zero on failure would clear an exhausted-budget alert precisely when the database is
    /// unhealthy - the moment it is least safe to conclude that spending has stopped.
    /// </remarks>
    [Fact]
    public void A_failed_poll_leaves_the_last_known_values_in_place()
    {
        var snapshot = new BudgetUtilizationSnapshot();
        snapshot.Set(0.5, 0.6, 0.7, 1.20, 6.00);

        // What the publisher's catch block does: nothing at all.

        snapshot.Measure().Select(m => m.Value).Should().Equal(0.5, 0.6, 0.7);
    }

    /// <summary>
    /// The gauge really is registered on the meter the exporter reads.
    /// </summary>
    /// <remarks>
    /// This is the test that would have caught the original gap. Every other assertion here
    /// passes happily against a snapshot that no instrument is wired to - which is exactly the
    /// state this file exists to prevent recurring. Asserting through a real
    /// <see cref="MeterListener"/> is the only way to tell "the value is computed" from "the
    /// value is exported".
    /// </remarks>
    [Fact]
    public void The_instrument_exists_on_the_hephaisto_meter()
    {
        using var factory = new TestMeterFactory();
        var snapshot = new BudgetUtilizationSnapshot();
        snapshot.Set(0.25, 0.5, 0.75, 1.50, 5.00);

        using var publisher = new BudgetGaugePublisher(
            new ThrowingScopeFactory(),
            snapshot,
            factory,
            NullLogger<BudgetGaugePublisher>.Instance);

        var observed = new List<(string Scope, double Value)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == HephaistoTelemetry.Metrics.LlmBudgetUtilization)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            observed.Add(((string)tags.ToArray().Single().Value!, value)));

        listener.Start();
        listener.RecordObservableInstruments();

        observed.Should().BeEquivalentTo(new[]
        {
            (Scope: LlmBudgetService.WindowHourTokens, Value: 0.25),
            (Scope: LlmBudgetService.WindowHourCost, Value: 0.5),
            (Scope: LlmBudgetService.WindowDayCost, Value: 0.75),
        }, "the gauge was declared and alerted on for months without an instrument behind it");
    }

    /// <summary>
    /// Dollars remaining is absent before the first poll too.
    /// </summary>
    /// <remarks>
    /// Same reasoning as the utilization gauge, pointing the other way: an unpolled
    /// <c>budget.remaining</c> defaulting to 0.0 would read as "the budget is gone" and could
    /// scare someone into stopping a healthy agent.
    /// </remarks>
    [Fact]
    public void Nothing_remaining_is_published_before_the_first_poll() =>
        new BudgetUtilizationSnapshot().MeasureRemaining().Should().BeEmpty(
            "0.0 would claim the budget was spent at the exact moment the value is unknown");

    /// <summary>
    /// Only the two dollar windows appear, because the metric's unit is USD.
    /// </summary>
    /// <remarks>
    /// The hour-tokens window has no dollar value. Publishing it here would put a token count
    /// and a currency amount on one instrument under one unit, and any <c>sum</c> across scopes
    /// - which is the obvious thing to write - would silently add them together.
    /// </remarks>
    [Fact]
    public void Only_the_cost_windows_have_a_dollars_remaining()
    {
        var snapshot = new BudgetUtilizationSnapshot();
        snapshot.Set(0.1, 0.2, 0.3, 2.40, 18.00);

        snapshot.MeasureRemaining()
            .Select(m => (Scope: m.Tags.ToArray().Single().Value, m.Value))
            .Should().BeEquivalentTo(new[]
            {
                (Scope: (object?)LlmBudgetService.WindowHourCost, Value: 2.40),
                (Scope: LlmBudgetService.WindowDayCost, Value: 18.00),
            });
    }

    /// <summary>
    /// An overspent window reports 0 remaining, not a negative amount.
    /// </summary>
    /// <remarks>
    /// The deliberate asymmetry with <see cref="An_overrun_is_reported_rather_than_clamped"/>:
    /// utilization must show 1.4 because the size of the overshoot is the information, but
    /// "minus forty cents remaining" is not an amount anyone has. The overshoot is still on the
    /// utilization gauge, so clamping here loses nothing.
    /// </remarks>
    [Fact]
    public void An_overspent_window_has_zero_remaining_rather_than_a_negative_balance()
    {
        var snapshot = new BudgetUtilizationSnapshot();
        snapshot.Set(0.0, 1.4, 0.5, 0.0, 10.00);

        snapshot.MeasureRemaining().Select(m => m.Value).Should().Equal(0.0, 10.00);
    }

    /// <summary>
    /// The remaining gauge is registered on the meter the exporter reads.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="The_instrument_exists_on_the_hephaisto_meter"/>, and worth
    /// duplicating rather than folding in: <c>hephaisto.budget.remaining</c> was in the metric
    /// spec, on the dashboard and in the roadmap's exit criterion with no instrument behind it,
    /// which is the same failure this file already exists to prevent.
    /// </remarks>
    [Fact]
    public void The_remaining_instrument_exists_on_the_hephaisto_meter()
    {
        using var factory = new TestMeterFactory();
        var snapshot = new BudgetUtilizationSnapshot();
        snapshot.Set(0.25, 0.5, 0.75, 1.50, 5.00);

        using var publisher = new BudgetGaugePublisher(
            new ThrowingScopeFactory(),
            snapshot,
            factory,
            NullLogger<BudgetGaugePublisher>.Instance);

        var observed = new List<(string Scope, double Value)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == HephaistoTelemetry.Metrics.BudgetRemaining)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            observed.Add(((string)tags.ToArray().Single().Value!, value)));

        listener.Start();
        listener.RecordObservableInstruments();

        observed.Should().BeEquivalentTo(new[]
        {
            (Scope: LlmBudgetService.WindowHourCost, Value: 1.50),
            (Scope: LlmBudgetService.WindowDayCost, Value: 5.00),
        }, "budget.remaining was specced, charted and named in the exit criterion but never emitted");
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in meters)
            {
                meter.Dispose();
            }
        }
    }

    /// <summary>
    /// The publisher is constructed but never started here, so its scope factory is never used.
    /// Throwing makes that explicit rather than leaving a null to be dereferenced later.
    /// </summary>
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new NotSupportedException(
            "this test constructs the publisher for its instrument registration only");
    }
}
