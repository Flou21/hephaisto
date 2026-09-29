using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Investigations.Jobs;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Abstractions;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// The investigator port (v0.12.0 F5) as a Job reaches it: real Kestrel on two ports, the real
/// port guard, and the MCP SDK's own Streamable HTTP client. What it has to show: a Job calls the
/// runner's own wrapped tools and each call is a recorded step; conclude ends the run; a token is
/// for one run and until its deadline; and the route exists on its port and nowhere else.
/// </summary>
public sealed class InvestigatorEndpointTests : IAsyncLifetime
{
    private const string LogLine = "FATAL: could not connect to mongo: connection refused";

    private static readonly Regex StepHeader = new(@"^\[step ([0-9a-fA-F-]{36})\] ", RegexOptions.CultureInvariant);

    private readonly TestClock clock = new();

    private WebApplication? app;
    private HttpClient? http;
    private int investigatorPort;
    private int consolePort;

    private InvestigationRecorder recorder = null!;
    private InvestigationRunner.ConclusionHolder conclusion = null!;
    private string token = string.Empty;

    private Uri Endpoint => new($"http://127.0.0.1:{investigatorPort}{InvestigatorEndpoint.Route}");

    public async ValueTask InitializeAsync()
    {
        investigatorPort = FreePort();
        consolePort = FreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{investigatorPort}", $"http://127.0.0.1:{consolePort}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Investigation:Job:Enabled"] = "true",
            ["Investigation:Job:Port"] = investigatorPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Investigation:Job:EndpointUrl"] = $"http://127.0.0.1:{investigatorPort}/investigate",
        });
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddHephaistoInvestigationJob(builder.Configuration);

        app = builder.Build();
        app.UseInvestigatorPort();
        app.MapGet("/healthz", () => "Healthy");
        app.MapInvestigatorEndpoint();
        await app.StartAsync();

        http = new HttpClient();
        token = OpenSession();
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();

        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private string OpenSession(TimeSpan? lifetime = null)
    {
        recorder = new InvestigationRecorder(Guid.CreateVersion7(), clock, TimeSpan.FromDays(30));
        conclusion = new InvestigationRunner.ConclusionHolder();
        var budget = new InvestigationBudget(new InvestigationBudgetOptions(), clock);

        var logs = AIFunctionFactory.Create(
            (string @namespace, string name) => LogLine, "get_pod_logs", "Reads a pod's logs.");
        var query = AIFunctionFactory.Create(
            (string query, string? start) => "the series", "query_prometheus", "Runs PromQL.");

        AIFunction[] tools =
        [
            new SafeToolDecorator(logs, "kubernetes", new SafeToolOptions(), budget, recorder),
            new SafeToolDecorator(query, "grafana-mcp", new SafeToolOptions(), budget, recorder),
            new SafeToolDecorator(InvestigationRunner.CreateConcludeTool(conclusion), "internal", new SafeToolOptions(), null, recorder),
        ];

        return app!.Services.GetRequiredService<InvestigationJobSessions>().Open(new InvestigationJobSession
        {
            InvestigationId = recorder.InvestigationId,
            IncidentId = Guid.CreateVersion7(),
            Tools = tools.ToDictionary(t => t.Name),
            Conclusion = conclusion,
            ExpiresAt = clock.UtcNow + (lifetime ?? TimeSpan.FromMinutes(15)),
        });
    }

    private async Task<McpClient> ConnectAsync(string bearer) =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
        }));

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task A_Job_sees_the_runs_tools_and_each_call_is_a_recorded_step()
    {
        await using var client = await ConnectAsync(token);

        var tools = await client.ListToolsAsync();
        tools.Select(t => t.Name).Should().BeEquivalentTo(["get_pod_logs", "query_prometheus", "conclude"]);
        tools.Single(t => t.Name == "get_pod_logs").JsonSchema.GetRawText().Should().Contain("namespace");

        var result = await client.CallToolAsync("get_pod_logs", new Dictionary<string, object?>
        {
            ["namespace"] = "hephaisto-chaos",
            ["name"] = "api-1",
        });

        result.IsError.Should().NotBe(true);
        var shown = Text(result);
        var stepId = StepHeader.Match(shown).Groups[1].Value;
        stepId.Should().NotBeEmpty("the model can only cite an id it was shown");
        shown.Should().Contain(LogLine);

        var step = recorder.Steps.Should().ContainSingle(s => s.ToolName == "get_pod_logs").Subject;
        step.Id.ToString().Should().Be(stepId);
        step.ResultDigest.Should().Be(shown, "grounding checks against exactly the bytes the Job read");
        step.ToolServer.Should().Be("kubernetes");
    }

    [Fact]
    public async Task Conclude_ends_the_run_and_nothing_is_recorded_after_it()
    {
        await using var client = await ConnectAsync(token);

        var logs = Text(await client.CallToolAsync("get_pod_logs", new Dictionary<string, object?>
        {
            ["namespace"] = "ns",
            ["name"] = "api-1",
        }));

        var concluded = await client.CallToolAsync("conclude", new Dictionary<string, object?>
        {
            ["summary"] = "mongo is unreachable",
            ["confidence"] = 0.8,
            ["findings"] = JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    category = "dependency",
                    hypothesis = "api cannot reach mongo",
                    confidence = 0.8,
                    primary = true,
                    evidence = new[] { new { step_id = StepHeader.Match(logs).Groups[1].Value, excerpt = LogLine } },
                },
            }),
        });

        concluded.IsError.Should().NotBe(true);
        conclusion.Value.Should().NotBeNull();
        conclusion.Value!.Findings.Should().ContainSingle().Which.Evidence.Should().ContainSingle();

        var steps = recorder.Steps.Count;
        var after = await client.CallToolAsync("get_pod_logs", new Dictionary<string, object?> { ["namespace"] = "ns", ["name"] = "x" });

        after.IsError.Should().BeTrue();
        Text(after).Should().Contain("has concluded");
        recorder.Steps.Count.Should().Be(steps);
    }

    [Fact]
    public async Task A_string_argument_reaches_the_decorator_as_a_string_so_its_refusals_hold()
    {
        await using var client = await ConnectAsync(token);

        var refused = Text(await client.CallToolAsync("query_prometheus", new Dictionary<string, object?>
        {
            ["query"] = "rate(http_requests_total[30d])",
            ["start"] = "now-1h",
        }));

        refused.Should().StartWith("REFUSED", "a Job is held to the same query limits as the in-process loop");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token-not-a-token-not-a-token-not-a-token")]
    public async Task No_token_and_a_wrong_token_are_the_same_401(string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };

        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await http!.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("a refusal says nothing about which runs exist");
    }

    [Fact]
    public async Task A_token_dies_with_its_deadline()
    {
        clock.Advance(TimeSpan.FromMinutes(16));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http!.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_closed_run_refuses_its_token()
    {
        app!.Services.GetRequiredService<InvestigationJobSessions>().Close(recorder.InvestigationId);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await http!.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_route_answers_on_its_port_and_nowhere_else()
    {
        (await http!.PostAsync($"http://127.0.0.1:{consolePort}/investigate", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "/investigate is not on the console port");

        (await http.GetAsync($"http://127.0.0.1:{investigatorPort}/healthz")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the investigator port serves nothing but /investigate");

        (await http.GetAsync($"http://127.0.0.1:{consolePort}/healthz")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task There_is_no_stream_to_open()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await http!.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task A_notification_is_accepted_with_nothing_to_say()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await http!.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task An_unknown_method_is_a_json_rpc_error()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":7,"method":"resources/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http!.SendAsync(request);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        body["id"]!.GetValue<int>().Should().Be(7);
        body["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
