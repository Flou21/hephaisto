# pager: P27 | - | shared | a note for the alert name reaches the card and the prompt, framed as operator-written
#
# Backlog #145. What people learned about an alert - what it means, what was done the last
# times, which dashboard - belongs to its name, and is the part of the old system people would
# miss. It must reach the person paged and the model, and the model must be told who wrote it.

scenario() {
    local n id code deadline text
    n=$(pager_name P27)

    code=$(_pager_curl -o /dev/null -w '%{http_code}' -X PUT "$PAGER_API/api/alerts/$n/note" \
        -H 'Content-Type: application/json' \
        --data '{"body":"Check the widget queue depth first; restarting the consumer has never helped.","updatedBy":"pager-suite"}')
    want "the note was saved" "$code" -lt 300

    pager_fire "$n" provider=kappa
    pager_wait_count "$n" 1 60 || { fail "an incident opened"; return; }
    id=$(pager_first "$n")

    deadline=$(( SECONDS + 90 ))
    while [ "$SECONDS" -lt "$deadline" ] && [ "$(pager_llm "$n" | jq length)" -lt 1 ]; do sleep 3; done
    text=$(pager_llm "$n" | jq -r '.[0].text // ""')
    grep -q "widget queue depth" <<<"$text" && pass "the note is in the prompt" || fail "the note is in the prompt"
    grep -qi "operator" <<<"$text" && pass "framed as written by operators" || fail "framed as written by operators"

    deadline=$(( SECONDS + 60 ))
    while [ "$SECONDS" -lt "$deadline" ] && [ "$(pager_alerts_sent "$id")" -lt 1 ]; do sleep 3; done
    pager_teams "$id" | jq -r '.[].text' | grep -q "widget queue" && pass "the card shows it" \
        || fail "the card shows it"
}
