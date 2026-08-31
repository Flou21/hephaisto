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

# --full. Every fixture that can run on this hardware, which is ten of the twelve: c6 and c9
# are excluded for the stated reasons above and no flag overrides that, because neither is a
# scheduling choice - c6 cannot fire on local-path and c9 evicts the observability stack it
# would be measured by.
#
# This is the release gate, not the inner loop. It is slow on purpose: c8 alone needs a
# 30-minute window and c10 sits behind 5-minute rate windows, so budget about two hours. The
# four-fixture default stays the thing you run while working, because a two-hour gate that
# nobody runs is worth less than a five-minute one that everybody does.
FULL_FIXTURES="c1,c2,c3,c4,c5,c7,c8,c10,c11,c12"

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
        # Fires the same shipped rule as c2 - it IS a crash loop, it just stops
        # being one when the pod is replaced. That is the whole fixture.
        c11) echo CrashLoopBackOff ;;
        # Same rule again, and the same reason. c12 is c11's mechanism with one
        # volume instead of two - see infra/chaos/c12-stale-lease.yaml and #41.
        c12) echo CrashLoopBackOff ;;
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

# The target an incident opens under, which is NOT always the fixture's own name.
#
# Workload-derived fixtures are detected from a pod, so the incident carries that pod's name
# and a `c<N>-` prefix matches. c10 is derived from a METRIC - Tempo's span metrics - whose
# only identity label is `service`, so its incident opens on `faulty-service` with an EMPTY
# namespace. That is docs/backlog.md #33, and no namespace fallback can rescue it: the
# spanmetrics series carries no namespace label at all, so there is nothing to fall back to.
#
# The agent is right in both cases; only the harness's fixture-to-incident mapping was wrong,
# and it reported c10 as undetected across two release candidates while the incident existed.
fixture_target() {
    case "$1" in
        c10) echo faulty-service ;;
        *)   echo "$1" ;;
    esac
}

# The Deployment name a fixture creates, which is not the fixture id. Needed by the act
# phase, which asks the cluster directly rather than asking the API about an incident.
fixture_workload() {
    case "$1" in
        c11) echo c11-transient ;;
        c12) echo c12-stale-lease ;;
        *)   echo "" ;;
    esac
}

# THE FIXTURE THE ACT PHASE ACTS ON.
#
# c12 by default, and c11 is still selectable with ACT_FIXTURE=c11. Both are faults a pod
# replacement repairs; they differ in how many inferences that takes. v0.5.0 measured that
# c11 takes two - reconciling a PVC against an emptyDir marker - and that the agent does not
# make the second one, over twelve replays and four prompt arms (#41). Resting v0.2.0's
# acceptance criterion on the harder of the two was not a decision anyone made; it was the
# only transient fixture that existed.
ACT_FIXTURE="${ACT_FIXTURE:-c12}"

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
    local f want_json
    want_json=$(for f in $APPLIED; do fixture_target "$f"; done | jq -R . | jq -sc .)

    # Matched on target only. Requiring the chaos namespace as well would exclude c10, whose
    # incident has none (#33), and the target prefixes are unique enough to stand alone.
    #
    # `|| true` because a timeout here must not abort the run. wait_for returns 1, and under
    # `set -e` that killed the whole suite the first time this wait was made strict enough to
    # actually time out: 46 assertions passed and the remaining forty never ran, so one slow
    # fixture cost every other answer. The per-fixture check below reports precisely which one
    # is missing, which is the useful output.
    # The count term models contention: more fixtures, more to evaluate, more to wait for. It
    # does not model a fixture whose rule cannot be true sooner than a fixed wall-clock window
    # no matter how idle the cluster is. c8 needs changes(...)[30m] >= 4 - thirty minutes of
    # evidence before the expression can evaluate true at all - and c10 sits behind two
    # 5-minute rate windows and a 5-minute `for:`. At the ten fixtures of --full the count term
    # yields 2100s, which is UNDER c8's floor, so the gate would time out on a fixture that was
    # exactly on schedule. A timeout reads as a broken harness rather than as a bad diagnosis,
    # which is the one confusion this file spends the most comments trying to prevent.
    local derived=$(( 600 + 150 * want ))
    local floor; floor=$(chaos_incident_floor)
    if [ "$floor" -gt "$derived" ]; then
        derived="$floor"
    fi

    wait_for "an incident for each of: $APPLIED" "${INCIDENT_TIMEOUT:-$derived}" \
        bash -c "curl -sS --max-time 10 'http://127.0.0.1:$PF_PORT_APP/api/incidents?limit=100' | jq -e --argjson want '$want_json' 'type == \"array\" and (. as \$inc | \$want | all(. as \$t | \$inc | any(.targetName // \"\" | startswith(\$t))))' >/dev/null" \
        || warn "not every fixture opened an incident within the deadline; see the per-fixture results below"

    local got
    got=$(api_array "/api/incidents?limit=100" | jq --arg ns "$CHAOS_NS" '[.[] | select((.namespace // "") == $ns)] | length')
    [ "${got:-0}" -ge "$want" ] \
        && pass "$got incident(s) opened in $CHAOS_NS from $want fixture(s)" \
        || fail "only ${got:-0} incident(s) in $CHAOS_NS, expected $want" \
                "check Alertmanager: curl 127.0.0.1:$PF_PORT_ALERT/api/v2/alerts"
}

# The wall-clock a fixture needs before its alert rule can fire at all, regardless of how many
# other fixtures are running. Only the slow ones appear here; everything else is covered by the
# count-based term. These are read off the shipped rule windows, not guessed.
chaos_incident_floor() {
    local f need floor=0
    for f in $APPLIED; do
        case "$f" in
            c8)  need=2400 ;;   # changes(...)[30m] >= 4, plus evaluation lag behind the window
            c10) need=1200 ;;   # two 5-minute rate windows and a 5-minute for:
            *)   need=0 ;;
        esac
        if [ "$need" -gt "$floor" ]; then
            floor="$need"
        fi
    done
    echo "$floor"
}

# ---------------------------------------------------------------------------------------
# Detection
# ---------------------------------------------------------------------------------------
chaos_assert_detection() {
    local incidents
    incidents=$(api_array "/api/incidents?limit=100")
    printf '%s' "$incidents" > "$WORKDIR/incidents.json"

    # Fixture -> incident, resolved ONCE and written down for every later reader.
    #
    # judge.sh used to redo this mapping itself, and did it differently: it matched the raw
    # fixture id against target.name, while this function matches fixture_target. For c10
    # those are not the same string - its incident opens on `faulty-service` with no namespace
    # (#33) - so the judge matched nothing and reported "no primary finding" for an incident
    # that had one. Two resolutions of one question is how a run asserts on one incident and
    # grades another, which makes the denominator quietly smaller than the corpus.
    : > "$WORKDIR/fixture-incidents.tsv"

    local f kind found
    for f in $APPLIED; do
        kind=$(fixture_kind "$f")
        [ -n "$kind" ] || continue

        # Match on the workload rather than the kind alone: two fixtures producing one
        # incident each of the right kind is a different thing from one fixture producing two.
        local target; target=$(fixture_target "$f")

        found=$(jq --arg t "$target" '[.[] | select(.targetName // "" | startswith($t))] | length' \
                <<<"$incidents")

        # Every match, in the order the API returned them - not just the one whose kind is
        # checked below. One fixture routinely opens two incidents, and a reader that takes
        # only the first has no way to tell "this fixture produced no diagnosis" from "the
        # row I happened to pick did not carry it".
        jq -r --arg f "$f" --arg t "$target" \
            '.[] | select(.targetName // "" | startswith($t)) | "\($f)\t\(.id)"' \
            <<<"$incidents" >> "$WORKDIR/fixture-incidents.tsv"

        if [ "${found:-0}" -ge 1 ]; then
            local got_kind
            got_kind=$(jq -r --arg t "$target" \
                '[.[] | select(.targetName // "" | startswith($t))] | .[0].kind' <<<"$incidents")
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
        skip "investigations" "no reachable model; detection was still exercised"
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

    # Containment, and it is now conditional on the mode the harness installed with.
    #
    # This used to assert unconditionally that nothing had executed, which was right for every
    # release in which nothing COULD - and would have made v0.2.0's own e2e fail the moment the
    # agent did the thing it was built to do. What has to stay true is the pairing: in Observe
    # nothing may execute, and in Auto something must. An assertion that passes in both
    # directions is not an assertion.
    local executed
    executed=$(jq -r 'select(.actions | length > 0)
                      | .actions[] | select(.dryRun == false and .executedAt != null) | .id' "$details" | wc -l | tr -d ' ')

    if [ "${E2E_MODE:-Observe}" = "Observe" ]; then
        # Only meaningful because the agent HOLDS delete on the chaos namespace - it could
        # have acted and did not.
        [ "${executed:-0}" -eq 0 ] \
            && pass "no action was executed (Observe mode held)" \
            || fail "$executed action(s) were executed in Observe mode" "this is a containment failure"
    else
        say "$executed action(s) executed in ${E2E_MODE} mode; containment assertion does not apply"

    # No audit, no action: every action that was APPROVED must name who approved it.
    #
    # Scoped to states that imply a decision to go ahead, which the earlier version was not -
    # it asserted over every action, and a Denied one has no approver by construction. The
    # eight-fixture run produced exactly that: two PatchResources proposals the policy engine
    # refused, correctly carrying a null approvedBy, reported as a failure of the audit trail.
    # Requiring a name there would mean inventing one, which is worse than the gap it claims to
    # close. Proposed, AwaitingApproval, Denied and Expired are all legitimately unapproved.
    fi

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

# ---------------------------------------------------------------------------------------
# The acting half. Only runs when the harness installed in a mode that can act.
# ---------------------------------------------------------------------------------------

# True once anything has actually been applied to the cluster. Re-collects on every poll,
# because the answer lives in the incident detail and that snapshot is what goes stale.
_something_executed() {
    chaos_collect_details >/dev/null 2>&1 || return 1

    local n
    n=$(jq -r --argjson dry "$(chaos_expected_dryrun)" \
        '[.actions[]? | select(.executedAt != null and .dryRun == $dry)] | length' \
        "$WORKDIR/details.jsonl" 2>/dev/null | awk '{t += $1} END {print t + 0}')

    [ "${n:-0}" -ge 1 ]
}

# What an executed action should look like in this mode. DryRun's whole purpose is to run the
# plan without mutating anything, so it records executions with dryRun=true - and asserting
# dryRun=false against it, as this harness did, is a condition DryRun cannot satisfy by
# definition. The mode was unrunnable rather than failing: three phases would pass and then the
# act phase would report the agent had not acted, which was never a claim about the agent.
chaos_expected_dryrun() {
    case "${E2E_MODE:-Observe}" in
        DryRun) echo true ;;
        *)      echo false ;;
    esac
}

# An action was admitted, executed and recorded against the fixture that needed it.
chaos_assert_action_executed() {
    local details="$WORKDIR/details.jsonl"
    local target; target=$(fixture_target "$ACT_FIXTURE")

    # details.jsonl was collected during `validate`, BEFORE anything acted - the action
    # happens once the investigation concludes, which is after that snapshot was taken. So
    # asserting against it here reads a file written before the thing it is asserting about.
    # Wait for an execution and re-collect; _something_executed re-collects on every poll.
    wait_for "an action to be executed" 180 _something_executed \
        || say "no execution seen within 180s; asserting on what was collected"

    chaos_collect_details

    local dry; dry=$(chaos_expected_dryrun)
    local executed
    executed=$(jq -r --arg t "$target" --argjson dry "$dry" \
        'select(.target.name != null and (.target.name | contains($t)))
         | .actions[]? | select(.executedAt != null and .dryRun == $dry) | .type' \
        "$details" 2>/dev/null | wc -l | tr -d ' ')

    # Recorded, not just reported. chaos_assert_verification asks two questions that only have
    # a subject if something ran, and answering them anyway is how a report ends up confidently
    # wrong about why - see the comment there.
    local shape="non-dry-run"; [ "$dry" = "true" ] && shape="dry-run"

    if [ "${executed:-0}" -ge 1 ]; then
        ACT_EXECUTED=1
        pass "$ACT_FIXTURE was acted on ($executed $shape action(s) executed)"
    else
        ACT_EXECUTED=0
        fail "$ACT_FIXTURE was not acted on" "expected at least one executed, $shape action"
    fi

    # DryRun's other half, and the one worth more: it planned, and it still changed nothing.
    # Without this the mode only proves the plan existed, which Observe already proves.
    if [ "$dry" = "true" ]; then
        local mutated
        mutated=$(jq -r '[.actions[]? | select(.executedAt != null and .dryRun == false)] | length' \
            "$details" 2>/dev/null | awk '{t += $1} END {print t + 0}')
        [ "${mutated:-0}" -eq 0 ] \
            && pass "nothing was executed for real (DryRun held)" \
            || fail "$mutated action(s) executed for real in DryRun" "this is a containment failure"
    fi

    # Every executed action must name an approver. In Auto that is hephaisto/auto; the point
    # is that "no audit, no action" holds on the path that actually writes to the cluster.
    local anonymous
    anonymous=$(jq -r '.actions[]? | select(.executedAt != null) | select((.approvedBy // "") == "") | .id' \
        "$details" 2>/dev/null | wc -l | tr -d ' ')

    [ "${anonymous:-0}" -eq 0 ] \
        && pass "every executed action names an approver" \
        || fail "$anonymous executed action(s) have no approvedBy"
}

# True once the acting fixture's incident has been closed by the verifier.
_act_resolved() {
    [ "$(api_array "/api/incidents?state=Resolved&limit=100" \
        | jq -r --arg t "$(fixture_target "$ACT_FIXTURE")" \
            '[.[] | select(.target.name != null and (.target.name | contains($t)))] | length' \
        2>/dev/null || echo 0)" -ge 1 ]
}

# Available AND settled. availableReplicas alone is not enough: the fixture's container has no
# readiness probe, so while wedged it is Ready for the two seconds it runs before exiting, and
# the Deployment duly reports one available replica for part of every crash cycle. The fixture
# now carries minReadySeconds to close that, and this asserts the pod is genuinely Ready too -
# belt and braces, because a false pass here would report a broken workload as fixed.
_act_available() {
    local w; w=$(fixture_workload "$ACT_FIXTURE")

    [ "$(kc -n "$CHAOS_NS" get deploy "$w" \
        -o jsonpath='{.status.availableReplicas}' 2>/dev/null || echo 0)" -ge 1 ] \
        && [ "$(kc -n "$CHAOS_NS" get pods -l "app.kubernetes.io/name=$w" \
            -o jsonpath='{.items[*].status.containerStatuses[*].ready}' 2>/dev/null)" = "true" ]
}

# The fixture is actually healthy afterwards. This is the half that distinguishes "the agent
# did something" from "the agent fixed it", and it is why c11 and c12 exist rather than c8:
# a fixture that recovers on its own would pass this whatever the agent did.
chaos_assert_verification() {
    # NOTHING RAN MEANS THERE IS NOTHING TO VERIFY, and saying so is the whole point.
    #
    # Both assertions below carry an explanation of their own failure - "the action ran but the
    # workload did not recover", "the workload recovered but verification never closed the
    # incident". When no action was executed, neither sentence is true: no action ran and
    # nothing recovered. They are the first failure restated as downstream symptoms with
    # confident and incorrect causes attached, and they cost eight minutes of wall clock
    # burning two 240s timeouts to produce. A reader then goes looking for a broken restart
    # and a broken verifier when the actual finding is that nothing was proposed.
    #
    # So: skip, naming the assertion that actually failed. A report that is wrong about why is
    # worse than one that is merely incomplete.
    if [ "${ACT_EXECUTED:-0}" != "1" ]; then
        local because="nothing was executed - see '$ACT_FIXTURE was not acted on'; there is no action to verify"
        skip "$ACT_FIXTURE is available after the restart" "$because"
        skip "$ACT_FIXTURE's incident reached Resolved" "$because"
        return 0
    fi

    # WAIT, do not sample. The first verification is not due until T+60s and the scheduler
    # polls every 10s, so an assertion made the moment the action returns is asking a question
    # the system has not been given time to answer - and it would report a healthy agent as a
    # failure. Five of v0.1.0's six release candidates went that way: the harness's own
    # instrumentation, not the thing being measured.
    #
    # 4 minutes covers the T+60s check plus scheduler poll, pod recreation and image pull, and
    # stops short of the T+5m second attempt - if the first check did not settle it, waiting
    # for the second would be measuring something else.
    wait_for "$ACT_FIXTURE to become available again" 240 _act_available \
        && pass "$ACT_FIXTURE is available after the restart" \
        || fail "$ACT_FIXTURE is still not available" "the action ran but the workload did not recover"

    # And the incident says so. Resolved is granted only by hephaisto/verifier, after a
    # deterministic predicate looked at the cluster - a model may never grant it.
    wait_for "$ACT_FIXTURE's incident to reach Resolved" 240 _act_resolved \
        && pass "$ACT_FIXTURE's incident reached Resolved" \
        || fail "$ACT_FIXTURE's incident did not reach Resolved" \
               "the workload recovered but verification never closed the incident"
}

chaos_cleanup() {
    say "removing chaos fixtures"
    local f file
    for f in $APPLIED; do
        file=$(ls "$REPO/infra/chaos/${f}-"*.yaml 2>/dev/null | head -1)
        [ -n "$file" ] && kc delete -f "$file" --ignore-not-found >/dev/null 2>&1
    done
}
