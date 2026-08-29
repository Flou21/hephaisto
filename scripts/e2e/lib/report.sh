#!/usr/bin/env bash
# Phase 9: the summary.
#
# Rendered from $RESULTS, which every assertion appended to as it ran. Collected rather than
# reconstructed, so a run that died half way still reports everything it had proved by then -
# which is exactly when the summary is most useful.

report_render() {
    local aborted="${1:-0}"
    local total passed failed skipped
    total=$(wc -l < "$RESULTS" | tr -d ' ')
    passed=$(jq -r 'select(.status == "pass")' "$RESULTS" | jq -s length)
    failed=$(jq -r 'select(.status == "fail")' "$RESULTS" | jq -s length)
    skipped=$(jq -r 'select(.status == "skip")' "$RESULTS" | jq -s length)

    local elapsed=$(( SECONDS - START_TIME ))

    printf '\n'
    printf '%s========================================================================%s\n' "$C_BOLD" "$C_RESET"
    printf '%s  hephaisto end-to-end: %s%s\n' "$C_BOLD" "${VERSION:-<no build>}" "$C_RESET"
    printf '%s========================================================================%s\n' "$C_BOLD" "$C_RESET"
    printf '\n'
    printf '  channel          %s\n' "${CHANNEL:-nightly}"
    printf '  kubernetes       %s (kind, single node)\n' "$E2E_K8S_VERSION"
    printf '  fixtures         %s\n' "${APPLIED:-none}"
    printf '  wall clock       %dm %ds\n' $(( elapsed / 60 )) $(( elapsed % 60 ))
    [ -n "${TOTAL_COST:-}" ] && printf '  llm spend        $%s\n' "$TOTAL_COST"
    [ -n "${JUDGE_SCORE:-}" ] && printf '  root cause       %s correct (reported, not gating)\n' "$JUDGE_SCORE"
    printf '\n'

    # Per-phase tally, in the order the phases ran.
    printf '  %-26s %5s %5s %5s\n' "phase" "pass" "fail" "skip"
    printf '  %-26s %5s %5s %5s\n' "--------------------------" "-----" "-----" "-----"
    local ph
    for ph in $(jq -r '.phase' "$RESULTS" | awk '!seen[$0]++'); do
        printf '  %-26s %5d %5d %5d\n' "$ph" \
            "$(jq -r --arg p "$ph" 'select(.phase == $p and .status == "pass")' "$RESULTS" | jq -s length)" \
            "$(jq -r --arg p "$ph" 'select(.phase == $p and .status == "fail")' "$RESULTS" | jq -s length)" \
            "$(jq -r --arg p "$ph" 'select(.phase == $p and .status == "skip")' "$RESULTS" | jq -s length)"
    done
    # Phases that recorded nothing at all. An absent row reads as "no assertions here",
    # which is indistinguishable from "this phase never ran" - and those are very different.
    local ph_missing="" ph
    for ph in "${PHASES[@]}"; do
        case "$ph" in report) continue ;; esac
        grep -q "\"phase\":\"$ph\"" "$RESULTS" 2>/dev/null || ph_missing="${ph_missing:+$ph_missing }$ph"
    done
    [ -z "$ph_missing" ] || printf '  %sphases that recorded nothing:%s %s\n\n' \
        "$C_YELLOW" "$C_RESET" "$ph_missing"

    if [ "$failed" -gt 0 ]; then
        printf '  %sfailures%s\n' "$C_RED" "$C_RESET"
        jq -r 'select(.status == "fail") | "    \(.phase): \(.name)" + (if .detail != "" then "\n        \(.detail)" else "" end)' \
            "$RESULTS"
        printf '\n'
    fi

    if [ "$skipped" -gt 0 ]; then
        printf '  %sskipped%s\n' "$C_YELLOW" "$C_RESET"
        jq -r 'select(.status == "skip") | "    \(.name)" + (if .detail != "" then " -- \(.detail)" else "" end)' \
            "$RESULTS"
        printf '\n'
    fi

    # The diagnoses themselves. Whatever the grader said, these are what a human actually
    # wants to read after a release test, and they are the part no assertion can summarise.
    if [ -s "$WORKDIR/details.jsonl" ]; then
        printf '  %sdiagnoses%s\n' "$C_BOLD" "$C_RESET"
        jq -r '
            select(.investigations | length > 0)
            | . as $inc
            | [.investigations[].findings[]? | select(.isPrimary)] | .[0] // empty
            | "    \($inc.target.namespace)/\($inc.target.name) [\($inc.kind)]\n      \(.hypothesis)"
        ' "$WORKDIR/details.jsonl" 2>/dev/null || true
        printf '\n'
    fi

    # Budget, per incident. The invariant is asserted elsewhere; this is the number.
    if [ -s "$WORKDIR/details.jsonl" ] && [ "${LLM_AVAILABLE:-0}" = "1" ]; then
        printf '  %sllm accounting%s\n' "$C_BOLD" "$C_RESET"
        printf '    %-28s %8s %9s %7s\n' "incident" "steps" "tokens" "usd"
        jq -r '
            select(.investigations | length > 0)
            | . as $inc
            | .investigations[]
            | "    \($inc.target.name[0:28] | .+ (" " * (28 - length))) \(.stepsUsed|tostring|(" "*(8-length))+.) \((.inputTokens+.outputTokens)|tostring|(" "*(9-length))+.) \(.costUsd|tostring|(" "*(7-length))+.)"
        ' "$WORKDIR/details.jsonl" 2>/dev/null || true
        printf '\n'
    fi

    # Limits, restated every run. A green tick means only as much as what was actually
    # checked, and the things this harness cannot check are exactly the ones someone would
    # otherwise assume it did.
    printf '  %snot covered by this run%s\n' "$C_DIM" "$C_RESET"
    printf '    NetworkPolicy enforcement -- kind'"'"'s CNI accepts the objects and ignores them,\n'
    printf '      and that policy is the Alertmanager webhook'"'"'s entire authentication.\n'
    printf '    Root cause quality gates nothing -- only the deterministic assertions above do.\n'
    [ "${LLM_AVAILABLE:-0}" = "1" ] || \
    printf '    Investigations did not run at all -- no Gemini key was available.\n'
    printf '\n'

    printf '  full results: %s\n' "$RESULTS"
    printf '\n'

    # Three outcomes, not two. "Nothing recorded a failure" is not the same as "everything
    # was checked": a run that died before its last phase has an empty failure list and a
    # perfectly clean tally, and calling that PASSED is how a release gate lies.
    if [ "$aborted" != "0" ]; then
        printf '%s  ABORTED -- the run exited %s before finishing.%s\n' "$C_RED" "$aborted" "$C_RESET"
        printf '%s  %d assertions passed before it stopped; the rest never ran.%s\n\n' \
            "$C_RED" "$passed" "$C_RESET"
    elif [ "$failed" -gt 0 ]; then
        printf '%s  FAILED -- %d of %d assertions%s\n\n' "$C_RED" "$failed" "$total" "$C_RESET"
    else
        printf '%s  PASSED -- %d assertions, %d skipped%s\n\n' "$C_GREEN" "$passed" "$skipped" "$C_RESET"
    fi

    report_markdown
}

# A markdown copy alongside the terminal output, because comparing two runs means reading two
# of these side by side and terminal scrollback is a poor place to keep them.
report_markdown() {
    local md="$WORKDIR/report.md"
    {
        printf '# hephaisto e2e -- %s\n\n' "${VERSION:-unknown}"
        printf '| | |\n|---|---|\n'
        printf '| channel | %s |\n' "${CHANNEL:-nightly}"
        printf '| kubernetes | %s (kind, single node) |\n' "$E2E_K8S_VERSION"
        printf '| fixtures | %s |\n' "${APPLIED:-none}"
        printf '| wall clock | %dm %ds |\n' $(( (SECONDS - START_TIME) / 60 )) $(( (SECONDS - START_TIME) % 60 ))
        [ -n "${TOTAL_COST:-}" ]  && printf '| llm spend | $%s |\n' "$TOTAL_COST"
        [ -n "${JUDGE_SCORE:-}" ] && printf '| root cause | %s (reported, not gating) |\n' "$JUDGE_SCORE"
        printf '\n## Assertions\n\n| status | phase | assertion | detail |\n|---|---|---|---|\n'
        jq -r '"| \(.status) | \(.phase) | \(.name) | \(.detail) |"' "$RESULTS"

        if [ -s "$WORKDIR/judgements.jsonl" ]; then
            printf '\n## Root cause grading\n\n| fixture | verdict | reason |\n|---|---|---|\n'
            jq -r '"| \(.fixture) | \(if .correct then "correct" else "incorrect" end) | \(.reason) |"' \
                "$WORKDIR/judgements.jsonl"
        fi

        printf '\n## Not covered\n\n'
        printf -- '- NetworkPolicy enforcement: kind'"'"'s default CNI accepts the objects and does not\n'
        printf -- '  enforce them, and that policy is the Alertmanager webhook'"'"'s entire authentication.\n'
        printf -- '- Root cause quality is graded and reported, never gating.\n'
    } > "$md"
    say "markdown report: $md"
}
