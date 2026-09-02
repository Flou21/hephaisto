# Verify your install

The agent has a specific and unpleasant failure mode: **it reports itself perfectly healthy while
seeing nothing at all.** If Prometheus does not select its rules, every object exists, every probe
passes, and no alert ever arrives. There is no error anywhere.

So verification here is not ceremony. Run these.

## 1. Prometheus actually selects the rules

The chart labels its `PrometheusRule` and `PodMonitor` objects with
`prometheusOperator.selectorLabels`. Your kube-prometheus-stack must select on the same label.

```sh
# What your Prometheus selects on
kubectl get prometheus -A \
  -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.spec.ruleSelector}{"\n"}{end}'

# What the chart labelled its rules with
kubectl -n hephaisto get prometheusrule -o jsonpath='{.items[*].metadata.labels}'
```

Then confirm the rules actually **loaded**, which is the only real proof:

```sh
kubectl exec -n <observability-ns> sts/prometheus-<release> -c prometheus -- \
  wget -qO- localhost:9090/api/v1/rules | grep -c hephaisto
```

A count of zero means the labels do not match, whatever the two commands above appeared to say.

## 2. The alert path reaches the agent

The `AgentWatchdog` rule fires constantly by design — **its absence is the signal**. Within about
five minutes of install:

```sh
kubectl -n hephaisto logs deploy/hephaisto | grep -i watchdog

kubectl -n hephaisto port-forward svc/hephaisto 8080:8080
curl -s localhost:8080/api/status | jq '{mode: .effectiveMode, watchdogStale, openIncidents}'
```

`watchdogStale: true` means Prometheus, Alertmanager, the NetworkPolicy or the agent's receiver is
broken — and that the agent is not seeing your cluster at all.

## 3. RBAC is genuinely bounded

Read access is cluster-wide; write access is a `Role` bound into named namespaces and nowhere
else. Verify rather than trust:

```sh
SA=system:serviceaccount:hephaisto:hephaisto

kubectl auth can-i --list --as=$SA
```

**These must all say `no`:**

```sh
kubectl auth can-i get secrets -A --as=$SA
kubectl auth can-i delete pods -n kube-system --as=$SA
kubectl auth can-i create clusterrolebindings --as=$SA
```

No access to Secrets at all, ever, is a design invariant rather than a configuration. If any of
these says `yes`, something bound a role the chart did not render.

## 4. The webhook is not reachable from where it should not be

The Alertmanager receiver is unauthenticated by necessity, so the NetworkPolicy is its entire
authentication.

```sh
# From a pod that should NOT be able to reach it
kubectl run probe --rm -it --image=curlimages/curl --restart=Never -- \
  curl -s -m 5 -o /dev/null -w '%{http_code}\n' http://hephaisto.hephaisto:8080/healthz
```

A connection timeout is the correct result. A `200` means the policy is not doing its job.

## 5. The mode is what you think it is

Three arms, most restrictive wins.

```sh
curl -s localhost:8080/api/status | jq '{effectiveMode, arms}'
```

`effectiveMode` is the answer. If it disagrees with your values file, one of the other two arms is
more restrictive — which is the design working, not a bug.

## 6. Budget accounting is real

```sh
curl -s localhost:8080/api/status | jq '.budget'
```

If cost is reported as zero after investigations have run, the model has **no `Llm:Pricing`
entry** and the cost budget is therefore switched off rather than approximating. See
[agent options](/reference/agent-options#llm).

## What the project runs on itself

`docs/verification.md` in the repository is a 17-step hand-run checklist against this project's
own dev cluster, plus the per-release acceptance tests. It is written against a specific machine —
it derives its host from `tilt_config.json`, uses fish shell and assumes Tilt port-forwards — so it
is not directly runnable elsewhere. It is worth reading as a model of what "verified" means here.
