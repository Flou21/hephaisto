#!/usr/bin/env bash
# Creates the four secrets the stack needs and that no chart creates.
#
# Run once after `tilt up` has created the namespaces. Safe to re-run: every step is
# idempotent and will not rotate a secret that already exists, because rotating the caller
# token without restarting both sides breaks grafana-mcp until the next rollout.
#
# Why these are not in git: three of them are credentials, and a credential in a values file
# is a credential in every clone and in `helm get values` forever. The Grafana token cannot
# be in git for a stronger reason - it does not exist until Grafana is running, since only
# Grafana can mint it.
set -euo pipefail

CONTEXT_REQUIRED="studio-rancher-desktop"
OBS_NS="hephaisto-obs"
APP_NS="hephaisto"

# The guard is not ceremony. This script creates secrets and reads a Grafana admin password;
# the same kubeconfig can reach production, and the release names here are the same ones used
# there. Refuse rather than assume.
ctx=$(kubectl config current-context)
if [ "$ctx" != "$CONTEXT_REQUIRED" ]; then
  echo "REFUSING: context is '$ctx', expected '$CONTEXT_REQUIRED'" >&2
  exit 1
fi
echo "context: $ctx"

have() { kubectl -n "$1" get secret "$2" >/dev/null 2>&1; }

# ---------------------------------------------------------------------------------------
# 1. grafana-mcp caller token - the bearer every inbound MCP call must present
# ---------------------------------------------------------------------------------------
# The SAME value has to exist in both namespaces: grafana-mcp validates it, and the agent
# presents it. Generated here rather than committed so it differs per environment.
if have "$OBS_NS" grafana-mcp-caller-token && have "$APP_NS" grafana-mcp-caller-token; then
  echo "grafana-mcp-caller-token: already present in both namespaces, leaving alone"
else
  token=$(openssl rand -hex 32)
  kubectl -n "$OBS_NS" delete secret grafana-mcp-caller-token --ignore-not-found >/dev/null
  kubectl -n "$APP_NS" delete secret grafana-mcp-caller-token --ignore-not-found >/dev/null
  kubectl -n "$OBS_NS" create secret generic grafana-mcp-caller-token --from-literal=token="$token" >/dev/null
  kubectl -n "$APP_NS" create secret generic grafana-mcp-caller-token --from-literal=token="$token" >/dev/null
  echo "grafana-mcp-caller-token: created in $OBS_NS and $APP_NS"
fi

# ---------------------------------------------------------------------------------------
# 2. Postgres credentials
# ---------------------------------------------------------------------------------------
# Read by BOTH the postgres container (as POSTGRES_*) and the agent (which composes its
# connection string from them), so there is exactly one place the password is defined.
if have "$APP_NS" hephaisto-postgres; then
  echo "hephaisto-postgres: already present, leaving alone"
else
  kubectl -n "$APP_NS" create secret generic hephaisto-postgres \
    --from-literal=POSTGRES_USER=hephaisto \
    --from-literal=POSTGRES_PASSWORD="$(openssl rand -hex 24)" \
    --from-literal=POSTGRES_DB=hephaisto >/dev/null
  echo "hephaisto-postgres: created"
fi

# ---------------------------------------------------------------------------------------
# 3. Gemini API key
# ---------------------------------------------------------------------------------------
# Set HEPHAISTO_GEMINI_API_KEY in the environment, or this step is skipped and the agent
# runs with no model - it still detects, dedups, correlates and serves the UI, it just
# cannot investigate. That is a legible degraded state rather than a crash.
if have "$APP_NS" hephaisto-llm; then
  echo "hephaisto-llm: already present, leaving alone"
elif [ -n "${HEPHAISTO_GEMINI_API_KEY:-}" ]; then
  kubectl -n "$APP_NS" create secret generic hephaisto-llm \
    --from-literal=GEMINI_API_KEY="$HEPHAISTO_GEMINI_API_KEY" >/dev/null
  echo "hephaisto-llm: created"
else
  echo "hephaisto-llm: SKIPPED - set HEPHAISTO_GEMINI_API_KEY to create it"
fi

# ---------------------------------------------------------------------------------------
# 4. Grafana service-account token for grafana-mcp
# ---------------------------------------------------------------------------------------
# This one cannot be pre-generated: only a running Grafana can mint it. Created through the
# API from inside the Grafana pod, so the admin password never crosses the network and never
# appears in a shell history or a log.
#
# Role is Admin because mcp-grafana enumerates datasources via /api/datasources, which is an
# admin-scoped endpoint; an Editor token returns 403 and the agent loses every Prometheus and
# Loki tool with a confusing error. Scoped to this local cluster only.
if have "$OBS_NS" grafana-mcp-grafana-token; then
  echo "grafana-mcp-grafana-token: already present, leaving alone"
else
  echo "grafana-mcp-grafana-token: minting from Grafana..."
  kubectl -n "$OBS_NS" rollout status deploy/hephaisto-grafana --timeout=180s >/dev/null

  # Both calls run inside the pod. `$GF_SECURITY_ADMIN_PASSWORD` is expanded by the pod's
  # shell, not this one, so the password stays in the container.
  sa_id=$(kubectl -n "$OBS_NS" exec deploy/hephaisto-grafana -c grafana -- sh -c '
      curl -s -u "admin:$GF_SECURITY_ADMIN_PASSWORD" \
        -X POST http://localhost:3000/api/serviceaccounts \
        -H "Content-Type: application/json" \
        -d "{\"name\":\"grafana-mcp\",\"role\":\"Admin\",\"isDisabled\":false}"
    ' | python3 -c 'import json,sys; print(json.load(sys.stdin).get("id",""))')

  if [ -z "$sa_id" ]; then
    # Already exists from a previous run: look the id up instead of failing.
    sa_id=$(kubectl -n "$OBS_NS" exec deploy/hephaisto-grafana -c grafana -- sh -c '
        curl -s -u "admin:$GF_SECURITY_ADMIN_PASSWORD" \
          "http://localhost:3000/api/serviceaccounts/search?query=grafana-mcp"
      ' | python3 -c 'import json,sys; r=json.load(sys.stdin).get("serviceAccounts",[]); print(r[0]["id"] if r else "")')
  fi

  if [ -z "$sa_id" ]; then
    echo "could not create or find the grafana-mcp service account" >&2
    exit 1
  fi

  glsa=$(kubectl -n "$OBS_NS" exec deploy/hephaisto-grafana -c grafana -- sh -c "
      curl -s -u \"admin:\$GF_SECURITY_ADMIN_PASSWORD\" \
        -X POST http://localhost:3000/api/serviceaccounts/$sa_id/tokens \
        -H 'Content-Type: application/json' \
        -d '{\"name\":\"grafana-mcp-$(date +%s)\"}'
    " | python3 -c 'import json,sys; print(json.load(sys.stdin).get("key",""))')

  if [ -z "$glsa" ]; then
    echo "service account $sa_id created but token minting failed" >&2
    exit 1
  fi

  kubectl -n "$OBS_NS" create secret generic grafana-mcp-grafana-token \
    --from-literal=token="$glsa" >/dev/null
  echo "grafana-mcp-grafana-token: created (service account id $sa_id)"

  # grafana-mcp is stuck in CreateContainerConfigError until this Secret exists; it does not
  # retry the mount on its own.
  kubectl -n "$OBS_NS" rollout restart deploy/grafana-mcp >/dev/null 2>&1 || true
fi

echo
echo "secrets in $OBS_NS:"; kubectl -n "$OBS_NS" get secret --no-headers | awk '{print "  ", $1}'
echo "secrets in $APP_NS:";  kubectl -n "$APP_NS"  get secret --no-headers | awk '{print "  ", $1}'
