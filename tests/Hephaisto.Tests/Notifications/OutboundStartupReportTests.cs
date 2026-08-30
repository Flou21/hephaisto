using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Observability;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The startup report actually says something.
/// </summary>
/// <remarks>
/// <para>
/// backlog #43 was that <c>GrafanaAnnotator.Describe</c> is documented as a startup line and had
/// no caller. Adding the caller closed it; nothing then asserted the caller <i>works</i>, which
/// is the same gap one level up. The first cluster run of v0.3.0 failed exactly here - the e2e
/// grepped the pod log for <c>Outbound webhook channel is ON</c> and did not find it - and no
/// test in the repo could say whether the product or the harness was wrong.
/// </para>
/// <para>
/// So this drives the real <see cref="OutboundStartupReport"/> over real channel instances and
/// asserts the strings. If it passes, a missing line in a pod log is the harness's problem.
/// </para>
/// </remarks>
public sealed class OutboundStartupReportTests
{
    [Fact]
    public async Task It_names_every_configured_channel_and_the_route_count()
    {
        var (report, log) = Build(Configured());

        await report.StartAsync(TestContext.Current.CancellationToken);

        log.Lines.Should().Contain(l => l.Contains("Outbound webhook channel is ON", StringComparison.Ordinal));
        log.Lines.Should().Contain(l => l.Contains("Notifications are ON", StringComparison.Ordinal));
        log.Lines.Should().Contain(l => l.Contains("1 route(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_says_so_when_nothing_is_configured()
    {
        // The shipped default. Reported at Information rather than Warning, because warning on
        // every start would train people to ignore the line on exactly the installs that chose
        // to notify nowhere.
        var (report, log) = Build(new NotificationOptions(), withChannel: false);

        await report.StartAsync(TestContext.Current.CancellationToken);

        log.Lines.Should().Contain(l => l.Contains("Notifications are OFF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_reports_the_grafana_annotator_and_the_silencer_too()
    {
        // The two other outbound integrations that degrade silently. Naming them here is what
        // makes "nothing happened" distinguishable from "never switched on".
        var (report, log) = Build(Configured());

        await report.StartAsync(TestContext.Current.CancellationToken);

        log.Lines.Should().Contain(l => l.Contains("Grafana annotations are", StringComparison.Ordinal));
        log.Lines.Should().Contain(l => l.Contains("Alert silencing is", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_route_naming_an_unregistered_channel_is_reported_as_an_error()
    {
        // Startup validation refuses this, so reaching it means a hot-reload got there - and the
        // routing table then looks correct while delivering nothing.
        var options = Configured();
        options.Routes[0].Channel = "teams";

        var (report, log) = Build(options);

        await report.StartAsync(TestContext.Current.CancellationToken);

        log.Lines.Should().Contain(l => l.Contains("not registered", StringComparison.Ordinal));
    }

    private static NotificationOptions Configured() => new()
    {
        BaseUrl = "https://hephaisto.example",
        Webhook = new HttpChannelOptions { Url = "http://receiver.example/hook" },
        Routes =
        [
            new NotificationRoute
            {
                Channel = NotificationChannelNames.Webhook,
                Events = [NotificationEvent.IncidentEscalated],
            },
        ],
    };

    private static (OutboundStartupReport Report, RecordingLogger Log) Build(
        NotificationOptions options,
        bool withChannel = true)
    {
        var services = new ServiceCollection();

        if (withChannel)
        {
            services.AddSingleton<INotificationChannel>(_ => new HttpNotificationChannel(
                new HttpClient(new NoopHandler()),
                new StaticOptions<NotificationOptions>(options),
                NullLogger<HttpNotificationChannel>.Instance));
        }

        var provider = services.BuildServiceProvider();
        var log = new RecordingLogger();

        var report = new OutboundStartupReport(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptions<NotificationOptions>(options),
            new StaticOptions<GrafanaOptions>(new GrafanaOptions { Url = "http://grafana", AnnotationToken = "t" }),
            new NullAlertSilencer(),
            log);

        return (report, log);
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class RecordingLogger : ILogger<OutboundStartupReport>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
