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

    /// <summary>
    /// The parameters a tool describes as optional must not be required by its schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A nullable C# parameter with no default value is still emitted as <c>required</c>. The
    /// method handles null perfectly and the description invites the model to omit it, so
    /// nothing looks wrong - until the model does omit it and the call dies on "The arguments
    /// dictionary is missing a value for the required parameter", burning a tool call and a
    /// turn on a question the tool was designed to answer.
    /// </para>
    /// <para>
    /// Observed in production on 2026-08-28: <c>get_events</c> without an objectName (all
    /// events in a namespace) and <c>list_pods</c> without a labelSelector (all pods in a
    /// namespace) - the two most natural opening moves in any investigation.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("list_pods", "labelSelector")]
    [InlineData("get_events", "objectName")]
    [InlineData("get_pod_logs", "container")]
    [InlineData("get_pod_logs", "previous")]
    [InlineData("get_resource_usage", "namespace")]
    public void Optional_parameters_are_not_required_by_the_schema(string tool, string parameter)
    {
        var function = Tools().CreateFunctions().Single(f => f.Name == tool);

        var required = function.JsonSchema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()).ToArray()
            : [];

        required.Should().NotContain(
            parameter,
            $"{tool}.{parameter} is optional; requiring it makes the model's natural call fail");
    }

    [Fact]
    public void The_parameters_a_tool_cannot_work_without_stay_required()
    {
        // The other half of the same property: defaults must not be sprayed onto everything.
        // A get_pod with no name is a bug the schema should catch, not a call that reaches the
        // API server and 404s.
        var functions = Tools().CreateFunctions();

        foreach (var (tool, parameter) in new[]
        {
            ("list_pods", "namespace"),
            ("get_pod", "name"),
            ("get_events", "namespace"),
            ("get_pod_logs", "name"),
        })
        {
            var function = functions.Single(f => f.Name == tool);

            var required = function.JsonSchema.TryGetProperty("required", out var req)
                ? req.EnumerateArray().Select(e => e.GetString()).ToArray()
                : [];

            required.Should().Contain(parameter, $"{tool} cannot do anything without {parameter}");
        }
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
