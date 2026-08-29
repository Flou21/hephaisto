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

# Overridable, but NOT defaulted away. The e2e harness points this at its own throwaway kind
# cluster; everything else gets the same refusal it always did. Making it a variable weakens
# nothing, because the guard's job is to stop an ACCIDENTAL context - and a caller that sets
# this has said which cluster it means.
CONTEXT_REQUIRED="${CONTEXT_REQUIRED:-studio-rancher-desktop}"
OBS_NS="hephaisto-obs"
APP_NS="hephaisto"

# The guard is not ceremony. This script creates secrets and reads a Grafana admin password;
# the same kubeconfig can reach production, and the release names here are the same ones used
# there. Refuse rather than assume.
#
# CONTEXT_REQUIRED above can be set by a caller that knows which cluster it means - the e2e
# harness sets it to its own kind context. That is a deliberate statement, not a bypass: the
# check still runs, and still refuses anything else.
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
  # Left alone, with one exception: POSTGRES_APP_PASSWORD is newer than this Secret, and the
  # agent falls back to serving as the database OWNER without it - which means audit_events is
  # not append-only. Adding just the missing key is safe (the existing owner password is
  # untouched) and is what turns an upgraded release into an enforcing one.
  if kubectl -n "$APP_NS" get secret hephaisto-postgres \
       -o jsonpath='{.data.POSTGRES_APP_PASSWORD}' 2>/dev/null | grep -q .; then
    echo "hephaisto-postgres: already present, leaving alone"
  else
    kubectl -n "$APP_NS" patch secret hephaisto-postgres --type merge \
      -p "{\"data\":{\"POSTGRES_APP_PASSWORD\":\"$(openssl rand -hex 24 | tr -d '\n' | base64)\"}}" >/dev/null
    echo "hephaisto-postgres: present; added the missing POSTGRES_APP_PASSWORD"
  fi
else
  kubectl -n "$APP_NS" create secret generic hephaisto-postgres \
    --from-literal=POSTGRES_USER=hephaisto \
    --from-literal=POSTGRES_PASSWORD="$(openssl rand -hex 24)" \
    --from-literal=POSTGRES_APP_PASSWORD="$(openssl rand -hex 24)" \
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
  echo "hephaisto-llm: SKIPPED - no HEPHAISTO_GEMINI_API_KEY set."
  echo "  Either export it and re-run, or edit the placeholder in"
  echo "    secrets/hephaisto-llm.secret.yaml"
  echo "  and apply it:  kubectl apply -f secrets/hephaisto-llm.secret.yaml"
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

# ---------------------------------------------------------------------------------------
# 5. Grafana service-account token the AGENT uses to write annotations
# ---------------------------------------------------------------------------------------
# A second, separate service account, and deliberately not the Admin one above.
#
# This is the only Grafana credential in the system that may WRITE. Editor is the least
# privilege that can create an annotation, and keeping it apart from grafana-mcp's Admin token
# means the credential the model's tools ride on stays read-shaped. It lives in the APP
# namespace because it is the agent, not grafana-mcp, that presents it.
if have "$APP_NS" hephaisto-grafana-annotation; then
  echo "hephaisto-grafana-annotation: already present, leaving alone"
else
  echo "hephaisto-grafana-annotation: minting from Grafana..."
  kubectl -n "$OBS_NS" rollout status deploy/hephaisto-grafana --timeout=180s >/dev/null

  ann_id=$(kubectl -n "$OBS_NS" exec deploy/hephaisto-grafana -c grafana -- sh -c '
      curl -s -u "admin:$GF_SECURITY_ADMIN_PASSWORD" \
        -X POST http://localhost:3000/api/serviceaccounts \
        -H "Content-Type: application/json" \
        -d "{\"name\":\"hephaisto-annotations\",\"role\":\"Editor\",\"isDisabled\":false}"
    ' | python3 -c 'import json,sys; print(json.load(sys.stdin).get("id",""))')

  if [ -z "$ann_id" ]; then
    ann_id=$(kubectl -n "$OBS_NS" exec deploy/hephaisto-grafana -c grafana -- sh -c '
        curl -s -u "admin:$GF_SECURITY_ADMIN_PASSWORD" \
          "http://localhost:3000/api/serviceaccounts/search?query=hephaisto-annotations"
      ' | python3 -c 'import json,sys; r=json.load(sys.stdin).get("serviceAccounts",[]); print(r[0]["id"] if r else "")')
  fi

  if [ -z "$ann_id" ]; then
    echo "could not create or find the hephaisto-annotations service account" >&2
    exit 1
  fi

  ann_key=$(kubectl -n "$OBS_NS" exec deploy/hephaisto-grafana -c grafana -- sh -c "
      curl -s -u \"admin:\$GF_SECURITY_ADMIN_PASSWORD\" \
        -X POST http://localhost:3000/api/serviceaccounts/$ann_id/tokens \
        -H 'Content-Type: application/json' \
        -d '{\"name\":\"hephaisto-annotations-$(date +%s)\"}'
    " | python3 -c 'import json,sys; print(json.load(sys.stdin).get("key",""))')

  if [ -z "$ann_key" ]; then
    echo "service account $ann_id created but token minting failed" >&2
    exit 1
  fi

  kubectl -n "$APP_NS" create secret generic hephaisto-grafana-annotation \
    --from-literal=token="$ann_key" >/dev/null
  echo "hephaisto-grafana-annotation: created (service account id $ann_id)"
fi

echo
echo "secrets in $OBS_NS:"; kubectl -n "$OBS_NS" get secret --no-headers | awk '{print "  ", $1}'
echo "secrets in $APP_NS:";  kubectl -n "$APP_NS"  get secret --no-headers | awk '{print "  ", $1}'
