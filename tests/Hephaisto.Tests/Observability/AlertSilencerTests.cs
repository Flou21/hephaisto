using System.Net;
using System.Text.Json;
using Hephaisto.Agent.Observability;
using Hephaisto.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.Observability;

/// <summary>
/// Silencing an alert is how you hide a problem, which makes this the one action whose failure
/// mode is that everything looks fine.
/// </summary>
/// <remarks>
/// Every test here is a refusal or a clamp. There is exactly one happy path and it is the least
/// interesting thing in the file: Alertmanager will cheerfully accept a silence with no matchers
/// - which matches every alert in the cluster - and nothing about the response would tell you
/// that is what you just did.
/// </remarks>
public sealed class AlertSilencerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_silence_with_no_alertname_is_refused_before_any_call()
    {
        // The single most important assertion in the file. No matchers means every alert in
        // the cluster, and Alertmanager accepts it without complaint.
        var (silencer, handler) = Build();

        var result = await silencer.SilenceAsync(
            Request() with { AlertName = "" },
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("every alert");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_silence_with_no_namespace_is_refused_before_any_call()
    {
        var (silencer, handler) = Build();

        var result = await silencer.SilenceAsync(
            Request() with { Namespace = "" },
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("cluster-wide");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_over_long_duration_is_clamped_rather_than_refused()
    {
        // The model asking for eight hours is not an error worth failing an approved action
        // over. It is a number worth ignoring.
        var (silencer, handler) = Build();

        await silencer.SilenceAsync(
            Request() with { Duration = TimeSpan.FromHours(8) },
            TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.Body!).RootElement;

        var start = body.GetProperty("startsAt").GetDateTimeOffset();
        var end = body.GetProperty("endsAt").GetDateTimeOffset();

        (end - start).Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task A_missing_duration_falls_back_to_the_default_rather_than_forever()
    {
        var (silencer, handler) = Build();

        await silencer.SilenceAsync(
            Request() with { Duration = TimeSpan.Zero },
            TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.Body!).RootElement;

        (body.GetProperty("endsAt").GetDateTimeOffset() - body.GetProperty("startsAt").GetDateTimeOffset())
            .Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task The_silence_is_scoped_to_the_alert_and_the_namespace()
    {
        var (silencer, handler) = Build();

        await silencer.SilenceAsync(Request(), TestContext.Current.CancellationToken);

        var matchers = JsonDocument.Parse(handler.Body!).RootElement.GetProperty("matchers");

        matchers.GetArrayLength().Should().Be(2);
        matchers[0].GetProperty("name").GetString().Should().Be("alertname");
        matchers[0].GetProperty("value").GetString().Should().Be("KubePodCrashLooping");
        matchers[0].GetProperty("isRegex").GetBoolean().Should().BeFalse();
        matchers[1].GetProperty("name").GetString().Should().Be("namespace");
    }

    [Fact]
    public async Task It_returns_the_silence_id_so_the_action_can_be_undone()
    {
        // Unlike a pod delete this has a real inverse, which is why it takes a genuine rollback
        // spec rather than the gate-14 self-healing exemption.
        var (silencer, _) = Build(response: """{"silenceID":"abc-123"}""");

        var result = await silencer.SilenceAsync(Request(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.SilenceId.Should().Be("abc-123");
    }

    [Fact]
    public async Task A_rejected_silence_is_reported_rather_than_swallowed()
    {
        // Distinct from every other outbound integration here, which swallow failures because
        // they are side effects. This one IS the action.
        var (silencer, _) = Build(status: HttpStatusCode.BadRequest, response: "bad matcher");

        var result = await silencer.SilenceAsync(Request(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("400");
    }

    [Fact]
    public async Task Expiring_without_an_id_is_refused()
    {
        var (silencer, handler) = Build();

        (await silencer.ExpireAsync("", TestContext.Current.CancellationToken))
            .Succeeded.Should().BeFalse();

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Expiring_deletes_the_silence_by_id()
    {
        var (silencer, handler) = Build();

        await silencer.ExpireAsync("abc-123", TestContext.Current.CancellationToken);

        handler.Requests[0].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[0].RequestUri!.ToString().Should().EndWith("/api/v2/silence/abc-123");
    }

    [Fact]
    public void An_unconfigured_silencer_says_so_and_refuses()
    {
        var silencer = new NullAlertSilencer();

        silencer.IsConfigured.Should().BeFalse();
        silencer.Describe().Should().Contain("OFF").And.Contain("Alertmanager:Url");
    }

    [Fact]
    public void Describe_names_the_ceiling_when_it_is_on()
    {
        var (silencer, _) = Build();

        silencer.Describe().Should().Contain("ON").And.Contain("02:00:00");
    }

    private static SilenceRequest Request() => new(
        "KubePodCrashLooping",
        "hephaisto-chaos",
        TimeSpan.FromMinutes(30),
        "someone@example.com",
        "known noisy during the deploy");

    private static (AlertSilencer Silencer, RecordingHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.OK,
        string response = """{"silenceID":"id"}""")
    {
        var handler = new RecordingHandler(status, response);

        var silencer = new AlertSilencer(
            new HttpClient(handler),
            new StaticOptions(new AlertmanagerOptions
            {
                Url = "http://alertmanager:9093",
                MaxDuration = TimeSpan.FromHours(2),
                DefaultDuration = TimeSpan.FromMinutes(30),
            }),
            new FixedClock(Now),
            NullLogger<AlertSilencer>.Instance);

        return (silencer, handler);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StaticOptions(AlertmanagerOptions value) : IOptionsMonitor<AlertmanagerOptions>
    {
        public AlertmanagerOptions CurrentValue => value;

        public AlertmanagerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AlertmanagerOptions, string?> listener) => null;
    }
}
