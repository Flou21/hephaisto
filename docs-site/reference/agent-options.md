# Agent options

Every option class the agent binds, its config section, and its defaults. Most are *not* exposed
as Helm values — set those through [`extraEnv`](/reference/configuration#the-escape-hatch).

Durations are .NET `TimeSpan` strings (`"00:10:00"`, not `"10m"`).

## `Llm`

The chat model, the embedding model, and what a single investigation is allowed to spend.

| Key | Type | Default |
|---|---|---|
| `Llm:Provider` | string | `gemini` |
| `Llm:Model` | string | `gemini-3.7-flash` |
| `Llm:PlanningModel` | string? | `null` — falls back to `Model` |
| `Llm:EmbeddingProvider` | string | `gemini` |
| `Llm:EmbeddingModel` | string | `gemini-embedding-001` |
| `Llm:EmbeddingDimensions` | int | `768` |
| `Llm:ApiKey` | string? | `null` — from the secret |
| `Llm:EmbeddingApiKey` | string? | `null` |
| `Llm:EmbeddingEndpoint` | string? | `null` |
| `Llm:Endpoint` | string? | `null` |
| `Llm:ApiVersion` | string? | `null` |
| `Llm:PlanningStructuredOutput` | `JsonSchema` \| `JsonObject` | `JsonSchema` |
| `Llm:Temperature` | double | `0.2` |
| `Llm:MaxOutputTokens` | int? | `8192` |
| `Llm:Pricing` | map of id → price | eight Gemini entries |

::: warning An unpriced model is charged at zero
`Llm:Pricing` maps a model id to `InputPerMillionUsd` / `OutputPerMillionUsd`. A model with no
entry costs nothing as far as the budget is concerned, which **switches the cost budget off**
rather than approximating it. Ship a price with the model, always.

The seeded Gemini prices are promotional and double on 2027-01-01. Nothing in the code knows that
date.
:::

::: warning `JsonSchema` is not universally supported
DeepSeek answers `400 "This response_format type is unavailable now"`. Phase 1 is unaffected, so
the agent diagnoses correctly and then proposes nothing — indistinguishable in a run summary from
an agent that considered acting and declined. `JsonObject` moves the schema into the prompt. It is
the weaker mode and stays off by default, and it is safe only because nothing downstream takes the
model's word for it: every cited finding id is verified, and an action missing a namespace, kind
or name is dropped before an executor sees it.
:::

### `Llm:Investigation` — one investigation's budget

| Key | Type | Default |
|---|---|---|
| `MaxSteps` | int | `12` |
| `MaxToolCalls` | int | `20` |
| `MaxWallClock` | TimeSpan | `00:10:00` |
| `MaxInputTokens` | long | `400000` |
| `MaxCostUsd` | decimal | `0.50` |
| `MaxConsecutiveNoToolTurns` | int | `2` |

A reserved concluding step lets a run that hits `MaxSteps` still write a finding. It releases two
calls rather than one, because the conclusion is taken through the `conclude` tool and a tool call
is two model round trips; reserving a single step paid for the first and refused the second, which
is [#78](https://github.com/hephaisto-dev/hephaisto/blob/main/docs/backlog.md).

That rescue used not to be able to land when the exhausted ceiling was **tokens**, because the
concluding call resends the conversation. That limitation was closed by #82 — the conversation is
trimmed for the concluding call — and this page went on describing it for two releases after.

### `Llm:Budget` — the rolling global budget, counted in Postgres

| Key | Type | Default |
|---|---|---|
| `MaxTokensPerHour` | long | `2000000` |
| `MaxCostUsdPerHour` | decimal | `3.00` |
| `MaxCostUsdPerDay` | decimal | `20.00` |
| `MaxCostUsdPerIncident` | decimal | `0.50` |
| `WarnAtUtilization` | double | `0.80` |
| `RunawayHourlyHitsBeforeLatch` | int | `3` |
| `RunawayWindow` | TimeSpan | `1.00:00:00` |

### `Llm:Retry`

| Key | Type | Default |
|---|---|---|
| `Attempts` | int | `5` |
| `InitialDelay` | TimeSpan | `00:00:01` |
| `MaxDelay` | TimeSpan | `00:00:30` |
| `ExpBase` | double | `2.0` |
| `Jitter` | double | `1.0` |

### `Llm:Tools` — what a tool call may return

| Key | Type | Default |
|---|---|---|
| `Timeout` | TimeSpan | `00:00:20` |
| `MaxResultBytes` | int | `8192` |
| `MaxRawBytes` | int | `1000000` |
| `MaxQueryRange` | TimeSpan | `7.00:00:00` |
| `RequireTimeRange` | bool | `true` |
| `RedactedArgumentNames` | list | seeded |

::: danger Redaction covers arguments, not results
`RedactedArgumentNames` redacts tool **arguments** in logs and traces. It does not redact tool
**results** — raw `describe_pod` and `get_pod_logs` output carries env vars, hostnames and log
contents verbatim into evidence blobs. That is why recorded cassettes are not committed to the
repository.
:::

## `Policy`

The default-deny policy engine. A pure function over facts passed in by the caller, which is what
makes it exhaustively unit-testable.

| Key | Type | Default |
|---|---|---|
| `AllowedNamespaces` | set | `[]` — **act nowhere** |
| `ProtectedNamespaces` | set | seeded, never actionable |
| `AutoEnabledActionTypes` | set | `[]` |
| `ProtectedLabels` | map | seeded |
| `RequiredNamespaceLabel` | string | `hephaisto.dev/destructive-actions-allowed` |
| `AllowSingleReplicaRestartLabel` | string | `hephaisto.dev/allow-single-replica-restart` |
| `MaxPodsPerAction` | int | `10` |
| `MaxWorkloadFraction` | double | `0.5` |
| `MaxActionsPerIncident` | int | `3` |
| `MaxActionsPerWorkloadPerHour` | int | `2` |
| `MaxActionsPerHour` | int | `10` |
| `MaxActionsPerDay` | int | `20` |
| `WorkloadCooldown` | TimeSpan | `00:15:00` |
| `MinPodAgeBeforeAction` | TimeSpan | `00:02:00` |
| `ClusterUnhealthyCeiling` | double | `0.3` |
| `RollbackFreshRevisionWindow` | TimeSpan | `00:30:00` |
| `RollbackPreviousHealthyMinimum` | TimeSpan | `01:00:00` |
| `MaintenanceWindows` | list | `[]` |

`ClusterUnhealthyCeiling` is the circuit breaker: above that fraction of unhealthy workloads the
agent stops acting, on the reasoning that a cluster-wide problem is not one a pod restart fixes.

## `Kubernetes`

| Key | Type | Default |
|---|---|---|
| `Enabled` | bool | `true` |
| `ClusterName` | string | `default` |
| `KubeconfigPath` / `KubeconfigContext` | string? | `null` |
| `ReadableNamespaces` / `DeniedNamespaces` | set | `{}` |
| `SignalQueueCapacity` | int | `2048` |
| `RelistInterval` | TimeSpan | `00:10:00` |
| `WatchTimeout` | TimeSpan | `00:05:00` |
| `ReconnectBaseDelay` / `ReconnectMaxDelay` | TimeSpan | `00:00:01` / `00:01:00` |
| `StormThreshold` | int | `50` |
| `StormWindow` | TimeSpan | `00:00:30` |
| `StormAggregateInterval` | TimeSpan | `00:01:00` |
| `RestartStormThreshold` | int | `3` |
| `RestartStormWindow` | TimeSpan | `00:10:00` |
| `ReadinessFlapThreshold` | int | `4` |
| `ReadinessFlapWindow` | TimeSpan | `00:10:00` |
| `LogTailLines` | int | `2000` |
| `LogLimitBytes` | int | `4194304` |
| `MaxRows` | int | `200` |
| `RbacMode` | `Enforce` \| `WarnOnly` | `Enforce` |

::: danger `Kubernetes:Enabled=false` is a demo setting
It skips the RBAC self-check and the watchers, and leaves the executor that refuses everything in
place. An agent that watches nothing while reporting itself healthy is the worst failure mode this
project has, so it logs at **warning** level on every start. It exists so the console can boot on
a laptop with no kubeconfig.
:::

## `Persistence`

| Key | Type | Default |
|---|---|---|
| `ConnectionString` | string? | `null` |
| `ConnectionStringName` | string | `hephaisto` |
| `AppConnectionStringName` | string | `hephaisto_app` |
| `ApplyMigrationsOnStartup` | bool | `false` |
| `CommandTimeout` | TimeSpan | `00:00:30` |
| `EvidenceBlobRetention` | TimeSpan | `30.00:00:00` |
| `LlmUsageRetention` | TimeSpan | `30.00:00:00` |
| `NotificationRetention` | TimeSpan | `30.00:00:00` |
| `RetentionSweepInterval` | TimeSpan | `01:00:00` |
| `RetentionBatchSize` | int | `1000` |
| `MaxAdmissionRetries` | int | `3` |
| `SearchPoolFactor` | int | `8` |
| `SearchMinPool` | int | `100` |

Two connection strings, deliberately. The serving role holds `INSERT` but not `UPDATE` or `DELETE`
on `audit_events` — Postgres cannot restrain a table's owner, so audit immutability depends on the
agent not being one.

## `Investigation`

| Key | Type | Default |
|---|---|---|
| `MaxOuterTurns` | int | `8` |
| `MinConfidenceForPlan` | double | `0.5` |
| `PlanningCostUsd` | decimal | `0.10` |
| `PlanningTimeout` | TimeSpan | `00:01:30` |
| `PlanningMaxInputTokens` | long | `200000` |
| `EvidenceBlobRetention` | TimeSpan | `30.00:00:00` |
| `StallNudge`, `FinalConclusionNudge`, `OpeningMessage` | string | see [prompts](/internals/prompts) |

### `Investigation:Environment` — the environment card

What the agent is told about *your* cluster. **The shipped defaults are this repository's own dev
cluster** and should be overridden.

| Key | Type | Default |
|---|---|---|
| `ClusterName` | string | `studio-rancher-desktop` |
| `InScopeNamespaces` | list | `["hephaisto-chaos"]` |
| `ProtectedNamespaces` | list | `["hephaisto","hephaisto-obs","kube-system"]` |
| `DatasourceUids` | map | `{}` |
| `WorkloadOwners` | map | `{}` |
| `Notes` | list | `[]` |

## `Ingest`

| Key | Type | Default |
|---|---|---|
| `ClusterName` | string | `studio-rancher-desktop` |
| `BurstWindow` | TimeSpan | `00:05:00` |
| `FlapThreshold` | int | `3` |
| `FlapWindow` | TimeSpan | `01:00:00` |
| `FlapCooldown` | TimeSpan | `04:00:00` |
| `CorrelationWindow` | TimeSpan | `00:10:00` |
| `SelfNamespaces` | set | `["hephaisto","hephaisto-obs"]` |

## `Notifications`

| Key | Type | Default |
|---|---|---|
| `BaseUrl` | string? | `null` — required once any route exists |
| `Routes` | list | `[]` |
| `Webhook:Url`, `Webhook:SigningSecret` | string? | `null` |
| `Teams:WorkflowUrl` | string? | `null` |
| `GrafanaUrl` | string? | `null` |
| `MaxPerChannelPerHour` | int | `60` |
| `CorrelationCooldown` | TimeSpan | `00:15:00` |
| `MaxAttempts` | int | `8` |
| `FirstRetryDelay` | TimeSpan | `00:00:30` |
| `MaxRetryDelay` | TimeSpan | `00:30:00` |
| `DispatchInterval` | TimeSpan | `00:00:10` |
| `DispatchBatchSize` | int | `20` |
| `SendTimeout` | TimeSpan | `00:00:10` |

A route is `Channel`, `Events`, `MinSeverity` (default `Info`) and `Namespaces`. Routing is
**additive only** — there is no deny rule. See [Notifications](/operate/notifications).

## `Grafana`

| Key | Type | Default |
|---|---|---|
| `McpUrl` | string? | `null` — no PromQL/LogQL tools without it |
| `ServiceAccountToken` | string? | `null` |
| `Url` | string? | `null` |
| `AnnotationToken` | string? | `null` |
| `AnnotationTimeout` | TimeSpan | `00:00:05` |
| `ToolCacheDuration` | TimeSpan | `00:00:30` |
| `ConnectTimeout` | TimeSpan | `00:00:10` |

## `Alertmanager`

Write-only, and the only thing written is a silence.

| Key | Type | Default |
|---|---|---|
| `Url` | string? | `null` |
| `MaxDuration` | TimeSpan | `02:00:00` |
| `DefaultDuration` | TimeSpan | `00:30:00` |
| `Timeout` | TimeSpan | `00:00:10` |

## `KillSwitch`

| Key | Type | Default |
|---|---|---|
| `SwitchDirectory` | string? | `null` |
| `ModeFileName` | string | `mode` |
| `EmergencyStopFileName` | string | `killSwitch` |
| `ModeEnvironmentVariable` | string | `HEPHAISTO_MODE` |

## `Demo`

| Key | Type | Default |
|---|---|---|
| `Seed` | bool | `false` |
| `TranscriptPath` | string | `Demo/transcripts` |

Refuses outright if the database already holds an incident, so it cannot overwrite anything.
