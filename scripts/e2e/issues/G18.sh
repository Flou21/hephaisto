# issues: G18 | kubectl | exclusive | an approver's rocket on the plan comment is an approval: one implementing Job, and the reactions to click are Hephaisto's own
#
# The other way to answer a plan (#298). GitHub's comment editor suggests none of the three
# commands, and a mistyped one is passed over in silence; a reaction is one click. Hephaisto
# sets a rocket and a thumbs-down on its own plan comment, so that GitHub shows both below it -
# and those two are never an answer, or every plan would approve itself.
#
# Then the click: an approver's rocket on THAT comment goes through the door /approve knocks
# on. One implementing Job, the approver on record by the account that reacted, and - the
# reaction stays on the comment for good - still one Job after the agent has looked again.

scenario() {
    issues_ready || return
    local n attempt attempt_id plan own mark url

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G18 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it. The total reads null where it should read 0.") \
        || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")
    plan=$(issues_plan_comment_id "$ISSUES_REPO" "$n" "$attempt_id")
    [ -n "$plan" ] || { fail "the plan is a comment on the issue" "none carries the attempt's marker"; issues_done "$ISSUES_REPO" "$n"; return; }

    want "the plan says that a click answers it" "$(gh_bot_said "$ISSUES_REPO" "$n" 'clicks 🚀 below this comment')" -ge 1
    want "and each command stands in a block of its own, to be copied" \
        "$(issues_plan_comment "$ISSUES_REPO" "$n" "$attempt_id" | grep -cxF -e '/approve' -e '/replan' -e '/reject <reason>')" -eq 3

    # Both to click, set by the bot's own account - by the pass that wrote the plan, so they
    # are there now and not a poll later.
    own=$(gh_reactions "$ISSUES_REPO" "$n" "$plan" | jq -c --arg b "$ISSUES_BOT" '[.[] | select(.user.login == $b) | .content] | sort')
    want "Hephaisto set a rocket and a thumbs-down on its plan" "$own" = '["-1","rocket"]'

    # ... and they answer nothing: the agent reads them twice, and the plan still waits.
    mark=$(gh_mark)
    gh_wait_reaction_reads "$mark" 2 "$ISSUES_REPO" "$plan" || fail "the agent reads the reactions on a waiting plan" "fewer than two reads in ${ISSUES_SEEN_WAIT}s"
    want "its own reactions approved nothing" "$(wi_attempt_state "$WI")" = PlanReady
    want "and are still the only ones" "$(gh_reactions "$ISSUES_REPO" "$n" "$plan" | jq 'length')" -eq 2

    gh_react_as "$ISSUES_REPO" "$plan" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" rocket >/dev/null

    wi_wait_attempt "$WI" "$ISSUES_IMPLEMENT_WAIT" PrOpened Failed Cancelled Denied Expired || true
    attempt=$(wi_attempt "$WI")
    url=$(jq -r '.prUrl // empty' <<<"$attempt")
    [ "$(jq -r .state <<<"$attempt")" = PrOpened ] && pass "the click ended in a pull request" "$url" \
        || { fail "the click ended in a pull request" "$(jq -r '(.state // "no attempt") + ": " + (.failureReason // "")' <<<"$attempt")"; issues_done "$ISSUES_REPO" "$n"; return; }

    want "the approver is on record, by the account that reacted" \
        "$(jq --arg a "$ISSUES_APPROVER" '(.approvedBy // "") | contains($a)' <<<"$attempt")" = true
    want "exactly one Job implemented it" "$(issues_job_count "$attempt_id" implement)" -eq 1
    want "and it is still the one attempt" "$(wi_attempt_count "$WI")" -eq 1
    want "nothing was said on the issue about the click itself" "$(gh_bot_said "$ISSUES_REPO" "$n" 'Not done|Not counted')" -eq 0

    # The rocket is still on the comment. Three more polls: it is not read as a second approval.
    mark=$(gh_mark)
    gh_wait_polls "$mark" 3 || true
    want "the reaction that stayed started nothing more" "$(issues_job_count "$attempt_id" implement)" -eq 1

    issues_done "$ISSUES_REPO" "$n"
}
