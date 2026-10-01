using Microsoft.Extensions.Configuration;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Kubernetes;

public class KubernetesOptionsBindingTests
{
    [Fact]
    public void IgnoredKinds_binds_from_the_env_var_production_sets()
    {
        // Kubernetes__IgnoredKinds__0, as k8s/hephaisto/values.yaml sets it. A name that does
        // not bind would leave the watcher reporting what production meant to switch off.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Kubernetes:IgnoredKinds:0", "Unschedulable")])
            .Build();

        var options = new KubernetesOptions();
        configuration.GetSection(KubernetesOptions.SectionName).Bind(options);

        options.IgnoredKinds.Should().BeEquivalentTo([SignalKind.Unschedulable]);
    }

    [Fact]
    public void IgnoredKinds_is_empty_by_default()
    {
        new KubernetesOptions().IgnoredKinds.Should().BeEmpty();
    }
}
