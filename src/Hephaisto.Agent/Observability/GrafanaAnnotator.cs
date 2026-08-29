using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Domain;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Observability;

/// <summary>
/// Writes the incident timeline onto Grafana's dashboards as annotations.
/// </summary>
/// <remarks>
/// <para>
/// The point is correlation, not notification. An operator looking at a latency graph gets a
/// vertical line where the agent opened an incident and another where it concluded, with the
/// hypothesis in the tooltip - so the diagnosis is read against the metrics it was drawn from
/// rather than in a separate console.
/// </para>
/// <para>
/// <b>Nothing here may fail an investigation.</b> This is an observability side effect. A
/// Grafana that is down, unauthorised, or slow must cost the incident nothing but a log line -
/// the investigation is the product, the annotation is a convenience.
/// </para>
/// </remarks>
public interface IGrafanaAnnotator
{
    /// <summary>Marks the instant an incident opened.</summary>
    Task IncidentOpenedAsync(Incident incident, CancellationToken ct);

    /// <summary>
    /// Marks the instant it reached an outcome, as a region spanning the incident.
    /// </summary>
    /// <param name="incident">Read after the transition, so <c>State</c> is the outcome.</param>
    /// <param name="summary">The primary hypothesis, when there is one.</param>
    Task IncidentClosedAsync(Incident incident, string? summary, CancellationToken ct);
}

/// <summary>
/// What runs when Grafana annotation is not configured. Does nothing, deliberately silently.
/// </summary>
/// <remarks>
/// The absence is reported once at startup by <see cref="GrafanaAnnotator.Describe"/> rather
/// than per incident: a warning on every transition would train people to ignore the log on
/// exactly the installs that have chosen not to wire Grafana up.
/// </remarks>
public sealed class NullGrafanaAnnotator : IGrafanaAnnotator
{
    public Task IncidentOpenedAsync(Incident incident, CancellationToken ct) => Task.CompletedTask;

    public Task IncidentClosedAsync(Incident incident, string? summary, CancellationToken ct) =>
        Task.CompletedTask;
}

public sealed class GrafanaAnnotator(
    HttpClient http,
    IOptionsMonitor<GrafanaOptions> options,
    ILogger<GrafanaAnnotator> logger) : IGrafanaAnnotator
{
    /// <summary>
    /// Every annotation carries this, so a dashboard can select the agent's marks and only
    /// those, and so a botched run can be deleted by tag rather than by hand.
    /// </summary>
    public const string SourceTag = "hephaisto";

    public Task IncidentOpenedAsync(Incident incident, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incident);

        return PostAsync(
            new AnnotationRequest
            {
                Time = incident.OpenedAt.ToUnixTimeMilliseconds(),
                Tags = TagsFor(incident, "opened"),
                Text = $"<b>Incident opened</b> — {incident.Kind} on {Describe(incident)}",
            },
            incident,
            ct);
    }

    public Task IncidentClosedAsync(Incident incident, string? summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incident);

        // A region, not a point: TimeEnd makes Grafana draw the whole incident as a shaded
        // band, which is the shape that answers "was this happening while that spike was".
        var text = $"<b>Incident {incident.State}</b> — {incident.Kind} on {Describe(incident)}";

        if (!string.IsNullOrWhiteSpace(summary))
        {
            text += $"<br/>{summary}";
        }

        return PostAsync(
            new AnnotationRequest
            {
                Time = incident.OpenedAt.ToUnixTimeMilliseconds(),
                TimeEnd = incident.LastSignalAt > incident.OpenedAt
                    ? incident.LastSignalAt.ToUnixTimeMilliseconds()
                    : null,
                Tags = TagsFor(incident, incident.State.ToString().ToLowerInvariant()),
                Text = text,
            },
            incident,
            ct);
    }

    /// <summary>One line at startup saying whether this is on, and why not when it is off.</summary>
    public static string Describe(GrafanaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            return "Grafana annotations are OFF: Grafana:Url is not set.";
        }

        return string.IsNullOrWhiteSpace(options.AnnotationToken)
            ? "Grafana annotations are OFF: Grafana:AnnotationToken is not set."
            : $"Grafana annotations are ON, posting to {options.Url}.";
    }

    /// <summary>
    /// Tags are how a dashboard finds these, so they are the enum names verbatim and never
    /// free text. An incident id would be unique per annotation and useless as a filter.
    /// </summary>
    private static List<string> TagsFor(Incident incident, string phase) =>
    [
        SourceTag,
        phase,
        $"kind:{incident.Kind}",
        $"severity:{incident.Severity}",
        .. string.IsNullOrWhiteSpace(incident.Target?.Namespace)
            ? Array.Empty<string>()
            : [$"namespace:{incident.Target.Namespace}"],
    ];

    private static string Describe(Incident incident) =>
        incident.Target is null
            ? "an unnamed target"
            : $"{incident.Target.Namespace}/{incident.Target.Kind}/{incident.Target.Name}";

    private async Task PostAsync(AnnotationRequest annotation, Incident incident, CancellationToken ct)
    {
        var o = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(o.Url) || string.IsNullOrWhiteSpace(o.AnnotationToken))
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(o.Url.TrimEnd('/') + "/"), "api/annotations"))
            {
                Content = JsonContent.Create(annotation),
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AnnotationToken);

            // Its own timeout rather than the handler's: this call sits on the ingest path,
            // and a Grafana that accepts connections but never answers must not hold an
            // incident open behind it.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(o.AnnotationTimeout);

            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // The body carries Grafana's reason - most usefully "Permission denied" when
                // the service-account token is read-only, which is the mistake worth naming
                // because the token this reuses is read-only by convention everywhere else.
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                logger.LogWarning(
                    "Grafana rejected the annotation for incident {IncidentId}: {Status} {Body}",
                    incident.Id,
                    (int)response.StatusCode,
                    body.Length > 200 ? body[..200] : body);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The incident's own cancellation. Not ours to report.
            throw;
        }
        catch (Exception ex)
        {
            // Includes the annotation timing out on its own token, which surfaces here rather
            // than above because the linked source cancelled without ct being cancelled.
            logger.LogWarning(
                ex,
                "Could not annotate Grafana for incident {IncidentId}; the investigation is unaffected.",
                incident.Id);
        }
    }

    /// <summary>Grafana's POST /api/annotations body.</summary>
    private sealed class AnnotationRequest
    {
        /// <summary>Epoch milliseconds. Grafana rejects seconds silently by placing the mark in 1970.</summary>
        [JsonPropertyName("time")]
        public long Time { get; init; }

        [JsonPropertyName("timeEnd")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? TimeEnd { get; init; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; init; } = [];

        [JsonPropertyName("text")]
        public string Text { get; init; } = string.Empty;
    }
}
