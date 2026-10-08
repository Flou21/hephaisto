# live: L05 | the planner's questions are on the real issue, an answer and /replan give a second plan, and assigning again after a rejection a third
#
# The issue as a conversation (#286, #252, #285), on github.com. What the stand-in cannot say:
# whether GitHub renders the questions as a list and the notes as a fold - a comment is Markdown
# GitHub interprets, and a <details> block is HTML it may or may not let through - whether its
# comment list hands back an answer the way the agent's reading of it assumes, and whether its
# TIMELINE says when the bot was assigned, which is the one thing that tells an assignment made
# between two polls from one that was there all along.
#
# The person is one account here, the approver and the issue's author at once, so "nobody else's
# comment reaches the Job" stays the stand-in's (G14).

scenario() {
    issues_ready || return
    local n first first_id second second_id html questions shown job request plans polls
    local answer="L05-ANSWER-${ISSUES_RUN}"

    n=$(gh_issue_create "$LIVE_REPO" "$(live_title L05 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it. The total reads null where it should read 0.") \
        || { fail "gh opened an issue in the sandbox"; return; }
    gh_assign "$LIVE_REPO" "$n" || { fail "GitHub accepted the bot as the assignee"; issues_done "$LIVE_REPO" "$n"; return; }
    issues_plan_ready "$LIVE_REPO" "$n" || { issues_done "$LIVE_REPO" "$n"; return; }
    first="$ATTEMPT"; first_id=$(jq -r .id <<<"$first")

    # The plan comment, as GitHub rendered it.
    questions=$(jq -c '.questions // []' <<<"$first")
    want "the plan asks something of a person" "$(jq 'length' <<<"$questions")" -ge 1
    html=$(live_comment "$(jq -r '.planCommentId' <<<"$first")" | jq -r '.body_html // empty')
    [ -n "$html" ] && pass "GitHub has the plan comment" || { fail "GitHub has the plan comment" "no body_html for comment $(jq -r '.planCommentId' <<<"$first")"; issues_done "$LIVE_REPO" "$n"; return; }
    shown=$(jq -r --arg h "$html" '[.[] | select(. as $q | $h | contains("<li>" + $q + "</li>"))] | length' <<<"$questions")
    want "GitHub renders every question as an item of a list" "$shown" -eq "$(jq 'length' <<<"$questions")"
    want "and the planner's notes as a fold" "$(grep -c '<details>' <<<"$html")" -eq 1
    want "whose summary is Hephaisto's own" "$(grep -c '<summary>The planner' <<<"$html")" -eq 1
    want "the plan names /replan" "$(grep -c '<code[^>]*>/replan</code>' <<<"$html")" -ge 1

    # An answer, and the command - with a second answer in the command's own comment.
    gh_comment_as "$LIVE_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" \
        "To 1: an absent section means no endpoints. FAKE-SDK-REPEAT: ${answer}" >/dev/null \
        || { fail "gh commented an answer"; issues_done "$LIVE_REPO" "$n"; return; }
    gh_comment_as "$LIVE_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/replan
To 2: leave the second list alone." >/dev/null || { fail "gh commented /replan"; issues_done "$LIVE_REPO" "$n"; return; }

    issues_next_plan_ready "$WI" 2 || { issues_done "$LIVE_REPO" "$n"; return; }
    second="$ATTEMPT"; second_id=$(jq -r .id <<<"$second")

    want "it is the same work item" "$(wi_count "$LIVE_REPO" "$n")" -eq 1
    first=$(wi_attempts "$WI" | jq -c '.[0]')
    want "the first plan ended as denied, by the replanning" \
        "$(jq -r '"\(.state): \(.failureReason)"' <<<"$first")" = "Denied: replanned by github:${ISSUES_APPROVER}"

    job=$(jq -r '.planJobName // empty' <<<"$second")
    request=$(issues_request "$job")
    [ -n "$request" ] && pass "the replanning Job's request can be read" \
        || { fail "the replanning Job's request can be read" "no ConfigMap ${job}-req"; issues_done "$LIVE_REPO" "$n"; return; }
    want "it carries the two comments GitHub was given, in order, and none of the bot's" \
        "$(jq -c --arg a "$ISSUES_APPROVER" --arg t "$answer" '.work_item.comments | (length == 2 and all(.[]; .author == $a) and (.[0].body | contains($t)) and (.[1].body | startswith("/replan")))' <<<"$request")" = true
    want "and what the earlier plan asked" "$(jq --argjson f "$first" '.previous.questions == $f.questions' <<<"$request")" = true

    plans=$(gh_bot_comments "$LIVE_REPO" "$n" | jq -c '[.[] | select(.body | contains("hephaisto:plan:"))]')
    want "the issue carries two plan comments" "$(jq length <<<"$plans")" -eq 2
    want "the second was made with the answer" "$(issues_plan_comment "$LIVE_REPO" "$n" "$second_id" | grep -c "$answer")" -ge 1
    want "and says that it replaces the first" "$(issues_plan_comment "$LIVE_REPO" "$n" "$second_id" | grep -c 'new plan for this issue')" -ge 1
    want "the bot has written three comments: the status and two plans" "$(gh_bot_comments "$LIVE_REPO" "$n" | jq length)" -eq 3

    # The second plan is refused too, and then what the person on the first real issue did:
    # off and on again, a second apart. GitHub's list of assigned issues shows it assigned
    # before and after; only the timeline says that the assignment is a new one.
    gh_comment_as "$LIVE_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/reject nor this ${ISSUES_RUN}" >/dev/null
    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Denied || { fail "the second plan was rejected" "the attempt is $(wi_attempt_state "$WI")"; issues_done "$LIVE_REPO" "$n"; return; }

    polls=$(gh_mark)
    gh_reassign "$LIVE_REPO" "$n" || { fail "GitHub took the bot off the issue and put it back"; issues_done "$LIVE_REPO" "$n"; return; }

    _l05_followed() { [ "$(wi_attempt_count "$WI")" -ge 3 ] || [ "$(wi_count "$LIVE_REPO" "$n")" -ge 2 ]; }
    wait_for "the new assignment to be noticed" "$ISSUES_SEEN_WAIT" _l05_followed || true

    if [ "$(wi_count "$LIVE_REPO" "$n")" -ge 2 ]; then
        # A poll fell into the second between the two requests: the agent saw the issue
        # unassigned, and that is the road it always had - a new work item.
        skip "an assignment made between two polls is known by GitHub's timeline" \
            "a poll saw the issue unassigned in between ($(( $(gh_mark) - polls )) polls since), so this run took the other road"
    else
        want "an assignment made between two polls is known by GitHub's timeline: a third attempt, same work item" \
            "$(wi_attempt_count "$WI")" -eq 3
        want "its Job is told what was planned before" \
            "$(issues_request "$(wi_attempt "$WI" | jq -r '.planJobName // empty')" | jq '(.previous.summary // "") | length > 0')" = true
        wi_wait_attempt "$WI" "$ISSUES_PLAN_WAIT" PlanReady Failed Cancelled Denied Expired || true
        want "and it is planned" "$(wi_attempt_state "$WI")" = PlanReady
    fi

    issues_done "$LIVE_REPO" "$n"
}
