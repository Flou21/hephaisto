#!/usr/bin/env bash
# The pager suite's driver: posts alerts, reads back who was told.
#
# Sourced by scripts/e2e/pager.sh (and through it by run.sh's `pager` phase); not executable on
# its own. Every scenario under scripts/e2e/pager/ is written against these functions and
# nothing else, so the three places the suite runs - CI on kind, the dev cluster, the release
# harness - differ only in the five addresses below.
#
#   PAGER_API      the agent's HTTP base, for /api (e.g. http://127.0.0.1:18080)
#   PAGER_HOOK     where /webhooks is served; the same as PAGER_API unless webhookPort is set
#   PAGER_STANDIN  the stand-in: Teams (/teams/messages), the outbound receiver (/received) and
#                  the model (/llm/requests) - one binary, infra/e2e/notification-receiver
#   PAGER_AM       an Alertmanager whose route ends at the agent, or empty
#   PAGER_TOKEN    the webhook's bearer token, or empty
#
# shellcheck disable=SC2034

PAGER_TOKEN="${PAGER_TOKEN:-}"
PAGER_AM="${PAGER_AM:-}"

# Short and unique per run, so a scenario on a shared cluster never reads another run's
# incidents. Lowercase hex only: the kind classifier matches substrings of the alert name, and a
# suffix that happened to spell `oom` or `slo` would change what the alert is classified as.
PAGER_RUN="${PAGER_RUN:-$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')}"

# Every alert name a scenario uses: E2ePager<id><name><run>. The prefix classifies as Unknown,
# which is the kind a rule written by somebody else gets, and nothing in it is a keyword.
pager_name() { printf 'E2ePager%s%s' "$1" "$PAGER_RUN"; }

_pager_now() { date -u +%Y-%m-%dT%H:%M:%SZ; }

_pager_curl() {
    curl -sS --max-time "${PAGER_TIMEOUT:-15}" "$@"
}

# One alert object in Alertmanager's webhook shape.
#   pager_alert <status> <alertname> [label=value ...]
# Labels default to severity=warning; any label given replaces its default. An annotation is
# given as @key=value.
pager_alert() {
    local status="$1" name="$2"; shift 2
    local labels annotations kv key value ends

    labels=$(jq -cn --arg n "$name" '{alertname:$n, severity:"warning"}')
    annotations=$(jq -cn --arg n "$name" '{description:("pager suite alert " + $n)}')

    for kv in "$@"; do
        key="${kv%%=*}"; value="${kv#*=}"
        case "$key" in
            @*) annotations=$(jq -c --arg k "${key#@}" --arg v "$value" '. + {($k):$v}' <<<"$annotations") ;;
            *)  labels=$(jq -c --arg k "$key" --arg v "$value" '. + {($k):$v}' <<<"$labels") ;;
        esac
    done

    ends="0001-01-01T00:00:00Z"
    [ "$status" = "resolved" ] && ends=$(_pager_now)

    jq -cn --arg s "$status" --argjson l "$labels" --argjson a "$annotations" \
        --arg starts "${PAGER_STARTS_AT:-$(_pager_now)}" --arg ends "$ends" \
        '{status:$s, labels:$l, annotations:$a, startsAt:$starts, endsAt:$ends,
          generatorURL:("http://prometheus.pager-suite/graph?g0.expr=" + ("up{job=\"" + $l.alertname + "\"} == 0" | @uri)),
          fingerprint:($l | tostring | @base64 | .[0:16])}'
}

# POST a webhook payload holding the given alert objects, straight to the agent. Sets
# PAGER_CODE to the HTTP status.
#   pager_post <alert-json> [<alert-json> ...]
pager_post() {
    local alerts status payload
    alerts=$(printf '%s\n' "$@" | jq -cs '.')
    status=$(jq -r 'if any(.[]; .status == "firing") then "firing" else "resolved" end' <<<"$alerts")

    payload=$(jq -cn --argjson a "$alerts" --arg s "$status" \
        '{version:"4", groupKey:("{}:{alertname=\"" + $a[0].labels.alertname + "\"}"),
          truncatedAlerts:0, status:$s, receiver:"hephaisto", externalURL:"http://alertmanager.pager-suite",
          groupLabels:{alertname:$a[0].labels.alertname}, commonLabels:$a[0].labels,
          commonAnnotations:{}, alerts:$a}')

    if [ -n "$PAGER_TOKEN" ]; then
        PAGER_CODE=$(_pager_curl -o /dev/null -w '%{http_code}' -X POST "$PAGER_HOOK/webhooks/alertmanager" \
            -H 'Content-Type: application/json' -H "Authorization: Bearer $PAGER_TOKEN" --data "$payload" || true)
    else
        PAGER_CODE=$(_pager_curl -o /dev/null -w '%{http_code}' -X POST "$PAGER_HOOK/webhooks/alertmanager" \
            -H 'Content-Type: application/json' --data "$payload" || true)
    fi
}

# The same, with an explicit Authorization header (or none), for the token scenarios.
#   pager_post_as <"Bearer x" | ""> <alert-json>
pager_post_as() {
    local header="$1"; shift
    local saved="$PAGER_TOKEN"
    PAGER_TOKEN=""
    if [ -n "$header" ]; then
        local payload
        payload=$(jq -cn --argjson a "$(printf '%s\n' "$@" | jq -cs '.')" \
            '{version:"4", status:"firing", receiver:"hephaisto", alerts:$a}')
        PAGER_CODE=$(_pager_curl -o /dev/null -w '%{http_code}' -X POST "$PAGER_HOOK/webhooks/alertmanager" \
            -H 'Content-Type: application/json' -H "Authorization: $header" --data "$payload" || true)
    else
        pager_post "$@"
    fi
    PAGER_TOKEN="$saved"
}

pager_fire()    { local n="$1"; shift; pager_post "$(pager_alert firing "$n" "$@")"; }
pager_resolve() { local n="$1"; shift; pager_post "$(pager_alert resolved "$n" "$@")"; }

# Through Alertmanager: the alert is posted to its API and IT decides when to deliver, retry
# and resolve. Alertmanager's API takes no status: an alert with endsAt in the past is resolved.
#   pager_am_fire <alertname> [label=value ...]
pager_am_fire() {
    [ -n "$PAGER_AM" ] || return 2
    local a
    a=$(pager_alert firing "$@" | jq -c '{labels, annotations, startsAt, generatorURL}')
    PAGER_CODE=$(_pager_curl -o /dev/null -w '%{http_code}' -X POST "$PAGER_AM/api/v2/alerts" \
        -H 'Content-Type: application/json' --data "[$a]" || true)
}

pager_am_resolve() {
    [ -n "$PAGER_AM" ] || return 2
    local a
    a=$(pager_alert firing "$@" | jq -c --arg e "$(_pager_now)" '{labels, annotations, startsAt, endsAt:$e, generatorURL}')
    PAGER_CODE=$(_pager_curl -o /dev/null -w '%{http_code}' -X POST "$PAGER_AM/api/v2/alerts" \
        -H 'Content-Type: application/json' --data "[$a]" || true)
}

# ---------------------------------------------------------------------------------------
# Reading back
# ---------------------------------------------------------------------------------------

# Every incident, in any state, with a signal named <alertname>. Newest first. Dies on a body
# that is not an array - see api_array in common.sh for the false positive that taught that.
pager_incidents() {
    local body
    body=$(_pager_curl "$PAGER_API/api/incidents?state=any&limit=100&alertname=$1") || body=""
    if [ "$(jq -r 'type' <<<"$body" 2>/dev/null)" != "array" ]; then
        printf 'GET /api/incidents?alertname=%s returned: %s\n' "$1" "${body:0:300}" >&2
        echo '[]'
        return 1
    fi
    printf '%s' "$body"
}

pager_count()   { pager_incidents "$1" | jq 'length'; }
pager_first()   { pager_incidents "$1" | jq -r 'sort_by(.openedAt) | .[0].id // empty'; }
pager_open()    { pager_incidents "$1" | jq '[.[] | select(.state | IN("Closed","Resolved","Expired","Suppressed") | not)] | length'; }

pager_incident() { _pager_curl "$PAGER_API/api/incidents/$1"; }
pager_state()    { pager_incident "$1" | jq -r '.state'; }

# Waits until both of the agent's addresses answer again. The release harness reaches the agent
# through a kubectl port-forward, which dies with the pod it picked: after a scenario that
# restarts or scales the agent, a webhook posted before the forward reconnects gets an empty
# reply and the alert is lost - v0.10.0-rc1's release gate failed P17 on exactly that, one
# scenario after P16. Any HTTP status from the hook will do; 000 means nothing answered.
pager_wait_agent() {
    local timeout="${1:-180}" code
    local deadline=$(( SECONDS + timeout ))
    while [ "$SECONDS" -lt "$deadline" ]; do
        code=$(curl -s -o /dev/null --max-time 5 -w '%{http_code}' "$PAGER_HOOK/webhooks/alertmanager" || true)
        if [ "${code:-000}" != "000" ] \
            && [ "$(pager_incidents "$(pager_name probe)" 2>/dev/null | jq -r type 2>/dev/null)" = "array" ]; then
            return 0
        fi
        sleep 3
    done
    return 1
}

# Waits until pager_count <alertname> reaches at least <n>. Returns non-zero on timeout.
pager_wait_count() {
    local name="$1" n="$2" timeout="${3:-60}" c
    local deadline=$(( SECONDS + timeout ))
    while [ "$SECONDS" -lt "$deadline" ]; do
        # Not `$(pager_count ...) || echo 0`: under pipefail a failed read prints jq's 0 AND the
        # fallback's, and `[` chokes on "0<newline>0".
        c=$(pager_count "$name" 2>/dev/null) || c=0
        [ "${c:-0}" -ge "$n" ] && return 0
        sleep 2
    done
    return 1
}

# Waits until incident <id> is in one of the given states.
pager_wait_state() {
    local id="$1" timeout="$2"; shift 2
    local deadline=$(( SECONDS + timeout )) s
    while [ "$SECONDS" -lt "$deadline" ]; do
        s=$(pager_state "$id" 2>/dev/null || true)
        for want in "$@"; do [ "$s" = "$want" ] && return 0; done
        sleep 2
    done
    return 1
}

# Waits until the investigation of incident <id> has ended one way or the other.
pager_wait_settled() {
    pager_wait_state "$1" "${2:-90}" Escalated Closed Resolved Suppressed AwaitingApproval Expired
}

# Teams messages that mention the incident: every card links to it by id.
#   pager_teams <incident-id>   -> array of {id, kind, conversation, edits, postedAt, editedAt, text}
pager_teams() {
    _pager_curl "$PAGER_STANDIN/teams/messages" \
        | jq -c --arg id "$1" '[.messages[] | select((.activity | tostring) | contains($id)) | del(.activity)]'
}

# Personal-chat alerts about the incident (the board is kind "channel" and is not an alert).
pager_alerts_sent() { pager_teams "$1" | jq '[.[] | select(.kind == "chat")] | length'; }

pager_teams_deletes() { _pager_curl "$PAGER_STANDIN/teams/messages" | jq '.deletes'; }

# Outbound webhook deliveries that mention the incident.
pager_received() {
    _pager_curl "$PAGER_STANDIN/received" \
        | jq -c --arg id "$1" '[.[] | select((.body | tostring) | contains($id))]'
}

# Model requests whose prompt contains <text> (an alert name, an incident id).
pager_llm() {
    _pager_curl "$PAGER_STANDIN/llm/requests" \
        | jq -c --arg t "$1" '[.[] | select(.text | contains($t))]'
}

pager_llm_hold()    { _pager_curl -X POST "$PAGER_STANDIN/llm/hold" >/dev/null; }
pager_llm_release() { _pager_curl -X POST "$PAGER_STANDIN/llm/release" >/dev/null; }

# Human acts, through the same API the console uses.
pager_close() {
    _pager_curl -o /dev/null -w '%{http_code}' -X POST "$PAGER_API/api/incidents/$1/close" \
        -H 'Content-Type: application/json' --data '{"closedBy":"pager-suite","reason":"closed by the pager suite"}'
}

pager_ack() {
    _pager_curl -o /dev/null -w '%{http_code}' -X POST "$PAGER_API/api/incidents/$1/acknowledge" \
        -H 'Content-Type: application/json' --data '{"actor":"pager-suite"}'
}

# Asserts, recording through common.sh's pass/fail.
#   want <name> <actual> <op> <expected>     op: -eq -ne -ge -le -gt -lt = !=
want() {
    local name="$1" actual="$2" op="$3" want="$4"
    case "$op" in
        =|!=) [ "$actual" "$op" "$want" ] ;;
        *)    [ "${actual:-0}" "$op" "$want" ] 2>/dev/null ;;
    esac && pass "$name" || fail "$name" "got '${actual}', wanted $op '${want}'"
}
