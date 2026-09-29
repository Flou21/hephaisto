#!/usr/bin/env bash
# The pager suite's MCP driver: asks the agent's MCP endpoint what a coding agent would ask.
#
# Sourced by scripts/e2e/pager.sh next to lib/pager.sh; not executable on its own. No SDK and no
# client image: the endpoint is stateless streamable HTTP, so every request stands alone - no
# initialize, no session id - and one POST carrying both Accept types is all a gateway sends per
# call. The answer is one SSE `data:` line or plain JSON, and _mcp_message takes either.
#
#   PAGER_MCP               the agent's MCP endpoint, http://<host>:<port>/mcp
#   PAGER_MCP_TOKEN_READER    a person token, role reader      (subject pager-reader)
#   PAGER_MCP_TOKEN_APPROVER  a person token, role approver    (subject pager-approver)
#   PAGER_MCP_TOKEN_ONCALL    a person token, role reader      (subject pager-oncall)
#   PAGER_MCP_TOKEN_SHARED    a shared token, role reader, may write - what a gateway holds
#   PAGER_MCP_TOKEN_READONLY  a shared token, role reader, may not write
#
# The five are made by scripts/e2e/mcp-secrets.sh and named in values-pager.yaml's mcp block.
#
# shellcheck disable=SC2034

PAGER_MCP="${PAGER_MCP:-}"

# POST one JSON-RPC body into <file>. Prints the HTTP status (000 when nothing answered).
#   _mcp_post <file> <token|""> <body> [url]
_mcp_post() {
    local out="$1" token="$2" body="$3" url="${4:-$PAGER_MCP}" code
    local -a h=(-H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream'
                -H 'User-Agent: hephaisto-pager-suite')
    [ -n "$token" ] && h+=(-H "Authorization: Bearer $token")
    code=$(_pager_curl -o "$out" -w '%{http_code}' -X POST "$url" "${h[@]}" --data "$body" 2>/dev/null) || true
    printf '%s' "${code:-000}"
}

# The JSON-RPC message in a response body: the last SSE data line, or the body itself.
_mcp_message() {
    local body="$1" data
    data=$(sed -n 's/^data: //p' <<<"$body" | tail -1)
    if [ -n "$data" ]; then printf '%s' "$data"; else printf '%s' "$body"; fi
}

# One JSON-RPC request. Prints the response message with the HTTP status added as `_http` -
# a status set in a variable would not survive the $( ) every caller reads this through.
#   mcp_rpc <token> <method> [params-json] [url]
mcp_rpc() {
    local token="$1" method="$2" params="${3:-}" url="${4:-$PAGER_MCP}" body out code msg
    [ -n "$params" ] || params="{}"
    body=$(jq -cn --arg m "$method" --argjson p "$params" \
        '{jsonrpc:"2.0", id:(now * 1000 | floor), method:$m, params:$p}')
    out=$(mktemp)
    code=$(_mcp_post "$out" "$token" "$body" "$url")
    msg=$(_mcp_message "$(cat "$out")")
    rm -f "$out"
    jq -e 'type == "object"' >/dev/null 2>&1 <<<"$msg" || msg='{}'
    jq -c --arg c "$code" '. + {_http: ($c | tonumber? // 0)}' <<<"$msg"
}

# The tools the token is shown, in the order the server lists them. Full objects.
#   mcp_tool_list <token> [url]
mcp_tool_list() { mcp_rpc "$1" tools/list '{}' "${2:-$PAGER_MCP}" | jq -c '.result.tools // []'; }

# Just the names, in order.
mcp_tools() { mcp_tool_list "$@" | jq -c '[.[].name]'; }

# One tool call, normalised so a scenario asserts on one shape whatever went wrong:
#   {http, error: {code,message}|null, isError, text, chars}
# `text` is the tool's single text block - the agent's JSON, still a string. mcp_json parses it.
#   mcp_call <token> <tool> [arguments-json] [url]
mcp_call() {
    local token="$1" tool="$2" args="${3:-}" url="${4:-$PAGER_MCP}" msg
    [ -n "$args" ] || args="{}"
    msg=$(mcp_rpc "$token" tools/call "$(jq -cn --arg n "$tool" --argjson a "$args" '{name:$n, arguments:$a}')" "$url")
    jq -c '{
        http: ._http,
        error: (.error // null),
        isError: (.result.isError // false),
        text: ([.result.content[]? | select(.type == "text") | .text] | join("")),
      } | . + {chars: (.text | length)}' <<<"$msg"
}

# The tool's own JSON out of an mcp_call result, or null when the text is not JSON.
mcp_json() { jq -c '.text | fromjson? // null' <<<"$1"; }

# The HTTP status of a bare request, for the refusals an SDK would hide.
#   mcp_raw <url> [authorization-header-value] [method]
mcp_raw() {
    local url="$1" auth="${2:-}" method="${3:-POST}"
    local -a h=(-H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream')
    [ -n "$auth" ] && h+=(-H "Authorization: $auth")
    local code
    code=$(_pager_curl -o /dev/null -w '%{http_code}' -X "$method" "$url" "${h[@]}" \
        --data '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' 2>/dev/null) || true
    printf '%s' "${code:-000}"
}

# True when <canary> occurs in <text>, and every occurrence sits inside an
# <untrusted-evidence>...</untrusted-evidence> envelope. The check #157 asks for: incident
# content reaches a model as data, never as the server's own words.
#   mcp_envelope_holds <text> <canary>
mcp_envelope_holds() {
    local text="$1" canary="$2" outside
    grep -qF -- "$canary" <<<"$text" || return 1
    outside=$(perl -0pe 's/<untrusted-evidence>.*?<\/untrusted-evidence>//gs' <<<"$text")
    ! grep -qF -- "$canary" <<<"$outside"
}

# The incident's timeline through the tool, audit entries only.
#   mcp_audit_rows <token> <incident-id>
mcp_audit_rows() {
    mcp_json "$(mcp_call "$1" get_incident_timeline "$(jq -cn --arg id "$2" '{incidentId:$id, limit:100}')")" \
        | jq -c '[.entries[]? | select(.kind == "audit")]'
}

# Tell the model stand-in to investigate for real, once, for incidents whose prompt contains
# <match>: call <tool> with <arguments>, then conclude citing <excerpt> from that step.
#   mcp_script <match> <tool> <arguments-json> <excerpt>
mcp_script() {
    _pager_curl -o /dev/null -w '%{http_code}' -X POST "$PAGER_STANDIN/llm/script" \
        -H 'Content-Type: application/json' \
        --data "$(jq -cn --arg m "$1" --arg t "$2" --argjson a "$3" --arg e "$4" \
            '{match:$m, tool:$t, arguments:$a, excerpt:$e}')"
}

# Fails the scenario when the suite's MCP settings are missing - see pager.sh's `mcp` capability.
mcp_ready() {
    local v
    for v in PAGER_MCP PAGER_MCP_TOKEN_READER PAGER_MCP_TOKEN_APPROVER PAGER_MCP_TOKEN_ONCALL \
             PAGER_MCP_TOKEN_SHARED PAGER_MCP_TOKEN_READONLY; do
        if [ -z "${!v:-}" ]; then
            fail "the suite knows the MCP endpoint and its tokens" "$v is not set"
            return 1
        fi
    done
}
