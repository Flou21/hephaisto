using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.ServiceDefaults;

/// <summary>
/// Telemetry, health and resilience defaults. This assembly IS deployed; the Aspire
/// AppHost is not.
/// </summary>
/// <remarks>
/// <para>
/// Two rules govern everything here, and both are reactions to how the neighbouring Cait
/// project does it.
/// </para>
/// <para>
/// <b>Telemetry is never gated on a dev flag.</b> Cait's OpenTelemetryConfiguration returns
/// early and disables all tracing when DEV_LOGGING=true - the mode every dev manifest sets -
/// so in the environment Hephaisto actually lives in it would be blind. For a project whose
/// entire product is telemetry that is not a tradeoff, it is a defect. There is deliberately
/// no global off switch in this file.
/// </para>
/// <para>
/// <b>Every Aspire convenience must degrade when its environment variable is absent.</b>
/// No OTLP endpoint configured means console logging plus the Prometheus scrape endpoint -
/// never a crash, and never silence. The agent has to be able to start in a broken cluster,
/// because a broken cluster is exactly when someone needs it.
/// </para>
/// </remarks>
public static class Extensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Retries and a circuit breaker for every client built by IHttpClientFactory -
            // grafana-mcp and the Kubernetes API. An investigation that dies because Loki
            // blipped is worse than one that takes two seconds longer.
            //
            // NOTE this does NOT cover Gemini, though it used to claim it did.
            // Google.GenAI.Client builds its own HttpClient and never asks the factory, so
            // nothing here has ever seen a provider call. Gemini retry is configured on the
            // SDK's own transport instead - see LlmOptions.Retry.
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // Structured console logging is always on, in addition to OTLP. If the collector is
        // the thing that is broken, `kubectl logs` has to still tell you something.
        builder.Logging.AddJsonConsole();

        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(HephaistoTelemetry.MeterName)
                    // Microsoft.Extensions.AI emits token and duration metrics under its own
                    // meter; without this line LLM cost is invisible to Prometheus.
                    .AddMeter(HephaistoTelemetry.ExtensionsAiSourceName);
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(HephaistoTelemetry.ActivitySourceName)
                    // gen_ai.* spans - model, prompt, tool arguments, token counts - come
                    // from here for free via ChatClientBuilder.UseOpenTelemetry().
                    .AddSource(HephaistoTelemetry.ExtensionsAiSourceName)
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        // Health probes fire every few seconds and would otherwise be most
                        // of what Tempo stores.
                        o.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/healthz")
                            && !context.Request.Path.StartsWithSegments("/readyz")
                            && !context.Request.Path.StartsWithSegments("/metrics");
                    })
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            // One endpoint, deliberately. The collector fans out to Tempo, Loki, Prometheus
            // and the Aspire dashboard, so adding a destination is a line of collector config
            // rather than a redeploy of the agent.
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Always on, independent of OTLP: a PodMonitor scrapes /metrics directly, which is
        // the path that still works when the collector itself is the outage.
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddPrometheusExporter());

        return builder;
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapPrometheusScrapingEndpoint();

        // Liveness stays deliberately dumb: it answers "is the process up", not "is the
        // cluster healthy". A readiness check that depends on Postgres would let a database
        // blip restart the one pod that is trying to diagnose the database blip.
        app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
        });

        app.MapHealthChecks("/readyz");

        return app;
    }
}
