# Human surface — what Program.cs has to wire

Everything under `Web/`, `Components/` and `wwwroot/` is inert until the composition root
calls the two methods below. Nothing in this stream edits `Program.cs`.

## The wiring

```csharp
using Watchtower.Agent.Components;
using Watchtower.Agent.Persistence;
using Watchtower.Agent.Web;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Required: IncidentQueries resolves IIncidentRepository, IAuditRepository,
// IAgentModeStore, IncidentSearch, LlmBudgetService and WatchtowerDbContext out of a
// per-call scope. Without this, every page and every /api route throws on first use.
builder.Services.AddWatchtowerPersistence(builder.Configuration);

// This stream: the notifier, the watchdog, the read model, the no-op signal sink and
// string-enum JSON.
builder.Services.AddWatchtowerWeb();

// Blazor Server. AddInteractiveServerComponents is the half that opens the SignalR circuit;
// without it the pages render once, statically, and never update.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.UseStaticFiles();          // serves wwwroot/app.css and wwwroot/app.js
app.UseAntiforgery();          // required by MapRazorComponents

app.MapWatchtowerEndpoints();  // /webhooks/*, /api/incidents/*, /api/status, /api/evidence-blobs/*

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();
```

Order matters in two places: `UseStaticFiles` before `MapRazorComponents` (otherwise the
stylesheet 404s and the console is unreadable), and `UseAntiforgery` before it (Blazor's
endpoint refuses to map without the middleware present).

## Seams other streams must fill

| Type | Where | Who implements it |
|---|---|---|
| `ISignalSink` | `Web/SignalSink.cs` | **Ingest.** Register the real one with `services.AddSingleton<ISignalSink, …>()` *before* `AddWatchtowerWeb()`, or replace it after — `AddWatchtowerWeb` uses `TryAdd`, so it never overwrites an existing registration. The placeholder logs and drops. |
| `IIncidentNotifier` | `Web/IncidentNotifier.cs` | **Consumed here, published by everyone else.** Inject it into the state machine, the ingest loop and the investigation loop and call `Publish(new IncidentLiveEvent { … })` on every transition. `Publish` never blocks and never throws. |

`ISignalSink.SubmitAsync` must **enqueue and return**. The webhook is on Alertmanager's retry
timer; a sink that waits for Postgres turns one firing alert group into a delivery storm.

The webhook leaves `Signal.Fingerprint` empty — computing it needs the cluster name, which is
ingest configuration. The sink owns fingerprinting, dedup and correlation.

## Endpoints

| Route | Notes |
|---|---|
| `POST /webhooks/alertmanager` | v4 payload bound to records. **Unauthenticated on purpose** — Alertmanager cannot send a custom header. Protected by a NetworkPolicy restricting ingress to the observability namespace. Do not put an Ingress in front of `/webhooks`. |
| `POST /webhooks/watchdog` | Records a timestamp, produces no signal. Absence is the signal. |
| `GET /api/incidents?state=&kind=&namespace=&limit=` | No `state` means open only. `state=all` for everything. |
| `GET /api/incidents/{id}` | Full detail: signals, transitions, investigations with steps/findings/evidence/plan, actions, feedback. |
| `GET /api/incidents/search?q=&namespace=&resolvedOnly=&limit=` | Lexical-only until an embedding generator exists — by design, not a stub. |
| `POST /api/incidents/{id}/feedback` | `submittedBy` required and non-empty. |
| `GET /api/status` | Mode, open/escalated counts, three budget utilisations, watchdog freshness. |
| `GET /api/evidence-blobs/{id}` | The raw tool result behind a step. 404 means expired (30-day retention), not missing. |

## Two things worth knowing before changing anything here

**The read model does not go through a repository for everything.** `IIncidentRepository` has
no list-with-filters method and `GetWithDetailAsync` does not load investigations, so
`IncidentQueries` issues those two LINQ queries against `WatchtowerDbContext` directly. It is
the same context, `AsNoTracking`, mapped to view records before the scope closes — not a
second DbContext and not raw SQL. If the persistence stream ever adds `ListAsync(filter)` and
widens the detail load, move those two queries onto it.

**`IncidentQueries` is a singleton over `IServiceScopeFactory`, not a scoped service.** A
Blazor circuit lives as long as its browser tab; injecting anything scoped into a page pins a
DbContext and its change tracker for that whole lifetime.

## Deliberately absent

There is no approve or execute button anywhere. The MVP runs in `observe` and the plan section
says *"Would have done this — observe mode, nothing was executed"*. When approval does arrive,
it belongs on a route that goes through `IActionRepository.TryAdmitActionAsync`, never a
direct write from a component.
