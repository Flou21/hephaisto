# issues: G02 | - | exclusive | an unassigned issue and one in a repository that is not listed are ignored, beside an assigned one that is not
#
# What makes an issue work is two things, and each is missing once here: the bot is an assignee,
# and the repository is one the install lists. The third issue has both and is the control -
# without it "nothing happened to the other two" is true of an agent that polls nothing.
#
# The last assertion is about the price of polling. GitHub answers a list it has already given
# with a 304 that costs nothing against the rate limit, but only to a client that sends the ETag
# back; a poller that does not asks five thousand questions an hour's worth of nothing.

scenario() {
    issues_ready || return
    local mark assigned unassigned foreign

    mark=$(gh_mark)
    assigned=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G02 "assigned, in a listed repository")" "The control.") \
        || { fail "the stand-in opened an issue"; return; }
    unassigned=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G02 "nobody assigned it")" "Not Hephaisto's.")
    foreign=$(gh_issue_create "$ISSUES_FOREIGN_REPO" "$(issues_title G02 "assigned, in a repository that is not listed")" "Not Hephaisto's either.")
    gh_assign "$ISSUES_REPO" "$assigned"
    gh_assign "$ISSUES_FOREIGN_REPO" "$foreign"

    wi_wait "$ISSUES_REPO" "$assigned" && pass "the assigned issue in a listed repository became a work item" \
        || { fail "the assigned issue in a listed repository became a work item" "none within ${ISSUES_SEEN_WAIT}s"
             issues_done "$ISSUES_REPO" "$assigned" "$unassigned"; issues_done "$ISSUES_FOREIGN_REPO" "$foreign"; return; }

    # Twice more, so that the other two have been there to be found.
    gh_wait_polls "$(gh_mark)" 2 || fail "the agent kept polling" "fewer than two polls in ${ISSUES_SEEN_WAIT}s"

    want "the issue nobody assigned is not a work item" "$(wi_count "$ISSUES_REPO" "$unassigned")" -eq 0
    want "the issue in a repository that is not listed is not a work item" "$(wi_count "$ISSUES_FOREIGN_REPO" "$foreign")" -eq 0
    want "nothing was written on the issue nobody assigned" "$(gh_bot_comments "$ISSUES_REPO" "$unassigned" | jq length)" -eq 0
    want "nothing was written on the one that is not listed" "$(gh_bot_comments "$ISSUES_FOREIGN_REPO" "$foreign" | jq length)" -eq 0
    want "GitHub was never asked about the repository that is not listed" \
        "$(gh_requests_since "$mark" | jq --arg p "/repos/$ISSUES_FOREIGN_REPO/" '[.[] | select(.path | startswith($p))] | length')" -eq 0

    _g02_not_modified() {
        [ "$(gh_requests_since "$mark" | jq --arg p "/repos/$ISSUES_REPO/issues" '[.[] | select(.path == $p and .status == 304)] | length')" -ge 1 ]
    }
    wait_for "a poll that found nothing new" "$ISSUES_SEEN_WAIT" _g02_not_modified \
        && pass "a poll that found nothing new was a 304" \
        || fail "a poll that found nothing new was a 304" "no conditional request was answered 304 - is the ETag sent back?"

    issues_done "$ISSUES_REPO" "$assigned" "$unassigned"
    issues_done "$ISSUES_FOREIGN_REPO" "$foreign"
}
