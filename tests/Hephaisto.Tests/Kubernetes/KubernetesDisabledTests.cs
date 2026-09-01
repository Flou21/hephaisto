using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Agent.Pipeline;

namespace Hephaisto.Tests.Kubernetes;

/// <summary>
/// Pins the shape of a process running with no cluster.
/// </summary>
/// <remarks>
/// <para>
/// The published image could not start without a cluster at all: the Kubernetes layer
/// registered unconditionally, <c>RbacSelfCheck</c> fired forty-odd access reviews at boot, and
/// building a client outside a pod fell back to a kubeconfig that was not there. A stranger
/// could not look at the console before committing to a full install, which is most of why
/// nobody had.
/// </para>
/// <para>
/// The risk in fixing that is the interesting part, and it is what these assert: a host with no
/// cluster must be <b>less</b> capable, never more. The Kubernetes layer is what replaces the
/// pipeline's refusing executor, so skipping it has to leave the refusal in place.
/// </para>
/// </remarks>
public class KubernetesDisabledTests
{
    private static ServiceCollection Build(bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>(
                    "Kubernetes:Enabled", enabled ? "true" : "false"),
            ])
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        // Exactly what AddHephaistoPipeline does, in the order the composition root does it:
        // the pipeline TryAdds the refusing executor, and the Kubernetes layer is what would
        // replace it. Reproduced rather than calling the pipeline so this test turns on the
        // one registration it is about.
        services.TryAddScoped<IActionExecutor, RefusingActionExecutor>();
        services.AddHephaistoKubernetes(configuration);

        return services;
    }

    [Fact]
    public void A_host_with_no_cluster_keeps_the_executor_that_refuses()
    {
        var services = Build(enabled: false);

        var executor = services.Last(d => d.ServiceType == typeof(IActionExecutor));

        Assert.Equal(typeof(RefusingActionExecutor), executor.ImplementationType);
    }

    [Fact]
    public void The_real_executor_is_registered_when_a_cluster_is_expected()
    {
        // The other direction, so the test above cannot pass by the registration having been
        // dropped for everyone.
        var services = Build(enabled: true);

        var executor = services.Last(d => d.ServiceType == typeof(IActionExecutor));

        Assert.Equal(typeof(ActionExecutor), executor.ImplementationType);
    }

    [Fact]
    public void Neither_boot_time_hosted_service_is_registered_without_a_cluster()
    {
        // These two are what made a cluster mandatory: one runs access reviews before
        // anything else happens, the other opens watches.
        var hosted = Build(enabled: false)
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        Assert.DoesNotContain(typeof(RbacSelfCheck), hosted);
        Assert.DoesNotContain(typeof(KubernetesWatcherService), hosted);
    }

    [Fact]
    public void Running_without_a_cluster_announces_itself()
    {
        // An agent that detects nothing is otherwise indistinguishable from a healthy one, so
        // the disabled path must not be silent.
        var hosted = Build(enabled: false)
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType?.Name)
            .ToList();

        Assert.Contains("KubernetesDisabledAnnouncer", hosted);
    }

    [Fact]
    public void Reaching_for_the_cluster_says_which_setting_turned_it_off()
    {
        // Not a kubeconfig FileNotFound from four frames down.
        using var sp = Build(enabled: false).BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(sp.GetRequiredService<IKubernetes>);

        Assert.Contains("Kubernetes:Enabled", ex.Message, StringComparison.Ordinal);
    }
}
