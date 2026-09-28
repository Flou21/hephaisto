using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Agent.Options;
using Hephaisto.Core.Policy;

namespace Hephaisto.Tests;

/// <summary>
/// One key names the cluster (backlog #139).
/// </summary>
/// <remarks>
/// Three options named the cluster, the chart set none of them, and all three defaulted to the
/// development machine. Every install that was not that machine fingerprinted its signals with
/// that name and told its model to filter every query on a label that matched nothing - and none
/// of it errored.
/// </remarks>
public sealed class ClusterOptionsTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<IngestOptions>(configuration.GetSection(IngestOptions.SectionName));
        services.Configure<KubernetesOptions>(configuration.GetSection(KubernetesOptions.SectionName));
        services.Configure<EnvironmentCardOptions>(configuration.GetSection(EnvironmentCardOptions.SectionName));
        services.AddHephaistoCluster(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Cluster_name_fills_all_three_names()
    {
        using var sp = Build(("Cluster:Name", "eu-1"));

        sp.GetRequiredService<IOptions<IngestOptions>>().Value.ClusterName.Should().Be("eu-1");
        sp.GetRequiredService<IOptions<KubernetesOptions>>().Value.ClusterName.Should().Be("eu-1");
        sp.GetRequiredService<IOptions<EnvironmentCardOptions>>().Value.ClusterName.Should().Be("eu-1");
    }

    /// <summary>
    /// A legacy <c>extraEnv</c> entry left over from before the key existed must not quietly
    /// win - the operator set cluster.name and expects it to be in effect.
    /// </summary>
    [Fact]
    public void Cluster_name_beats_a_legacy_key_that_disagrees()
    {
        using var sp = Build(("Cluster:Name", "eu-1"), ("Ingest:ClusterName", "studio-rancher-desktop"));

        sp.GetRequiredService<IOptions<IngestOptions>>().Value.ClusterName.Should().Be("eu-1");
    }

    /// <summary>The refusal to start: no default can be right for a cluster it has never seen.</summary>
    [Fact]
    public void An_empty_cluster_name_is_refused()
    {
        using var sp = Build();

        var read = () => sp.GetRequiredService<IOptions<ClusterOptions>>().Value;

        read.Should().Throw<OptionsValidationException>().WithMessage("*cluster.name*");
    }

    /// <summary>
    /// And only there. Reading ingest options in a container without a name - the eval harness,
    /// most of these tests - must not throw about the cluster.
    /// </summary>
    [Fact]
    public void The_refusal_belongs_to_the_cluster_options_alone()
    {
        using var sp = Build();

        sp.GetRequiredService<IOptions<IngestOptions>>().Value.ClusterName.Should().BeEmpty();
    }

    [Fact]
    public void Registering_twice_is_registering_once()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddHephaistoCluster(configuration);
        var once = services.Count;
        services.AddHephaistoCluster(configuration);

        services.Count.Should().Be(once, "the pipeline, the Kubernetes layer and the LLM layer each call it");
    }

    /// <summary>The image carries no cluster name: that is what every other install inherited.</summary>
    [Fact]
    public void The_shipped_appsettings_name_no_cluster()
    {
        var shipped = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Hephaisto.Agent", "appsettings.json"));

        shipped.Should().NotContain("\"ClusterName\"");
        shipped.Should().NotContain("studio-rancher-desktop");
    }

    [Fact]
    public void The_code_defaults_are_empty()
    {
        new IngestOptions().ClusterName.Should().BeEmpty();
        new KubernetesOptions().ClusterName.Should().BeEmpty();
        new EnvironmentCardOptions().ClusterName.Should().BeEmpty();
        new EnvironmentCardOptions().InScopeNamespaces.Should().BeEmpty();
    }

    /// <summary>
    /// "Namespaces in scope: (none configured)" reads as an instruction not to look anywhere.
    /// </summary>
    [Fact]
    public void The_environment_card_omits_an_empty_scope()
    {
        var card = new PromptComposer(Options.Create(new EnvironmentCardOptions { ClusterName = "eu-1" }))
            .ComposeEnvironmentCard();

        card.Should().Contain("cluster=eu-1");
        card.Should().NotContain("Namespaces in scope");
    }

    /// <summary>
    /// With no list of its own, the card names what the policy engine actually enforces rather
    /// than a second, hand-kept copy.
    /// </summary>
    [Fact]
    public void The_environment_card_falls_back_to_the_enforced_protected_namespaces()
    {
        var card = new PromptComposer(
                Options.Create(new EnvironmentCardOptions { ClusterName = "eu-1" }),
                policy: Options.Create(new PolicyOptions { ProtectedNamespaces = ["team-secrets"] }))
            .ComposeEnvironmentCard();

        card.Should().Contain("Permanently out of scope: `team-secrets`");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
