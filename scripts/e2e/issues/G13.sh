# issues: G13 | kubectl | exclusive | the plan comment shows the planner's questions, and its notes folded away
#
# A plan is made with what the issue says, and an issue rarely says everything. What the planner
# had to assume, and what it saw next to the request and left alone, it asks - and until this
# scenario nobody was shown: on 2026-10-08 the first real plan left an entry where it was, the
# owner asked why Hephaisto had not suggested moving it too, and the suggestion was in a field no
# comment rendered (#286). So the questions are part of the plan comment, as a numbered list a
# person can answer by number; the planner's notes are there too, folded, for whoever wants to
# know what was left out; and the comment says the three things that can be done next.

scenario() {
    issues_ready || return
    local n attempt_id plan status questions asked shown note

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G13 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it. The total reads null where it should read 0.") \
        || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")

    # What the planner asked is on the attempt, as its files and steps are.
    questions=$(jq -c '.questions // []' <<<"$ATTEMPT")
    asked=$(jq 'length' <<<"$questions")
    want "the plan asks something of a person" "$asked" -ge 1

    plan=$(issues_plan_comment "$ISSUES_REPO" "$n" "$attempt_id")
    [ -n "$plan" ] && pass "the plan is a comment of its own" \
        || { fail "the plan is a comment of its own" "no comment of the bot's carries the plan's marker"; issues_done "$ISSUES_REPO" "$n"; return; }

    want "the plan comment has a section for the questions" "$(grep -c '^\*\*Questions\*\*' <<<"$plan")" -eq 1
    shown=$(jq -r --arg p "$plan" '[.[] | select(. as $q | $p | contains($q))] | length' <<<"$questions")
    want "every question is in it, as it was asked" "$shown" -eq "$asked"
    want "as a numbered list" "$(grep -c '^[0-9][0-9]*\. ' <<<"$(sed -n '/^\*\*Questions\*\*/,/^<details>/p' <<<"$plan")")" -eq "$asked"

    # The notes: there, and folded. A note that speaks of injected text is where a model quotes
    # what it was told to ignore, and that is not repeated on the issue under the bot's name.
    note=$(jq -r '[.notes[]? | select(test("injection"; "i") | not)][0] // empty' <<<"$ATTEMPT")
    [ -n "$note" ] && pass "the plan has a note that is not about injected text" || fail "the plan has a note that is not about injected text" "the fixture's plan script carries none"
    want "the notes are in a folded block" \
        "$(jq -rn --arg p "$plan" --arg n "$note" '$p | (index("<details>")) as $a | (index("</details>")) as $z | (index($n)) as $i | ($n != "" and $a != null and $z != null and $i != null and $i > $a and $i < $z)')" = true
    want "a note about injected text is counted and not quoted" \
        "$(jq -rn --arg p "$plan" --argjson a "$ATTEMPT" '[$a.notes[]? | select(test("injection"; "i")) | select(. as $q | $p | contains($q))] | length')" -eq 0

    # Each command is a line of its own, in a block GitHub puts a copy button on (#298).
    want "it says how to take the plan as it is" "$(grep -cxF '/approve' <<<"$plan")" -eq 1
    want "and that this takes it with its assumptions" "$(grep -cF 'That takes the plan as it is, with the assumptions above.' <<<"$plan")" -eq 1
    want "how to have it planned again with answers" "$(grep -cxF '/replan' <<<"$plan")" -eq 1
    want "and how to refuse it" "$(grep -cxF '/reject <reason>' <<<"$plan")" -eq 1

    # Still two comments: where the work stands, and the plan. The questions are not a third.
    status=$(issues_status_comment "$ISSUES_REPO" "$n" "$WI")
    want "the bot has written two comments" "$(gh_bot_comments "$ISSUES_REPO" "$n" | jq length)" -eq 2
    want "and the status comment does not repeat the questions" "$(grep -c '^\*\*Questions\*\*' <<<"$status")" -eq 0

    issues_done "$ISSUES_REPO" "$n"
}
