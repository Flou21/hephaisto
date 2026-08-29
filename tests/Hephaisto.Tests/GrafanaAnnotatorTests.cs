using System.Net;
using System.Text.Json;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Observability;
using Hephaisto.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests;

/// <summary>
/// Grafana annotations: the shape Grafana needs, and the failures that must cost nothing.
/// </summary>
/// <remarks>
/// Deferred since the MVP, and <c>docs/verification.md</c>'s acceptance test has required them
/// the whole time - "annotate Grafana" is a clause in the MVP test that nothing implemented.
/// The risk in adding them late is the opposite of the risk in leaving them out: an
/// observability side effect that can fail an investigation is worse than no annotation at all.
/// </remarks>
public class GrafanaAnnotatorTests
{
    private static Incident NewIncident() => new()
    {
        Kind = SignalKind.ImagePullBackOff,
        Severity = Severity.Warning,
        Title = "broken cannot pull its image",
        OpenedAt = DateTimeOffset.UnixEpoch,
        LastSignalAt = DateTimeOffset.UnixEpoch.AddMinutes(5),
        Target = new TargetRef { Namespace = "hephaisto-chaos", Kind = "Pod", Name = "broken" },
    };

    private static (GrafanaAnnotator Annotator, RecordingHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.OK,
        Exception? throws = null)
    {
        var handler = new RecordingHandler(status, throws);

        var options = new GrafanaOptions
        {
            Url = "http://grafana.hephaisto-obs",
            AnnotationToken = "glsa_testtoken",
            AnnotationTimeout = TimeSpan.FromSeconds(5),
        };

        var annotator = new GrafanaAnnotator(
            new HttpClient(handler),
            new StaticOptions<GrafanaOptions>(options),
            NullLogger<GrafanaAnnotator>.Instance);

        return (annotator, handler);
    }

    [Fact]
    public async Task An_opened_incident_posts_a_point_annotation_in_epoch_milliseconds()
    {
        var (annotator, handler) = Build();

        await annotator.IncidentOpenedAsync(NewIncident(), TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle();
        handler.LastUri.Should().Be("http://grafana.hephaisto-obs/api/annotations");

        var body = handler.LastJson();

        // Milliseconds, not seconds. Grafana accepts a seconds value without complaint and
        // draws the mark in January 1970, which looks like the annotation simply not appearing.
        body.GetProperty("time").GetInt64().Should().Be(0);
        body.TryGetProperty("timeEnd", out _).Should().BeFalse("an opening is an instant, not a region");
    }

    [Fact]
    public async Task A_closed_incident_posts_a_region_so_it_can_be_read_against_a_graph()
    {
        var (annotator, handler) = Build();
        var incident = NewIncident();
        incident.State = IncidentState.Escalated;

        await annotator.IncidentClosedAsync(
            incident, "The tag this-tag-does-not-exist is not published.", TestContext.Current.CancellationToken);

        var body = handler.LastJson();

        body.GetProperty("time").GetInt64().Should().Be(0);
        body.GetProperty("timeEnd").GetInt64().Should().Be(300_000, "five minutes after it opened");
        body.GetProperty("text").GetString().Should().Contain("this-tag-does-not-exist");
    }

    [Fact]
    public async Task Every_annotation_is_tagged_so_a_dashboard_can_select_the_agents_marks()
    {
        var (annotator, handler) = Build();

        await annotator.IncidentOpenedAsync(NewIncident(), TestContext.Current.CancellationToken);

        var tags = handler.LastJson().GetProperty("tags")
            .EnumerateArray().Select(t => t.GetString()).ToList();

        tags.Should().Contain(GrafanaAnnotator.SourceTag)
            .And.Contain("opened")
            .And.Contain("kind:ImagePullBackOff")
            .And.Contain("severity:Warning")
            .And.Contain("namespace:hephaisto-chaos");

        // No incident id. It would be unique per annotation, so it can never be selected on -
        // it would just be unbounded tag cardinality in Grafana's annotation store.
        tags.Should().NotContain(t => t!.StartsWith("id:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_token_is_sent_as_a_bearer()
    {
        var (annotator, handler) = Build();

        await annotator.IncidentOpenedAsync(NewIncident(), TestContext.Current.CancellationToken);

        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("glsa_testtoken");
    }

    /// <summary>
    /// A refusal from Grafana is logged, not thrown.
    /// </summary>
    /// <remarks>
    /// 403 is the expected failure rather than an exotic one: every other Grafana credential in
    /// this system is read-only by convention, so pointing this at the wrong token is the
    /// obvious mistake - and it must not take incidents down with it.
    /// </remarks>
    [Fact]
    public async Task A_permission_denied_does_not_propagate()
    {
        var (annotator, _) = Build(HttpStatusCode.Forbidden);

        await annotator.Invoking(a => a.IncidentOpenedAsync(NewIncident(), TestContext.Current.CancellationToken))
            .Should().NotThrowAsync();
    }

    /// <summary>An unreachable Grafana costs the investigation nothing.</summary>
    [Fact]
    public async Task A_transport_failure_does_not_propagate()
    {
        var (annotator, _) = Build(throws: new HttpRequestException("connection refused"));

        await annotator.Invoking(a => a.IncidentOpenedAsync(NewIncident(), TestContext.Current.CancellationToken))
            .Should().NotThrowAsync();
    }

    /// <summary>
    /// The incident's own cancellation still propagates.
    /// </summary>
    /// <remarks>
    /// The one exception to "swallow everything". Shutdown has to be able to stop the pipeline;
    /// a catch broad enough to eat OperationCanceledException would make the agent take its
    /// termination grace period to notice it was asked to stop.
    /// </remarks>
    [Fact]
    public async Task Cancellation_of_the_incident_is_not_swallowed()
    {
        var (annotator, _) = Build(throws: new HttpRequestException("would not get this far"));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await annotator.Invoking(a => a.IncidentOpenedAsync(NewIncident(), cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Unconfigured means no call at all, not a call to a null host.</summary>
    [Fact]
    public async Task Without_a_url_or_token_nothing_is_posted()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, null);

        var annotator = new GrafanaAnnotator(
            new HttpClient(handler),
            new StaticOptions<GrafanaOptions>(new GrafanaOptions()),
            NullLogger<GrafanaAnnotator>.Instance);

        await annotator.IncidentOpenedAsync(NewIncident(), TestContext.Current.CancellationToken);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void The_startup_line_names_what_is_missing()
    {
        GrafanaAnnotator.Describe(new GrafanaOptions()).Should().Contain("Grafana:Url");

        GrafanaAnnotator.Describe(new GrafanaOptions { Url = "http://grafana" })
            .Should().Contain("Grafana:AnnotationToken");

        GrafanaAnnotator.Describe(new GrafanaOptions { Url = "http://grafana", AnnotationToken = "t" })
            .Should().Contain("ON");
    }

    private sealed class RecordingHandler(HttpStatusCode status, Exception? throws) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public string? LastUri => Requests.LastOrDefault()?.RequestUri?.ToString();

        private string lastBody = string.Empty;

        public JsonElement LastJson() => JsonDocument.Parse(lastBody).RootElement;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (throws is not null)
            {
                throw throws;
            }

            Requests.Add(request);
            lastBody = await request.Content!.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status) { Content = new StringContent("{}") };
        }
    }

    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
