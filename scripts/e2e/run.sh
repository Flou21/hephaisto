#!/usr/bin/env bash
#
# One command that takes a published Hephaisto build, stands it up next to a real
# observability stack on a throwaway cluster, breaks things, and reports what happened.
#
#   scripts/e2e/run.sh                    # dispatch a nightly and test it
#   scripts/e2e/run.sh --rc               # cut a real release candidate and test it
#   scripts/e2e/run.sh --tag 0.0.1-rc2    # test something already published
#
# WHY THIS EXISTS
#
# ci.yml's e2e-kind job states its own limits in a comment, and they are the gap:
#
#     - kind's default CNI ACCEPTS NetworkPolicy objects and does not enforce them.
#     - No Prometheus here, so the `release:` selector is only covered by a render-time grep.
#     - No real LLM key, so nothing exercises an investigation end to end.
#
# Nothing anywhere proved that a PUBLISHED artifact, installed from GHCR beside a real
# Prometheus, Loki, Tempo and collector, detects a real fault and investigates it. That is
# what this does. It closes the second and third of those limits; the first is still open and
# the summary says so at the end of every run.
#
# SAFETY
#
# ~/.kube/config on this machine holds seventeen contexts, several of them production. This
# harness never reads it - kind writes to a dedicated file and only that is exported, so a
# mistyped namespace cannot reach a cluster that matters. Teardown is an EXIT trap rather
# than a final step, so a failure or a Ctrl-C still deletes the cluster and puts the system's
# inotify limit back.
#
# COST
#
# Investigations are real Gemini calls. values-e2e.yaml caps spend per incident, per hour and
# per day, so a run is bounded at about a dollar even if something loops.

set -Eeuo pipefail

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$E2E_DIR/../.." && pwd)"
GH_REPO="${GH_REPO:-Flou21/hephaisto}"

# shellcheck source=lib/common.sh
source "$E2E_DIR/lib/common.sh"
source "$E2E_DIR/lib/cluster.sh"
source "$E2E_DIR/lib/build.sh"
source "$E2E_DIR/lib/deps.sh"
source "$E2E_DIR/lib/deploy.sh"
source "$E2E_DIR/lib/chaos.sh"
source "$E2E_DIR/lib/judge.sh"
source "$E2E_DIR/lib/notify.sh"
source "$E2E_DIR/lib/report.sh"

# ---------------------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------------------
CHANNEL=nightly
VERSION=""
FIXTURES=""
FULL=0
KEEP_CLUSTER=0
RUN_JUDGE=1
RUN_UI=1
ASSUME_YES=0
FROM_PHASE=""
ONLY_PHASE=""

usage() {
    # Print the header block: every line from 2 until the first that is not a comment. A
    # fixed line range goes stale the moment the header is edited, and prints a stray `set
    # -Eeuo pipefail` at people.
    awk 'NR>1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "$0"
    cat <<'EOF'

Options:
  --nightly            publish the current branch, dispatch nightly.yml, test what it
                       builds (default). Refuses on a dirty tree, and never publishes
                       main - see HEPHAISTO_E2E_NO_PUSH below.
  --rc                 cut the next release candidate, publish it, test it (prompts)
  --tag <version>      test a version that is already published, e.g. 0.0.1-rc2
  --fixtures <list>    comma-separated chaos fixtures (default: c2,c3,c4,c7)
  --full               every fixture that can run here:
                       c1,c2,c3,c4,c5,c7,c8,c10,c11,c12,c13. The release gate. About two
                       hours - c8 alone needs a 30-minute window. c6 and c9 are excluded
                       and no flag overrides that.
  --k8s <version>      Kubernetes version for the kind node (default: 1.36.4)
  --from <phase>       start at this phase, reusing an existing cluster
  --only <phase>       run just this phase against an existing cluster
  --keep-cluster       do not delete the cluster on exit
  --no-judge           skip the LLM root-cause grading
  --mode <M>           Off, Observe (default), DryRun or Auto. Auto installs the chart able
                       to act and asserts that it did; DryRun asserts it planned and still
                       changed nothing; Observe and Off assert the opposite, that nothing
                       executed. DryRun and Auto both add $ACT_FIXTURE (c13 by default) to
                       whatever fixtures were selected.
  --no-ui              skip the Playwright suite
  --yes                do not prompt before pushing an rc tag
  -h, --help           this

Phases: build, cluster, deps, deploy, chaos, validate, act, notify, ui, report

A local model, which is free: set HEPHAISTO_LLM_PROVIDER=openai with an endpoint the CLUSTER
can reach - not localhost, which from a pod is a different machine. See scripts/e2e/README.md.

HEPHAISTO_E2E_NO_PUSH=1 dispatches whatever origin already points at instead of publishing the
current branch first. For testing a branch somebody else pushed. Note that the harness installs
the PUBLISHED artifact either way, so with this set you are testing the remote's commit, not
yours.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --nightly)      CHANNEL=nightly; shift ;;
        --rc)           CHANNEL=rc; shift ;;
        --tag)          CHANNEL=existing; VERSION="${2:?--tag needs a version}"; shift 2 ;;
        --fixtures)     FIXTURES="${2:?--fixtures needs a list}"; shift 2 ;;
        --full)         FULL=1; shift ;;
        --k8s)          E2E_K8S_VERSION="${2:?--k8s needs a version}"; shift 2 ;;
        --from)         FROM_PHASE="${2:?--from needs a phase}"; shift 2 ;;
        --only)         ONLY_PHASE="${2:?--only needs a phase}"; shift 2 ;;
        --keep-cluster) KEEP_CLUSTER=1; shift ;;
        --no-judge)     RUN_JUDGE=0; shift ;;
        --no-ui)        RUN_UI=0; shift ;;
        --mode)         E2E_MODE="${2:?--mode needs Observe|DryRun|Auto}"; shift 2 ;;
        --yes)          ASSUME_YES=1; shift ;;
        -h|--help)      usage; exit 0 ;;
        *)              printf 'unknown option: %s\n\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done

# ---------------------------------------------------------------------------------------
# Run state
# ---------------------------------------------------------------------------------------

# Observe by default, because the default run is the containment run: it proves the agent
# does not act when it is not supposed to, which is the property that has to hold on every
# release. --mode Auto swaps that for the opposite proof and turns on the act phase.
E2E_MODE="${E2E_MODE:-Observe}"
export E2E_MODE

# Validate rather than pass through. The mode is handed straight to --set mode=, and the chart
# enum would reject it - but only after a cluster has been built and the dependencies
# installed, which is ten minutes spent to learn about a typo.
case "$E2E_MODE" in
    Off|Observe|DryRun|Auto) ;;
    *) die "--mode must be Off, Observe, DryRun or Auto (got '$E2E_MODE')" ;;
esac

# --full is the release gate: every fixture that can run here. An explicit --fixtures wins,
# so the flag is a shorthand and not an override of something the caller asked for.
if [ "$FULL" = "1" ] && [ -z "$FIXTURES" ]; then
    FIXTURES="$FULL_FIXTURES"
    say "--full: $FIXTURES"
    say "this is the release gate and takes about two hours; c8 alone needs a 30-minute window"
fi
START_TIME=$SECONDS
FAILED=0
RUN_COMPLETED=0
CURRENT_PHASE=setup
LLM_AVAILABLE=0

# A space-separated string, NOT an array. bash 3.2 - which is what macOS ships and therefore
# what this runs on - errors with "unbound variable" on "${arr[@]}" when the array is empty
# and `set -u` is on, so an --only run that applies no fixtures would die in the reporter
# rather than report. Fixture names are single tokens, so a string loses nothing.
APPLIED=""

WORKDIR="${E2E_WORKDIR:-$(mktemp -d "${TMPDIR:-/tmp}/hephaisto-e2e.XXXXXX")}"
mkdir -p "$WORKDIR"
RESULTS="$WORKDIR/results.jsonl"
: > "$RESULTS"

# NEVER ~/.kube/config. This is the property that makes the harness unable to reach
# production, and it must not be softened into "the context is checked".
E2E_KUBECONFIG="$WORKDIR/kubeconfig"

# High ports, so a forward cannot collide with the Tilt stack this machine also runs.
PF_PORT_APP=18100
PF_PORT_PROM=19090
PF_PORT_ALERT=19093
PF_PORT_GRAFANA=13030
PF_PORT_RECEIVER=18099

trap teardown EXIT INT TERM

# ---------------------------------------------------------------------------------------
# Phase sequencing
# ---------------------------------------------------------------------------------------
PHASES=(build cluster deps deploy chaos validate act notify ui report)

should_run() {
    local p="$1"
    [ -n "$ONLY_PHASE" ] && { [ "$p" = "$ONLY_PHASE" ] && return 0 || return 1; }
    [ -z "$FROM_PHASE" ] && return 0

    local seen=0 x
    for x in "${PHASES[@]}"; do
        [ "$x" = "$FROM_PHASE" ] && seen=1
        [ "$x" = "$p" ] && { [ "$seen" = 1 ] && return 0 || return 1; }
    done
    return 1
}

# ---------------------------------------------------------------------------------------
say "hephaisto e2e -- workdir $WORKDIR"
require_tools

[ -n "$FROM_PHASE$ONLY_PHASE" ] && say "resuming: reusing cluster $E2E_CLUSTER if it exists"

# --- build --------------------------------------------------------------------------------
CURRENT_PHASE=build
if should_run build; then
    phase "1. get a build ($CHANNEL)"
    case "$CHANNEL" in
        nightly)  build_nightly ;;
        rc)       build_rc ;;
        existing) say "testing already-published $VERSION" ;;
    esac

    phase "2. wait for the artifacts to be pullable"
    build_await_artifacts
else
    [ -n "$VERSION" ] || die "--from/--only past 'build' needs --tag <version> so the harness knows what to install"
    say "skipping build; testing $VERSION"
fi

# --- cluster ------------------------------------------------------------------------------
CURRENT_PHASE=cluster
if should_run cluster; then
    phase "3. throwaway kind cluster"
    cluster_create
else
    kind export kubeconfig --name "$E2E_CLUSTER" --kubeconfig "$E2E_KUBECONFIG" >/dev/null 2>&1 \
        || die "no cluster $E2E_CLUSTER to resume against"
fi

# --- deps ---------------------------------------------------------------------------------
CURRENT_PHASE=deps
if should_run deps; then
    phase "4. observability stack"
    deps_install
fi

# Forwards are needed by every phase from here, including a resumed one.
port_forward prometheus   "$OBS_NS" svc/hephaisto-kube-prometheus-prometheus   "$PF_PORT_PROM"    9090 || true
port_forward alertmanager "$OBS_NS" svc/hephaisto-kube-prometheus-alertmanager "$PF_PORT_ALERT"   9093 || true
port_forward grafana      "$OBS_NS" svc/hephaisto-grafana                      "$PF_PORT_GRAFANA" 80   || true

if should_run deps; then
    deps_secrets
    deps_verify

    # Test equipment rather than a dependency of the product: the agent posts notifications
    # here so the delivery path can be asserted without a third-party account.
    notify_install || true
fi

# LLM_AVAILABLE is set inside deps_secrets and nowhere else, so a run that starts after the
# deps phase carries the initial 0 - and every investigation, act, judge and budget assertion
# then reports skip while the run exits 0. That is a false green, and a resumed run is exactly
# when somebody is least likely to reread the middle of the log.
#
# Refuse rather than guess. HEPHAISTO_E2E_ASSUME_LLM=1 says "the key was good when deps ran",
# which is a claim the caller is entitled to make and the harness is not.
if ! should_run deps && should_run validate; then
    case "${HEPHAISTO_E2E_ASSUME_LLM:-}" in
        1) LLM_AVAILABLE=1
           say "assuming the model is reachable (HEPHAISTO_E2E_ASSUME_LLM=1)" ;;
        0) LLM_AVAILABLE=0
           warn "assuming NO model (HEPHAISTO_E2E_ASSUME_LLM=0); investigations will skip" ;;
        *) die "resuming past 'deps' cannot know whether the model is reachable, and would skip every investigation while still exiting 0. Set HEPHAISTO_E2E_ASSUME_LLM=1 (or 0), or resume with --from deps" ;;
    esac
fi

port_forward receiver "$OBS_NS" svc/notification-receiver "$PF_PORT_RECEIVER" 8080 || true

# --- deploy -------------------------------------------------------------------------------
CURRENT_PHASE=deploy
if should_run deploy; then
    phase "5. install the published build"
    deploy_install
fi

port_forward hephaisto "$APP_NS" svc/hephaisto "$PF_PORT_APP" 8080 \
    || die "the agent is not reachable; nothing after this can be checked"

if should_run deploy; then
    deploy_assert
    deploy_assert_rbac
    deploy_assert_selected
    deploy_assert_watchdog
fi

# --- chaos --------------------------------------------------------------------------------
CURRENT_PHASE=chaos
if should_run chaos; then
    # An acting run needs a fixture a restart actually fixes; a containment run has no use
    # for one. ACT_FIXTURE names it (c13 by default; c12 and c11 remain selectable), and adding it
    # here rather than naming a fixture twice keeps the thing injected and the thing asserted
    # from drifting apart.
    # This has to hold however the fixture list was chosen. It used to apply only when
    # FIXTURES was empty, so `--mode Auto --fixtures ...` - and therefore `--mode Auto --full`
    # - dropped the act fixture silently and then failed the act phase against a fixture that
    # had never been applied. That failure reads as the agent refusing to act, which is the
    # single claim this harness exists to test and the last one it should be able to fake.
    case "$E2E_MODE" in
        DryRun|Auto)
            FIXTURES="${FIXTURES:-$DEFAULT_FIXTURES}"
            case ",$FIXTURES," in
                *",$ACT_FIXTURE,"*) ;;
                *) FIXTURES="${FIXTURES},${ACT_FIXTURE}"
                   say "acting mode: added $ACT_FIXTURE" ;;
            esac
            export FIXTURES
            ;;
    esac

    phase "6. inject faults"
    chaos_apply
    chaos_await_incidents
fi

# A resumed run never entered chaos_apply, so APPLIED is empty - and an empty APPLIED does not
# fail loudly, it makes every per-fixture assertion loop zero times and the wait for
# investigations report "waiting on 0" and pass trivially. A resumed validate then reports a
# tidy green having asserted nothing about any fixture, which is the same false-green this
# harness has now produced in four different costumes.
#
# The fixtures are still on the cluster; the selection is still known. Reconstruct it.
if ! should_run chaos && should_run validate && [ -z "$APPLIED" ]; then
    APPLIED=$(printf '%s' "${FIXTURES:-$DEFAULT_FIXTURES}" | tr ',' ' ')
    say "resumed: asserting against fixtures $APPLIED"
fi

# --- validate -----------------------------------------------------------------------------
CURRENT_PHASE=validate
if should_run validate; then
    phase "7. validate"
    chaos_assert_detection
    chaos_await_investigations
    chaos_collect_details
    chaos_assert_investigations
    chaos_assert_budget
    chaos_assert_annotations
    chaos_assert_no_mutation
    judge_run
fi

# --- act ----------------------------------------------------------------------------------
# Only when the harness installed in a mode that can act. Skipped rather than silently absent,
# so the report says which half of the release this run covered.
CURRENT_PHASE=act
if should_run act; then
    if [ "${E2E_MODE:-Observe}" = "Observe" ] || [ "${E2E_MODE:-Observe}" = "Off" ]; then
        phase "7a. acting (skipped)"
        skip "acting" "installed in ${E2E_MODE:-Observe}; run with --mode Auto to exercise the executor"
    elif [ "${LLM_AVAILABLE:-0}" != "1" ]; then
        # No key means no investigation, which means no plan, which means there was never
        # going to be an action. Asserting anyway would spend eleven minutes waiting and then
        # report "the agent did not act" - which measures the absence of an API key rather
        # than anything about the agent, and is the exact failure this harness has now made
        # five times in different clothes.
        phase "7a. acting (skipped)"
        skip "acting" "no reachable model, so nothing investigated and nothing could be proposed"
    else
        phase "7a. acting"
        chaos_assert_action_executed
        chaos_assert_verification
    fi
fi

# --- notify -------------------------------------------------------------------------------
# Outbound delivery. The first assertions show a message can be sent; the last is the only one
# that could not have been a unit test - a delivery surviving the process that queued it, which
# is the claim v0.3.0 actually makes.
CURRENT_PHASE=notify
if should_run notify; then
    if ! kc -n "$OBS_NS" get deploy/notification-receiver >/dev/null 2>&1; then
        phase "7b. outbound notifications (skipped)"
        skip "outbound notifications" "the notification receiver is not installed"
    else
        phase "7b. outbound notifications"
        notify_assert_configured
        notify_assert_delivered
        notify_assert_outage_survived
    fi
fi

# --- ui -----------------------------------------------------------------------------------
CURRENT_PHASE=ui
if should_run ui && [ "$RUN_UI" = "1" ]; then
    phase "7c. the console"
    # `bash <script>` rather than executing it directly: a lost exec bit used to turn this
    # phase into a silent skip, which reads on the report as "the console is fine". The file
    # missing entirely is still a skip - that is a real "not present" - but a file that exists
    # and merely is not chmod +x is not a reason to stop testing the console.
    if [ -f "$E2E_DIR/ui/run.sh" ]; then
        # Bounded, because this phase is the one that hung a whole run with no output.
        # Playwright serialises browser installs on a lock in ~/.cache/ms-playwright, so a
        # second install - another run, another project, a leftover process - blocks this one
        # indefinitely. Every other wait here has a deadline; this one did not, and an
        # unbounded step looks exactly like a passing one until somebody checks.
        export HEPHAISTO_URL="http://127.0.0.1:$PF_PORT_APP"

        UI_STATUS=0
        run_bounded "${UI_TIMEOUT:-1800}" "the console suite" bash "$E2E_DIR/ui/run.sh" || UI_STATUS=$?

        case "$UI_STATUS" in
            0)   pass "playwright suite" ;;
            124) fail "playwright suite" \
                     "timed out after ${UI_TIMEOUT:-1800}s; a cold or contended browser install is the usual cause" ;;
            *)   fail "playwright suite" "see $E2E_DIR/ui/playwright-report" ;;
        esac
    else
        skip "playwright suite" "scripts/e2e/ui/run.sh not present"
    fi
elif should_run ui; then
    skip "playwright suite" "--no-ui"
fi

# --- done ---------------------------------------------------------------------------------
CURRENT_PHASE=report
# Not gated on `should_run chaos`. An --only ui or --from validate run never enters that
# phase but still finds fixtures on the cluster from the run that applied them, and leaving
# them there is how the next run inherits somebody else's incidents.
chaos_cleanup

# Reaching this line is the only proof the run completed rather than died somewhere in the
# middle, and the reporter needs that as a fact rather than as an inference from the exit
# code: a completed-but-red run and a run that died both exit non-zero, and calling the first
# one ABORTED tells the reader "the rest never ran" about assertions that all ran.
RUN_COMPLETED=1

# teardown (the EXIT trap) renders the report and preserves this exit status.
[ "$FAILED" -eq 0 ] || exit 1
exit 0
