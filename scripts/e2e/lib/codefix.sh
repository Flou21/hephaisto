#!/usr/bin/env bash
# shellcheck shell=bash
#
# The code-fix tier (v0.9.0): steps shared by the kind nightly (`run.sh --codefix`) and the
# local Tilt runner (`scripts/e2e/codefix-local.sh`).
#
# Everything here goes through two seams the caller provides:
#
#   kc            kubectl, already pinned to the one cluster this run may touch
#   CF_API        base URL of the agent's console port, e.g. http://127.0.0.1:18100
#
# and records every assertion with pass/fail/skip from common.sh, so a run that dies half way
# still reports what it proved. Each refusal is asserted beside the thing that makes it mean
# something - "no Job for c13" is only worth reading next to "one Job for c15".

CF_APP_NS="${CF_APP_NS:-hephaisto}"
CF_CODER_NS="${CF_CODER_NS:-hephaisto-coder}"
CF_CHAOS_NS="${CF_CHAOS_NS:-hephaisto-chaos}"
CF_SA="${CF_SA:-hephaisto}"
CF_ACTOR="${CF_ACTOR:-e2e-harness}"

cf_get() {
    local path="$1" timeout="${2:-15}"
    curl -sS --max-time "$timeout" "${CF_API%/}${path}"
}

cf_post() {
    local path="$1" body="${2:-{\}}" timeout="${3:-30}"
    curl -sS --max-time "$timeout" -X POST -H 'Content-Type: application/json' -d "$body" \
        -w '\n%{http_code}' "${CF_API%/}${path}"
}

# --- RBAC: the grant exists in exactly one namespace ----------------------------------------

cf_assert_rbac() {
    local as="system:serviceaccount:${CF_APP_NS}:${CF_SA}"
    local verb

    for verb in create get list watch delete; do
        [ "$(kc auth can-i "$verb" jobs.batch -n "$CF_CODER_NS" --as "$as" 2>/dev/null)" = yes ] \
            && pass "rbac: can $verb jobs in $CF_CODER_NS" \
            || fail "rbac: can $verb jobs in $CF_CODER_NS"
    done

    [ "$(kc auth can-i get pods --subresource=log -n "$CF_CODER_NS" --as "$as" 2>/dev/null)" = yes ] \
        && pass "rbac: can read coder pod logs" || fail "rbac: can read coder pod logs"

    # The refusals. -A is the cluster-wide form; the chaos namespace is the actionable one.
    [ "$(kc auth can-i create jobs.batch -A --as "$as" 2>/dev/null)" = no ] \
        && pass "rbac: cannot create jobs cluster-wide" || fail "rbac: cannot create jobs cluster-wide"
    [ "$(kc auth can-i create jobs.batch -n "$CF_APP_NS" --as "$as" 2>/dev/null)" = no ] \
        && pass "rbac: cannot create jobs in $CF_APP_NS" || fail "rbac: cannot create jobs in $CF_APP_NS"
    [ "$(kc auth can-i create jobs.batch -n "$CF_CHAOS_NS" --as "$as" 2>/dev/null)" = no ] \
        && pass "rbac: cannot create jobs in $CF_CHAOS_NS" || fail "rbac: cannot create jobs in $CF_CHAOS_NS"
    [ "$(kc auth can-i create pods -A --as "$as" 2>/dev/null)" = no ] \
        && pass "rbac: cannot create pods anywhere" || fail "rbac: cannot create pods anywhere"
    [ "$(kc auth can-i get secrets -n "$CF_CODER_NS" --as "$as" 2>/dev/null)" = no ] \
        && pass "rbac: cannot read the coder Secret" || fail "rbac: cannot read the coder Secret"

    # The coder's own ServiceAccount is bound to nothing.
    local coder_as="system:serviceaccount:${CF_CODER_NS}:hephaisto-coder"
    [ "$(kc auth can-i --list --as "$coder_as" -n "$CF_CODER_NS" 2>/dev/null | grep -cvE '^(Resources|selfsubject|  )|selfsubject' || true)" -le 1 ] \
        && pass "rbac: the coder ServiceAccount holds nothing beyond discovery" \
        || fail "rbac: the coder ServiceAccount holds nothing beyond discovery" \
                "$(kc auth can-i --list --as "$coder_as" -n "$CF_CODER_NS" 2>&1 | head -5 | tr '\n' ' ')"
}

# --- incidents ------------------------------------------------------------------------------

# Newest incident whose owner (or target) is the given workload in the chaos namespace.
cf_incident_for() {
    local workload="$1" state="${2:-open}"
    cf_get "/api/incidents?state=${state}&limit=200" | jq -r --arg w "$workload" --arg ns "$CF_CHAOS_NS" '
        [ .[] | select(.namespace == $ns and ((.ownerName // "") == $w or (.targetName | startswith($w + "-")))) ]
        | sort_by(.openedAt) | last | .id // empty'
}

cf_incident_state() { cf_get "/api/incidents/$1" | jq -r '.state // .incident.state // empty'; }

cf_is_escalated() { [ "$(cf_incident_state "$1")" = "Escalated" ]; }

# --- attempts -------------------------------------------------------------------------------

cf_codefix() { cf_get "/api/incidents/$1/codefix"; }

cf_attempt_state() { cf_codefix "$1" | jq -r '.attempts[0].state // empty'; }

cf_attempt_in() {
    local incident="$1"; shift
    local s
    s=$(cf_attempt_state "$incident")
    for want in "$@"; do [ "$s" = "$want" ] && return 0; done
    return 1
}

cf_wait_attempt() {
    local incident="$1" timeout="$2"; shift 2
    wait_for "code fix of $incident to reach $*" "$timeout" cf_attempt_in "$incident" "$@"
}

cf_attempt_json() { cf_codefix "$1" | jq -c '.attempts[0]'; }

cf_evaluation() { cf_codefix "$1" | jq -c '.latestEvaluation'; }

# Asserts the pod a coder Job ran in: no ServiceAccount token, non-root, read-only root, a
# deadline, no retries, credentials only by reference.
cf_assert_job_spec() {
    local job="$1" spec
    spec=$(kc -n "$CF_CODER_NS" get job "$job" -o json 2>/dev/null) || { fail "job $job exists" "not found"; return 0; }
    pass "job $job exists"

    jq -e '.spec.template.spec.automountServiceAccountToken == false' <<<"$spec" >/dev/null \
        && pass "$job: no ServiceAccount token mounted" || fail "$job: no ServiceAccount token mounted"
    jq -e '.spec.template.spec.securityContext.runAsNonRoot == true and .spec.template.spec.securityContext.runAsUser == 64198' <<<"$spec" >/dev/null \
        && pass "$job: non-root, uid 64198" || fail "$job: non-root, uid 64198"
    jq -e '.spec.template.spec.containers[0].securityContext.readOnlyRootFilesystem == true' <<<"$spec" >/dev/null \
        && pass "$job: read-only root filesystem" || fail "$job: read-only root filesystem"
    jq -e '.spec.backoffLimit == 0 and (.spec.activeDeadlineSeconds // 0) > 0' <<<"$spec" >/dev/null \
        && pass "$job: no retries, a deadline" || fail "$job: no retries, a deadline"
    jq -e '[.spec.template.spec.containers[0].env[] | select(.name | test("TOKEN|KEY")) | select(.value != null)] | length == 0' <<<"$spec" >/dev/null \
        && pass "$job: no credential value in the spec" || fail "$job: no credential value in the spec"

    # And the pod itself, if it is still there: no projected token volume.
    local pod
    pod=$(kc -n "$CF_CODER_NS" get pods -l "job-name=$job" -o json 2>/dev/null | jq -c '.items[0] // empty')
    if [ -n "$pod" ]; then
        jq -e '[.spec.volumes[]? | select(.projected != null)] | length == 0' <<<"$pod" >/dev/null \
            && pass "$job: pod has no projected token volume" || fail "$job: pod has no projected token volume"
    fi
}

cf_decide() {
    local incident="$1" attempt="$2" verdict="$3" reason="${4:-}"
    cf_post "/api/incidents/$incident/codefix/$attempt/$verdict" \
        "$(jq -cn --arg a "$CF_ACTOR" --arg r "$reason" '{decidedBy:$a, reason:(if $r == "" then null else $r end)}')"
}

cf_request() {
    local incident="$1"
    cf_post "/api/incidents/$incident/codefix" "$(jq -cn --arg a "$CF_ACTOR" '{requestedBy:$a}')"
}

# The number of attempts and Jobs that exist for an incident.
cf_attempt_count() { cf_codefix "$1" | jq '.attempts | length'; }

cf_job_count() {
    local incident="$1"
    kc -n "$CF_CODER_NS" get jobs -l "hephaisto.dev/incident=$incident" -o json 2>/dev/null | jq '.items | length'
}

# --- the switch -----------------------------------------------------------------------------

cf_set_switch() {
    local mode="$1"
    kc -n "$CF_APP_NS" patch configmap "${CF_SWITCHES_CM:-hephaisto-switches}" --type merge \
        -p "$(jq -cn --arg m "$mode" '{data:{codeFixMode:$m}}')" >/dev/null
}

cf_mode_is() { [ "$(cf_get /api/codefixes/mode | jq -r '.effective')" = "$1" ]; }
