#!/usr/bin/env bash
#
# The properties this chart exists to hold, asserted as tests.
#
# Every check below is a NEGATIVE: something the chart must REFUSE to render, or something
# that must never appear in its output. Positive rendering is covered by `helm template` in
# CI; these are the ones that rot silently, because a chart that quietly starts granting
# `secrets` still installs perfectly and still looks fine in a diff.
#
#     charts/hephaisto/ci/negative-tests.sh
#
set -uo pipefail

CHART="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PASS=0
FAIL=0

pass() { printf '  ok    %s\n' "$1"; PASS=$((PASS + 1)); }
fail() { printf '  FAIL  %s\n' "$1"; FAIL=$((FAIL + 1)); }

# Renders and expects FAILURE. The message must also be explanatory: a `fail` with no reason
# sends someone to read the template instead of the values file they got wrong.
refuses() {
    local what="$1"; shift
    local out
    if out=$(helm template t "$CHART" --namespace hephaisto "$@" 2>&1); then
        fail "$what -- rendered successfully, but must be refused"
    elif grep -qi "may not contain\|may not set\|is required\|don't meet the specifications of the schema" <<<"$out"; then
        # values.schema.json rejects some of these before a template runs, which is an
        # earlier and better refusal than a `fail` in a template. Both count.
        pass "$what"
    else
        fail "$what -- refused, but with an unexplained error: $(head -2 <<<"$out" | tr '\n' ' ')"
    fi
}

renders() {
    local what="$1"; shift
    if helm template t "$CHART" --namespace hephaisto "$@" >/dev/null 2>&1; then
        pass "$what"
    else
        fail "$what -- must render, but did not"
    fi
}

echo "The write Role refuses namespaces it must never hold delete on:"
refuses "kube-system"                 --set 'policy.actionableNamespaces[0]=kube-system'
refuses "kube-public"                 --set 'policy.actionableNamespaces[0]=kube-public'
refuses "default"                     --set 'policy.actionableNamespaces[0]=default'
refuses "its own release namespace"   --set 'policy.actionableNamespaces[0]=hephaisto'
refuses "the observability namespace" --set 'policy.actionableNamespaces[0]=hephaisto-obs' --set observabilityNamespace=hephaisto-obs
refuses "a bad namespace hidden behind a good one" \
    --set 'policy.actionableNamespaces[0]=app' --set 'policy.actionableNamespaces[1]=kube-system'

echo
echo "Secret names are required rather than silently dangling:"
refuses "no postgres secret name" --set secrets.postgres=""
refuses "no llm secret name"      --set secrets.llm=""
refuses "grafana-mcp url with no token secret" \
    --set grafanaMcp.url=http://grafana-mcp:8000/mcp --set secrets.grafanaMcp=""

echo
echo "The safe defaults still render:"
renders "defaults"     --values "$CHART/ci/minimal-values.yaml"
renders "full values"  --values "$CHART/ci/full-values.yaml"
renders "an allowed namespace" --set 'policy.actionableNamespaces[0]=hephaisto-chaos'

echo
echo "Invariants in the rendered output:"

FULL=$(helm template t "$CHART" --namespace hephaisto --values "$CHART/ci/full-values.yaml" 2>/dev/null)
MIN=$(helm template t "$CHART" --namespace hephaisto --values "$CHART/ci/minimal-values.yaml" 2>/dev/null)

# The single most valuable assertion here. "No access to Secrets at all, ever" is the claim
# the whole safety argument rests on, and it is one careless line away from being false.
if grep -A3 -E '^\s+resources:' <<<"$FULL" | grep -qE '^\s*-?\s*("?secrets"?)\s*$|"secrets"'; then
    fail "the read ClusterRole must never mention secrets"
else
    pass "no rule anywhere mentions secrets"
fi

# Read means read. Any of these verbs in the cluster-wide ClusterRole would make "reads
# cannot break anything" untrue.
if grep -qE '"(create|update|patch|delete|deletecollection)"' <<<"$(awk '/name: t-hephaisto-read/,/^---/' <<<"$FULL")"; then
    fail "the read ClusterRole has grown a write verb"
else
    pass "the read ClusterRole has no write verbs"
fi

# A chart that creates a Secret puts its contents in `helm get values`, in the release Secret,
# and in whatever git repo holds the Argo Application - forever.
if grep -qE '^kind: Secret$' <<<"$FULL"; then
    fail "the chart rendered a Secret; it must only ever reference them"
else
    pass "no Secret is ever rendered"
fi

# Two replicas make the budget/cooldown/kill-switch check and the action INSERT a distributed
# TOCTOU race on the one code path that ends in `kubectl delete pod`.
if grep -qE '^\s+replicas: 1$' <<<"$FULL" && ! grep -qE '^\s+replicas: [02-9]' <<<"$FULL"; then
    pass "the Deployment is a singleton"
else
    fail "the Deployment is not replicas: 1"
fi

if grep -q 'type: Recreate' <<<"$FULL"; then
    pass "strategy is Recreate, so a rollout never runs two executors"
else
    fail "strategy must be Recreate"
fi

# The cordon/drain role ships unbound. Binding it is a separate, hand-written human act.
if grep -q 'hephaisto-node' <<<"$FULL" && ! awk '/^kind: ClusterRoleBinding$/,/^---$/' <<<"$FULL" | grep -q 'hephaisto-node'; then
    pass "the node ClusterRole exists and is not bound"
else
    fail "the node ClusterRole must be created but never bound"
fi

# The failure mode of getting these wrong is silence, so they are asserted rather than trusted.
for kind in PodMonitor PrometheusRule; do
    if awk "/^kind: $kind\$/,/^spec:/" <<<"$FULL" | grep -qE '^\s+release: "?kube-prometheus-stack"?$'; then
        pass "every $kind carries the operator selector label"
    else
        fail "a $kind is missing the operator selector label; Prometheus would never select it"
    fi
done

# The empty allowlist must produce no write access at all, not an empty Role.
if grep -qE '^kind: Role$' <<<"$MIN"; then
    fail "an empty policy.actionableNamespaces still rendered a write Role"
else
    pass "an empty allowlist renders no write Role at all"
fi

# The published image writes nothing to its own filesystem, so the DEFAULT must be a read-only
# root. values-dev.yaml turns it off deliberately, because Tilt runs a dev image whose
# entrypoint is a compiler - but that is an override, and it must never become the default.
if grep -q 'readOnlyRootFilesystem: true' <<<"$FULL" && ! grep -q 'readOnlyRootFilesystem: false' <<<"$FULL"; then
    pass "the root filesystem is read-only by default"
else
    fail "readOnlyRootFilesystem is not true by default"
fi

# CI stamps the version at package time; a real number here is a second source of truth.
if grep -qE '^version: 0\.0\.0$' "$CHART/Chart.yaml" && grep -qE '^appVersion: "0\.0\.0"$' "$CHART/Chart.yaml"; then
    pass "Chart.yaml still reads 0.0.0 (CI stamps the real version)"
else
    fail "Chart.yaml has a hard-coded version; the git tag is meant to be the only source"
fi

# The serving role must be a DIFFERENT role from the owner, or audit_events is not append-only:
# Postgres cannot restrain a table's owner, which may always grant itself back. This renders the
# connection string, so a template edit that drops it fails the release rather than quietly
# returning the agent to owner privileges.
if grep -q 'ConnectionStrings__hephaisto_app' <<<"$FULL" \
   && grep -q 'Username=hephaisto_app' <<<"$FULL"; then
    pass "the agent serves on a non-owner role (audit_events stays append-only)"
else
    fail "no hephaisto_app connection string rendered; the agent would serve as the database owner"
fi

# The optional flag is what keeps an upgrade from wedging on a Secret that predates the key.
# Without it the pod sits in CreateContainerConfigError instead of falling back and warning.
if grep -A 4 'key: POSTGRES_APP_PASSWORD' <<<"$FULL" | grep -q 'optional: true'; then
    pass "POSTGRES_APP_PASSWORD is optional, so an older Secret degrades rather than wedges"
else
    fail "POSTGRES_APP_PASSWORD is a hard secretKeyRef; upgrading an older release would not start"
fi

# OCI tags cannot contain '+'. Helm rewrites it to '_', so the chart would ask for a tag no
# registry has ever heard of - and the error arrives at pull time, not at render time.
if grep -E '^\s+image:' <<<"$FULL" | grep -q '+'; then
    fail "an image tag contains '+', which is not a legal OCI tag"
else
    pass "no image tag contains '+'"
fi

# -------------------------------------------------------------------------------------------
# extraEnv is appended LAST, which is what makes it useful and what makes it dangerous.
# Kubernetes takes the last value for a duplicated env name, so a collision with a
# chart-managed name does not conflict - it silently wins. Three of these are safety
# properties, not preferences: shadowing HEPHAISTO_MODE or HEPHAISTO_SWITCHES_DIR disables an
# arm of the kill switch, and a literal GEMINI_API_KEY is a plaintext credential in
# `helm get values` forever.
# -------------------------------------------------------------------------------------------
refuses "extraEnv cannot shadow GEMINI_API_KEY" \
    --set 'extraEnv[0].name=GEMINI_API_KEY' --set 'extraEnv[0].value=sk-plaintext'
refuses "extraEnv cannot shadow HEPHAISTO_MODE" \
    --set 'extraEnv[0].name=HEPHAISTO_MODE' --set 'extraEnv[0].value=Auto'
refuses "extraEnv cannot redirect the kill switch's ConfigMap dir" \
    --set 'extraEnv[0].name=HEPHAISTO_SWITCHES_DIR' --set 'extraEnv[0].value=/tmp/nowhere'
refuses "extraEnv cannot half-override the OTEL_* block" \
    --set 'extraEnv[0].name=OTEL_SERVICE_NAME' --set 'extraEnv[0].value=something-else'
refuses "extraEnv cannot widen the namespace allowlist behind RBAC's back" \
    --set 'extraEnv[0].name=Policy__AllowedNamespaces__0' --set 'extraEnv[0].value=kube-system'
refuses "extraEnv entries must be named" \
    --set 'extraEnv[0].value=orphan'

# The whole point of the seam: configuration the chart does not expose must still be settable.
renders "extraEnv can set an unexposed option" \
    --set 'extraEnv[0].name=Llm__Budget__MaxCostUsdPerHour' --set 'extraEnv[0].value=1.00'

# And it must land after the chart's own entries, or last-wins works against the operator
# rather than for them.
EXTRA=$(helm template t "$CHART" --namespace hephaisto \
    --set 'extraEnv[0].name=Llm__Budget__MaxCostUsdPerHour' --set 'extraEnv[0].value=1.00' 2>/dev/null)
if [ "$(grep -n 'Llm__Budget__MaxCostUsdPerHour' <<<"$EXTRA" | head -1 | cut -d: -f1)" -gt \
     "$(grep -n 'name: GEMINI_API_KEY' <<<"$EXTRA" | head -1 | cut -d: -f1)" ]; then
    pass "extraEnv is appended after the chart's own env"
else
    fail "extraEnv renders before the chart's env, so it cannot override anything"
fi

echo
printf '%d passed, %d failed\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
