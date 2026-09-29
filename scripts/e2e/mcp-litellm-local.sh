#!/usr/bin/env bash
#
# The MCP endpoint through an MCP gateway with tool search on (#157, tier C) - how production
# reaches it: a model sees mcp_tool_search and mcp_tool_call, and finds Hephaisto's tools by the
# words of its question.
#
#   scripts/e2e/mcp-litellm-local.sh           # studio-rancher-desktop, the Tilt stack with "mcp" on
#   scripts/e2e/mcp-litellm-local.sh --keep    # leave the gateway running afterwards
#
# Applies infra/e2e/litellm.yaml (a throwaway LiteLLM in hephaisto-obs, no models, its own
# Postgres), makes three keys - tool search on, off, and one without the agent - and deletes them
# on exit. Costs nothing: no model is ever called.
#
#   L00  the gateway is new enough for tool search (>= 1.92)
#   L02  a key with tool search lists the two search tools and none of the agent's; without it, 20+
#   L03  the agent's tools as the gateway names them are the reviewed list, prefixed
#   L04  every findability question puts its tool in the top five - and in the top five the offline
#        scorer (McpFindabilityTests) predicts, so the unit test is a test of the real gateway
#   L05  an incident fired now is found and read through the gateway
#   L06  an oversized incident arrives whole: under the budget, and still JSON
#   L07  a key without the agent's server cannot call its tools
#   L08  an acknowledgement through the gateway is the gateway's token, with the person as a claim
#   L09  whether tool search ranks across servers - reported, not asserted
#
# SAFETY. Refuses any context but a local dev cluster.

set -uo pipefail

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$E2E_DIR/../.." && pwd)"
CONTEXT="${PAGER_CONTEXT:-studio-rancher-desktop}"
KEEP=false
[ "${1:-}" = "--keep" ] && KEEP=true

case "$CONTEXT" in
    studio-rancher-desktop|rancher-desktop) ;;
    *) echo "refusing: $CONTEXT is not a local dev context" >&2; exit 1 ;;
esac

H="$(jq -r '.host // "localhost"' "$REPO/tilt_config.json" 2>/dev/null || echo localhost)"
API="http://$H:8100"
GW=http://127.0.0.1:14000
FAILED=0
k() { kubectl --context "$CONTEXT" "$@"; }
ok()   { printf '  ok    %s\n' "$1"; }
bad()  { printf '  FAIL  %s -- %s\n' "$1" "${2:-}"; FAILED=$((FAILED + 1)); }
note() { printf '  note  %s\n' "$1"; }
want() { if [ "$2" "$3" "$4" ] 2>/dev/null; then ok "$1"; else bad "$1" "got '$2', wanted $3 '$4'"; fi; }

# --- the gateway ----------------------------------------------------------------------------

if ! k -n hephaisto-obs get secret litellm-e2e >/dev/null 2>&1; then
    shared=$(k -n hephaisto get secret hephaisto-mcp -o jsonpath='{.data.pager-shared}' | base64 -d)
    [ -n "$shared" ] || { echo "no hephaisto-mcp Secret in hephaisto: turn \"mcp\" on in tilt_config.json" >&2; exit 1; }
    k -n hephaisto-obs create secret generic litellm-e2e \
        --from-literal=master-key="sk-$(openssl rand -hex 16)" \
        --from-literal=postgres-password="$(openssl rand -hex 16)" \
        --from-literal=hephaisto-mcp-token="$shared" >/dev/null
fi

k apply -f "$REPO/infra/e2e/litellm.yaml" >/dev/null
k -n hephaisto-obs rollout status deploy/litellm-e2e --timeout=600s >/dev/null || { echo "the gateway did not start" >&2; exit 1; }

k -n hephaisto-obs port-forward svc/litellm-e2e 14000:4000 >/dev/null 2>&1 &
PF=$!
MASTER=$(k -n hephaisto-obs get secret litellm-e2e -o jsonpath='{.data.master-key}' | base64 -d)
KEYS=()

cleanup() {
    if [ ${#KEYS[@]} -gt 0 ]; then
        curl -s -o /dev/null -X POST "$GW/key/delete" -H "Authorization: Bearer $MASTER" -H 'Content-Type: application/json' \
            --data "$(printf '%s\n' "${KEYS[@]}" | jq -R . | jq -cs '{keys: .}')" || true
    fi
    kill "$PF" 2>/dev/null || true
    $KEEP || k -n hephaisto-obs delete -f "$REPO/infra/e2e/litellm.yaml" --wait=false >/dev/null 2>&1 || true
}
trap cleanup EXIT

for _ in $(seq 1 30); do curl -s -o /dev/null "$GW/health/liveliness" && break; sleep 1; done

key() {
    local k
    k=$(curl -s -X POST "$GW/key/generate" -H "Authorization: Bearer $MASTER" -H 'Content-Type: application/json' \
        --data "$1" | jq -r '.key // empty')
    [ -n "$k" ] || { echo "the gateway made no key for $1" >&2; exit 1; }
    KEYS+=("$k")
    printf '%s' "$k"
}

SEARCH=$(key '{"object_permission":{"mcp_servers":["hephaisto","neighbours"],"mcp_tool_search_enabled":true}}')
PLAIN=$(key '{"object_permission":{"mcp_servers":["hephaisto","neighbours"]}}')
OTHER=$(key '{"object_permission":{"mcp_servers":["neighbours"]}}')

# One JSON-RPC call through the gateway; prints the message.
rpc() {
    local key="$1" method="$2" params="$3" path="${4:-hephaisto,neighbours}"
    curl -s --max-time 60 -X POST "$GW/$path/mcp" -H "Authorization: Bearer $key" \
        -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
        --data "$(jq -cn --arg m "$method" --argjson p "$params" '{jsonrpc:"2.0", id:1, method:$m, params:$p}')" \
        | awk '/^data: /{sub(/^data: /, ""); last=$0; sse=1} !/^(data|event|id):/ && !sse && NF {plain=plain $0} END {print (sse ? last : plain)}'
}
# A tool call through the gateway's mcp_tool_call; prints the tool's text.
through() {
    local msg
    msg=$(rpc "$SEARCH" tools/call "$(jq -cn --arg t "hephaisto-$1" --argjson a "$2" '{name:"mcp_tool_call", arguments:{tool_name:$t, arguments:$a}}')")
    if ! jq -e '.result.content' >/dev/null 2>&1 <<<"$msg"; then
        echo "  ...   $1 through the gateway answered: ${msg:0:300}" >&2
    fi
    jq -r '[.result.content[]? | .text] | join("")' <<<"$msg" 2>/dev/null
}
search() {
    rpc "$SEARCH" tools/call "$(jq -cn --arg q "$1" '{name:"mcp_tool_search", arguments:{query:$q, top_k:5}}')" \
        | jq -r '.result.content[0].text' | jq -c '[.[].name]' 2>/dev/null || echo '[]'
}

echo "L00 the gateway"
version=$(curl -s "$GW/openapi.json" | jq -r '.info.version // "0"')
if printf '%s\n%s\n' "1.92.0" "$version" | sort -V -C; then ok "LiteLLM $version has tool search"; else bad "LiteLLM $version has tool search" "needs 1.92 or later"; exit 1; fi

echo "L02 what a key is shown"
listed=$(rpc "$SEARCH" tools/list '{}' | jq -c '[.result.tools[].name]')
want "a key with tool search is shown mcp_tool_search" "$(jq 'index("mcp_tool_search") != null' <<<"$listed")" = true
want "and mcp_tool_call" "$(jq 'index("mcp_tool_call") != null' <<<"$listed")" = true
want "and none of the agent's tools" "$(jq '[.[] | select(startswith("hephaisto-"))] | length' <<<"$listed")" -eq 0
plain=$(rpc "$PLAIN" tools/list '{}' | jq -c '[.result.tools[].name]')
want "a key without tool search is shown the agent's tools" "$(jq '[.[] | select(startswith("hephaisto-"))] | length' <<<"$plain")" -ge 20

echo "L03 the names the gateway gives them"
golden=$(jq -c '[.tools[].name | "hephaisto-" + .] | sort' "$E2E_DIR/mcp/tools.golden.json")
# The shared token may write but is no approver: it is shown the reviewed list less the approver tools.
expected=$(jq -c '[.tools[] | select(.needs != "approver") | "hephaisto-" + .name] | sort' "$E2E_DIR/mcp/tools.golden.json")
want "the gateway's names are the reviewed list, prefixed" "$(jq -c '[.[] | select(startswith("hephaisto-"))] | sort' <<<"$plain")" = "$expected"
note "the approver tools ($(jq -c 'length' <<<"$golden") reviewed, $(jq 'length' <<<"$expected") for this token) stay out of a reader token's list"

echo "L04 tool search finds each question's tool, as the scorer predicts"
registry=$(rpc "$PLAIN" tools/list '{}' | jq -c '[.result.tools[] | {name, description}]')
while IFS=$'\t' read -r query tool; do
    case "$query" in ''|'#'*) continue ;; esac
    if [ "$(jq --arg t "hephaisto-$tool" 'any(.[]; .name == $t)' <<<"$registry")" != true ]; then
        note "'$query' -> $tool: not listed to this gateway's token (reader), so not indexed - as intended"
        continue
    fi
    got=$(search "$query")
    predicted=$(python3 - "$query" "$registry" <<'PY'
import json, sys
query, registry = sys.argv[1], json.loads(sys.argv[2])
words = query.lower().split()
scored = [(sum(1 for w in words if w in (t["name"] + " " + t["description"]).lower()), i, t["name"]) for i, t in enumerate(registry)]
print(json.dumps([n for s, i, n in sorted(scored, key=lambda x: (-x[0], x[1])) if s > 0][:5]))
PY
)
    if [ "$(jq --arg t "hephaisto-$tool" 'index($t) != null' <<<"$got")" = true ]; then
        ok "'$query' finds $tool"
    else
        bad "'$query' finds $tool" "top five: $got"
    fi
    if [ "$got" = "$(jq -c . <<<"$predicted")" ]; then
        ok "'$query' ranks as the scorer predicts"
    else
        bad "'$query' ranks as the scorer predicts" "gateway $got, scorer $predicted"
    fi
done < "$E2E_DIR/mcp/findability.tsv"

echo "L05 an incident fired now, read through the gateway"
webhook_token=$(k -n hephaisto get secret hephaisto-webhook-token -o jsonpath='{.data.token}' 2>/dev/null | base64 -d || true)
run=$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')
fire() {
    local name="$1" description="$2" ns="${3:-gateway-$run}"
    local body
    # Each alert in its own namespace, or a second one inside the correlation window joins the first.
    body=$(jq -cn --arg n "$name" --arg d "$description" --arg ns "$ns" '{version:"4", status:"firing", receiver:"hephaisto",
        alerts:[{status:"firing", labels:{alertname:$n, severity:"warning", namespace:$ns, deployment:"gw"}, annotations:{description:$d},
                 startsAt:(now | todate), endsAt:"0001-01-01T00:00:00Z", fingerprint:$n}]}')
    curl -s -o /dev/null -X POST "$API/webhooks/alertmanager" -H 'Content-Type: application/json' \
        ${webhook_token:+-H "Authorization: Bearer $webhook_token"} --data "$body"
}
name="E2eGateway${run}"
fire "$name" "fired by mcp-litellm-local.sh"
id=""
for _ in $(seq 1 30); do
    id=$(through search_incidents "$(jq -cn --arg n "$name" '{alertName:$n}')" | jq -r '.incidents[0].id // empty' 2>/dev/null)
    [ -n "$id" ] && break
    sleep 2
done
want "search_incidents through the gateway finds it" "${id:-none}" != none
read_back=$(through get_incident "$(jq -cn --arg i "$id" '{id:$i}')")
want "get_incident through the gateway reads it" "$(jq -r '.incident.alertName' <<<"$read_back" 2>/dev/null)" = "$name"

echo "L06 an oversized incident, whole"
big="${name}Big"
fire "$big" "$(head -c 60000 /dev/zero | tr '\0' 'x')" "gateway-$run-big"
bid=""
for _ in $(seq 1 30); do
    bid=$(through search_incidents "$(jq -cn --arg n "$big" '{alertName:$n}')" | jq -r '.incidents[0].id // empty' 2>/dev/null)
    [ -n "$bid" ] && break
    sleep 2
done
text=$(through get_incident_signals "$(jq -cn --arg i "$bid" '{incidentId:$i}')")
want "it arrives, and not empty" "${#text}" -gt 1000
[ "${#text}" -gt 1000 ] || note "it answered: ${text:0:300} (incident '${bid:-none}')"
want "it arrives under the budget" "${#text}" -le 32000
want "and still parses" "$(jq -r type <<<"$text" 2>/dev/null)" = object

echo "L07 a key without the agent's server"
refused=$(rpc "$OTHER" tools/call '{"name":"hephaisto-get_status","arguments":{}}' "neighbours")
want "cannot call the agent's tools" "$(jq -r '(.error != null) or (.result.isError == true)' <<<"$refused")" = true
want "and is not shown them" "$(rpc "$OTHER" tools/list '{}' "neighbours" | jq '[.result.tools[]?.name | select(startswith("hephaisto-"))] | length')" -eq 0

echo "L08 an acknowledgement through the gateway"
through acknowledge_incident "$(jq -cn --arg i "$id" '{id:$i, onBehalfOf:"gateway-user"}')" >/dev/null
detail=$(curl -s "$API/api/incidents/$id")
want "the actor is the gateway's token" "$(jq -r .acknowledgedBy <<<"$detail")" = mcp/pager-shared
want "the person is a claim beside it" "$(jq -r .acknowledgedClaimedBy <<<"$detail")" = gateway-user

echo "L09 across servers"
mixed=$(search "search incidents")
note "top five for 'search incidents': $mixed"
if jq -e 'any(.[]; startswith("neighbours-"))' <<<"$mixed" >/dev/null; then
    note "tool search ranks across servers: the neighbours' search_incidents competes with the agent's"
else
    note "tool search did not rank a neighbour for it"
fi

echo
[ "$FAILED" -eq 0 ] && echo "gateway tier: all green" || echo "gateway tier: $FAILED failed"
exit "$FAILED"
