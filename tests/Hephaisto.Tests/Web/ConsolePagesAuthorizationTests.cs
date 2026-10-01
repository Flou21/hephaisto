using Hephaisto.Agent.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hephaisto.Tests.Web;

/// <summary>
/// The console's pages carry the read policy. They are the only place a browser can be sent to
/// the IdP from - the API answers 401 - so a page without it means nobody ever signs in, and
/// with Auth on that reads as "no approver may close anything".
/// </summary>
public sealed class ConsolePagesAuthorizationTests
{
    [Fact]
    public async Task Every_page_and_the_circuit_require_the_read_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Enabled"] = "true",
            ["Auth:Authority"] = "http://127.0.0.1:1/realm",
            ["Auth:RequireHttpsMetadata"] = "false",
            ["Auth:ReaderRole"] = "hephaisto-reader",
        });

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddHephaistoAuth(builder.Configuration);

        await using var app = builder.Build();
        app.MapHephaistoConsolePages();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        endpoints.Select(e => e.RoutePattern.RawText).Should()
            .Contain("/incidents/{Id:guid}", "the incident page is where closing happens");

        endpoints.Should().AllSatisfy(endpoint =>
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy)
                .Should().Contain(AuthenticationExtensions.ReadPolicy, $"{endpoint.RoutePattern.RawText} is the console"));
    }
}
