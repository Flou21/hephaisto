using Hephaisto.Agent.Pipeline;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests;

/// <summary>
/// Asserts that <see cref="PolicyOptions"/> is actually bound to configuration.
/// </summary>
/// <remarks>
/// <para>
/// This test exists because the failure it catches shipped. Every other options class in the
/// repo is bound with <c>services.Configure&lt;T&gt;(configuration.GetSection(T.SectionName))</c>;
/// <see cref="PolicyOptions"/> was not, and nothing said so.
/// <c>IOptionsMonitor&lt;PolicyOptions&gt;</c> resolves perfectly happily to a
/// default-constructed instance, so the policy engine ran against an empty
/// <see cref="PolicyOptions.AllowedNamespaces"/> and denied every action at gate 2 - which is
/// the correct-looking answer for the wrong reason. The chart had been setting
/// <c>Policy__AllowedNamespaces__N</c> the whole time and no code read it.
/// </para>
/// <para>
/// A smoke test would not have caught it either, because "denies everything" is exactly what a
/// correctly-configured agent does before anyone opts a namespace in.
/// </para>
/// </remarks>
public sealed class PolicyOptionsBindingTests
{
    private static PolicyOptions Bind(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddHephaistoPipeline(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<PolicyOptions>>().CurrentValue;
    }

    [Fact]
    public void AllowedNamespaces_binds_from_the_indexed_keys_the_chart_emits()
    {
        // Exactly the shape of deployment.yaml's Policy__AllowedNamespaces__{{ $i }}.
        var options = Bind(
            ("Policy:AllowedNamespaces:0", "hephaisto-chaos"),
            ("Policy:AllowedNamespaces:1", "team-sandbox"));

        options.AllowedNamespaces.Should().BeEquivalentTo(["hephaisto-chaos", "team-sandbox"]);
    }

    [Fact]
    public void AutoEnabledActionTypes_binds_a_HashSet_of_enum_members_from_their_names()
    {
        // The autonomy gate. A set of enums bound from strings is the binding most likely to
        // fail quietly - a typo yields an empty set, which reads as "not auto-enabled".
        var options = Bind(("Policy:AutoEnabledActionTypes:0", "RestartPod"));

        options.AutoEnabledActionTypes.Should().BeEquivalentTo([ActionType.RestartPod]);
    }

    [Fact]
    public void ProtectedLabels_binds_a_dictionary()
    {
        var options = Bind(("Policy:ProtectedLabels:example.io/frozen", "yes"));

        // The configured entry replaces the default rather than merging alongside it, because
        // a dictionary bound from configuration is populated key by key onto the instance.
        options.ProtectedLabels.Should().ContainKey("example.io/frozen")
            .WhoseValue.Should().Be("yes");
    }

    [Fact]
    public void Scalar_and_TimeSpan_knobs_bind_too()
    {
        var options = Bind(
            ("Policy:MaxActionsPerHour", "3"),
            ("Policy:WorkloadCooldown", "00:45:00"));

        options.MaxActionsPerHour.Should().Be(3);
        options.WorkloadCooldown.Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void An_unconfigured_agent_may_act_nowhere()
    {
        Bind().AllowedNamespaces.Should().BeEmpty(
            "an empty allowlist is the only safe default for a process that can delete pods");
    }

    [Fact]
    public void The_shipped_appsettings_names_no_actionable_namespace()
    {
        // appsettings.json goes into the published image. A namespace named there would be a
        // second source of truth that no helm value can take away: the chart's
        // policy.actionableNamespaces defaults to [] and renders no write Role, but the policy
        // engine would still return Allow instead of Deny. RBAC would block the call, so this
        // is defence in depth rather than the hard floor - which is exactly why it is worth a
        // test rather than a comment. The dev namespace lives in appsettings.Development.json.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(ShippedAppSettings(), optional: false)
            .Build();

        configuration.GetSection("Policy:AllowedNamespaces").GetChildren().Should().BeEmpty();
    }

    private static string ShippedAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, "src", "Hephaisto.Agent", "appsettings.json");
        File.Exists(path).Should().BeTrue("the guard is worthless if it cannot find the file");

        return path;
    }
}
