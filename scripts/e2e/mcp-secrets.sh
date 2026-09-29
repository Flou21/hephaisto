#!/usr/bin/env bash
#
# The MCP tokens the pager suite signs in with (#157): one Secret, five keys, random values.
#
#   scripts/e2e/mcp-secrets.sh                                   # studio-rancher-desktop, namespace hephaisto
#   scripts/e2e/mcp-secrets.sh --context kind-hephaisto-e2e      # CI
#   scripts/e2e/mcp-secrets.sh --print                           # export lines for the five tokens
#
# values-pager.yaml names the Secret (secrets.mcp: hephaisto-mcp) and one key per token, so an
# install that layers it and has no Secret does not start - run this before the install. An
# existing Secret is kept: its values are what the running agent already holds.
#
#   key              role      kind    may write   subject
#   pager-reader     reader    person  yes         pager-reader
#   pager-approver   approver  person  yes         pager-approver
#   pager-oncall     reader    person  yes         pager-oncall
#   pager-shared     reader    shared  yes         -             what a gateway holds
#   pager-readonly   reader    shared  no          -
#
# The values are made here and exist nowhere else: not in git, not in values, not in a log.
#
# SAFETY. Refuses any context but a local dev cluster or the suite's own kind cluster.

set -euo pipefail

CONTEXT=studio-rancher-desktop
NS=hephaisto
PRINT=false

while [ $# -gt 0 ]; do
    case "$1" in
        --context)   CONTEXT="$2"; shift 2 ;;
        --namespace) NS="$2"; shift 2 ;;
        --print)     PRINT=true; shift ;;
        -h|--help)   awk 'NR>1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "$0"; exit 0 ;;
        *)           echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "$CONTEXT" in
    studio-rancher-desktop|rancher-desktop|kind-*) ;;
    *) echo "refusing: $CONTEXT is not a local dev or kind context" >&2; exit 1 ;;
esac

KEYS="pager-reader pager-approver pager-oncall pager-shared pager-readonly"
k() { kubectl --context "$CONTEXT" "$@"; }

if ! k -n "$NS" get secret hephaisto-mcp >/dev/null 2>&1; then
    args=()
    for key in $KEYS; do
        args+=(--from-literal="$key=$(openssl rand -hex 24)")
    done
    k -n "$NS" create secret generic hephaisto-mcp "${args[@]}" >/dev/null
    echo "created secret hephaisto-mcp in $NS ($CONTEXT)" >&2
fi

if $PRINT; then
    for key in $KEYS; do
        var="PAGER_MCP_TOKEN_$(tr '[:lower:]' '[:upper:]' <<<"${key#pager-}")"
        printf 'export %s=%s\n' "$var" "$(k -n "$NS" get secret hephaisto-mcp -o jsonpath="{.data.$key}" | base64 -d)"
    done
fi
