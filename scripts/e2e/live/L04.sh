# live: L04 | what was only assumed of GitHub: an unchanged poll is a 304 through the proxy, and text Hephaisto repeats mentions nobody and references nothing
#
# Three things the stand-in answered the way this project's authors expected, asked of GitHub:
#
#   1. A poll of a repository in which nothing changed is answered 304 Not Modified, to the
#      entity tag GitHub itself handed out - which is what makes polling every few seconds cost
#      nothing against the rate limit. Counted by the agent (hephaisto_github_polls_total), and
#      the agent's connection is the egress proxy's: its log has the tunnel to api.github.com.
#   2. `github` is Healthy in /api/status with a real token.
#   3. What IssueComments.Neutralise does to text a model or a person wrote is enough for
#      GitHub's renderer. The scripted model repeats a line of the issue - a mention, "#n",
#      "GH-n", an issue's address - into the plan; the approver's /reject carries the same into
#      the status comment. Asked of GitHub: the comment's HTML has no mention and no link to the
#      bystander, and the bystander's timeline has no reference from this issue.
#
# The third has a control, because "nothing happened" proves nothing by itself: the approver's
# own /reject comment says the same words WITHOUT being neutralised, and for that one GitHub
# does render a mention and does write the reference into the bystander's timeline.

scenario() {
    issues_ready || return
    local n b before_304 before_ok ip tunnels plan html reject_id reason pr

    # --- 1 and 2: a quiet repository ----------------------------------------------------------------
    want "github is Healthy in /api/status" "$(issues_github_health)" = Healthy

    # Quiet first: the scenario before this one ended by closing its issue, and the poll that
    # sees the list change is read in full. Counted from the first 304 after it.
    _l04_idle() { [ "$(wi_all | jq --arg r "$LIVE_REPO" '[.[] | select(.repository == $r and .state == "Taken")] | length')" -eq 0 ]; }
    wait_for "the agent to hold nothing of the sandbox" "$ISSUES_SEEN_WAIT" _l04_idle || true
    before_304=$(live_polls not_modified)
    _l04_settled() { [ $(( $(live_polls not_modified) - before_304 )) -ge 1 ]; }
    wait_for "the first poll that finds nothing changed" "$ISSUES_SEEN_WAIT" _l04_settled || true

    before_304=$(live_polls not_modified); before_ok=$(live_polls ok)
    _l04_quiet() { [ $(( $(live_polls not_modified) - before_304 )) -ge 3 ]; }
    wait_for "three polls of a repository in which nothing changes" "$ISSUES_SEEN_WAIT" _l04_quiet || true
    want "an unchanged repository is answered 304 Not Modified" $(( $(live_polls not_modified) - before_304 )) -ge 3
    want "and is not read in full meanwhile" $(( $(live_polls ok) - before_ok )) -eq 0
    want "GitHub limited nothing and refused nothing" $(( $(live_polls rate_limited) + $(live_polls unauthorized) + $(live_polls not_found) )) -eq 0

    # The agent asks two more things with a tag, on every pass, for as long as a plan waits and
    # for as long as a pull request is open - days. Its own counter is of the list only, so
    # GitHub is asked here, as the person, the way the agent's client asks: same headers, the
    # tag handed back as given.
    pr=$(_live_api GET "pulls?state=all&per_page=1" --jq '.[0].number // empty' 2>/dev/null)
    if [ -n "$pr" ]; then
        want "GitHub answers 304 for a pull request that did not change" "$(live_asked_twice "pulls/$pr")" = "200 304"
    else
        skip "GitHub answers 304 for a pull request that did not change" "the sandbox has never had a pull request; L01 opens the first"
    fi

    want "the agent's GitHub client is given the egress proxy" \
        "$(kc -n "$ISSUES_NS" get deploy "$ISSUES_DEPLOY" -o json | jq -r '[.spec.template.spec.containers[0].env[] | select(.name == "GitHub__ProxyUrl")][0].value // empty' | grep -c 'hephaisto-coder-egress')" -eq 1
    # squid writes a tunnel's line when the tunnel ends, and the agent keeps one for up to five
    # minutes (PooledConnectionLifetime): the first line of a young pod may still be to come.
    ip=$(kc -n "$ISSUES_NS" get pods -l "app.kubernetes.io/name=$ISSUES_DEPLOY" -o json | jq -r '[.items[] | select(.metadata.deletionTimestamp == null)][0].status.podIP // empty')
    _l04_tunnels() { [ "$(kc -n "$ISSUES_CODER_NS" logs deploy/hephaisto-coder-egress --since=30m 2>/dev/null | grep -F " $ip " | grep -c 'CONNECT api.github.com:443')" -ge 1 ]; }
    wait_for "the proxy to have logged a tunnel of the agent's to api.github.com" 360 _l04_tunnels || true
    tunnels=$(kc -n "$ISSUES_CODER_NS" logs deploy/hephaisto-coder-egress --since=30m 2>/dev/null | grep -F " $ip " | grep -c 'CONNECT api.github.com:443')
    want "the proxy's log has the agent's tunnels to api.github.com" "$tunnels" -ge 1
    want "and nothing of the agent's that it denied" \
        "$(kc -n "$ISSUES_CODER_NS" logs deploy/hephaisto-coder-egress --since=30m 2>/dev/null | grep -F " $ip " | grep -c 'TCP_DENIED')" -eq 0

    # --- 3: what Hephaisto repeats ------------------------------------------------------------------
    b=$(live_bystander) || { fail "the bystander issue exists"; return; }
    n=$(gh_issue_create "$LIVE_REPO" "$(live_title L04 "the order total is null for an empty cart")" "$(live_hostile_body "$b")") \
        || { fail "gh opened an issue in the sandbox"; return; }
    want "the issue's own text points at nothing: its line is in a code block" \
        "$(live_cross_references "$b" | jq --argjson n "$n" '[.[] | select(.source == $n)] | length')" -eq 0
    gh_assign "$LIVE_REPO" "$n" || { fail "GitHub accepted the bot as the assignee"; issues_done "$LIVE_REPO" "$n"; return; }
    issues_plan_ready "$LIVE_REPO" "$n" || { issues_done "$LIVE_REPO" "$n"; return; }

    want "GitHub answers 304 for an issue's comments since a time, when none was added" \
        "$(live_asked_twice "issues/$n/comments?per_page=100&since=$(jq -r '.planReadyAt // .createdAt' <<<"$ATTEMPT" | cut -c1-19)Z")" = "200 304"

    plan=$(live_comment "$(jq -r '.planCommentId' <<<"$ATTEMPT")")
    html=$(jq -r '.body_html // empty' <<<"$plan")
    jq -r '.body' <<<"$plan" | grep -qF "The reporter asked for this to be repeated" \
        && pass "the plan comment carries what the model repeated" \
        || { fail "the plan comment carries what the model repeated" "the repeated line is not in the plan, so nothing below is about it"; issues_done "$LIVE_REPO" "$n"; return; }
    grep -qF "$ISSUES_APPROVER" <<<"$html" && pass "and GitHub renders the name it was told to mention - as text" \
        || fail "and GitHub renders the name it was told to mention - as text" "the name is not in the rendered comment"
    want "the plan comment mentions nobody" "$(live_html_mentions <<<"$html")" -eq 0
    want "the plan comment links to no issue" "$(live_html_links_to "$b" <<<"$html")" -eq 0
    want "GitHub made no link at all of what the model wrote" "$(grep -o '<a [^>]*href=' <<<"$html" | wc -l | tr -d ' ')" -eq 0
    # A reference is written into the other issue's timeline when the comment is saved; the
    # plan has been on the issue for at least one poll by now.
    want "the bystander's timeline has no reference from this issue" \
        "$(live_cross_references "$b" | jq --argjson n "$n" '[.[] | select(.source == $n)] | length')" -eq 0

    # The same words from the approver: as a comment of a person's they are NOT neutralised (the
    # control), and as the reason in Hephaisto's status comment they are.
    reason="ask @$LIVE_BOT instead, this is #$b again - https://github.com/$LIVE_REPO/issues/$b ${ISSUES_RUN}"
    reject_id=$(gh_comment_as "$LIVE_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/reject $reason") \
        || { fail "gh commented /reject"; issues_done "$LIVE_REPO" "$n"; return; }

    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Denied Implementing PrOpened Failed Cancelled || true
    want "the attempt is denied" "$(wi_attempt_state "$WI")" = Denied
    _l04_told() { [ "$(gh_bot_comments "$LIVE_REPO" "$n" | jq --arg r "$ISSUES_RUN" '[.[] | select((.body | contains("plan was rejected")) and (.body | contains($r)))] | length')" -ge 1 ]; }
    wait_for "the status comment to carry the reason" "$ISSUES_SEEN_WAIT" _l04_told || true

    html=$(gh_comments "$LIVE_REPO" "$n" html | jq -r --arg b "$LIVE_BOT" '[.[] | select(.user.login == $b and (.body | contains("plan was rejected")))][0].body_html // empty')
    [ -n "$html" ] && grep -qF "${ISSUES_RUN}" <<<"$html" && pass "the status comment carries the reason" \
        || { fail "the status comment carries the reason" "no status comment says the plan was rejected, with the reason"; issues_done "$LIVE_REPO" "$n"; return; }
    want "the status comment mentions nobody" "$(live_html_mentions <<<"$html")" -eq 0
    want "the status comment links to no issue" "$(live_html_links_to "$b" <<<"$html")" -eq 0

    # The control. If these two fail, GitHub changed how it renders, and every "nobody" and
    # "nothing" above has stopped meaning anything.
    html=$(live_comment "$reject_id" | jq -r '.body_html // empty')
    want "control: in the approver's own comment the same words ARE a mention" "$(live_html_mentions <<<"$html")" -ge 1
    want "control: and ARE a link to the bystander" "$(live_html_links_to "$b" <<<"$html")" -ge 1
    _l04_referenced() { [ "$(live_cross_references "$b" | jq --argjson n "$n" --arg a "$ISSUES_APPROVER" '[.[] | select(.source == $n and .actor == $a)] | length')" -ge 1 ]; }
    wait_for "the bystander's timeline to show the approver's reference" 60 _l04_referenced \
        && pass "control: and the bystander's timeline shows that reference, made by the approver" \
        || fail "control: and the bystander's timeline shows that reference, made by the approver" "no cross-reference from #$n by $ISSUES_APPROVER"
    want "the bot made none" "$(live_cross_references "$b" | jq --arg a "$LIVE_BOT" '[.[] | select(.actor == $a)] | length')" -eq 0

    issues_done "$LIVE_REPO" "$n"
}
