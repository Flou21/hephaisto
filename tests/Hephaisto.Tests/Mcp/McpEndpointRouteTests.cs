using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Hephaisto.Agent;
using Hephaisto.Agent.Mcp;
using Hephaisto.Agent.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hephaisto.Tests.Mcp;

/// <summary>
/// The endpoint as a gateway reaches it: real Kestrel on two ports, the real token scheme, the
/// real policies, the port guard and the MCP transport. What only a running pipeline can show -
/// that a missing and a wrong token look alike, that /mcp is nowhere but its port and its port
/// has nothing else, and that a caller is who its token says.
/// </summary>
public sealed class McpEndpointRouteTests : IAsyncLifetime
{
    private const string Reader = "reader-token-reader-token-reader-token-01";
    private const string Approver = "approver-token-approver-token-approver-02";
    private const string Shared = "shared-token-shared-token-shared-token-03";
    private const string ReadOnly = "readonly-token-readonly-token-readonly-04";

    private WebApplication? app;
    private HttpClient? http;
    private int mcpPort;
    private int consolePort;

    public async ValueTask InitializeAsync()
    {
        mcpPort = FreePort();
        consolePort = FreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{mcpPort}", $"http://127.0.0.1:{consolePort}");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:Port"] = mcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Mcp:Tokens:0:Name"] = "flo",
            ["Mcp:Tokens:0:Kind"] = "person",
            ["Mcp:Tokens:0:Subject"] = "flo",
            ["Mcp:Tokens:0:Value"] = Reader,
            ["Mcp:Tokens:1:Name"] = "lead",
            ["Mcp:Tokens:1:Kind"] = "person",
            ["Mcp:Tokens:1:Subject"] = "lead",
            ["Mcp:Tokens:1:Role"] = "approver",
            ["Mcp:Tokens:1:Value"] = Approver,
            ["Mcp:Tokens:2:Name"] = "litellm",
            ["Mcp:Tokens:2:Value"] = Shared,
            ["Mcp:Tokens:3:Name"] = "dashboards",
            ["Mcp:Tokens:3:MayWrite"] = "false",
            ["Mcp:Tokens:3:Value"] = ReadOnly,
        });

        builder.Services.AddMetrics();
        builder.Services.AddSingleton<HephaistoMetrics>();
        builder.Services.AddOptions<AuthOptions>();
        builder.Services.AddAuthorization();
        builder.Services.AddHephaistoMcp(builder.Configuration);

        app = builder.Build();
        app.UseMcpPort();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/incidents", () => Results.Ok("the console"));
        app.MapHephaistoMcp();

        await app.StartAsync();
        http = new HttpClient();
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

    [Fact]
    public async Task No_token_and_a_wrong_token_are_the_same_401()
    {
        var none = await Rpc(mcpPort, null, "tools/list");
        var wrong = await Rpc(mcpPort, "not-a-token-not-a-token-not-a-token-xx", "tools/list");

        foreach (var response in new[] { none, wrong })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_right_token_lists_the_tools_in_catalogue_order()
    {
        var tools = await Tools(Reader);

        tools.Should().Equal("get_status", "get_caller_identity");
    }

    [Fact]
    public async Task The_mcp_route_answers_on_its_port_only_and_its_port_answers_nothing_else()
    {
        (await Rpc(consolePort, Reader, "tools/list")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http!.GetAsync(Url(mcpPort, "/api/incidents"), TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await http!.GetAsync(Url(consolePort, "/api/incidents"), TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK, "the control: the console still answers on its own port");
    }

    [Fact]
    public async Task A_person_token_is_its_person_and_me_is_them()
    {
        var who = await Call(Reader, "get_caller_identity");

        who["name"]!.GetValue<string>().Should().Be("flo");
        who["kind"]!.GetValue<string>().Should().Be("person");
        who["role"]!.GetValue<string>().Should().Be("reader");
        who["me"]!.GetValue<string>().Should().Be("flo");
        who["mayWrite"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_shared_token_is_itself_and_me_is_nobody()
    {
        var who = await Call(Shared, "get_caller_identity");

        who["name"]!.GetValue<string>().Should().Be("mcp/litellm");
        who["kind"]!.GetValue<string>().Should().Be("shared");
        who["me"].Should().BeNull();
    }

    [Fact]
    public async Task The_roles_and_the_write_switch_reach_the_caller()
    {
        (await Call(Approver, "get_caller_identity"))["role"]!.GetValue<string>().Should().Be("approver");
        (await Call(ReadOnly, "get_caller_identity"))["mayWrite"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task A_tool_that_does_not_exist_is_a_protocol_error()
    {
        var message = await Message(Reader, "tools/call", new JsonObject { ["name"] = "approve_action", ["arguments"] = new JsonObject() });

        message["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
    }

    private async Task<IReadOnlyList<string>> Tools(string token)
    {
        var message = await Message(token, "tools/list", new JsonObject());

        return [.. message["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];
    }

    private async Task<JsonObject> Call(string token, string tool)
    {
        var message = await Message(token, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject() });
        var text = message["result"]!["content"]![0]!["text"]!.GetValue<string>();

        return JsonNode.Parse(text)!.AsObject();
    }

    private async Task<JsonObject> Message(string token, string method, JsonObject parameters)
    {
        var response = await Rpc(mcpPort, token, method, parameters);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var data = body.Split('\n').LastOrDefault(l => l.StartsWith("data: ", StringComparison.Ordinal))?["data: ".Length..] ?? body;

        return JsonNode.Parse(data)!.AsObject();
    }

    private async Task<HttpResponseMessage> Rpc(int port, string? token, string method, JsonObject? parameters = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(port, McpOptions.Route))
        {
            Content = new StringContent(
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters ?? new JsonObject() }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await http!.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Url(int port, string path) => $"http://127.0.0.1:{port}{path}";

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
