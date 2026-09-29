#!/usr/bin/env bash
#
# Installs the pager suite's sign-in-on agent (values-signin.yaml) next to the main one, for P48.
#
#   scripts/e2e/signin-install.sh --context kind-hephaisto-e2e --chart /tmp/chart/hephaisto-X.tgz \
#       --image hephaisto/agent:e2e
#   scripts/e2e/signin-install.sh --image hephaisto/agent:signin      # studio-rancher-desktop
#   scripts/e2e/signin-install.sh --print                              # export lines for P48
#
# Namespace hephaisto-signin, release hephaisto-signin, its own embedded Postgres. The image must
# already be on the node (kind load, or the dev node's docker): the production image, because the
# dev image compiles for minutes before it answers.
#
# SAFETY. Refuses any context but a local dev cluster or the suite's own kind cluster.

set -euo pipefail

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$E2E_DIR/../.." && pwd)"
CONTEXT=studio-rancher-desktop
CHART="$REPO/charts/hephaisto"
IMAGE=""
PRINT=false
NS=hephaisto-signin

while [ $# -gt 0 ]; do
    case "$1" in
        --context) CONTEXT="$2"; shift 2 ;;
        --chart)   CHART="$2"; shift 2 ;;
        --image)   IMAGE="$2"; shift 2 ;;
        --print)   PRINT=true; shift ;;
        -h|--help) awk 'NR>1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "$0"; exit 0 ;;
        *)         echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "$CONTEXT" in
    studio-rancher-desktop|rancher-desktop|kind-*) ;;
    *) echo "refusing: $CONTEXT is not a local dev or kind context" >&2; exit 1 ;;
esac

k() { kubectl --context "$CONTEXT" "$@"; }

if $PRINT; then
    printf 'export PAGER_SIGNIN_TOKEN=%s\n' "$E2E_DIR/signin-token.sh"
    printf 'export PAGER_SIGNIN_STATIC=%s\n' "$(k -n "$NS" get secret hephaisto-mcp -o jsonpath='{.data.pager-shared}' | base64 -d)"
    exit 0
fi

[ -n "$IMAGE" ] || { echo "--image is required: the production image, already on the node" >&2; exit 2; }

k get namespace "$NS" >/dev/null 2>&1 || k create namespace "$NS" >/dev/null
k -n "$NS" get secret hephaisto-postgres >/dev/null 2>&1 || k -n "$NS" create secret generic hephaisto-postgres \
    --from-literal=POSTGRES_USER=hephaisto \
    --from-literal=POSTGRES_PASSWORD="$(openssl rand -hex 16)" \
    --from-literal=POSTGRES_DB=hephaisto >/dev/null
k -n "$NS" get secret hephaisto-llm >/dev/null 2>&1 || k -n "$NS" create secret generic hephaisto-llm \
    --from-literal=LLM_API_KEY=the-stand-in-accepts-anything >/dev/null
"$E2E_DIR/mcp-secrets.sh" --context "$CONTEXT" --namespace "$NS"

helm --kube-context "$CONTEXT" upgrade --install hephaisto-signin "$CHART" \
    --namespace "$NS" \
    -f "$E2E_DIR/values-signin.yaml" \
    --set image.repository="${IMAGE%:*}" \
    --set image.tag="${IMAGE##*:}" \
    --set image.pullPolicy=Never \
    --wait --timeout 6m >/dev/null

echo "hephaisto-signin is up in $NS ($CONTEXT)" >&2
