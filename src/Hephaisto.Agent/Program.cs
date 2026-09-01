using Hephaisto.Agent.Components;
using Hephaisto.Agent.Demo;
using Microsoft.Extensions.AI;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Agent.Safety;
using Hephaisto.Agent.Web;
using Hephaisto.ServiceDefaults;

// The composition root. Each stream contributes one AddXxx extension method rather than
// editing this file, so the whole wiring stays one readable page - which matters, because
// "what is actually switched on in this process" is a security question here, not a
// stylistic one.

var builder = WebApplication.CreateBuilder(args);

// Telemetry, health and resilience. Deliberately unconditional: there is no dev flag that
// turns observability off, because the agent's product IS telemetry and being blind in
// development is how you ship an agent that is blind in production.
builder.AddServiceDefaults();

builder.Services.AddHephaistoPersistence(builder.Configuration);
builder.Services.AddHephaistoKubernetes(builder.Configuration);

// The kill switch, before anything that consults it. Three independent arms - the env var,
// the projected ConfigMap and the database row - and the most restrictive one wins. This is
// registered here rather than inside the pipeline because the executor, the investigation
// coordinator and the UI all have to agree on one answer.
builder.Services.AddHephaistoSafety(builder.Configuration);

// Order matters here. The pipeline registers the real ISignalSink; AddHephaistoWeb
// registers a logging no-op with TryAdd so the webhook route is exercisable before ingest
// exists. Registering the pipeline first makes that TryAdd a no-op. The other order also
// happens to work, but only by accident of last-registration-wins - and a signal sink that
// silently drops everything is exactly the bug you would not notice.
builder.Services.AddHephaistoLlm(builder.Configuration);
builder.Services.AddHephaistoPipeline(builder.Configuration);
builder.Services.AddHephaistoNotifications(builder.Configuration);
builder.Services.AddHephaistoWeb();

// The demo seed. Inert unless Demo:Seed is set, and refuses on a database that already holds
// an incident - so it ships in the image without being a thing a real install can trip over.
builder.Services.AddHephaistoDemo(builder.Configuration);

// The bridge between the two streams: the Kubernetes layer builds its read-only tools, the
// investigation loop consumes IEnumerable<AIFunction> without knowing where they came from.
// Registering them individually is what lets the runner stay ignorant of Kubernetes entirely
// - and is why nothing in the investigation layer can hold a mutating handle.
builder.Services.AddSingleton<IEnumerable<AIFunction>>(sp =>
    sp.GetRequiredService<KubernetesReadTools>().CreateFunctions());

// Replaces the escalate-only placeholder registered by AddHephaistoPipeline.
builder.Services.AddScoped<IIncidentInvestigator, InvestigationCoordinator>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Before anything serves a request. The tables have to exist before the first webhook
// arrives, and an agent that cannot persist must not pretend to be healthy - see
// MigrateHephaistoDatabaseAsync for why this fails fast rather than degrading.
await app.PrepareDatabaseAsync();

// MapStaticAssets, not UseStaticFiles. It fingerprints app.css and app.js at build time and
// serves them immutable, so a released fix to either actually reaches a browser that already
// has the old one cached. With UseStaticFiles the URLs never change, and a console left open
// on a wall keeps running last release's stylesheet and last release's scripts indefinitely -
// which is how a fix to the reconnect overlay would ship and then not apply to the one page
// that most needed it.
app.MapStaticAssets();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapHephaistoEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .WithStaticAssets();

await app.RunAsync();
