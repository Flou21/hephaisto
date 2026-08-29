#!/usr/bin/env bash
# Shared state, logging, result recording and the safety wrappers.
#
# Sourced by run.sh; not executable on its own.
#
# shellcheck disable=SC2034
# ^ Many variables here are read by the other lib/*.sh files after run.sh sources them all
#   into one shell. shellcheck analyses each file separately and reports them as unused; they
#   are not. Verified by running the harness, not by silencing the check blindly.

# ---------------------------------------------------------------------------------------
# Paths and names
# ---------------------------------------------------------------------------------------
E2E_CLUSTER="${E2E_CLUSTER:-hephaisto-e2e}"
E2E_CONTEXT="kind-${E2E_CLUSTER}"
E2E_K8S_VERSION="${E2E_K8S_VERSION:-1.36.4}"

# The kind node image, pinned BY DIGEST as kind's own release notes publish it. A floating
# tag would silently change the Kubernetes version under test between two runs of the same
# harness, which is the one variable a regression test must hold still.
#
# 1.36.4 rather than kind 0.33's default 1.37.0: it is the closest image kind ships to the
# v1.36.2+k3s1 this workspace actually runs, so the chaos fixtures behave the way their
# README documents. Override with --k8s to test the newest.
# A function rather than an associative array: macOS ships bash 3.2, which has neither
# `declare -A` nor a useful error when it meets one - it reinterprets the subscript as
# arithmetic and dies on "invalid arithmetic operator". Requiring bash 4 would mean a
# `brew install` before the harness runs, for a lookup table with four rows.
e2e_node_image() {
    case "$1" in
        1.37.0)  echo "kindest/node:v1.37.0@sha256:a1ed56cfb0e7b93589bdf97c8cd566405a265939e3620fc4f5de89adff580ae5" ;;
        1.36.4)  echo "kindest/node:v1.36.4@sha256:099e049362a1526b2db71494e1947aae99bd16290d7c895f2b7ea312e3cbfaed" ;;
        1.35.8)  echo "kindest/node:v1.35.8@sha256:07b2536e30b803ed61d1677a79df6115f798ce64c80f9e22f6ed45afd09323c0" ;;
        1.34.11) echo "kindest/node:v1.34.11@sha256:44e222ee2132dab25ff87301682f89eb82c7880ea3a1bf543bfe9708fd08d67d" ;;
        *)       echo "" ;;
    esac
}
E2E_NODE_IMAGE_VERSIONS="1.37.0 1.36.4 1.35.8 1.34.11"

IMAGE_REPO="ghcr.io/flou21/hephaisto"
CHART_REPO="oci://ghcr.io/flou21/charts"

APP_NS=hephaisto
OBS_NS=hephaisto-obs
CHAOS_NS=hephaisto-chaos

# ---------------------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------------------
if [ -t 1 ]; then
    C_RESET=$'\033[0m'; C_DIM=$'\033[2m'; C_RED=$'\033[31m'
    C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'; C_BOLD=$'\033[1m'
else
    C_RESET=; C_DIM=; C_RED=; C_GREEN=; C_YELLOW=; C_BOLD=
fi

_stamp() { date -u '+%H:%M:%S'; }

say()   { printf '%s%s%s  %s\n'      "$C_DIM" "$(_stamp)" "$C_RESET" "$*"; }
phase() { printf '\n%s%s== %s ==%s\n' "$C_BOLD" "$C_DIM" "$*" "$C_RESET"; }
warn()  { printf '%s%s  WARN%s  %s\n' "$C_YELLOW" "$(_stamp)" "$C_RESET" "$*" >&2; }
die()   { printf '%s%s  FATAL%s %s\n' "$C_RED" "$(_stamp)" "$C_RESET" "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------------------
# Results
# ---------------------------------------------------------------------------------------
# Every assertion appends one JSON line to $RESULTS. The report is collected rather than
# reconstructed at the end, so a run that dies half way still has everything it proved up to
# that point - which is exactly when you most want it.
record() {
    local status="$1" phase="$2" name="$3" detail="${4:-}"
    jq -cn --arg s "$status" --arg p "$phase" --arg n "$name" --arg d "$detail" \
        --arg t "$(date -u +%FT%TZ)" \
        '{at:$t, phase:$p, status:$s, name:$n, detail:$d}' >> "$RESULTS"

    case "$status" in
        pass) printf '  %sok%s    %s\n'   "$C_GREEN"  "$C_RESET" "$name" ;;
        fail) printf '  %sFAIL%s  %s%s\n' "$C_RED"    "$C_RESET" "$name" \
                  "${detail:+ -- $detail}" ; FAILED=$((FAILED + 1)) ;;
        skip) printf '  %sskip%s  %s%s\n' "$C_YELLOW" "$C_RESET" "$name" \
                  "${detail:+ -- $detail}" ;;
    esac
}

# All three return 0 EXPLICITLY, and that is load-bearing rather than tidy. Assertions
# throughout the harness are written as `<test> && pass "..." || fail "..."`, which is only
# equivalent to if-then-else while the middle command cannot fail. Leaving it to whatever
# `record`'s last statement happened to return would mean a `pass` could one day also trigger
# the `fail` beside it - reporting both outcomes for a single assertion.
pass() { record pass "$CURRENT_PHASE" "$1" "${2:-}"; return 0; }
fail() { record fail "$CURRENT_PHASE" "$1" "${2:-}"; return 0; }
skip() { record skip "$CURRENT_PHASE" "$1" "${2:-}"; return 0; }

# Assert helper: `check "<name>" <command...>`, where a zero exit is a pass.
check() {
    local name="$1"; shift
    local out
    if out=$("$@" 2>&1); then
        pass "$name"
    else
        fail "$name" "$(head -2 <<<"$out" | tr '\n' ' ')"
    fi
}

# ---------------------------------------------------------------------------------------
# The safety wrappers
# ---------------------------------------------------------------------------------------
# ~/.kube/config on this machine holds seventeen contexts, several of them production
# clusters. The harness therefore never reads it: kind writes to a dedicated file and only
# that file is exported, so a mistyped namespace cannot reach a cluster that matters. That is
# strictly stronger than a context guard, which a stray --context on one command defeats.
#
# The assertion below is the second layer, for the case where something re-points the
# dedicated file. Belt and braces, on the one operation in this repo that can delete pods.
kc() {
    local ctx
    ctx=$(KUBECONFIG="$E2E_KUBECONFIG" kubectl config current-context 2>/dev/null || true)
    [ "$ctx" = "$E2E_CONTEXT" ] \
        || die "refusing kubectl: context is '${ctx:-<none>}', expected '$E2E_CONTEXT'"
    KUBECONFIG="$E2E_KUBECONFIG" kubectl "$@"
}

helm_e2e() {
    local ctx
    ctx=$(KUBECONFIG="$E2E_KUBECONFIG" kubectl config current-context 2>/dev/null || true)
    [ "$ctx" = "$E2E_CONTEXT" ] \
        || die "refusing helm: context is '${ctx:-<none>}', expected '$E2E_CONTEXT'"
    KUBECONFIG="$E2E_KUBECONFIG" helm --kube-context "$E2E_CONTEXT" "$@"
}

# ---------------------------------------------------------------------------------------
# HTTP
# ---------------------------------------------------------------------------------------
# EVERY curl in this harness goes through here, and the reason is --max-time. A port-forward
# that has dropped accepts the connection and then never answers, so a bare curl hangs
# forever rather than failing - which turns a five-second assertion into a run that has to be
# killed by hand. Ask me how I know.
api() {
    local path="$1" timeout="${2:-10}"
    curl -sS --max-time "$timeout" "http://127.0.0.1:${PF_PORT_APP}${path}"
}

api_json() { api "$@" | jq "${JQ_ARGS[@]:-.}"; }

# Fetch a path that MUST return a JSON array, and fail loudly if it does not.
#
# This exists because of a false positive it would have caught. The harness asked for
# `/api/incidents?state=all`, which is not a valid state - the endpoint accepts `open` or an
# IncidentState name and returns a 400 ValidationProblem for anything else. `jq length` over
# that error OBJECT counts its keys, which happened to be four, so "4 incidents opened from 4
# fixtures" passed while the agent had in fact been asked a question it rejected.
#
# An assertion that reads an error body as data is worse than one that fails.
api_array() {
    local path="$1" timeout="${2:-10}" body kind
    body=$(api "$path" "$timeout")

    kind=$(jq -r 'type' <<<"$body" 2>/dev/null || echo "not-json")
    if [ "$kind" != "array" ]; then
        printf '%s' "$body" >&2
        die "GET $path returned $kind, not an array -- see the body above"
    fi

    printf '%s' "$body"
}

# Waits for a condition, polling, with a bounded total. Prints a dot per attempt so a long
# wait looks like progress rather than a hang.
wait_for() {
    local what="$1" timeout="$2"; shift 2
    local deadline=$(( SECONDS + timeout ))

    printf '  %swaiting for%s %s ' "$C_DIM" "$C_RESET" "$what"
    while [ "$SECONDS" -lt "$deadline" ]; do
        if "$@" >/dev/null 2>&1; then
            printf ' %sok%s (%ss)\n' "$C_GREEN" "$C_RESET" "$(( timeout - (deadline - SECONDS) ))"
            return 0
        fi
        printf '.'
        sleep 5
    done
    printf ' %stimeout after %ss%s\n' "$C_RED" "$timeout" "$C_RESET"
    return 1
}

require_tools() {
    local missing=()
    for t in kind kubectl helm gh docker jq git curl; do
        command -v "$t" >/dev/null 2>&1 || missing+=("$t")
    done
    [ ${#missing[@]} -eq 0 ] || die "missing required tools: ${missing[*]}"
}

# $APPLIED is a space-separated fixture list rather than an array; see run.sh for why.
applied_count() { set -- $APPLIED; echo $#; }
applied_has()   { case " $APPLIED " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }
