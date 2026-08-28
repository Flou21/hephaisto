using k8s;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Hephaisto.Agent.Kubernetes;

namespace Hephaisto.Tests.Kubernetes;

/// <summary>
/// Construction-time checks only - nothing here opens a connection.
/// </summary>
/// <remarks>
/// <see cref="AIFunctionFactory"/> builds each tool's JSON schema by reflecting over the
/// delegate, and a parameter it cannot describe fails at <i>that</i> moment rather than at
/// compile time. Without this test the first symptom of a bad signature is an investigation
/// crashing at the point the tool set is assembled, during an incident.
/// </remarks>
public class KubernetesReadToolsTests
{
    private static KubernetesReadTools Tools()
    {
        // Points at a port nothing listens on. The client is constructed but never used: every
        // assertion below is about the tool metadata, not about a cluster.
        var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:1" });
        var api = new KubernetesApi(client);

        return new KubernetesReadTools(
            api,
            new OwnerCache(api, TimeProvider.System, NullLogger<OwnerCache>.Instance),
            Options.Create(new KubernetesOptions()),
            TimeProvider.System,
            NullLogger<KubernetesReadTools>.Instance);
    }

    [Fact]
    public void Every_tool_builds_a_schema_and_carries_a_description()
    {
        var functions = Tools().CreateFunctions();

        functions.Should().HaveCount(17);
        functions.Should().AllSatisfy(f =>
        {
            f.Name.Should().NotBeNullOrWhiteSpace();

            // The description is the only documentation the model gets. A short one is a tool
            // it will call at the wrong moment and then reason from.
            f.Description.Should().NotBeNullOrWhiteSpace();
            f.Description.Length.Should().BeGreaterThan(60);
        });
    }

    [Fact]
    public void Tool_names_are_the_snake_case_ones_the_prompts_and_runbooks_refer_to()
    {
        var names = Tools().CreateFunctions().Select(f => f.Name).ToArray();

        names.Should().BeEquivalentTo(
        [
            "list_pods", "get_pod", "describe_pod", "get_events", "get_pod_logs",
            "list_deployments", "list_statefulsets", "list_daemonsets", "get_workload",
            "get_rollout_history", "list_nodes", "get_node", "get_resource_usage",
            "list_hpa", "list_pvcs", "get_service_endpoints", "who_owns",
        ]);
    }

    /// <summary>
    /// The runbooks tell the model to use <c>previous: true</c> after a restart, so the tool's
    /// own description has to say the same thing - a model that only reads tool descriptions
    /// must still reach the right call.
    /// </summary>
    [Fact]
    public void The_log_tool_tells_the_model_about_previous()
    {
        var logs = Tools().CreateFunctions().Single(f => f.Name == "get_pod_logs");

        logs.Description.Should().Contain("previous=true");
        logs.JsonSchema.ToString().Should().Contain("previous");
    }
}
