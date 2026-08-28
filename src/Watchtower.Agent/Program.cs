using Watchtower.Agent.Components;
using Microsoft.Extensions.AI;
using Watchtower.Agent.Kubernetes;
using Watchtower.Agent.Llm;
using Watchtower.Agent.Persistence;
using Watchtower.Agent.Pipeline;
using Watchtower.Agent.Safety;
using Watchtower.Agent.Web;
using Watchtower.ServiceDefaults;

// The composition root. Each stream contributes one AddXxx extension method rather than
// editing this file, so the whole wiring stays one readable page - which matters, because
// "what is actually switched on in this process" is a security question here, not a
// stylistic one.

var builder = WebApplication.CreateBuilder(args);

// Telemetry, health and resilience. Deliberately unconditional: there is no dev flag that
// turns observability off, because the agent's product IS telemetry and being blind in
// development is how you ship an agent that is blind in production.
builder.AddServiceDefaults();

builder.Services.AddWatchtowerPersistence(builder.Configuration);
builder.Services.AddWatchtowerKubernetes(builder.Configuration);

// The kill switch, before anything that consults it. Three independent arms - the env var,
// the projected ConfigMap and the database row - and the most restrictive one wins. This is
// registered here rather than inside the pipeline because the executor, the investigation
// coordinator and the UI all have to agree on one answer.
builder.Services.AddWatchtowerSafety(builder.Configuration);

// Order matters here. The pipeline registers the real ISignalSink; AddWatchtowerWeb
// registers a logging no-op with TryAdd so the webhook route is exercisable before ingest
// exists. Registering the pipeline first makes that TryAdd a no-op. The other order also
// happens to work, but only by accident of last-registration-wins - and a signal sink that
// silently drops everything is exactly the bug you would not notice.
builder.Services.AddWatchtowerLlm(builder.Configuration);
builder.Services.AddWatchtowerPipeline(builder.Configuration);
builder.Services.AddWatchtowerWeb();

// The bridge between the two streams: the Kubernetes layer builds its read-only tools, the
// investigation loop consumes IEnumerable<AIFunction> without knowing where they came from.
// Registering them individually is what lets the runner stay ignorant of Kubernetes entirely
// - and is why nothing in the investigation layer can hold a mutating handle.
builder.Services.AddSingleton<IEnumerable<AIFunction>>(sp =>
    sp.GetRequiredService<KubernetesReadTools>().CreateFunctions());

// Replaces the escalate-only placeholder registered by AddWatchtowerPipeline.
builder.Services.AddScoped<IIncidentInvestigator, InvestigationCoordinator>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapWatchtowerEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
