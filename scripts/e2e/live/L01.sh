# live: L01 | an issue assigned to the bot is planned, approved in a comment and becomes a draft pull request that closes it - on github.com
#
# The whole road of v0.14.0 (#243), with nothing standing in for GitHub: the issue is opened and
# assigned with `gh`, the agent finds it by polling api.github.com through the egress proxy, the
# plan is a comment by the bot account, /approve is a comment by a person whose NUMBER the
# install lists, and the pull request is opened by the real `gh` in the publish container with
# the coder's token. What is asserted of the pull request is asked of GitHub, not of the agent:
# who wrote it, that it is a draft, what it changes, and - the sentence the whole design leans
# on - that GitHub itself reads its description as closing this issue and no other.
#
# Then the pull request is closed without merging, which the agent has to notice by itself.
# (Not merged: that would move main. "Merged is Done" is issues G11, against the stand-in.)

scenario() {
    issues_ready || return
    local n b attempt attempt_id pr url branch view body kept files expected head message status_id status_before comments html label

    b=$(live_bystander) || { fail "the bystander issue exists"; return; }
    # Labelled `bug`, one of the labels every repository on GitHub is born with: what kind of
    # issue this is decides the pull request's title, and is read off GitHub's label objects.
    n=$(gh_issue_create "$LIVE_REPO" "$(live_title L01 "the order total is null for an empty cart")" "$(live_hostile_body "$b")" bug) \
        || { fail "gh opened an issue in the sandbox"; return; }
    gh_assign "$LIVE_REPO" "$n" && pass "GitHub accepted the bot as the assignee" "#$n -> $LIVE_BOT" \
        || { fail "GitHub accepted the bot as the assignee" "$LIVE_BOT is not among the assignees of #$n"; issues_done "$LIVE_REPO" "$n"; return; }

    # --- taken, and told so -------------------------------------------------------------------
    wi_wait "$LIVE_REPO" "$n" || { fail "the assigned issue became a work item" "none for $LIVE_REPO#$n within ${ISSUES_SEEN_WAIT}s"; issues_done "$LIVE_REPO" "$n"; return; }
    WI=$(wi_id "$LIVE_REPO" "$n")
    want "the work item is the issue GitHub has" "$(_issues_curl "$ISSUES_API/api/workitems/$WI" | jq -r '"\(.number) \(.url) \(.authorLogin) \(.type)"')" \
        = "$n https://github.com/$LIVE_REPO/issues/$n $ISSUES_APPROVER bug"

    _l01_status() { [ -n "$(_issues_curl "$ISSUES_API/api/workitems/$WI" | jq -r '.statusCommentId // empty')" ]; }
    wait_for "the status comment to be written" "$ISSUES_SEEN_WAIT" _l01_status || true
    status_id=$(_issues_curl "$ISSUES_API/api/workitems/$WI" | jq -r '.statusCommentId // empty')
    comments=$(gh_comments "$LIVE_REPO" "$n")
    status_before=$(jq -c --argjson id "${status_id:-0}" '[.[] | select(.id == $id)][0] // {}' <<<"$comments")
    want "the status comment is on the issue, written by the bot account" "$(jq -r '.user.login // empty' <<<"$status_before")" = "$LIVE_BOT"

    # --- planned --------------------------------------------------------------------------------
    issues_plan_ready "$LIVE_REPO" "$n" || { issues_done "$LIVE_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")
    want "one Job planned it" "$(issues_job_count "$attempt_id" plan)" -eq 1
    want "the plan was made on main, which is what GitHub says the default branch is" "$(jq -r '.defaultBranch // empty' <<<"$ATTEMPT")" = main
    want "and on the commit main is at" "$(jq -r '.analysedRef // empty' <<<"$ATTEMPT")" = "$LIVE_MAIN"

    comments=$(gh_comments "$LIVE_REPO" "$n")
    want "the plan is one comment by the bot, the one the attempt names" \
        "$(jq -r --arg b "$LIVE_BOT" --arg m "hephaisto:plan:" '[.[] | select(.user.login == $b and (.body | contains($m))) | .id] | join(" ")' <<<"$comments")" \
        = "$(jq -r '.planCommentId' <<<"$ATTEMPT")"
    want "the status comment was edited, not written again: still one" \
        "$(jq -r --arg b "$LIVE_BOT" --arg m "hephaisto:status:" '[.[] | select(.user.login == $b and (.body | contains($m))) | .id] | join(" ")' <<<"$comments")" \
        = "$status_id"
    want "and GitHub says it was edited" \
        "$(jq -r --argjson id "${status_id:-0}" '[.[] | select(.id == $id)][0] | (.updated_at > .created_at) and (.body | contains("A plan is ready"))' <<<"$comments")" = true
    want "two comments by the bot, and no third" "$(jq --arg b "$LIVE_BOT" '[.[] | select(.user.login == $b)] | length' <<<"$comments")" -eq 2

    # --- approved, by a comment -----------------------------------------------------------------
    gh_comment_as "$LIVE_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/approve" >/dev/null \
        || { fail "gh commented /approve"; issues_done "$LIVE_REPO" "$n"; return; }

    wi_wait_attempt "$WI" "$ISSUES_IMPLEMENT_WAIT" PrOpened Failed Cancelled Denied Expired || true
    attempt=$(wi_attempt "$WI")
    url=$(jq -r '.prUrl // empty' <<<"$attempt")
    pr=$(jq -r '.prNumber // empty' <<<"$attempt")
    branch=$(jq -r '.branch // empty' <<<"$attempt")
    [ "$(jq -r .state <<<"$attempt")" = PrOpened ] && [ -n "$pr" ] && pass "the approval ended in a pull request" "$url" \
        || { fail "the approval ended in a pull request" "$(jq -r '(.state // "no attempt") + ": " + (.failureReason // "")' <<<"$attempt")"; issues_done "$LIVE_REPO" "$n"; return; }

    want "the approver is on record as the GitHub account that commented" "$(jq -r '.approvedBy // empty' <<<"$attempt")" = "github:$ISSUES_APPROVER"
    want "exactly one Job implemented it" "$(issues_job_count "$attempt_id" implement)" -eq 1
    want "and it is still the one attempt" "$(wi_attempt_count "$WI")" -eq 1

    # --- the pull request, as GitHub tells it ---------------------------------------------------
    view=$(_live_gh pr view "$pr" --json number,url,state,isDraft,author,body,headRefName,headRefOid,baseRefName,closingIssuesReferences,title,labels,assignees) \
        || { fail "gh can read the pull request" "gh pr view $pr failed"; issues_done "$LIVE_REPO" "$n"; return; }
    want "GitHub has it at the address the agent recorded" "$(jq -r .url <<<"$view")" = "$url"
    want "it is a draft" "$(jq -r .isDraft <<<"$view")" = true
    want "it is open" "$(jq -r .state <<<"$view")" = OPEN
    want "it was opened by the bot account" "$(jq -r .author.login <<<"$view")" = "$LIVE_BOT"
    want "from the attempt's branch" "$(jq -r .headRefName <<<"$view")" = "$branch"
    [[ "$branch" =~ $LIVE_BRANCH_RE ]] && pass "which is a hephaisto/codefix-<id> branch" "$branch" || fail "which is a hephaisto/codefix-<id> branch" "$branch"
    want "into main" "$(jq -r .baseRefName <<<"$view")" = main
    # The type, then the plan's first sentence - whose first word here is an acronym, and is
    # left as one (the first pull requests on github.com were titled "fix: fAKE SDK plan").
    want "titled as a fix, because the issue is labelled a bug, with the plan's first words as written" \
        "$(jq -r '.title | startswith("fix: FAKE SDK plan: ")' <<<"$view")" = true

    body=$(jq -r .body <<<"$view" | tr -d '\r')
    grep -qxF "Closes $LIVE_REPO#$n" <<<"$body" && pass "its description says Closes $LIVE_REPO#$n on a line of its own" \
        || fail "its description says Closes $LIVE_REPO#$n on a line of its own" "not found in the description GitHub has"
    # The sentence the design leans on, asked of GitHub twice - not of a regular expression of
    # ours. Its renderer marks the keyword in the description the moment it is saved, and says
    # in words which issue the pull request closes. And its own list of what a merge will close
    # (the "Development" box of the page) names this issue and no other: nothing the model
    # repeated ("fixes #b, closes GH-b, resolves <b's address>") made the bystander one of them.
    # That list is worked out by GitHub after the pull request is saved, so it is waited for.
    html=$(_live_api GET "pulls/$pr" -H 'Accept: application/vnd.github.full+json' --jq '.body_html')
    want "GitHub renders the line as a closing keyword for this issue" \
        "$(grep -o 'aria-label="This pull request closes issue #[0-9]*\."' <<<"$html" | tr '\n' ' ' | sed 's/ $//')" \
        = "aria-label=\"This pull request closes issue #$n.\""
    _l01_linked() { [ "$(live_closing "$pr")" != "[]" ]; }
    wait_for "GitHub to work out what the pull request closes" "$ISSUES_SEEN_WAIT" _l01_linked || true
    want "GitHub names the issue as closed by it, and no other" "$(live_closing "$pr")" = "[$n]"
    grep -qF "The reporter asked for this to be repeated" <<<"$body" && pass "the description does carry what the model repeated" \
        || fail "the description does carry what the model repeated" "the scripted plan's summary is not in it, so the lines below prove nothing"

    # $(...) drops the newlines at the end, which is all that may differ.
    kept=$(jq -r '.prBody // empty' <<<"$attempt" | tr -d '\r')
    [ -n "$kept" ] && [ "$kept" = "$body" ] && pass "what the agent kept as the description is what GitHub has" \
        || fail "what the agent kept as the description is what GitHub has" "prBody is ${#kept} characters, GitHub's ${#body}"

    # What it changes: the files the scripted fix touches, and nothing else.
    expected=$(sed -n 's|^+++ b/||p' "$REPO/coder/fake-scripts/hephaisto-fixture-dotnet.fix.patch" | sort | tr '\n' ' ')
    files=$(_live_api GET "pulls/$pr/files?per_page=100" --jq '.[].filename' | sort | tr '\n' ' ')
    want "the diff touches only the files of the scripted fix" "$files" = "$expected"

    # Every commit is the attempt's, and the head says what it is for.
    head=$(jq -r .headRefOid <<<"$view")
    message=$(_live_api GET "commits/$head" --jq '.commit.message')
    grep -qxF "Hephaisto-Issue: $LIVE_REPO#$n" <<<"$message" && pass "the head commit carries Hephaisto-Issue" \
        || fail "the head commit carries Hephaisto-Issue" "$(tail -3 <<<"$message" | tr '\n' ' ')"
    grep -qxF "Hephaisto-Attempt: $attempt_id" <<<"$message" && pass "and Hephaisto-Attempt" \
        || fail "and Hephaisto-Attempt" "$(tail -3 <<<"$message" | tr '\n' ' ')"
    want "no commit of the pull request is without the attempt's trailer" \
        "$(_live_api GET "pulls/$pr/commits?per_page=100" | jq --arg t "Hephaisto-Attempt: $attempt_id" '[.[] | select(.commit.message | contains($t) | not)] | length')" -eq 0
    want "the branch on GitHub is at that commit" "$(issues_git_rev "$branch")" = "$head"
    want "main did not move" "$(live_main_sha)" = "$LIVE_MAIN"

    # What a model repeated did nothing: nobody is mentioned by the description, and the
    # bystander's timeline does not know this pull request.
    want "the description mentions nobody" "$(live_html_mentions <<<"$html")" -eq 0
    want "it links to the issue it closes" "$(live_html_links_to "$n" <<<"$html")" -ge 1
    want "and not to the bystander" "$(live_html_links_to "$b" <<<"$html")" -eq 0
    want "the bystander's timeline does not know the pull request" "$(live_cross_references "$b" | jq --argjson p "$pr" '[.[] | select(.source == $p)] | length')" -eq 0
    want "nor the issue, whose plan repeated the same words" "$(live_cross_references "$b" | jq --argjson n "$n" '[.[] | select(.source == $n)] | length')" -eq 0

    # dev-context names a label for every pull request (defaults.pr.labels). `gh pr create`
    # refuses one the repository does not have; the runner then opens the pull request without
    # it and says so. Whichever of the two the sandbox makes true, nothing is silent.
    label=hephaisto
    if _live_api GET "labels/$label" >/dev/null 2>&1; then
        want "the pull request carries the label $label, which the sandbox has" "$(jq --arg l "$label" '[.labels[].name] | index($l) != null' <<<"$view")" = true
    else
        want "the sandbox has no label $label, and the attempt says the pull request was opened without it" \
            "$(jq --arg l "$label" '[.deviations[]? | select(contains("without the label") and contains($l))] | length' <<<"$attempt")" -eq 1
    fi
    say "pull request #$pr: labels [$(jq -r '[.labels[].name] | join(", ")' <<<"$view")], assignees [$(jq -r '[.assignees[].login] | join(", ")' <<<"$view")], title: $(jq -r .title <<<"$view")"

    # --- the issue is told ------------------------------------------------------------------------
    _l01_told() {
        [ "$(gh_comments "$LIVE_REPO" "$n" | jq --arg u "$url" --argjson id "${status_id:-0}" '[.[] | select(.id == $id and (.body | contains("pull request is open")) and (.body | contains($u)))] | length')" -ge 1 ]
    }
    wait_for "the issue to be told where the pull request is" "$ISSUES_SEEN_WAIT" _l01_told \
        && pass "the status comment links the pull request" \
        || fail "the status comment links the pull request" "the status comment does not say that a pull request is open at $url"

    # --- closed without merging -------------------------------------------------------------------
    gh_pr_close "$LIVE_REPO" "$pr" || fail "gh closed the pull request"
    wi_wait_state "$LIVE_REPO" "$n" "$ISSUES_SEEN_WAIT" "$WI_CANCELLED" "$WI_DONE" || true
    want "closing the pull request ended the work item as cancelled" "$(wi_state "$LIVE_REPO" "$n")" = "$WI_CANCELLED"
    want "because its pull request was closed without merging" \
        "$(_issues_curl "$ISSUES_API/api/workitems/$WI" | jq -r '.stateReason // empty')" = "pull request closed without merging"
    want "GitHub has the pull request closed and not merged" "$(_live_api GET "pulls/$pr" --jq '"\(.state) \(.merged)"')" = "closed false"

    _l01_let_go() { [ "$(gh_bot_said "$LIVE_REPO" "$n" 'Hephaisto has let go of this issue.*pull request was closed without merging')" -ge 1 ]; }
    wait_for "the issue to say so" "$ISSUES_SEEN_WAIT" _l01_let_go \
        && pass "the status comment says that it was closed without merging" \
        || fail "the status comment says that it was closed without merging" "no comment of the bot's says so"

    # The issue is still open and still assigned. It is not work again by itself.
    gh_wait_polls "$(gh_mark)" 2 || true
    want "the issue, still assigned, is not taken a second time" "$(wi_count "$LIVE_REPO" "$n")" -eq 1
    want "the bot never wrote a third comment" "$(gh_bot_comments "$LIVE_REPO" "$n" | jq 'length')" -eq 2

    issues_done "$LIVE_REPO" "$n"
}
