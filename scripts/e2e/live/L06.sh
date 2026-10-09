# live: L06 | the two reactions to click are on the plan comment as GitHub tells it, and the approver's thumbs-down rejects the plan - on github.com
#
# A plan answered by a reaction (#298), asked of GitHub itself. Three things the stand-in can
# only assume: that the agent's token - Issues read and write, and nothing that names
# reactions - may SET a reaction on a comment and may LIST them; that GitHub's list names the
# account that reacted by number, which is what an approver is known by; and that what GitHub
# calls the two is what Hephaisto calls them, `rocket` and `-1`.
#
# The answer given here is the thumbs-down: the same door as the rocket, and it ends without a
# Job, a branch or a pull request to clean up. The rocket's road to a pull request is L01's,
# and the stand-in's G18.

scenario() {
    issues_ready || return
    local n attempt_id branch plan mine rollup mark attempt

    n=$(gh_issue_create "$LIVE_REPO" "$(live_title L06 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "gh opened an issue in the sandbox"; return; }
    gh_assign "$LIVE_REPO" "$n" || { fail "GitHub accepted the bot as the assignee"; issues_done "$LIVE_REPO" "$n"; return; }
    issues_plan_ready "$LIVE_REPO" "$n" || { issues_done "$LIVE_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")
    branch=$(jq -r '.branch // empty' <<<"$ATTEMPT")
    plan=$(jq -r '.planCommentId // empty' <<<"$ATTEMPT")
    [ -n "$plan" ] || { fail "the attempt names its plan comment"; issues_done "$LIVE_REPO" "$n"; return; }

    # --- the two to click, as GitHub lists them -------------------------------------------------
    _l06_offered() {
        [ "$(gh_reactions "$LIVE_REPO" "$n" "$plan" | jq -r --arg b "$LIVE_BOT" '[.[] | select(.user.login == $b) | .content] | sort | join(" ")')" = "-1 rocket" ]
    }
    wait_for "the bot's two reactions on its plan" "$ISSUES_SEEN_WAIT" _l06_offered \
        && pass "GitHub lists a rocket and a thumbs-down by the bot account on the plan comment" \
        || { fail "GitHub lists a rocket and a thumbs-down by the bot account on the plan comment" \
                "$(gh_reactions "$LIVE_REPO" "$n" "$plan" | jq -c '[.[] | {content, by: .user.login}]')"; issues_done "$LIVE_REPO" "$n"; return; }

    mine=$(gh_reactions "$LIVE_REPO" "$n" "$plan")
    want "and nobody else has reacted yet" "$(jq 'length' <<<"$mine")" -eq 2
    want "each names the account by number as well as by name" \
        "$(jq -r '[.[] | (.user.id | type) + ":" + (.id | type)] | unique | join(" ")' <<<"$mine")" = "number:number"

    # What a person sees below the comment is GitHub's own count of them.
    rollup=$(live_comment "$plan" | jq -r '"\(.reactions.rocket) \(.reactions["-1"]) \(.reactions.total_count)"')
    want "GitHub shows both below the comment: one rocket, one thumbs-down" "$rollup" = "1 1 2"

    # --- its own answer nothing -----------------------------------------------------------------
    mark=$(gh_mark)
    gh_wait_polls "$mark" 2 || true
    want "the bot's own reactions approved nothing" "$(wi_attempt_state "$WI")" = PlanReady

    # --- the person's click ---------------------------------------------------------------------
    gh_react_as "$LIVE_REPO" "$plan" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" -1 >/dev/null \
        || { fail "gh set a thumbs-down on the plan comment"; issues_done "$LIVE_REPO" "$n"; return; }

    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Denied Implementing PrOpened Failed Cancelled || true
    attempt=$(wi_attempt "$WI")
    want "the approver's thumbs-down rejected the plan" "$(jq -r '.state // empty' <<<"$attempt")" = Denied
    want "by the GitHub account that clicked" "$(jq -r '.approvedBy // empty' <<<"$attempt")" = "github:$ISSUES_APPROVER"
    want "and it is on record how it was given" \
        "$(jq -r '.failureReason // empty' <<<"$attempt")" = "no reason given: rejected with a thumbs-down on the plan"

    _l06_told() { [ "$(gh_bot_said "$LIVE_REPO" "$n" 'The plan was rejected')" -ge 1 ]; }
    wait_for "the issue to say that the plan was rejected" "$ISSUES_SEEN_WAIT" _l06_told \
        && pass "the status comment says that the plan was rejected" \
        || fail "the status comment says that the plan was rejected" "no comment of the bot's says so"

    want "no Job implemented anything" "$(issues_job_count "$attempt_id" implement)" -eq 0
    want "no branch was pushed" "$(issues_git_rev "${branch:-none}")" = ""
    want "and the bot wrote nothing more: two comments" "$(gh_bot_comments "$LIVE_REPO" "$n" | jq 'length')" -eq 2

    issues_done "$LIVE_REPO" "$n"
}
