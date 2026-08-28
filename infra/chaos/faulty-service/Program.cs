// C10 faulty-service - the only chaos fixture that emits a fully correlated
// three-signal trail (traces -> span metrics -> logs carrying trace_id).
// Endpoints: /ok /flaky /slow /flap /healthz. Knobs: ERROR_RATE, LATENCY_MS.
using System.Diagnostics;
using System.Globalization;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

const string ServiceName = "faulty-service";
var activitySource = new ActivitySource(ServiceName);

var builder = WebApplication.CreateBuilder(args);

// Knobs. Invariant parsing because InvariantGlobalization=true is set repo-wide.
var errorRate = double.TryParse(Environment.GetEnvironmentVariable("ERROR_RATE"),
    NumberStyles.Float, CultureInfo.InvariantCulture, out var er) ? Math.Clamp(er, 0d, 1d) : 0.15d;
var latencyMs = int.TryParse(Environment.GetEnvironmentVariable("LATENCY_MS"),
    NumberStyles.Integer, CultureInfo.InvariantCulture, out var lm) ? Math.Max(lm, 0) : 750;

// Logs via OTLP. OTel stamps TraceId/SpanId onto every LogRecord written inside
// an active Activity, which is what puts trace_id on the Loki line.
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
    o.ParseStateValues = true;
    o.AddOtlpExporter();
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? ServiceName))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation(o => o.RecordException = true)
        .AddSource(ServiceName)
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        // Exemplars are what let a latency histogram bucket link straight to an
        // exact slow trace. Without TraceBased filtering there are no exemplars
        // and the five-hop correlation test cannot complete its last hop.
        .SetExemplarFilter(ExemplarFilterType.TraceBased)
        .AddOtlpExporter());

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FaultyService");

app.MapGet("/healthz", () => Results.Text("healthy"));

app.MapGet("/ok", () =>
{
    log.LogInformation("served /ok");
    return Results.Ok(new { status = "ok" });
});

app.MapGet("/flaky", () =>
{
    using var span = activitySource.StartActivity("inventory.lookup", ActivityKind.Client);
    if (Random.Shared.NextDouble() < errorRate)
    {
        span?.SetStatus(ActivityStatusCode.Error, "inventory backend returned 500");
        log.LogError("FAULT /flaky failed: inventory backend returned 500 (error_rate={ErrorRate})", errorRate);
        return Results.Problem("inventory backend unavailable", statusCode: 500);
    }
    log.LogInformation("served /flaky");
    return Results.Ok(new { status = "ok" });
});

app.MapGet("/slow", async () =>
{
    using var span = activitySource.StartActivity("pricing.recalculate", ActivityKind.Internal);
    await Task.Delay(latencyMs);
    log.LogWarning("FAULT /slow exceeded latency budget: took {LatencyMs}ms (budget 200ms)", latencyMs);
    return Results.Ok(new { status = "slow", latencyMs });
});

app.MapGet("/flap", () =>
{
    // Rolling 60s window: even minute -> 200, odd minute -> 503.
    if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 % 2 == 1)
    {
        log.LogError("FAULT /flap in unavailable window: returning 503");
        return Results.Problem("temporarily unavailable", statusCode: 503);
    }
    log.LogInformation("served /flap");
    return Results.Ok(new { status = "ok" });
});

log.LogInformation("faulty-service started with ERROR_RATE={ErrorRate} LATENCY_MS={LatencyMs}", errorRate, latencyMs);
app.Run();
