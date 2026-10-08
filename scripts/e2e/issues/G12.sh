# issues: G12 | kubectl | exclusive | with the code-fix mode off or at plan, /approve is refused and says why
#
# The mode is the operator's, and a comment does not outrank it. At `plan` the agent plans and
# opens nothing; at `off` it does not run the stage at all. An approver who answers a plan that
# was made before the mode was lowered is refused - and told that it is the mode, since from the
# issue "nothing happened" and "you are not an approver" would otherwise look the same.
#
# The switch is the ConfigMap key lib/codefix.sh turns, lowered and put back here; the positive
# control - at `pr`, the same comment implements - is G03.

G12_FOUND=""

scenario() {
    issues_ready || return
    local n attempt_id mode effective told before

    G12_FOUND=$(issues_switch_get)
    [ -n "$G12_FOUND" ] || { fail "the code-fix switch can be read" "no codeFixMode in $ISSUES_SWITCHES_CM"; return; }
    # What the agent makes of the switch and its other arms, before anything is lowered.
    before=$(_issues_curl "$ISSUES_API/api/codefixes/mode" | jq -r '.effective // empty')
    # Whatever happens below. A switch left lowered turns every later scenario red, and the dev
    # cluster's own code fixes off.
    trap 'issues_switch_set "$G12_FOUND"' EXIT

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G12 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")

    for mode in plan off; do
        issues_switch_set "$mode"
        case "$mode" in plan) effective=Plan ;; *) effective=Off ;; esac
        wait_for "the mode to be $effective" 180 issues_mode_is "$effective" \
            || { fail "the mode was lowered to $mode" "it is $(_issues_curl "$ISSUES_API/api/codefixes/mode" | jq -r .effective)"; continue; }

        gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/approve" >/dev/null

        # "the code-fix mode is plan", "mode: off" - the word and the value in one sentence.
        # In a variable first: bash 3.2 brace-expands {0,40} when it stands inside a quoted
        # string inside a command substitution inside a quoted string, and the check was then
        # `[ 0 0 -ge 1 ]` for ever - whatever the agent had written.
        told="\\bmode\\b[^.\\n]{0,40}\\b${mode}\\b"
        _g12_told() { [ "$(gh_bot_said "$ISSUES_REPO" "$n" "$told")" -ge 1 ]; }
        wait_for "the refusal" "$ISSUES_SEEN_WAIT" _g12_told \
            && pass "at $mode the approver is told that the mode is $mode" \
            || fail "at $mode the approver is told that the mode is $mode" "no comment of the bot's says so"
        want "at $mode no Job implemented anything" "$(issues_job_count "$attempt_id" implement)" -eq 0
        want "at $mode the attempt was not approved" "$(wi_attempt "$WI" | jq -r '.approvedBy // ""')" = ""
    done

    issues_switch_set "$G12_FOUND"

    # Putting the key back is not the mode being back: the agent reads the ConfigMap from a
    # volume, and the kubelet takes up to a minute to carry a change there. A scenario that
    # ended before that left the cluster's code fixes off for whatever ran next - on 2026-10-07
    # codefix-local.sh started 36 seconds after this suite and its preflight read "Off".
    if [ -n "$before" ]; then
        wait_for "the mode to be $before again" 180 issues_mode_is "$before" \
            && pass "the mode is back where it was found" \
            || fail "the mode is back where it was found" "it is $(_issues_curl "$ISSUES_API/api/codefixes/mode" | jq -r .effective), and was $before"
    fi

    issues_done "$ISSUES_REPO" "$n"
}
