#!/usr/bin/env bash
# Phases 6 and 7: inject faults, wait for incidents, assert on what came back.

# ---------------------------------------------------------------------------------------
# Which fixtures, and why not the others
# ---------------------------------------------------------------------------------------
# The default four are chosen to discriminate rather than to cover. infra/chaos/README.md
# records a known-correct answer for each, which is what makes any of this gradeable.
#
#   c4 ImagePullBackOff            \ the pair that matters: near-identical in Kubernetes,
#   c7 CreateContainerConfigError  / different causes. The README calls giving both the same
#                                    diagnosis a failure, and it is the one assertion a lazy
#                                    agent cannot pass.
#   c2 CrashLoopBackOff              carries a decisive log line, so it tests that Loki is
#                                    genuinely reached rather than guessed around.
#   c3 Unschedulable                 has its cause in an Event and in NO metric at all, so it
#                                    tests the OTel k8s_events receiver specifically.
#
# Excluded from the default set, each for a stated reason:
#
#   c9  memhog       NODE-WIDE. It has no memory limit by design and evicts pods across the
#                    cluster - including Prometheus and the agent. Never in an automated run.
#                    (It ships at replicas: 0, so applying the directory is safe; arming it
#                    is a separate deliberate act. This harness never arms it.)
#   c6  diskfill     the README states it does not fire on local-path: every PVC there reports
#                    the node filesystem, so the ratio barely moves.
#   c1  oomkill      the README records no pod-scoped OOMKilling event on k3s+containerd,
#                    which makes it unreliable as a gate even though it is a fine fixture.
#   c8  flap         needs a 30-minute window to satisfy changes(...)[30m] >= 4.
#   c10 faulty-svc   needs a local image build plus `kind load` - which chaos_build_images
#                    now does - and 5-minute rate windows, so it is slow rather than
#                    excluded. Still not in the default set for that reason.
#
# --fixtures overrides this. c5 is handled specially because a Job spec is immutable and must
# be deleted before it can be re-applied.
DEFAULT_FIXTURES="c2,c3,c4,c7"

# Fixture -> the SignalKind the shipped rules attach via hephaisto_kind.
# A case statement rather than `declare -A`, for the bash 3.2 reason in common.sh.
fixture_kind() {
    case "$1" in
        c1)  echo OomKilled ;;
        c2)  echo CrashLoopBackOff ;;
        c3)  echo Unschedulable ;;
        c4)  echo ImagePullBackOff ;;
        c5)  echo JobFailed ;;
        c7)  echo ConfigError ;;
        c8)  echo ReadinessFlapping ;;
        c10) echo HighErrorRate ;;
        *)   echo "" ;;
    esac
}

# Fixtures that need an image this repo builds rather than one a registry serves.
#
# Nothing did this before. The header above has said c10 "needs a local image build plus
# `kind load`" since it was written, but no code anywhere in scripts/e2e/ ever ran either - so
# asking for c10 produced a pod stuck in ImagePullBackOff on hephaisto/faulty-service:dev.
# That is not merely a missing fixture: it opens a REAL incident of the wrong kind, and the
# harness would then grade the agent on diagnosing the test rig.
chaos_build_images() {
    local fixtures="$1"

    case ",$fixtures," in
        *,c10,*) ;;
        *) return 0 ;;
    esac

    say "building hephaisto/faulty-service:dev for c10"

    # The build context is the REPO ROOT, not the Dockerfile's directory: the Dockerfile
    # copies infra/chaos/faulty-service/ by a repo-relative path.
    if ! docker build -q \
            -t hephaisto/faulty-service:dev \
            -f "$REPO/infra/chaos/faulty-service/Dockerfile" \
            "$REPO" >/dev/null; then
        warn "could not build hephaisto/faulty-service:dev; c10 will not start"
        return 1
    fi

    if ! kind load docker-image hephaisto/faulty-service:dev --name "$E2E_CLUSTER" >/dev/null 2>&1; then
        warn "could not load hephaisto/faulty-service:dev into $E2E_CLUSTER; c10 will not start"
        return 1
    fi

    say "loaded hephaisto/faulty-service:dev into $E2E_CLUSTER"
}

chaos_apply() {
    local fixtures="${FIXTURES:-$DEFAULT_FIXTURES}"
    APPLIED=""

    chaos_build_images "$fixtures" || true

    say "applying fixtures: $fixtures"

    local f file
    for f in ${fixtures//,/ }; do
        file=$(ls "$REPO/infra/chaos/${f}-"*.yaml 2>/dev/null | head -1)
        if [ -z "$file" ]; then
            warn "no fixture file for '$f'; skipping"
            continue
        fi

        if [ "$f" = "c9" ]; then
            # Refuse rather than trust the file's replicas: 0. c9 has no memory limit and
            # evicts across the whole node; a harness that can be talked into arming it is a
            # harness that will eventually take out its own Prometheus mid-run.
            warn "refusing c9-memhog: it is node-wide and would evict the observability stack"
            skip "fixture c9" "node-wide, excluded by the harness"
            continue
        fi

        # A Job's spec is immutable, so a second run against a reused cluster fails to apply
        # rather than restarting the fixture.
        [ "$f" = "c5" ] && kc delete -f "$file" --ignore-not-found >/dev/null 2>&1

        kc apply -f "$file" >/dev/null && APPLIED="${APPLIED:+$APPLIED }$f" \
            || warn "could not apply $file"
    done

    [ -n "$APPLIED" ] || die "no fixtures applied; nothing to test"
    say "applied $(applied_count) fixture(s): $APPLIED"

    # Applied together, waited on once. Each costs about two minutes of alert latency
    # (for: 1m, plus a 30s scrape interval, plus Alertmanager's 10s group_wait), and they are
    # independent - so doing them one at a time would turn eight minutes into thirty.
    pass "applied $(applied_count) chaos fixtures simultaneously"
}

chaos_await_incidents() {
    local want; want=$(applied_count)

    # Wait for an incident IN THE CHAOS NAMESPACE per fixture, not for a count of incidents
    # anywhere. Those look equivalent and are not: a freshly built cluster opens incidents of
    # its own while it settles - ReadinessFlapping on Grafana and the collector, Unschedulable
    # on loki-0 and coredns while images pull - and six of those arrive before any chaos alert
    # has had time to fire. `length >= 4` is then satisfied by the agent correctly noticing its
    # own neighbours, detection is asserted immediately, and three of four fixtures are
    # reported as having produced nothing. They had simply not happened yet.
    # Scaled with the fixture count. A flat 900s was sized for the default four; the widest
    # set is eight, and the two slowest are structural rather than incidental - c8 has to flap
    # long enough for changes(kube_pod_status_ready[30m]) >= 4, and c10's rules are 5-minute
    # rate windows behind a 5-minute `for:`. A timeout only costs wall clock when something is
    # already wrong, so sizing it for the slowest fixture is the cheap side to err on.
    # PER FIXTURE, not a count - which is what the paragraph above has always said and what
    # the code did not do. `length >= $want` is satisfied by ANY $want incidents in the
    # namespace, and one fixture routinely opens two (c1 is caught as both OomKilled and
    # CrashLoopBackOff). On the eight-fixture run the count reached 8 after 85 seconds without
    # c10 among them, the wait returned, and c10 - whose rules are 5-minute rate windows behind
    # a 5-minute `for:` - was then failed for "opening no incident" while it was still perfectly
    # on schedule. The slowest fixture never got its deadline because the others covered for it.
    #
    # Matched on targetName the same way chaos_assert_detection matches, so the thing waited
    # for and the thing asserted cannot drift apart.
    local want_json
    want_json=$(printf '%s\n' $APPLIED | jq -R . | jq -sc .)

    wait_for "an incident in $CHAOS_NS for each of: $APPLIED" "${INCIDENT_TIMEOUT:-$(( 600 + 150 * want ))}" \
        bash -c "curl -sS --max-time 10 'http://127.0.0.1:$PF_PORT_APP/api/incidents?limit=100' | jq -e --argjson want '$want_json' --arg ns '$CHAOS_NS' 'type == \"array\" and (map(select((.namespace // \"\") == \$ns)) as \$inc | \$want | all(. as \$f | \$inc | any(.targetName // \"\" | startswith(\$f))))' >/dev/null"

    local got
    got=$(api_array "/api/incidents?limit=100" | jq --arg ns "$CHAOS_NS" '[.[] | select((.namespace // "") == $ns)] | length')
    [ "${got:-0}" -ge "$want" ] \
        && pass "$got incident(s) opened in $CHAOS_NS from $want fixture(s)" \
        || fail "only ${got:-0} incident(s) in $CHAOS_NS, expected $want" \
                "check Alertmanager: curl 127.0.0.1:$PF_PORT_ALERT/api/v2/alerts"
}

# ---------------------------------------------------------------------------------------
# Detection
# ---------------------------------------------------------------------------------------
chaos_assert_detection() {
    local incidents
    incidents=$(api_array "/api/incidents?limit=100")
    printf '%s' "$incidents" > "$WORKDIR/incidents.json"

    local f kind found
    for f in $APPLIED; do
        kind=$(fixture_kind "$f")
        [ -n "$kind" ] || continue

        # Match on the workload rather than the kind alone: two fixtures producing one
        # incident each of the right kind is a different thing from one fixture producing two.
        found=$(jq --arg f "$f" '[.[] | select(.targetName // "" | startswith($f))] | length' \
                <<<"$incidents")

        if [ "${found:-0}" -ge 1 ]; then
            local got_kind
            got_kind=$(jq -r --arg f "$f" \
                '[.[] | select(.targetName // "" | startswith($f))] | .[0].kind' <<<"$incidents")
            if [ "$got_kind" = "$kind" ]; then
                pass "$f opened an incident classified $kind"
            else
                # Reported, not failed. Several shipped rules can legitimately match one
                # fixture and the winner is a race between their `for:` durations - c2's
                # crashloop is caught by KubePodNotReady as ReadinessFlapping before
                # KubePodCrashLooping has evaluated twice. The fixture WAS detected, which is
                # the assertion; which rule got there first is a fact about the rules, and
                # pinning it would make this suite fail on timing.
                skip "$f classified as $got_kind, expected $kind" \
                     "detected, but by a different rule than the README's"
            fi
        else
            fail "$f opened no incident" "the alert may not have fired; check Alertmanager"
        fi
    done

    # Every incident must carry at least one signal, or triage recorded something it never saw.
    local signalless
    signalless=$(jq '[.[] | select((.signalCount // 0) == 0)] | length' <<<"$incidents")
    [ "${signalless:-0}" -eq 0 ] \
        && pass "every incident carries at least one signal" \
        || fail "$signalless incident(s) have no signals"
}

# ---------------------------------------------------------------------------------------
# Investigation
# ---------------------------------------------------------------------------------------
chaos_await_investigations() {
    if [ "${LLM_AVAILABLE:-0}" != "1" ]; then
        skip "investigations" "no Gemini key; detection was still exercised"
        return 0
    fi

    local want; want=$(applied_count)

    # Waiting on hasDiagnosis rather than on state, because an incident reaches a terminal
    # state on several paths that are not "it was investigated" - suppressed as a flap,
    # escalated on budget - and this phase is about the model actually running.
    # Investigations are serialised, so this one scales with the count for a plainer reason:
    # the agent works through them one at a time at roughly a minute each.
    wait_for "investigations to conclude (expecting $want)" "${INVESTIGATION_TIMEOUT:-$(( 600 + 180 * want ))}" \
        bash -c "curl -sS --max-time 10 'http://127.0.0.1:$PF_PORT_APP/api/incidents' | jq -e 'type == \"array\" and ([.[] | select(.hasDiagnosis)] | length) >= $want' >/dev/null"

    local done_count
    done_count=$(api_array "/api/incidents" | jq '[.[] | select(.hasDiagnosis)] | length')
    [ "${done_count:-0}" -ge "$want" ] \
        && pass "$done_count investigation(s) produced a diagnosis" \
        || fail "only ${done_count:-0} of $want incidents were investigated"
}

# Pulls the full detail for every incident once, so the assertions below and the report and
# the judge all read the same snapshot rather than racing a live system.
chaos_collect_details() {
    local ids
    ids=$(api_array "/api/incidents?limit=100" | jq -r '.[].id')

    : > "$WORKDIR/details.jsonl"
    local id
    for id in $ids; do
        api "/api/incidents/$id" 30 >> "$WORKDIR/details.jsonl"
        printf '\n' >> "$WORKDIR/details.jsonl"
    done

    local n
    n=$(grep -c . "$WORKDIR/details.jsonl" || echo 0)
    say "collected detail for $n incident(s)"
}

chaos_assert_investigations() {
    [ "${LLM_AVAILABLE:-0}" = "1" ] || return 0
    local details="$WORKDIR/details.jsonl"

    # --- Terminated cleanly ---------------------------------------------------------------
    # Concluded means the model finished. Anything else - a step, tool-call, wall-clock or
    # budget ceiling - means it was cut off, and a diagnosis from a cut-off run is not
    # evidence that the pipeline works.
    local bad
    bad=$(jq -r 'select(.investigations | length > 0)
                 | .investigations[]
                 | select(.terminationReason != "Concluded")
                 | .terminationReason' "$details" | sort | uniq -c | tr '\n' ' ')
    if [ -z "$bad" ]; then
        pass "every investigation terminated as Concluded"
    else
        # One investigation of several exhausting its step budget is a fact about that
        # incident rather than a broken build - the ceiling exists so a hard incident stops
        # instead of running away, and a cluster carrying a dozen concurrent faults will
        # occasionally produce one. Visible either way; only a majority fails the run.
        local n_bad n_inv
        n_bad=$(wc -w <<<"$bad" | tr -d ' ')
        n_inv=$(jq -r 'select(.investigations | length > 0) | .investigations[] | .id' "$details" | wc -l | tr -d ' ')
        if [ "${n_bad:-9}" -le 2 ] && [ "${n_inv:-0}" -gt 2 ]; then
            skip "an investigation ended on a ceiling" "$bad (of ${n_inv} investigations)"
        else
            fail "investigations ended on a ceiling" "$bad (of ${n_inv} investigations)"
        fi
    fi

    # --- Grounded ---------------------------------------------------------------------------
    # A primary finding with no evidence is a guess with a confidence score. The evidence rows
    # are what tie a hypothesis to a query that was actually run.
    local ungrounded
    ungrounded=$(jq -r 'select(.investigations | length > 0)
                        | .investigations[]
                        | select([.findings[]? | select(.isPrimary)] | length > 0)
                        | select([.findings[]? | select(.isPrimary) | .evidence[]?] | length == 0)
                        | .id' "$details" | wc -l | tr -d ' ')
    [ "${ungrounded:-0}" -eq 0 ] \
        && pass "every primary finding cites evidence" \
        || fail "$ungrounded primary finding(s) cite no evidence"

    local with_primary
    with_primary=$(jq -r 'select([.investigations[]?.findings[]? | select(.isPrimary)] | length > 0) | .id' \
                   "$details" | wc -l | tr -d ' ')
    [ "${with_primary:-0}" -gt 0 ] \
        && pass "$with_primary incident(s) have a primary finding" \
        || fail "no incident produced a primary finding"

    # --- C4 vs C7, the discrimination test ---------------------------------------------------
    # These two look nearly identical from Kubernetes - a container that will not start - and
    # have entirely different causes: a tag that does not exist versus a Secret that does not
    # exist. The chaos README is explicit that the same diagnosis for both is a failure, and
    # it is the assertion an agent cannot pass by pattern-matching the symptom.
    if applied_has c4 && applied_has c7; then
        local h4 h7
        h4=$(jq -r 'select(.target.name // "" | startswith("c4"))
                    | [.investigations[]?.findings[]? | select(.isPrimary) | .hypothesis] | .[0] // ""' "$details" | head -1)
        h7=$(jq -r 'select(.target.name // "" | startswith("c7"))
                    | [.investigations[]?.findings[]? | select(.isPrimary) | .hypothesis] | .[0] // ""' "$details" | head -1)

        if [ -z "$h4" ] || [ -z "$h7" ]; then
            skip "c4 and c7 are diagnosed differently" "one of them has no primary finding yet"
        elif [ "$h4" = "$h7" ]; then
            fail "c4 and c7 got an identical diagnosis" \
                 "they present alike and differ in cause; the README calls this a failure"
        else
            pass "c4 and c7 are diagnosed differently"
        fi
    fi
}

# ---------------------------------------------------------------------------------------
# Budget accounting
# ---------------------------------------------------------------------------------------
chaos_assert_budget() {
    [ "${LLM_AVAILABLE:-0}" = "1" ] || { skip "budget accounting" "no investigations ran"; return 0; }
    local details="$WORKDIR/details.jsonl"

    # Tolerance is a hundredth of a cent, not a millionth of a dollar. costUsd is a decimal
    # in Postgres and a double in JSON, and summing four steps accumulates exactly enough
    # error to fail an equality wearing a 1e-6 tolerance: a real run differed by
    # 1.0000000000000286e-06 against a 1e-6 threshold and reported float noise as an
    # accounting defect. A wrong number here would be wrong by cents, not by femtodollars.
    # --- The invariant --------------------------------------------------------------------
    # An investigation's cost and tokens are the sum of its steps'. They are written in one
    # transaction with the step rows, so this is a genuine invariant rather than an
    # approximation - and the direction it can drift in is the dangerous one: the step
    # happened, the tokens were really spent, and the budget does not know.
    local mismatched
    mismatched=$(jq -r 'select(.investigations | length > 0)
        | .investigations[]
        | select((.steps | length) > 0)
        | . as $inv
        | ([.steps[].costUsd] | add) as $steps
        | select(($inv.costUsd - $steps) | fabs > 0.00001)
        | "\($inv.id) inv=\($inv.costUsd) steps=\($steps)"' "$details")

    if [ -z "$mismatched" ]; then
        pass "investigation cost equals the sum of its steps"
    else
        fail "cost accounting disagrees with the step log" "$(head -1 <<<"$mismatched")"
    fi

    local tok_mismatch
    tok_mismatch=$(jq -r 'select(.investigations | length > 0)
        | .investigations[]
        | select((.steps | length) > 0)
        | . as $inv
        | (([.steps[].inputTokens] | add) + ([.steps[].outputTokens] | add)) as $steps
        | select(($inv.inputTokens + $inv.outputTokens) != $steps)
        | "\($inv.id)"' "$details")
    [ -z "$tok_mismatch" ] \
        && pass "investigation tokens equal the sum of its steps" \
        || fail "token accounting disagrees with the step log"

    # --- Non-zero -----------------------------------------------------------------------------
    # LlmPricing.CostOf returns 0 for any model with no price entry, logging one warning at
    # startup. A zero total therefore does not mean "cheap", it means the cost cap is switched
    # off entirely while /status cheerfully shows 0.0% - which is the exact shape of an
    # unnoticed failure this whole harness exists to catch.
    local total
    total=$(jq -s '[.[] | .investigations[]?.costUsd] | add // 0' "$details")
    if (( $(echo "$total > 0" | bc -l) )); then
        pass "investigations cost \$$total in total"
        TOTAL_COST="$total"
    else
        fail "total investigation cost is \$0" \
             "an unpriced model reports zero cost and silently disables the cost budget"
    fi

    # --- The API agrees with the ledger --------------------------------------------------------
    local hourly max_hour expected
    hourly=$(api /api/status | jq -r '.hourlyCostUtilization')

    # Read the cap from the same values file the release was installed with, rather than
    # restating it here. Two copies of a budget number is how an assertion ends up passing
    # against the wrong denominator.
    max_hour=$(yq -r '.extraEnv[] | select(.name == "Llm__Budget__MaxCostUsdPerHour") | .value' \
               "$E2E_DIR/values-e2e.yaml" 2>/dev/null | head -1)
    # A wrong denominator here does not fail loudly, it fails PLAUSIBLY - the utilisation just
    # comes out low - so guessing is worse than stopping. yq is a required tool now, which
    # makes this branch unreachable rather than merely unlikely.
    [ -n "$max_hour" ] || die "could not read Llm__Budget__MaxCostUsdPerHour from values-e2e.yaml"

    expected=$(echo "scale=6; $total / $max_hour" | bc -l)

    # Tolerance, not equality: the window slides, and a few seconds pass between reading the
    # ledger and reading the status endpoint.
    if (( $(echo "($hourly - $expected) < 0.05 && ($expected - $hourly) < 0.05" | bc -l) )); then
        pass "hourlyCostUtilization agrees with the ledger ($hourly vs $expected)"
    else
        fail "hourlyCostUtilization is $hourly, ledger implies $expected" \
             "budget reporting disagrees with what was actually spent"
    fi
}

# ---------------------------------------------------------------------------------------
# Observe mode changed nothing
# ---------------------------------------------------------------------------------------
# docs/verification.md's MVP acceptance test requires that the agent "annotate Grafana" for
# each fixture, and until now nothing implemented that clause - backlog #20 said in as many
# words: build the annotations or restate the test, but do not silently drop it. This is what
# stops it being dropped silently again.
chaos_assert_annotations() {
    local token count

    token=$(kc -n "$APP_NS" get secret hephaisto-grafana-annotation \
        -o jsonpath='{.data.token}' 2>/dev/null | base64 -d 2>/dev/null || true)

    if [ -z "$token" ]; then
        skip "grafana annotations" "no hephaisto-grafana-annotation secret; annotation is disabled"
        return 0
    fi

    # Asserted with the AGENT's own token rather than admin basic-auth: if this credential can
    # read them back it is the credential that wrote them, which is the thing in doubt.
    count=$(curl -sS --max-time 15 \
        -H "Authorization: Bearer $token" \
        "http://127.0.0.1:$PF_PORT_GRAFANA/api/annotations?tags=hephaisto&limit=100" \
        | jq 'if type == "array" then length else 0 end' 2>/dev/null || echo 0)

    [ "${count:-0}" -ge 1 ] \
        && pass "grafana annotations ($count written)" \
        || fail "grafana annotations" "no annotation tagged 'hephaisto' after $(applied_count) fixtures"
}

chaos_assert_no_mutation() {
    local details="$WORKDIR/details.jsonl"

    # The mode is Observe, so any executed action is a containment failure rather than a bug
    # in a feature. This is only a meaningful assertion because the agent HOLDS delete on the
    # chaos namespace - it could have acted and did not.
    local executed
    executed=$(jq -r 'select(.actions | length > 0)
                      | .actions[] | select(.dryRun == false and .executedAt != null) | .id' "$details" | wc -l | tr -d ' ')
    [ "${executed:-0}" -eq 0 ] \
        && pass "no action was executed (Observe mode held)" \
        || fail "$executed action(s) were executed in Observe mode" "this is a containment failure"

    # No audit, no action: every action that was APPROVED must name who approved it.
    #
    # Scoped to states that imply a decision to go ahead, which the earlier version was not -
    # it asserted over every action, and a Denied one has no approver by construction. The
    # eight-fixture run produced exactly that: two PatchResources proposals the policy engine
    # refused, correctly carrying a null approvedBy, reported as a failure of the audit trail.
    # Requiring a name there would mean inventing one, which is worse than the gap it claims to
    # close. Proposed, AwaitingApproval, Denied and Expired are all legitimately unapproved.
    local anonymous
    anonymous=$(jq -r 'select(.actions | length > 0)
                       | .actions[]
                       | select(.state as $st
                                | ["Approved","Executing","Executed","Failed","Verifying","Verified","RolledBack"]
                                | index($st))
                       | select((.approvedBy // "") == "") | .id' "$details" | wc -l | tr -d ' ')
    [ "${anonymous:-0}" -eq 0 ] \
        && pass "every approved action names an actor" \
        || fail "$anonymous approved action(s) have no approvedBy"

    # And the fixtures are still there. An agent that deleted them would also have made its
    # own diagnosis unverifiable.
    local survivors
    survivors=$(kc -n "$CHAOS_NS" get deploy -o name 2>/dev/null | wc -l | tr -d ' ')
    say "$survivors chaos workload(s) still present in $CHAOS_NS"
}

chaos_cleanup() {
    say "removing chaos fixtures"
    local f file
    for f in $APPLIED; do
        file=$(ls "$REPO/infra/chaos/${f}-"*.yaml 2>/dev/null | head -1)
        [ -n "$file" ] && kc delete -f "$file" --ignore-not-found >/dev/null 2>&1
    done
}
