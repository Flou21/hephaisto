using System.Text.Json;

namespace Hephaisto.Tests;

/// <summary>
/// The chart's values schema must reject a key it does not know (backlog #108).
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this exists to stop happened.</b> On 2026-09-11 a Fleet bundle was prepared
/// with a <c>values.yaml</c> still containing another chart's keys, and
/// <c>helm template</c> rendered it with <b>exit 0</b>: a complete, plausible, all-defaults
/// install with three <c>PrometheusRule</c> objects on a cluster that had its own, a
/// NetworkPolicy pointed at a namespace that did not exist - so no alert could ever arrive and
/// the agent reported itself healthy - and a Postgres host that was not there.
/// </para>
/// <para>
/// <b>The <c>required</c> list looks like it would catch that and cannot.</b> Helm validates
/// values AFTER merging the chart's own defaults, so every required key is always satisfied.
/// Without <c>additionalProperties: false</c> the schema therefore validates the chart against
/// itself and can never fail.
/// </para>
/// <para>
/// These assertions are structural rather than behavioural - they read the schema rather than
/// shelling out to <c>helm</c>, which the unit suite deliberately does not depend on. The
/// behaviour is covered by <c>scripts/e2e</c>, which renders the chart for real.
/// </para>
/// </remarks>
public sealed class ValuesSchemaIsClosedTests
{
    /// <summary>
    /// Objects whose keys are the operator's to choose, so locking them would reject every
    /// legitimate value. Each declares no properties, which is what makes them free-form.
    /// </summary>
    private static readonly string[] FreeForm =
    [
        "resources",
        "nodeSelector",
        "affinity",
        "podAnnotations",
    ];

    [Fact]
    public void The_root_rejects_keys_it_does_not_declare()
    {
        var root = Schema().RootElement;

        root.TryGetProperty("additionalProperties", out var value).Should().BeTrue(
            "without this a values file belonging to an entirely different chart renders as a "
            + "plausible all-defaults install, which is exactly what happened on 2026-09-11");

        value.GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// Every object with a closed key set is locked too, so a typo in a nested key is an error
    /// rather than a silently ignored setting.
    /// </summary>
    /// <remarks>
    /// <c>policy.actionableNamespace</c> - singular - is the shape of mistake this catches: the
    /// agent would act nowhere, which looks identical to the correct default.
    /// </remarks>
    [Fact]
    public void Every_object_with_declared_properties_is_locked()
    {
        var unlocked = new List<string>();

        foreach (var property in Schema().RootElement.GetProperty("properties").EnumerateObject())
        {
            if (FreeForm.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var value = property.Value;

            if (!value.TryGetProperty("type", out var type) || type.GetString() != "object")
            {
                continue;
            }

            // No declared properties means the keys are the operator's; locking would reject
            // everything. Those are listed in FreeForm and skipped above - reaching here with
            // none declared means a new one appeared and needs a decision.
            if (!value.TryGetProperty("properties", out var declared) || !declared.EnumerateObject().Any())
            {
                unlocked.Add($"{property.Name} (declares no properties - add it to FreeForm, or declare them)");
                continue;
            }

            if (!value.TryGetProperty("additionalProperties", out var additional) || additional.GetBoolean())
            {
                unlocked.Add(property.Name);
            }
        }

        unlocked.Should().BeEmpty();
    }

    /// <summary>
    /// The free-form objects stay open.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed, because the obvious over-correction to the bug above is to
    /// lock everything - and locking <c>nodeSelector</c> makes it impossible to schedule the pod.
    /// </remarks>
    [Theory]
    [InlineData("resources")]
    [InlineData("nodeSelector")]
    [InlineData("affinity")]
    [InlineData("podAnnotations")]
    public void The_free_form_objects_are_not_locked(string name)
    {
        var value = Schema().RootElement.GetProperty("properties").GetProperty(name);

        if (value.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind is JsonValueKind.False)
        {
            Assert.Fail($"{name} is locked, but its keys belong to whoever installs the chart.");
        }
    }

    /// <summary>
    /// Every key the chart's own values.yaml sets must be declared.
    /// </summary>
    /// <remarks>
    /// This is what makes locking safe, and it found three real gaps when it was written -
    /// <c>grafana</c>, <c>grafanaMcp.datasourceUids</c> and <c>postgres.appUser</c> were all
    /// undeclared, so locking the root without them would have broken the chart's own defaults.
    /// Nothing had ever compared the two files.
    /// </remarks>
    [Fact]
    public void Every_shipped_value_is_declared_in_the_schema()
    {
        var schema = Schema().RootElement.GetProperty("properties");
        var values = ValuesYamlTopLevelKeys();

        values.Should().NotBeEmpty("the values file was not found or could not be read");

        var undeclared = values
            .Where(key => !schema.TryGetProperty(key, out _))
            .ToArray();

        undeclared.Should().BeEmpty(
            "a locked root rejects anything undeclared, including the chart's own defaults");
    }

    // ----------------------------------------------------------------------------------

    private static JsonDocument Schema() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ChartDirectory(), "values.schema.json")));

    /// <summary>
    /// Top-level keys only, read with a deliberately small parser rather than a YAML dependency:
    /// the question is "which lines start a top-level mapping key", and the file is the chart's
    /// own, so its shape is known.
    /// </summary>
    private static string[] ValuesYamlTopLevelKeys() =>
        [.. File.ReadAllLines(Path.Combine(ChartDirectory(), "values.yaml"))
            .Where(line => line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
            .Select(line => line.Split(':', 2)[0].Trim())
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)];

    private static string ChartDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return Path.Combine(dir!.FullName, "charts", "hephaisto");
    }
}
