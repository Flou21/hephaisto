# Hephaisto chaos fixtures

Ten hand-written Kubernetes fault-injection fixtures, one per file, all in namespace
`hephaisto-chaos`. They exist to give the Hephaisto agent a stable, reproducible set
of failures with a **known-correct answer**, so its diagnoses can be regression-tested
rather than eyeballed.

Each YAML file carries a long header comment stating what it breaks, the expected
Kubernetes event, the expected metric signal and the expected log signal. **Those
comments and this table are the contract** the agent's regression tests assert against.
Where a signal does not behave the way the textbook says on this particular cluster,
the file says so explicitly rather than quietly shipping a rule that never fires — see
C6 in particular.

---

## Assumptions this table is written against

**Kubernetes / node.** Rancher Desktop k3s, context `studio-rancher-desktop`, single
node `lima-rancher-desktop`, `linux/arm64`, 14 vCPU / 115 GiB. Every image used is
verified to publish an arm64 manifest. The only StorageClass is `local-path`
(Delete, WaitForFirstConsumer).

**Metrics.** `kube_*` from kube-state-metrics, `container_*` from cAdvisor via the
kubelet, `kubelet_volume_stats_*` from the kubelet, `node_*` from node-exporter,
`traces_spanmetrics_*` from Tempo's metrics-generator, `http_server_*` from the
application's own OTLP export.

> Metric names were checked against the kube-state-metrics actually running on this
> machine, **v2.17.0**. One consequence matters: the whole `kube_endpoint_*` family
> (`kube_endpoint_address`, `kube_endpoint_address_available`) **no longer exists**.
> It has been replaced by `kube_endpointslice_endpoints{...,ready="true"}`. Rules
> written against `kube_endpoint_*` return nothing and never fire.

**Logs — the Loki label assumption, stated explicitly.** Log lines reach Loki through
the OTel collector, and the collector's `k8sattributes` processor supplies the labels.
Every LogQL selector below therefore uses OTel resource-attribute names with dots
lowered to underscores:

```
{k8s_namespace_name="hephaisto-chaos", k8s_deployment_name="<name>", k8s_container_name="<name>"}
```

If the collector is configured to promote different labels (e.g. Promtail-style
`namespace` / `pod` / `container`, or Loki's newer structured-metadata mapping), **every
LogQL expression in this table must be re-labelled accordingly**. The fixtures do not
depend on this — only the queries do. Jobs have no `k8s_deployment_name`; C5 uses
`k8s_job_name` with `k8s_pod_name=~"c5-badjob-.*"` as the fallback.

**Events.** Kubernetes Events are assumed to be ingested by the OTel collector's
`k8s_events` receiver. C3 and C7 are the fixtures that prove why that receiver is
MVP-critical: they carry information that exists in **no metric at all**.

---

## The table

| # | Scenario | Expected alert name | Expected PromQL | Expected LogQL | Expected Kubernetes event |
|---|---|---|---|---|---|
| C1 | OOMKill — 64Mi limit, ~200Mi allocated | `ChaosPodOOMKilled` | `kube_pod_container_status_last_terminated_reason{namespace="hephaisto-chaos",container="balloon",reason="OOMKilled"} == 1` | *(none — deliberate)* `count_over_time({k8s_namespace_name="hephaisto-chaos", k8s_deployment_name="c1-oomkill"}[15m]) == 0` | Node: `Warning SystemOOM`; Pod: `Warning BackOff` |
| C2 | CrashLoopBackOff, one decisive log line | `ChaosPodCrashLooping` | `kube_pod_container_status_waiting_reason{namespace="hephaisto-chaos",container="app",reason="CrashLoopBackOff"} == 1` | `{k8s_namespace_name="hephaisto-chaos", k8s_deployment_name="c2-crashloop"} \|= "FATAL"` | `Warning BackOff — Back-off restarting failed container app` |
| C3 | Unschedulable — requests 500Gi | `ChaosPodUnschedulable` | `kube_pod_status_unschedulable{namespace="hephaisto-chaos"} == 1` | *(none)* `count_over_time({k8s_namespace_name="hephaisto-chaos", k8s_deployment_name="c3-unschedulable"}[15m]) == 0` | `Warning FailedScheduling — 0/1 nodes are available: 1 Insufficient memory.` |
| C4 | ImagePullBackOff — nonexistent tag | `ChaosImagePullFailure` | `max_over_time(kube_pod_container_status_waiting_reason{namespace="hephaisto-chaos",reason=~"ImagePullBackOff\|ErrImagePull"}[10m]) == 1` | *(none)* `count_over_time({k8s_namespace_name="hephaisto-chaos", k8s_deployment_name="c4-imagepull"}[15m]) == 0` | `Warning Failed — Failed to pull image "busybox:this-tag-does-not-exist": ... not found` |
| C5 | Job exceeds `backoffLimit: 2` | `ChaosJobFailed` | `kube_job_failed{namespace="hephaisto-chaos",job_name=~"c5-badjob.*",reason="BackoffLimitExceeded"} == 1` | `{k8s_namespace_name="hephaisto-chaos", k8s_job_name="c5-badjob"} \|= "migration step 4 failed"` | `Warning BackoffLimitExceeded — Job has reached the specified backoff limit` |
| C6 | 1Gi PVC filled to ~950Mi | `ChaosVolumeAlmostFull` | `kubelet_volume_stats_used_bytes{namespace="hephaisto-chaos",persistentvolumeclaim="c6-diskfill-data"} / kubelet_volume_stats_capacity_bytes{namespace="hephaisto-chaos",persistentvolumeclaim="c6-diskfill-data"} > 0.90` **(does NOT fire on local-path — see below)** | `{k8s_namespace_name="hephaisto-chaos", k8s_deployment_name="c6-diskfill"} \|= "volume is 95% full"` | `Normal ProvisioningSucceeded`; **no warning event for the fill itself** |
| C7 | `CreateContainerConfigError` — missing Secret | `ChaosContainerConfigError` | `kube_pod_container_status_waiting_reason{namespace="hephaisto-chaos",container="app",reason="CreateContainerConfigError"} == 1` | *(none — identical to C4, which is the point)* | `Warning Failed — Error: secret "c7-database-credentials" not found` |
| C8 | Readiness flap, 60s on / 60s off | `ChaosEndpointFlapping` *(Sev3 — never page)* | `changes(kube_pod_status_ready{namespace="hephaisto-chaos",pod=~"c8-readiness-flap-.*",condition="true"}[30m]) >= 4` | `{k8s_namespace_name="hephaisto-chaos", k8s_deployment_name="c8-readiness-flap"} \|= "entering unhealthy window"` | `Warning Unhealthy — Readiness probe failed: HTTP probe failed with statuscode: 404` |
| C9 | Unbounded 4Gi memhog — **node-wide** | `ChaosNodeMemoryPressure` | `kube_node_status_condition{condition="MemoryPressure",status="true"} == 1` | `{k8s_namespace_name="hephaisto-chaos", k8s_deployment_name="c9-memhog"} \|= "allocated"` | `Warning NodeHasInsufficientMemory`; `Warning Evicted — The node was low on resource: memory.` |
| C10 | faulty-service — 15% 500s, 750ms p95, 503 window | `ChaosServiceErrorBudgetBurn` | `sum(rate(traces_spanmetrics_calls_total{service_name="faulty-service",status_code="STATUS_CODE_ERROR"}[5m])) / sum(rate(traces_spanmetrics_calls_total{service_name="faulty-service"}[5m])) > 0.05` | `{k8s_namespace_name="hephaisto-chaos", k8s_deployment_name="c10-faulty-service", k8s_container_name="app"} \|= "FAULT"` | *(none — deliberately event-silent; the pod stays Ready)* |
| C11 | Transient - first pod wedged, any later pod healthy | `ChaosPodCrashLooping` | `kube_pod_container_status_waiting_reason{namespace="hephaisto-chaos",container="app",reason="CrashLoopBackOff"} == 1` (and `kube_deployment_status_replicas_available{deployment="c11-transient"} == 1` once restarted) | `{k8s_namespace_name="hephaisto-chaos", k8s_deployment_name="c11-transient"} \|= "FATAL"` | `Warning BackOff` until the pod is replaced |

### Secondary expressions worth asserting

| # | Purpose | PromQL |
|---|---|---|
| C1 | Restart rate | `increase(kube_pod_container_status_restarts_total{namespace="hephaisto-chaos",container="balloon"}[15m]) > 3` |
| C1 | Memory sawtooth against the limit | `container_memory_working_set_bytes{namespace="hephaisto-chaos",container="balloon"} / on(namespace,pod,container) kube_pod_container_resource_limits{namespace="hephaisto-chaos",container="balloon",resource="memory"} > 0.9` |
| C2 | Termination reason is `Error`, not `OOMKilled` | `kube_pod_container_status_last_terminated_reason{namespace="hephaisto-chaos",container="app",reason="Error"} == 1` |
| C3 | Pod stuck Pending | `kube_pod_status_phase{namespace="hephaisto-chaos",phase="Pending"} == 1` |
| C4 vs C7 | Discrimination — C7 must not match C4's reasons | `absent(kube_pod_container_status_waiting_reason{namespace="hephaisto-chaos",pod=~"c7-configerror-.*",reason=~"ImagePullBackOff\|ErrImagePull"}) == 1` |
| C5 | One failure per attempt | `kube_job_status_failed{namespace="hephaisto-chaos",job_name=~"c5-badjob.*"} == 3` |
| C6 | The claim exists and is 1Gi | `kube_persistentvolumeclaim_resource_requests_storage_bytes{namespace="hephaisto-chaos",persistentvolumeclaim="c6-diskfill-data"} == 1073741824` |
| C8 | Counter-discriminator — it is NOT crashing | `increase(kube_pod_container_status_restarts_total{namespace="hephaisto-chaos",container="server"}[30m]) == 0` |
| C8 | Endpoint churn (KSM >= 2.13) | `count(kube_endpointslice_endpoints{namespace="hephaisto-chaos",endpointslice=~"c8-readiness-flap-.*",ready="true"}) or vector(0)` |
| C9 | The finding is the *absence* of a limit | `absent(kube_pod_container_resource_limits{namespace="hephaisto-chaos",container="hog",resource="memory"}) == 1` |
| C9 | Blast radius outside the chaos namespace | `sum(kube_pod_status_reason{reason="Evicted"}) > 0` |
| C10 | p95 latency / exemplar carrier | `histogram_quantile(0.95, sum by (le, span_name) (rate(traces_spanmetrics_latency_bucket{service_name="faulty-service"}[5m]))) > 0.5` |
| C10 | App-side view, independent of Tempo | `sum(rate(http_server_request_duration_seconds_count{job=~".*faulty-service.*",http_response_status_code="500"}[5m])) > 0` |
| C10 | Kubernetes stays green — this is the trap | `kube_deployment_status_replicas_available{namespace="hephaisto-chaos",deployment="c10-faulty-service"} == 1` |

---

## Things that are deliberately NOT true, and must not be asserted

These are the places where the obvious rule is wrong on this cluster. Encoding any of
them as a firing assertion produces a permanently red or permanently green test.

**C6 — `kubelet_volume_stats_*` is useless for per-PVC fill on `local-path`.**
Measured directly from the kubelet on this node
(`kubectl get --raw /api/v1/nodes/lima-rancher-desktop/proxy/metrics`), **every**
local-path PVC reports identical values, because local-path PVs are plain hostPath
directories and the kubelet derives all three stats from `statfs()` of the node root
filesystem:

```
kubelet_volume_stats_capacity_bytes  = 210778099712   (~196 GiB)
kubelet_volume_stats_used_bytes      = 130322243584   (~121 GiB)
kubelet_volume_stats_available_bytes =  69701718016   (~ 65 GiB)
```

The ratio sits at ~0.62 node-wide and moves by 0.0045 when C6 writes its 950Mi.
`local-path` also does not enforce the 1Gi claim, so the write simply succeeds. Ship
the ratio rule as *wired and parsing*, assert the **log** signal as the one that fires,
and treat the ratio as a rule that would fire on a real CSI driver.

**C8 — do not assert `up == 0`.** With Prometheus `role: endpoints` service discovery,
not-ready endpoints are excluded from discovery entirely, so the target **disappears**
rather than reporting `up == 0`. The correct form is
`absent(up{namespace="hephaisto-chaos", service="c8-readiness-flap"})`.

**C1 — there is no Pod-scoped `OOMKilling` event on k3s + containerd.** The kubelet's
OOM watcher raises `SystemOOM` against the **Node**. The per-container evidence is the
pod status field `lastState.terminated.reason == "OOMKilled"`, surfaced by KSM.

**C3 and C7 — no metric names the missing thing.** `kube_pod_status_unschedulable`
tells you *that* a pod cannot be placed but never *why*; only the `FailedScheduling`
event carries `Insufficient memory`. Likewise no metric says which Secret C7 is missing
— only the event text does. This is the argument for the `k8s_events` receiver.

**C10 — the Tempo latency histogram has two names.** Depending on Tempo version and
config it is either `traces_spanmetrics_latency_bucket` (classic) or
`traces_spanmetrics_duration_seconds_bucket` (newer / native histograms). Confirm
against the deployed Tempo before pinning.

---

## What the agent is expected to get *right*, not just detect

Three fixtures are graded on judgement, not detection:

* **C2** — the symptom is `CrashLoopBackOff`; the **cause** is
  `mongo.infra-db:27017`. Restating the symptom is a fail. Reaching Loki is mandatory.
* **C4 vs C7** — both are "container never starts, zero logs, zero restarts, zero
  available replicas". They are told apart only by the `waiting_reason` label and the
  event text. Reporting the same diagnosis for both is a fail.
* **C8** — this is the **false-positive test**. It looks like a 50% outage and is
  actually an intermittent readiness problem with zero restarts. **An agent that opens
  a Sev1 here has failed.** The expected report is "intermittent, not down".

And one is graded on reach:

* **C10** is the only fixture with a fully correlated three-signal trail — span
  metrics, error traces, logs carrying `trace_id`, and exemplars on the latency
  histogram. It is what makes the SLO rules and the five-hop correlation test
  (alert → exemplar → trace → log → cause) demonstrable. It is also deliberately
  **event-silent**: Kubernetes considers the pod perfectly healthy while 15% of its
  requests fail, so a kubectl-only or events-only agent reports "no issues found",
  which is wrong.

---

## Applying and removing

The namespace `hephaisto-chaos` is defined elsewhere and is **not** created by these
files. Create it first.

Build the C10 image once, from the **repo root** (the project inherits Central Package
Management from `/Directory.Packages.props`, so the build context must include it):

```sh
cd /Users/flo/hephaisto
docker build -f infra/chaos/faulty-service/Dockerfile -t hephaisto/faulty-service:dev .
```

Rancher Desktop runs the dockerd engine, so the image is visible to k3s directly with
`imagePullPolicy: IfNotPresent`. There is no registry and no push.

Apply everything, or one scenario at a time:

```sh
kubectl apply -f infra/chaos/                 # all ten; C9 lands disarmed at replicas: 0
kubectl apply -f infra/chaos/c2-crashloop.yaml
```

Validate without touching the cluster:

```sh
for f in infra/chaos/c*.yaml; do kubectl apply --dry-run=client -f "$f"; done
```

Observe:

```sh
kubectl -n hephaisto-chaos get pods
kubectl -n hephaisto-chaos get events --sort-by=.lastTimestamp
kubectl -n hephaisto-chaos describe pod -l hephaisto.chaos/scenario=c7
```

Remove:

```sh
kubectl delete -f infra/chaos/                # C6's PVC is Delete-reclaim, so its data goes too
kubectl delete -f infra/chaos/c5-badjob.yaml  # required before re-applying C5, see below
```

**C5 must be deleted before it is re-run.** A `Job` spec is immutable in the fields
that matter, so `kubectl apply` over a completed Job of the same name is rejected. It
also sets `ttlSecondsAfterFinished: 3600`, so it garbage-collects itself after an hour
rather than leaving `kube_job_failed` pinned at 1 forever.

Every fixture except C9 is bounded by its own resource limits and cannot affect
anything outside `hephaisto-chaos`.

---

## ⚠️ C9 — read this before running it

`c9-memhog.yaml` is the **only** fixture that can disturb workloads it does not own.
It has **no memory limit** on purpose — an unbounded container is the only way to drive
a *node*-level condition rather than a container-level one. That means the pressure
lands on the shared node, and eviction can take out:

* the observability stack in `hephaisto-obs` — losing the telemetry you were trying
  to collect,
* the other nine chaos fixtures,
* unrelated dev workloads on the same node.

**It ships disarmed: `replicas: 0`.** Applying the whole directory does nothing.
Firing it is a separate, conscious act:

```sh
kubectl -n hephaisto-chaos scale deploy/c9-memhog --replicas=1    # ARM
# ... capture the signal, 3-5 minutes is plenty ...
kubectl -n hephaisto-chaos scale deploy/c9-memhog --replicas=0    # DISARM
kubectl describe node lima-rancher-desktop | grep -A3 MemoryPressure
```

Run it **alone**, and do not leave it running.

Honest caveat: at its default `ALLOC_MIB=4096`, 4Gi on a 115 GiB node will **not** by
itself cross the kubelet's default `memory.available < 100Mi` eviction threshold. 4Gi
is the *safe* default — unmistakable in `container_memory_working_set_bytes`, very
unlikely to evict anything on an idle node. Genuinely reproducing `MemoryPressure` and
`Evicted` requires raising `ALLOC_MIB` toward the node's free memory
(`kubectl top node`), which is materially more destructive and should be done only with
intent. Assert the working-set signal unconditionally; treat `MemoryPressure` and
`Evicted` as conditional on `ALLOC_MIB` having been raised.

---

## Images used, and why

| Image | Used by | arm64 |
|---|---|---|
| `busybox:1.37` | C1, C2, C3, C5, C6, C7, C8, C9, C10 sidecar | multi-arch manifest includes `linux/arm64` — verified with `docker manifest inspect` |
| `busybox:this-tag-does-not-exist` | C4 | intentionally nonexistent; a real repo with a bogus tag gives a deterministic registry error, unlike a bogus hostname whose DNS error text varies by resolver |
| `mcr.microsoft.com/dotnet/sdk:10.0-alpine` | C10 build stage | `linux/arm64` present — verified |
| `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` | C10 runtime stage | `linux/arm64` present — verified |
| `hephaisto/faulty-service:dev` | C10 | built locally from the two images above; not in any registry |

`polinux/stress` was considered for C1/C9 and **rejected**: its Docker Hub manifest is a
single-architecture amd64 image with no manifest list, so it cannot run on this node.
Plain `busybox` allocating into a memory-backed `emptyDir` (tmpfs pages are charged to
the pod's memory cgroup) achieves the same thing natively on arm64 with no extra image.

## Directory contents

```
infra/chaos/
├── README.md                    this file
├── c1-oomkill.yaml              Deployment — 64Mi limit vs ~200Mi, emits NO logs on purpose
├── c2-crashloop.yaml            Deployment — one FATAL line, exit 1, CrashLoopBackOff
├── c3-unschedulable.yaml        Deployment — requests 500Gi, FailedScheduling forever
├── c4-imagepull.yaml            Deployment — nonexistent image tag, ImagePullBackOff
├── c5-badjob.yaml               Job — always exits 3, backoffLimit 2, BackoffLimitExceeded
├── c6-diskfill.yaml             PVC (1Gi, local-path) + Deployment that dd's 950Mi into it
├── c7-configerror.yaml          Deployment — secretKeyRef to a Secret that does not exist
├── c8-readiness-flap.yaml       Deployment + Service — readiness alternates 60s on / 60s off
├── c9-memhog.yaml               Deployment — NO memory limit, 4Gi, ships at replicas: 0
├── c10-faulty-service.yaml      Deployment + Service — OTel API + wget load sidecar
├── c11-transient.yaml           PVC + Deployment — first pod wedges, a replacement is healthy
└── faulty-service/
    ├── Program.cs               ~60-line ASP.NET Core minimal API, OTLP traces+metrics+logs
    ├── faulty-service.csproj    net10.0, inherits repo-root CPM, NOT in Hephaisto.slnx
    └── Dockerfile               multi-stage, arm64-native, build context = repo root
```
