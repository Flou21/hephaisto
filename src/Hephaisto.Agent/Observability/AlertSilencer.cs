using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hephaisto.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Observability;

/// <summary>Alertmanager, for silences. The only thing the agent writes there.</summary>
public sealed class AlertmanagerOptions
{
    public const string SectionName = "Alertmanager";

    /// <summary>
    /// Base URL. Empty means <c>SilenceAlert</c> is refused by the executor before any call is
    /// made, which is the honest answer rather than a 404 that reads like a misconfiguration.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Hard ceiling on how long a silence may last, whatever the model asks for.
    /// </summary>
    /// <remarks>
    /// The dangerous failure of this action is not a wrong silence, it is a <b>long</b> one: a
    /// silence nobody remembers is a monitoring gap that looks like a quiet system. Two hours
    /// is long enough to cover a deploy and short enough that it expires before anybody has
    /// forgotten it exists.
    /// </remarks>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(2);

    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <param name="AlertName">The <c>alertname</c> label to match. Required - see the remarks on
/// <see cref="IAlertSilencer"/> for why a silence with no alertname is refused.</param>
/// <param name="Namespace">Scopes the silence to one namespace. Also required.</param>
/// <param name="Duration">Clamped to <see cref="AlertmanagerOptions.MaxDuration"/>.</param>
/// <param name="CreatedBy">The acting identity, so a silence in the UI names who made it.</param>
public sealed record SilenceRequest(
    string AlertName,
    string Namespace,
    TimeSpan Duration,
    string CreatedBy,
    string Comment);

/// <param name="SilenceId">Alertmanager's id, which is what the rollback expires.</param>
public sealed record SilenceResult(bool Succeeded, string? SilenceId, string? Error);

/// <summary>
/// Creates and expires Alertmanager silences.
/// </summary>
/// <remarks>
/// <para>
/// <b>Silencing an alert is how you hide a problem</b>, which makes this the one action whose
/// failure mode is that everything looks fine. Every guard below exists because of that:
/// </para>
/// <list type="bullet">
/// <item>An empty <c>alertname</c> or namespace is refused. A silence with no matchers matches
/// EVERY alert in the cluster, and Alertmanager will happily accept one.</item>
/// <item>Duration is clamped rather than trusted. The dangerous silence is the long one.</item>
/// <item>It always requires approval - enforced in <c>PolicyEngine</c>, not here.</item>
/// </list>
/// <para>
/// Unlike a pod delete this has a <b>real inverse</b>: expiring the silence restores exactly
/// the prior state. So it takes a genuine rollback spec and does not belong in the gate-14
/// self-healing exemption.
/// </para>
/// </remarks>
public interface IAlertSilencer
{
    /// <summary>Whether this is wired up at all. The executor refuses the action when false.</summary>
    bool IsConfigured { get; }

    Task<SilenceResult> SilenceAsync(SilenceRequest request, CancellationToken ct);

    /// <summary>The inverse. Used by rollback, and by a human undoing one.</summary>
    Task<SilenceResult> ExpireAsync(string silenceId, CancellationToken ct);

    /// <summary>One line at startup saying whether this is on, and why not when it is off.</summary>
    string Describe();
}

/// <summary>What runs when Alertmanager is not configured: refuses, and says so.</summary>
public sealed class NullAlertSilencer : IAlertSilencer
{
    public bool IsConfigured => false;

    public Task<SilenceResult> SilenceAsync(SilenceRequest request, CancellationToken ct) =>
        Task.FromResult(new SilenceResult(false, null, "Alertmanager:Url is not set"));

    public Task<SilenceResult> ExpireAsync(string silenceId, CancellationToken ct) =>
        Task.FromResult(new SilenceResult(false, null, "Alertmanager:Url is not set"));

    public string Describe() => "Alert silencing is OFF: Alertmanager:Url is not set.";
}

public sealed class AlertSilencer(
    HttpClient http,
    IOptionsMonitor<AlertmanagerOptions> options,
    IClock clock,
    ILogger<AlertSilencer> logger) : IAlertSilencer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.CurrentValue.Url);

    public string Describe() =>
        IsConfigured
            ? $"Alert silencing is ON via {options.CurrentValue.Url} (max {options.CurrentValue.MaxDuration})."
            : "Alert silencing is OFF: Alertmanager:Url is not set.";

    public async Task<SilenceResult> SilenceAsync(SilenceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var o = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(o.Url))
        {
            return new SilenceResult(false, null, "Alertmanager:Url is not set");
        }

        // A silence with no matchers matches EVERYTHING, and Alertmanager accepts it without
        // complaint. This is the single most important check in the file.
        if (string.IsNullOrWhiteSpace(request.AlertName))
        {
            return new SilenceResult(false, null, "refusing a silence with no alertname: it would match every alert");
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            return new SilenceResult(false, null, "refusing a silence with no namespace: it would apply cluster-wide");
        }

        // Clamped, not validated. The model asking for eight hours is not an error worth
        // failing an approved action over - it is a number worth ignoring.
        var duration = request.Duration <= TimeSpan.Zero
            ? o.DefaultDuration
            : (request.Duration > o.MaxDuration ? o.MaxDuration : request.Duration);

        var now = clock.UtcNow;

        var body = new SilencePayload
        {
            Matchers =
            [
                new Matcher { Name = "alertname", Value = request.AlertName },

                // Both label spellings, because the shipped rules disagree: Kubernetes rules
                // label it `namespace` and the OTel spanmetrics rules label it
                // `k8s_namespace_name` - which is the same disagreement backlog #33 is about on
                // the ingest side. Matching only one would silence half of what was asked for
                // and leave the operator believing the alert was covered.
                new Matcher { Name = "namespace", Value = request.Namespace },
            ],
            StartsAt = now,
            EndsAt = now + duration,
            CreatedBy = request.CreatedBy,
            Comment = request.Comment,
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(o.Timeout);

            using var response = await http
                .PostAsJsonAsync(Endpoint(o.Url, "api/v2/silences"), body, Json, timeout.Token)
                .ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new SilenceResult(false, null, $"HTTP {(int)response.StatusCode}: {Trim(text)}");
            }

            var id = JsonDocument.Parse(text).RootElement.TryGetProperty("silenceID", out var v)
                ? v.GetString()
                : null;

            logger.LogInformation(
                "Silenced {Alert} in {Namespace} until {Until} (silence {SilenceId})",
                request.AlertName,
                request.Namespace,
                body.EndsAt,
                id);

            return new SilenceResult(true, id, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SilenceResult(false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<SilenceResult> ExpireAsync(string silenceId, CancellationToken ct)
    {
        var o = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(o.Url))
        {
            return new SilenceResult(false, null, "Alertmanager:Url is not set");
        }

        if (string.IsNullOrWhiteSpace(silenceId))
        {
            return new SilenceResult(false, null, "no silence id to expire");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(o.Timeout);

            using var response = await http
                .DeleteAsync(Endpoint(o.Url, $"api/v2/silence/{Uri.EscapeDataString(silenceId)}"), timeout.Token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? new SilenceResult(true, silenceId, null)
                : new SilenceResult(false, silenceId, $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SilenceResult(false, silenceId, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Uri Endpoint(string baseUrl, string path) =>
        new(new Uri(baseUrl.TrimEnd('/') + "/"), path);

    private static string Trim(string text) => text.Length > 200 ? text[..200] : text;

    private sealed class SilencePayload
    {
        [JsonPropertyName("matchers")]
        public List<Matcher> Matchers { get; init; } = [];

        [JsonPropertyName("startsAt")]
        public DateTimeOffset StartsAt { get; init; }

        [JsonPropertyName("endsAt")]
        public DateTimeOffset EndsAt { get; init; }

        [JsonPropertyName("createdBy")]
        public string CreatedBy { get; init; } = string.Empty;

        [JsonPropertyName("comment")]
        public string Comment { get; init; } = string.Empty;
    }

    private sealed class Matcher
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; init; } = string.Empty;

        [JsonPropertyName("isRegex")]
        public bool IsRegex { get; init; }

        [JsonPropertyName("isEqual")]
        public bool IsEqual { get; init; } = true;
    }
}
