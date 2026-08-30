#!/usr/bin/env bash
# Outbound notifications: install the receiver, then assert the agent actually reaches it.
#
# The point of this phase is not "a message can be sent" - a unit test covers that. It is the
# one property that cannot be asserted anywhere else: a delivery survives the process that
# queued it. Everything up to notify_assert_outage_survived is setup for that assertion.
#
# The receiver is deliberately NOT called a sink: ISignalSink is the inbound seam behind the
# Alertmanager webhook, and reusing the word for the opposite direction is how somebody later
# reads one thing and applies it to the other.

RECEIVER_NS="hephaisto-obs"
RECEIVER_IMAGE="hephaisto/notification-receiver:dev"

# The in-cluster URL the agent posts to. Must match Notifications__Webhook__Url in
# values-e2e.yaml, or the agent delivers nowhere and every assertion below fails for a reason
# that has nothing to do with the agent.
RECEIVER_URL="http://notification-receiver.hephaisto-obs:8080/hooks/hephaisto"

# ------------------------------------------------------------------------------------------
# Install
# ------------------------------------------------------------------------------------------
notify_install() {
    say "building $RECEIVER_IMAGE"

    # Repo root as the context, like faulty-service: the project inherits Central Package
    # Management from the root props files, so restore fails with NU1008 without them.
    if ! docker build -q \
            -t "$RECEIVER_IMAGE" \
            -f "$REPO/infra/e2e/notification-receiver/Dockerfile" \
            "$REPO" >/dev/null; then
        warn "could not build $RECEIVER_IMAGE; the notify phase will skip"
        return 1
    fi

    if ! kind load docker-image "$RECEIVER_IMAGE" --name "$E2E_CLUSTER" >/dev/null 2>&1; then
        warn "could not load $RECEIVER_IMAGE into $E2E_CLUSTER; the notify phase will skip"
        return 1
    fi

    kc apply -f "$REPO/infra/e2e/notification-receiver.yaml" >/dev/null || return 1

    kc -n "$RECEIVER_NS" rollout status deploy/notification-receiver --timeout=120s >/dev/null 2>&1 \
        || { warn "the notification receiver never became ready"; return 1; }

    say "notification receiver is up at $RECEIVER_URL"
}

# Everything the receiver was sent, as JSON.
notify_received() {
    curl -sS --max-time 10 "http://127.0.0.1:$PF_PORT_RECEIVER/received" 2>/dev/null || echo '[]'
}

notify_count() {
    notify_received | jq 'length' 2>/dev/null || echo 0
}

notify_mode() {
    curl -sS --max-time 10 -X POST "http://127.0.0.1:$PF_PORT_RECEIVER/mode/$1" >/dev/null 2>&1
}

notify_reset() {
    curl -sS --max-time 10 -X DELETE "http://127.0.0.1:$PF_PORT_RECEIVER/received" >/dev/null 2>&1
}

# ------------------------------------------------------------------------------------------
# Assertions
# ------------------------------------------------------------------------------------------

# The agent said, at startup, that this is switched on. Without this a run in which
# notifications were misconfigured would report "0 delivered" identically to one in which the
# agent tried and failed - which is the ambiguity backlog #43 was about.
notify_assert_configured() {
    # --tail=-1, i.e. everything. This was --tail=400 and it failed on the first cluster run
    # while the agent was working perfectly: OutboundStartupReport logs once at startup, and by
    # the time this phase runs the agent has completed four investigations of a dozen steps
    # each, so the startup lines are hundreds of lines back. A window sized for a quiet agent
    # is not a window at all once the agent has been busy - and the symptom, "no such line in
    # the log", reads exactly like the product failing to emit it.
    #
    # OutboundStartupReportTests asserts the product side, so if this ever fails again the
    # answer is genuinely in the agent rather than here.
    local logs
    logs=$(kc -n "$APP_NS" logs deploy/hephaisto --tail=-1 2>/dev/null || true)

    if printf '%s' "$logs" | grep -q "Outbound webhook channel is ON"; then
        pass "the agent reports the outbound channel is on"
    else
        fail "the agent reports the outbound channel is on" \
             "no 'Outbound webhook channel is ON' in $(printf '%s' "$logs" | wc -l | tr -d ' ') log lines"
    fi

    if printf '%s' "$logs" | grep -q "Notifications are ON"; then
        pass "the agent reports notification routes are configured"
    else
        fail "the agent reports notification routes are configured" \
             "no 'Notifications are ON' line; routes may be empty"
    fi
}

# Something escalated, and it arrived. The chaos phase has already run, so incidents exist.
notify_assert_delivered() {
    wait_for "a notification to arrive at the receiver" 240 \
        bash -c '[ "$(curl -sS --max-time 10 "http://127.0.0.1:'"$PF_PORT_RECEIVER"'/received/count" 2>/dev/null || echo 0)" -gt 0 ]' \
        || { fail "a notification reaches the receiver" "nothing arrived in 240s"; return 0; }

    pass "a notification reaches the receiver"

    local body
    body=$(notify_received)

    # Each delivery carries the id the receiver can dedupe on, and it must be non-empty -
    # at-least-once delivery is only safe for a receiver that can tell a repeat from a new one.
    if [ "$(printf '%s' "$body" | jq '[.[] | select(.deliveryId != "")] | length')" -gt 0 ]; then
        pass "deliveries carry a stable delivery id"
    else
        fail "deliveries carry a stable delivery id" "X-Hephaisto-Delivery-Id was empty"
    fi

    # The whole purpose of the message: a link somebody can open.
    if [ "$(printf '%s' "$body" | jq '[.[] | select(.body.links.incident != null)] | length')" -gt 0 ]; then
        pass "deliveries carry a link back to the incident"
    else
        fail "deliveries carry a link back to the incident" \
             "no links.incident in any payload - Notifications:BaseUrl may be unset"
    fi

    # Signing is off in values-e2e - the key would be a secretKeyRef and the chart has no
    # Secret template, so turning it on means minting another Secret for a property that unit
    # tests already cover. Skipped with a reason rather than asserted and failed, and it still
    # checks the header when it IS present, so enabling it later needs no change here.
    if [ "$(printf '%s' "$body" | jq '[.[] | select(.signature != "")] | length')" -gt 0 ]; then
        if [ "$(printf '%s' "$body" | jq '[.[] | select(.signature | startswith("sha256="))] | length')" -gt 0 ]; then
            pass "deliveries are signed"
        else
            fail "deliveries are signed" "a signature header was present but not sha256="
        fi
    else
        skip "deliveries are signed" "notifications.webhook.signed is false in values-e2e"
    fi

    # The event names an incident the API also knows about, so this is the agent's own
    # notification rather than something left over in the receiver.
    local id
    id=$(printf '%s' "$body" | jq -r '[.[] | .body.incident.id // empty][0] // ""')

    if [ -n "$id" ] && api_json "/api/incidents/$id" | jq -e '.id' >/dev/null 2>&1; then
        pass "the delivered incident exists in the API"
    else
        fail "the delivered incident exists in the API" "no incident id in the payload, or the API does not know it"
    fi
}

# THE assertion this phase exists for.
#
# Take the receiver down, restart the agent, bring the receiver back, and require the delivery
# to arrive anyway. An outbox that has never survived a restart is an outbox in name only, and
# this is the only place that can be shown - a unit test can prove the row is written, and
# nothing but a real process death can prove it is still there afterwards.
notify_assert_outage_survived() {
    notify_reset

    say "taking the receiver down (503) and restarting the agent mid-flight"
    notify_mode fail

    # Force an escalation to queue while nothing can accept it. Re-investigating an existing
    # incident is the cheapest way to produce a fresh terminal transition without waiting for
    # another fixture to fire.
    local id
    id=$(api_array "/api/incidents?limit=50" | jq -r '[.[] | select(.state == "Escalated")][0].id // ""')

    if [ -z "$id" ]; then
        skip "a delivery survives an agent restart" "no escalated incident to re-drive"
        notify_mode ok
        return 0
    fi

    api -X POST "/api/incidents/$id/reinvestigate" >/dev/null 2>&1 || true

    # Let the dispatcher try at least once against the failing receiver, so the row is
    # genuinely mid-flight rather than merely queued.
    wait_for "the agent to attempt a delivery against the failing receiver" 120 \
        bash -c 'kubectl --kubeconfig "$E2E_KUBECONFIG" -n hephaisto-obs logs deploy/notification-receiver --tail=200 2>/dev/null | grep -q "REFUSED"' \
        || warn "no refused delivery observed; the restart test may be weaker than intended"

    say "restarting the agent"
    kc -n "$APP_NS" rollout restart deploy/hephaisto >/dev/null 2>&1
    kc -n "$APP_NS" rollout status deploy/hephaisto --timeout=180s >/dev/null 2>&1 \
        || { fail "a delivery survives an agent restart" "the agent did not come back"; return 0; }

    # The forward died with the old pod.
    port_forward hephaisto "$APP_NS" svc/hephaisto "$PF_PORT_APP" 8080 || true

    say "bringing the receiver back"
    notify_mode ok

    if wait_for "the queued delivery to arrive after the restart" 300 \
            bash -c '[ "$(curl -sS --max-time 10 "http://127.0.0.1:'"$PF_PORT_RECEIVER"'/received/count" 2>/dev/null || echo 0)" -gt 0 ]'; then
        pass "a delivery survives an agent restart"
    else
        fail "a delivery survives an agent restart" \
             "nothing arrived in 300s after the receiver recovered - the outbox did not replay"
    fi
}
