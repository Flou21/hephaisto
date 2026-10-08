using System.Net;
using System.Net.Sockets;
using System.Text;
using Hephaisto.Agent.GitHub;

namespace Hephaisto.Tests.GitHub;

/// <summary>
/// <c>GitHub:ProxyUrl</c> is where the request actually goes.
/// </summary>
/// <remarks>
/// With the agent's egress policy on, the proxy is the only way out to GitHub - so a proxy
/// setting that is bound and then not used produces an agent that starts, reports GitHub as
/// unreachable and takes nothing. A real socket rather than an inspected property, because the
/// property being set is not the claim.
/// </remarks>
public sealed class GitHubProxyTests
{
    [Fact]
    public async Task With_a_proxy_url_the_request_is_sent_to_the_proxy()
    {
        var ct = TestContext.Current.CancellationToken;

        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var port = ((IPEndPoint)proxy.LocalEndpoint).Port;

        var seen = Task.Run(async () =>
        {
            using var connection = await proxy.AcceptTcpClientAsync(ct);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(ct) ?? string.Empty;

            while (await reader.ReadLineAsync(ct) is { Length: > 0 })
            {
            }

            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray(), ct);

            return requestLine;
        }, ct);

        using var handler = GitHubServiceCollectionExtensions.PrimaryHandler(new GitHubOptions { ProxyUrl = $"http://127.0.0.1:{port}" });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        // A name that resolves nowhere: only a proxy could have answered for it.
        using var response = await client.GetAsync(new Uri("http://api.github.invalid/user"), ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await seen).Should().Be("GET http://api.github.invalid/user HTTP/1.1");
    }

    [Fact]
    public void Without_one_the_connection_is_whatever_the_process_has()
    {
        using var handler = GitHubServiceCollectionExtensions.PrimaryHandler(new GitHubOptions());

        handler.Proxy.Should().BeNull();
    }
}
