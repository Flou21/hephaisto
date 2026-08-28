#!/usr/bin/env bash
# Clear the local agent's incident history, so a test run starts from a known-empty state.
#
# This is a DEVELOPMENT convenience. It destroys every incident, signal, investigation and
# audit event in the cluster's Postgres. There is no undo and no backup taken.
#
# It does NOT drop the database or the schema - the tables, indexes, the append-only GRANT
# on audit_events and the seeded agent_mode row all survive, so the agent starts clean
# rather than starting broken. Use `dotnet ef database update` for schema changes.
#
# Usage:
#   ./scripts/reset-db.sh              # ask first, keep llm_usage, restore replica count
#   ./scripts/reset-db.sh --yes        # no prompt
#   ./scripts/reset-db.sh --with-usage # also clear llm_usage and llm_budget_breaches
#   ./scripts/reset-db.sh --start      # leave the agent running at 1 replica afterwards
set -euo pipefail

CONTEXT_REQUIRED="studio-rancher-desktop"
NS=hephaisto
PG=postgres-0

ASSUME_YES=0
WITH_USAGE=0
FORCE_START=0

for arg in "$@"; do
  case "$arg" in
    --yes|-y)      ASSUME_YES=1 ;;
    --with-usage)  WITH_USAGE=1 ;;
    --start)       FORCE_START=1 ;;
    -h|--help)     sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

# The same guard bootstrap-secrets.sh carries, for the same reason: this kubeconfig can also
# reach real clusters, and every namespace and object name in this script exists there too.
# Aborting is always cheaper than discovering afterwards which cluster you were pointed at.
ctx=$(kubectl config current-context)
if [ "$ctx" != "$CONTEXT_REQUIRED" ]; then
  echo "REFUSING: context is '$ctx', expected '$CONTEXT_REQUIRED'" >&2
  exit 1
fi

# Tables are listed explicitly rather than discovered. A `truncate everything in the schema`
# loop would also take agent_mode - the row the kill switch reads - and __EFMigrationsHistory,
# which would make the next start re-run every migration against a populated database.
TABLES="
  hephaisto.evidence
  hephaisto.evidence_blobs
  hephaisto.findings
  hephaisto.investigation_steps
  hephaisto.investigations
  hephaisto.agent_actions
  hephaisto.action_plans
  hephaisto.verifications
  hephaisto.incident_digests
  hephaisto.incident_events
  hephaisto.human_feedback
  hephaisto.audit_events
  hephaisto.signals
  hephaisto.workload_action_locks
  hephaisto.incidents
"

# llm_usage is kept by default, and that is deliberate. It backs the rolling hourly and daily
# cost windows, so clearing it tells the budget the agent has spent nothing today and hands
# back a full allowance it has already used. Keeping it costs nothing; clearing it is the
# option you have to ask for.
if [ "$WITH_USAGE" = "1" ]; then
  TABLES="$TABLES hephaisto.llm_usage hephaisto.llm_budget_breaches"
fi

psql_agent() {
  kubectl -n "$NS" exec "$PG" -- sh -c "psql -U \"\$POSTGRES_USER\" -d hephaisto -v ON_ERROR_STOP=1 $*"
}

echo "context : $ctx"
echo "target  : $NS/$PG, schema hephaisto"
echo
echo "rows now:"
psql_agent -c "'select relname, n_live_tup from pg_stat_user_tables where n_live_tup > 0 order by n_live_tup desc;'"

if [ "$WITH_USAGE" = "1" ]; then
  echo
  echo "!! --with-usage: llm_usage will ALSO be cleared, resetting the spend windows to zero."
fi

if [ "$ASSUME_YES" != "1" ]; then
  echo
  printf "Delete all of the above (except llm_usage unless asked)? [y/N] "
  read -r reply
  case "$reply" in
    y|Y|yes|YES) ;;
    *) echo "aborted."; exit 1 ;;
  esac
fi

# Stop the agent first. Truncating underneath a running agent races an in-flight
# investigation: it holds an incident graph in a DbContext, and the save at the end would
# either resurrect rows this script just deleted or fail with a foreign key violation.
replicas=$(kubectl -n "$NS" get deploy hephaisto -o jsonpath='{.spec.replicas}' 2>/dev/null || echo 0)
echo
echo "agent replicas before: ${replicas:-0}"

if [ "${replicas:-0}" != "0" ]; then
  echo "scaling agent to 0 so nothing writes while the tables are being cleared..."
  kubectl -n "$NS" scale deploy hephaisto --replicas=0 >/dev/null
  kubectl -n "$NS" wait --for=delete pod -l app.kubernetes.io/name=hephaisto --timeout=120s >/dev/null 2>&1 || true
fi

# One statement, one transaction. CASCADE is for the foreign keys between these tables only -
# every table it could reach is already in the list.
list=$(echo "$TABLES" | tr -s '[:space:]' ' ' | sed 's/^ //; s/ $//; s/ /, /g')
echo "truncating..."
psql_agent -c "'TRUNCATE TABLE $list RESTART IDENTITY CASCADE;'"

target="${replicas:-0}"
[ "$FORCE_START" = "1" ] && target=1

if [ "$target" != "0" ]; then
  echo "restoring agent to $target replica(s)..."
  kubectl -n "$NS" scale deploy hephaisto --replicas="$target" >/dev/null
else
  echo "agent left at 0 replicas (it was already stopped; pass --start to bring it up)."
fi

echo
echo "rows after:"
psql_agent -c "'select relname, n_live_tup from pg_stat_user_tables where n_live_tup > 0 order by n_live_tup desc;'"
echo
echo "done."
