#!/usr/bin/env bash
# shellcheck shell=bash
#
# The investigate-in-a-Job tier (v0.12.0 F5): scenarios shared by the local Tilt runner
# (`scripts/e2e/investigate-local.sh`) and, later, the kind nightly.
#
# Needs lib/common.sh and lib/codefix.sh sourced first: this reuses cf_get, cf_post, cf_trigger,
# cf_incident_for, cf_assert_rbac and cf_assert_job_spec rather than growing a second copy of
# each. The seams are the same two: `kc`, pinned to the one cluster this run may touch, and
# CF_API, the agent's console port.
#
# Every scenario is a function `scenario_I<n>`. The runner decides what red and green mean
# (scripts/e2e/investigate/KNOWN_RED); a scenario only records what it saw.
#
# Two things every scenario relies on and none of them sets up:
#
#   - codeFixMode is off for the whole run. c15 and c19 escalate as application bugs, and on the
#     dev cluster a code fix would start a coder Job with the REAL SDK. The runner switches it
#     off before the first scenario and restores it on exit.
#   - The fake SDK. A Job-executor scenario is $0 only if the investigator runs scripted
#     (tilt_config.json "investigator-sdk": "fake"); preflight refuses otherwise.

IV_LABEL="${IV_LABEL:-hephaisto-investigator}"
IV_SWITCH_KEY="${IV_SWITCH_KEY:-investigationExecutor}"
IV_SVC="${IV_SVC:-hephaisto}"
IV_PORT="${IV_PORT:-8084}"

# --- switches ---------------------------------------------------------------------------------

iv_switch_get() {
    kc -n "$CF_APP_NS" get configmap "${CF_SWITCHES_CM:-hephaisto-switches}" -o json 2>/dev/null \
        | jq -r --arg k "$1" '.data[$k] // ""'
}

iv_switch_set() {
    local key="$1" value="$2"
    if [ -z "$value" ]; then
        kc -n "$CF_APP_NS" patch configmap "${CF_SWITCHES_CM:-hephaisto-switches}" --type json \
            -p "$(jq -cn --arg k "/data/$key" '[{op:"remove", path:$k}]')" >/dev/null 2>&1 || true
    else
        kc -n "$CF_APP_NS" patch configmap "${CF_SWITCHES_CM:-hephaisto-switches}" --type merge \
            -p "$(jq -cn --arg k "$key" --arg v "$value" '{data:{($k):$v}}')" >/dev/null
    fi
}

iv_set_executor() { iv_switch_set "$IV_SWITCH_KEY" "$1"; }

# What the agent resolves. The ConfigMap is projected into the pod, and the kubelet takes up to
# a minute to refresh a projected volume - so a scenario that flips the switch waits for THIS,
# never for the patch to return.
iv_executor() { cf_get /api/investigations/executor | jq -r '.effective // empty' 2>/dev/null; }

iv_executor_is() { [ "$(iv_executor)" = "$1" ]; }

iv_use_executor() {
    local want="$1" effective="$2"
    iv_set_executor "$want"
    wait_for "the executor to resolve to $effective" 120 iv_executor_is "$effective"
}

# --- incidents and investigations -------------------------------------------------------------

# The newest incident on a chaos workload, in any state: a re-investigation reopens a closed or
# escalated one, so the harness never waits for a fixture to raise a fresh alert.
iv_incident_on() {
    local workload="$1"
    cf_get "/api/incidents?limit=200" | jq -r --arg w "$workload" --arg ns "$CF_CHAOS_NS" '
        [ .[] | select(.namespace == $ns and ((.ownerName // "") == $w or (.targetName | startswith($w + "-")))) ]
        | sort_by(.openedAt) | last | .id // empty'
}

# An incident on the workload, bringing the fixture up first when there is none yet.
iv_ensure_incident() {
    local workload="$1" fixture="$2" incident
    incident=$(iv_incident_on "$workload")
    if [ -z "$incident" ]; then
        say "no incident on $workload yet - triggering $fixture" >&2
        cf_trigger "$fixture"
        find_it() { incident=$(iv_incident_on "$workload"); [ -n "$incident" ]; }
        wait_for "an incident on $workload" 600 find_it >&2 || true
    fi
    printf '%s' "$incident"
}

iv_incident() { cf_get "/api/incidents/$1"; }

iv_investigation_count() { iv_incident "$1" | jq '.investigations | length'; }

iv_latest_investigation() { iv_incident "$1" | jq -c '.investigations | sort_by(.startedAt) | last // empty'; }

iv_is_running() { [ "$(iv_incident "$1" | jq -r '.inProgress != null')" = true ]; }

iv_reinvestigate() {
    cf_post "/api/incidents/$1/reinvestigate" "$(jq -cn --arg a "$CF_ACTOR" '{requestedBy:$a}')"
}

# Re-investigates and waits for the new investigation to be written. Prints its JSON; empty on
# timeout. The count is read before the request so an investigation that finishes between the
# POST and the first poll is still seen.
iv_investigate_once() {
    local incident="$1" timeout="$2" before out code
    before=$(iv_investigation_count "$incident")
    out=$(iv_reinvestigate "$incident")
    code=$(tail -1 <<<"$out")
    if [ "$code" != 200 ] && [ "$code" != 202 ]; then
        warn "re-investigating $incident answered $code: $(head -1 <<<"$out")"
        return 1
    fi

    done_after() { [ "$(iv_investigation_count "$incident")" -gt "$before" ] && ! iv_is_running "$incident"; }
    wait_for "a new investigation of $incident" "$timeout" done_after >&2 || return 1
    iv_latest_investigation "$incident"
}

iv_jobs() {
    kc -n "$CF_CODER_NS" get jobs -l "app.kubernetes.io/name=$IV_LABEL,hephaisto.dev/incident=$1" -o json 2>/dev/null
}

iv_job_count() { iv_jobs "$1" | jq '.items | length'; }

iv_job_names() { iv_jobs "$1" | jq -r '.items[].metadata.name'; }

iv_running_job() {
    iv_jobs "$1" | jq -r '[.items[] | select((.status.active // 0) > 0)] | .[0].metadata.name // empty'
}

# Tool names, in order, of an investigation's recorded tool calls.
iv_tools() { jq -r '[.steps[]? | select(.kind == "ToolCall") | .toolName] | join(",")' <<<"$1"; }

# The keys a consumer of GET /api/incidents/{id} reads, at every level that has them.
iv_shape() {
    jq -c '{investigation: (del(.steps, .findings) | keys),
            step: ((.steps // [])[0] // {} | keys),
            finding: ((.findings // [])[0] // {} | del(.evidence) | keys),
            evidence: (((.findings // [])[0].evidence // [])[0] // {} | keys)}' <<<"$1"
}

# --- scenarios --------------------------------------------------------------------------------

# I0: what every other scenario stands on.
scenario_I0() {
    curl -sf --max-time 10 "$CF_API/healthz" >/dev/null \
        && pass "the agent is healthy" || fail "the agent is healthy" "$CF_API/healthz"

    [ "$(iv_switch_get codeFixMode)" = off ] \
        && pass "codeFixMode is off for this run" || fail "codeFixMode is off for this run"

    cf_assert_rbac
}

# I1: the baseline. With the executor left alone the agent investigates in-process exactly as
# v0.11 did - no Job, no executor other than in-process.
scenario_I1() {
    iv_use_executor inprocess InProcess >/dev/null 2>&1 || iv_set_executor inprocess
    local incident inv
    incident=$(iv_ensure_incident shop-api c15-null-deref)
    [ -n "$incident" ] || { fail "an incident on shop-api exists"; return 0; }
    pass "an incident on shop-api exists" "$incident"

    local jobs_before
    jobs_before=$(iv_job_count "$incident")

    inv=$(iv_investigate_once "$incident" 1500) || { fail "an in-process investigation completed"; return 0; }
    record_json I1-investigation "$inv"
    pass "an in-process investigation completed" "$(jq -r '.terminationReason + ", " + (.stepsUsed|tostring) + " steps"' <<<"$inv")"
    IV_INPROCESS_SHAPE=$(iv_shape "$inv")

    jq -e '(.executor // "InProcess") == "InProcess"' <<<"$inv" >/dev/null \
        && pass "its executor is in-process" || fail "its executor is in-process" "$(jq -r .executor <<<"$inv")"
    jq -e '.stepsUsed >= 1' <<<"$inv" >/dev/null \
        && pass "it recorded steps" || fail "it recorded steps"
    [ "$(iv_job_count "$incident")" = "$jobs_before" ] \
        && pass "no investigator Job was created" || fail "no investigator Job was created"
}

# I2: the executor axis. The ConfigMap arm takes the executor down to in-process and back; a typo
# reads as in-process - never an error and never Job; and with the key gone the env arm decides.
scenario_I2() {
    local view
    view=$(cf_get /api/investigations/executor)
    record_json I2-executor "$view"
    jq -e '.effective' <<<"$view" >/dev/null 2>&1 \
        || { fail "GET /api/investigations/executor answers" "$view"; return 0; }
    pass "GET /api/investigations/executor answers" "$(jq -r .explanation <<<"$view")"
    jq -e '.enabled == true' <<<"$view" >/dev/null \
        && pass "investigation.job is enabled on this stack" || fail "investigation.job is enabled on this stack" "tilt_config.json investigator: true"

    iv_set_executor inprocess
    wait_for "inprocess to resolve to InProcess" 120 iv_executor_is InProcess \
        && pass "the ConfigMap takes it down to in-process" || fail "the ConfigMap takes it down to in-process" "$(iv_executor)"

    iv_set_executor banana
    wait_for "a malformed switch to resolve to InProcess" 120 iv_executor_is InProcess \
        && pass "a malformed switch is in-process" || fail "a malformed switch is in-process" "$(iv_executor)"

    iv_set_executor job
    wait_for "job to resolve to Job" 120 iv_executor_is Job \
        && pass "job is Job" || fail "job is Job" "$(iv_executor)"

    iv_set_executor ""
    wait_for "the env arm to decide once the key is gone" 120 iv_executor_is Job \
        && pass "with the key gone the env arm (job) decides" || fail "with the key gone the env arm (job) decides" "$(iv_executor)"

    iv_set_executor inprocess
}

# I3: the investigator port refuses anyone without a live run's token, and exists nowhere else.
scenario_I3() {
    local pf_port=18084 pf_pid code
    kc -n "$CF_APP_NS" port-forward "svc/$IV_SVC" "$pf_port:$IV_PORT" >/dev/null 2>&1 &
    pf_pid=$!
    sleep 3

    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -X POST -H 'Content-Type: application/json' \
        -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' "http://127.0.0.1:$pf_port/investigate")
    [ "$code" = 401 ] && pass "no token is refused" || fail "no token is refused" "$code"

    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -X POST -H 'Content-Type: application/json' \
        -H "Authorization: Bearer $(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 48)" \
        -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' "http://127.0.0.1:$pf_port/investigate")
    [ "$code" = 401 ] && pass "an unknown token is refused" || fail "an unknown token is refused" "$code"

    kill "$pf_pid" 2>/dev/null || true; wait "$pf_pid" 2>/dev/null || true

    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -X POST "$CF_API/investigate")
    [ "$code" = 404 ] && pass "/investigate does not answer on the console port" \
        || fail "/investigate does not answer on the console port" "$code"
}

# I4: the Job path, end to end, on c15. The investigator runs scripted; what is asserted is
# everything around it: Hephaisto launched one Job, served its tool calls, recorded them as
# steps, and grounded its conclusion against those steps.
scenario_I4() {
    iv_use_executor job Job || { fail "the executor is Job"; return 0; }
    pass "the executor is Job"

    local incident inv
    incident=$(iv_ensure_incident shop-api c15-null-deref)
    [ -n "$incident" ] || { fail "an incident on shop-api exists"; return 0; }
    IV_JOB_INCIDENT="$incident"

    local jobs_before
    jobs_before=$(iv_job_count "$incident")

    inv=$(iv_investigate_once "$incident" 900) || { fail "a Job investigation completed"; iv_set_executor inprocess; return 0; }
    record_json I4-investigation "$inv"
    IV_JOB_INVESTIGATION="$inv"
    pass "a Job investigation completed" "$(jq -r '.terminationReason' <<<"$inv")"

    [ "$(iv_job_count "$incident")" -gt "$jobs_before" ] \
        && pass "an investigator Job ran for it" || fail "an investigator Job ran for it"
    jq -e '.executor == "Job"' <<<"$inv" >/dev/null \
        && pass "its executor is Job" || fail "its executor is Job" "$(jq -r .executor <<<"$inv")"
    jq -e '.terminationReason == "Concluded"' <<<"$inv" >/dev/null \
        && pass "it concluded" || fail "it concluded" "$(jq -r '.terminationReason + " " + (.error // "")' <<<"$inv")"

    local tools
    tools=$(iv_tools "$inv")
    [[ ",$tools," == *",get_pod_logs,"* ]] && [[ ",$tools," == *",conclude,"* ]] \
        && pass "Hephaisto recorded the Job's tool calls as steps" "$tools" \
        || fail "Hephaisto recorded the Job's tool calls as steps" "$tools"
    jq -e '[.steps[]? | select(.toolName == "get_pod_logs") | .toolServer] | all(. == "kubernetes")' <<<"$inv" >/dev/null \
        && pass "the tool calls ran in Hephaisto's own Kubernetes tools" \
        || fail "the tool calls ran in Hephaisto's own Kubernetes tools"
    jq -e '[.steps[]? | select(.kind == "ToolCall" and .toolName != "conclude")] | all(.resultDigest | startswith("[step "))' <<<"$inv" >/dev/null \
        && pass "every tool result carried its step header" || fail "every tool result carried its step header"

    jq -e '[.findings[]? | select(.isPrimary)] | length == 1' <<<"$inv" >/dev/null \
        && pass "one primary finding survived grounding" || fail "one primary finding survived grounding" "$(jq -c '[.findings[]? | {category, isPrimary}]' <<<"$inv")"
    jq -e '[.findings[]? | select(.isPrimary) | .evidence | length] | .[0] >= 1' <<<"$inv" >/dev/null \
        && pass "it cites evidence" || fail "it cites evidence"
    jq -e '[.findings[]? | select(.isPrimary) | .category] | .[0] == "application"' <<<"$inv" >/dev/null \
        && pass "the finding is an application bug" || fail "the finding is an application bug"
    jq -e '(.modelId // "") != ""' <<<"$inv" >/dev/null \
        && pass "the model is recorded" "$(jq -r .modelId <<<"$inv")" || fail "the model is recorded"

    iv_set_executor inprocess
}

# I5: the investigator pod is as sealed as a coder's, and carries only what it needs.
scenario_I5() {
    local incident="${IV_JOB_INCIDENT:-$(iv_incident_on shop-api)}" job spec
    job=$(iv_job_names "$incident" | tail -1)
    [ -n "$job" ] || { fail "an investigator Job exists to inspect"; return 0; }

    cf_assert_job_spec "$job"

    spec=$(kc -n "$CF_CODER_NS" get job "$job" -o json)
    jq -e '.spec.template.metadata.labels["app.kubernetes.io/name"] == "hephaisto-investigator"' <<<"$spec" >/dev/null \
        && pass "$job: labelled as an investigator, not a coder" || fail "$job: labelled as an investigator, not a coder"
    jq -e '[.spec.template.spec.containers[0].env[] | select(.name == "NUGET_GITHUB_TOKEN")] | length == 0' <<<"$spec" >/dev/null \
        && pass "$job: no NuGet token" || fail "$job: no NuGet token"

    local cm
    cm=$(jq -r '.spec.template.spec.volumes[] | select(.configMap != null) | .configMap.name' <<<"$spec" | head -1)
    if kc -n "$CF_CODER_NS" get configmap "$cm" >/dev/null 2>&1; then
        kc -n "$CF_CODER_NS" get configmap "$cm" -o json \
            | jq -e --arg j "$job" '.metadata.ownerReferences[]? | select(.kind == "Job" and .name == $j)' >/dev/null \
            && pass "$job: its request ConfigMap is owned by the Job" || fail "$job: its request ConfigMap is owned by the Job"
    else
        skip "$job: its request ConfigMap is owned by the Job" "already collected with the Job"
    fi
}

# I6: opening a route for investigators left the coders exactly as sealed as they were.
scenario_I6() {
    local coder inv agent
    coder=$(kc -n "$CF_CODER_NS" get networkpolicy -o json | jq -c '.items[] | select(.spec.podSelector.matchLabels["app.kubernetes.io/name"] == "hephaisto-coder")')
    [ -n "$coder" ] || { fail "the coder NetworkPolicy exists"; return 0; }
    pass "the coder NetworkPolicy exists"
    jq -e '[.spec.egress[].ports[]?.port] | map(tostring) | all(. == "53" or . == "3128")' <<<"$coder" >/dev/null \
        && pass "coders still reach only DNS and the proxy" || fail "coders still reach only DNS and the proxy" "$(jq -c '[.spec.egress[].ports]' <<<"$coder")"

    inv=$(kc -n "$CF_CODER_NS" get networkpolicy -o json | jq -c '.items[] | select(.spec.podSelector.matchLabels["app.kubernetes.io/name"] == "hephaisto-investigator")')
    [ -n "$inv" ] && pass "investigators have their own NetworkPolicy" || { fail "investigators have their own NetworkPolicy"; return 0; }
    jq -e --arg p "$IV_PORT" '[.spec.egress[].ports[]?.port | tostring] | index($p)' <<<"$inv" >/dev/null \
        && pass "investigators may reach the investigator port" || fail "investigators may reach the investigator port"

    agent=$(kc -n "$CF_APP_NS" get networkpolicy -o json | jq -c --arg p "$IV_PORT" '[.items[].spec.ingress[]? | select([.ports[]?.port | tostring] | index($p))]')
    jq -e 'length >= 1 and all(.[]; (.from | length) >= 1 and all(.from[]; .podSelector.matchLabels["app.kubernetes.io/name"] == "hephaisto-investigator"))' <<<"$agent" >/dev/null \
        && pass "the agent admits only investigators to that port" || fail "the agent admits only investigators to that port" "$agent"
}

# I7: a Job investigation reads, to every consumer of the API, like an in-process one.
scenario_I7() {
    local job_shape inproc_shape
    [ -n "${IV_JOB_INVESTIGATION:-}" ] || { fail "a Job investigation from I4 to compare" "run I4 first"; return 0; }
    job_shape=$(iv_shape "$IV_JOB_INVESTIGATION")

    if [ -z "${IV_INPROCESS_SHAPE:-}" ]; then
        # Any in-process investigation with findings will do; I1 did not run in this invocation.
        local any
        any=$(cf_get "/api/incidents?limit=200" | jq -r '.[] | select(.investigationCount > 0) | .id' | while read -r id; do
            iv_incident "$id" | jq -c '.investigations[] | select((.executor // "InProcess") == "InProcess" and (.findings | length) > 0)' | head -1
        done | head -1)
        [ -n "$any" ] && IV_INPROCESS_SHAPE=$(iv_shape "$any")
    fi
    [ -n "${IV_INPROCESS_SHAPE:-}" ] || { skip "the shapes match" "no in-process investigation with findings to compare"; return 0; }
    inproc_shape="$IV_INPROCESS_SHAPE"
    record_json I7-shapes "$(jq -cn --argjson j "$job_shape" --argjson i "$inproc_shape" '{job:$j, inprocess:$i}')"

    [ "$(jq -c .investigation <<<"$job_shape")" = "$(jq -c .investigation <<<"$inproc_shape")" ] \
        && pass "the investigation has the same fields" || fail "the investigation has the same fields"
    [ "$(jq -c .step <<<"$job_shape")" = "$(jq -c .step <<<"$inproc_shape")" ] \
        && pass "a step has the same fields" || fail "a step has the same fields"
    local jf if_
    jf=$(jq -c .finding <<<"$job_shape"); if_=$(jq -c .finding <<<"$inproc_shape")
    [ "$jf" = "$if_" ] || [ "$if_" = "[]" ] \
        && pass "a finding has the same fields" || fail "a finding has the same fields" "$jf vs $if_"
}

# I8: a Job that disappears mid-run does not cost the incident its investigation: the agent
# falls back to investigating in-process and says so.
scenario_I8() {
    iv_use_executor job Job || { fail "the executor is Job"; return 0; }

    local incident before job inv
    incident=$(iv_ensure_incident catalog-api c19-injection)
    [ -n "$incident" ] || { fail "an incident on catalog-api exists"; iv_set_executor inprocess; return 0; }
    before=$(iv_investigation_count "$incident")

    # catalog-api's script is the slow one: the Job is still running when it is deleted.
    iv_reinvestigate "$incident" >/dev/null
    find_job() { job=$(iv_running_job "$incident"); [ -n "$job" ]; }
    wait_for "a running investigator Job for catalog-api" 180 find_job \
        || { fail "a running investigator Job for catalog-api"; iv_set_executor inprocess; return 0; }
    pass "a running investigator Job for catalog-api" "$job"

    kc -n "$CF_CODER_NS" delete job "$job" --wait=false >/dev/null
    say "deleted $job mid-run"

    done_after() { [ "$(iv_investigation_count "$incident")" -gt "$before" ] && ! iv_is_running "$incident"; }
    wait_for "the investigation to finish after its Job was deleted" 1500 done_after \
        || { fail "the investigation finished anyway"; iv_set_executor inprocess; return 0; }
    inv=$(iv_latest_investigation "$incident")
    record_json I8-investigation "$inv"
    pass "the investigation finished anyway" "$(jq -r .terminationReason <<<"$inv")"

    jq -e '.executor == "JobFallback"' <<<"$inv" >/dev/null \
        && pass "it says it fell back to in-process" || fail "it says it fell back to in-process" "$(jq -r .executor <<<"$inv")"
    jq -e '[.steps[]? | select(.kind == "LlmTurn")] | length >= 1' <<<"$inv" >/dev/null \
        && pass "the in-process model took over" || fail "the in-process model took over"

    iv_set_executor inprocess
}

# I9: a second investigation while the one Job slot is taken runs in-process instead of waiting.
scenario_I9() {
    iv_use_executor job Job || { fail "the executor is Job"; return 0; }

    local slow fast job inv before
    slow=$(iv_ensure_incident catalog-api c19-injection)
    fast=$(iv_ensure_incident shop-api c15-null-deref)
    [ -n "$slow" ] && [ -n "$fast" ] || { fail "incidents on catalog-api and shop-api exist"; iv_set_executor inprocess; return 0; }

    iv_reinvestigate "$slow" >/dev/null
    find_job() { job=$(iv_running_job "$slow"); [ -n "$job" ]; }
    wait_for "the slow Job to hold the slot" 180 find_job || { fail "the slow Job holds the slot"; iv_set_executor inprocess; return 0; }
    pass "the slow Job holds the slot" "$job"

    before=$(iv_investigation_count "$fast")
    inv=$(iv_investigate_once "$fast" 1500) || { fail "the second investigation completed"; iv_set_executor inprocess; return 0; }
    record_json I9-investigation "$inv"
    jq -e '(.executor // "InProcess") == "InProcess"' <<<"$inv" >/dev/null \
        && pass "the overflow ran in-process" || fail "the overflow ran in-process" "$(jq -r .executor <<<"$inv")"
    [ "$(iv_investigation_count "$fast")" -eq $((before + 1)) ] \
        && pass "it was investigated once" || fail "it was investigated once"

    # Leave nothing running for the next scenario.
    kc -n "$CF_CODER_NS" delete job "$job" --wait=false >/dev/null 2>&1 || true
    iv_set_executor inprocess
}

# I10: an agent restart in the middle of a Job run leaves one investigation and no Job behind.
scenario_I10() {
    iv_use_executor job Job || { fail "the executor is Job"; return 0; }

    local incident before job
    incident=$(iv_ensure_incident catalog-api c19-injection)
    before=$(iv_investigation_count "$incident")

    iv_reinvestigate "$incident" >/dev/null
    find_job() { job=$(iv_running_job "$incident"); [ -n "$job" ]; }
    wait_for "a running investigator Job" 180 find_job || { fail "a running investigator Job"; iv_set_executor inprocess; return 0; }
    pass "a running investigator Job" "$job"

    local old_pod
    old_pod=$(kc -n "$CF_APP_NS" get pods -l app.kubernetes.io/name=hephaisto -o jsonpath='{.items[0].metadata.name}')
    kc -n "$CF_APP_NS" delete pod "$old_pod" --wait=false >/dev/null
    say "deleted the agent pod $old_pod mid-run"

    # A different pod, Ready, answering - not the old one still draining.
    replaced() {
        ! kc -n "$CF_APP_NS" get pod "$old_pod" >/dev/null 2>&1 \
            && [ "$(kc -n "$CF_APP_NS" get pods -l app.kubernetes.io/name=hephaisto -o json \
                   | jq '[.items[] | select(.status.conditions[]? | select(.type == "Ready" and .status == "True"))] | length')" -ge 1 ] \
            && curl -sf --max-time 5 "$CF_API/healthz" >/dev/null
    }
    wait_for "a new agent pod to be healthy" 900 replaced \
        && pass "a new agent pod came up" || { fail "a new agent pod came up"; return 0; }

    gone() { ! kc -n "$CF_CODER_NS" get job "$job" >/dev/null 2>&1 || \
        [ "$(kc -n "$CF_CODER_NS" get job "$job" -o json | jq -r '(.status.active // 0) == 0')" = true ]; }
    # Deleted by the old agent on its way down, or by the new one's sweeper - either is correct;
    # what must not happen is a Job running on with no run to answer to.
    wait_for "the orphaned Job to stop" 600 gone \
        && pass "the orphaned Job stopped" || fail "the orphaned Job stopped"

    done_after() { [ "$(iv_investigation_count "$incident")" -gt "$before" ] && ! iv_is_running "$incident"; }
    wait_for "the requeued investigation to finish" 1500 done_after \
        && pass "the incident was investigated after the restart" || fail "the incident was investigated after the restart"
    [ "$(iv_investigation_count "$incident")" -eq $((before + 1)) ] \
        && pass "exactly one investigation was written" || fail "exactly one investigation was written" "$before -> $(iv_investigation_count "$incident")"

    iv_set_executor inprocess
}

# I11: with source access on, the investigator reads the running revision's code and names the
# line, and the code reference never counts as grounding evidence.
scenario_I11() {
    iv_use_executor job Job || { fail "the executor is Job"; return 0; }

    local incident inv
    incident=$(iv_ensure_incident shop-api c15-null-deref)
    inv=$(iv_investigate_once "$incident" 900) || { fail "a Job investigation with source completed"; iv_set_executor inprocess; return 0; }
    record_json I11-investigation "$inv"
    pass "a Job investigation with source completed"

    jq -e '[.findings[]? | select(.isPrimary) | .codeRefs[]? | select(.path | endswith("Startup/Endpoints.cs"))] | length >= 1' <<<"$inv" >/dev/null \
        && pass "the primary finding names src/Shop.Api/Startup/Endpoints.cs" \
        || fail "the primary finding names src/Shop.Api/Startup/Endpoints.cs" "$(jq -c '[.findings[]?.codeRefs]' <<<"$inv")"
    jq -e '[.findings[]? | select(.isPrimary) | .codeRefs[]? | .ref] | all(test("^[0-9a-f]{7,40}$"))' <<<"$inv" >/dev/null \
        && pass "the reference is pinned to a commit" || fail "the reference is pinned to a commit"
    jq -e '[.findings[]? | .evidence[]? | .stepId] | length >= 1' <<<"$inv" >/dev/null \
        && pass "grounding evidence is still tool steps" || fail "grounding evidence is still tool steps"

    iv_set_executor inprocess
}

IV_SCENARIOS="I0 I1 I2 I3 I4 I5 I6 I7 I8 I9 I10 I11"
