using System.Net;

using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Options;
using Hephaisto.Core.Abstractions;
using Hephaisto.Tests.TestData;

using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.Observability;

/// <summary>
/// The identity provider's row on the connections panel.
/// </summary>
/// <remarks>
/// Worth testing rather than leaving to the network like the other probes, because every branch
/// here answers a different operator question and three of them are reachable without an IdP at
/// all. Authentication fails closed by design, so when Keycloak goes the console goes with it -
/// and this row is the only thing that distinguishes "the IdP is down" from "Hephaisto is down".
/// A row that got that backwards would send someone to restart the wrong service during an
/// outage.
/// </remarks>
public sealed class OidcProbeTests
{
    private static OidcProbe Probe(AuthOptions auth, HttpMessageHandler? handler = null) =>
        new(new HttpClient(handler ?? new Never()),
            new StaticOptions<AuthOptions>(auth),
            Given.Clock());

    /// <summary>
    /// Auth off is NotConfigured, not a failure - and no request is made.
    /// </summary>
    /// <remarks>
    /// This is the default, the state every e2e run is in, and the state of every install that
    /// predates v0.8.0. Reporting it as Degraded or Unreachable would paint a red row on a
    /// correctly configured deployment, which is how panels get ignored. The handler throws on
    /// any call, so this also pins that a disabled IdP is not probed over the network.
    /// </remarks>
    [Fact]
    public async Task Auth_disabled_is_NotConfigured_and_probes_nothing()
    {
        var report = await Probe(new AuthOptions { Enabled = false }).ProbeAsync(CancellationToken.None);

        report.Name.Should().Be("oidc");
        report.State.Should().Be(ConnectionState.NotConfigured);
        report.Detail.Should().Contain("Auth:Enabled is false");
    }

    [Fact]
    public async Task A_discovery_document_with_keys_is_Healthy()
    {
        var probe = Probe(
            Enabled("https://idp.example/realms/hephaisto"),
            new Canned(HttpStatusCode.OK, """{"issuer":"https://idp.example/realms/hephaisto","jwks_uri":"https://idp.example/realms/hephaisto/protocol/openid-connect/certs"}"""));

        var report = await probe.ProbeAsync(CancellationToken.None);

        report.State.Should().Be(ConnectionState.Healthy);
    }

    /// <summary>
    /// The realm path survives an authority with no trailing slash.
    /// </summary>
    /// <remarks>
    /// `new Uri(new Uri(authority), ".well-known/...")` - the obvious way to write this, and the
    /// way the sibling Grafana probe builds its URL - RESOLVES AWAY the last path segment when the
    /// base has no trailing slash. So it would fetch `https://idp.example/.well-known/...`, the
    /// host root, which on a multi-realm Keycloak is a different realm's document or a 404. Either
    /// way the row would be wrong about a working IdP. Pinned on the request that was actually
    /// made, because both spellings type-check and both look right.
    /// </remarks>
    [Fact]
    public async Task The_authoritys_realm_path_is_not_resolved_away()
    {
        var canned = new Canned(HttpStatusCode.OK, """{"jwks_uri":"x"}""");
        var probe = Probe(Enabled("https://idp.example/realms/hephaisto"), canned);

        await probe.ProbeAsync(CancellationToken.None);

        canned.LastUrl.Should().Be(
            "https://idp.example/realms/hephaisto/.well-known/openid-configuration");
    }

    [Fact]
    public async Task A_trailing_slash_does_not_double_up()
    {
        var canned = new Canned(HttpStatusCode.OK, """{"jwks_uri":"x"}""");
        var probe = Probe(Enabled("https://idp.example/realms/hephaisto/"), canned);

        await probe.ProbeAsync(CancellationToken.None);

        canned.LastUrl.Should().Be(
            "https://idp.example/realms/hephaisto/.well-known/openid-configuration");
    }

    /// <summary>
    /// A 200 that is not a discovery document is Degraded, not Healthy.
    /// </summary>
    /// <remarks>
    /// The reverse-proxy case: an ingress that answers every path with an HTML error page, or a
    /// login portal in front of the IdP. Status code alone would read Healthy while sign-in fails
    /// on a parse error - the panel green and the product unusable, which is the one combination
    /// it exists to rule out.
    /// </remarks>
    [Fact]
    public async Task A_two_hundred_that_publishes_no_keys_is_Degraded()
    {
        var probe = Probe(
            Enabled("https://idp.example/realms/hephaisto"),
            new Canned(HttpStatusCode.OK, "<html>Sign in to continue</html>"));

        var report = await probe.ProbeAsync(CancellationToken.None);

        report.State.Should().Be(ConnectionState.Degraded);
        report.Detail.Should().Contain("no jwks_uri");
    }

    [Fact]
    public async Task An_error_status_is_Unreachable_and_names_the_code()
    {
        var probe = Probe(
            Enabled("https://idp.example/realms/hephaisto"),
            new Canned(HttpStatusCode.ServiceUnavailable, "down"));

        var report = await probe.ProbeAsync(CancellationToken.None);

        report.State.Should().Be(ConnectionState.Unreachable);
        report.Detail.Should().Contain("503");
    }

    /// <summary>A refused connection is a row, never an exception out of the probe.</summary>
    [Fact]
    public async Task A_transport_failure_is_reported_rather_than_thrown()
    {
        var probe = Probe(Enabled("https://idp.example/realms/hephaisto"), new Never());

        var report = await probe.ProbeAsync(CancellationToken.None);

        report.State.Should().Be(ConnectionState.Unreachable);
    }

    private static AuthOptions Enabled(string authority) =>
        new() { Enabled = true, Authority = authority, ClientId = "hephaisto" };

    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    /// <summary>Fails every call, so "no request was made" is testable.</summary>
    private sealed class Never : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable OnChange(Action<T, string?> listener) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }
}
