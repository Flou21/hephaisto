// Dev-time orchestration only. Nothing here is ever deployed: the pod is described by
// infra/app/watchtower.yaml, applied by Tilt. This file exists so `dotnet run` from a
// laptop gives you Postgres, the agent and the fault simulator with one command, and the
// Aspire dashboard for free.
//
// The agent reads all of its configuration from IConfiguration. Aspire injects
// ConnectionStrings__* and OTEL_EXPORTER_OTLP_ENDPOINT here; in-cluster the same keys come
// from env in the Deployment. That symmetry is the reason the agent has no idea which
// environment it is in.

var builder = DistributedApplication.CreateBuilder(args);

// pgvector, not plain postgres: the incident history is embedded and searched with HNSW,
// so the extension has to be present in dev too or migrations fail only in the cluster.
var postgres = builder
    .AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "pg17")
    .WithDataVolume("watchtower-pgdata")
    .WithLifetime(ContainerLifetime.Persistent);

var db = postgres.AddDatabase("watchtower");

var agent = builder
    .AddProject<Projects.Watchtower_Agent>("watchtower")
    .WithReference(db)
    .WaitFor(db)
    .WithHttpHealthCheck("/healthz")
    // Locally there is no cluster stack to talk to, so the agent starts in observe mode
    // with the LLM path live and the executor inert.
    .WithEnvironment("WATCHTOWER_MODE", "observe");

builder
    .AddProject<Projects.Watchtower_Simulator>("simulator")
    .WithReference(agent)
    .WaitFor(agent);

builder.Build().Run();
