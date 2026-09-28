# pager: P25 | - | shared | a warning that turns critical tells the route that wants criticals
#
# Backlog #148. The suite's values route team=pager-b to oncall@ at any severity and to lead@
# only when critical. The alert opens as a warning; the same alert then fires critical. The
# incident's severity rises and lead@ must hear of it, although no state changed.

told() { pager_teams "$1" | jq --arg who "$2" '[.[] | select(.kind == "chat") | select(.conversation | contains($who))] | length'; }

scenario() {
    local n id deadline
    n=$(pager_name P25)

    pager_fire "$n" team=pager-b provider=iota severity=warning
    pager_wait_count "$n" 1 60 || { fail "an incident opened"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 120 || true
    sleep 15
    want "as a warning, oncall@ was told" "$(told "$id" oncall)" -ge 1
    want "as a warning, lead@ was not" "$(told "$id" lead)" -eq 0

    pager_fire "$n" team=pager-b provider=iota severity=critical
    deadline=$(( SECONDS + 60 ))
    while [ "$SECONDS" -lt "$deadline" ] && [ "$(told "$id" lead)" -lt 1 ]; do sleep 3; done
    want "the same incident, now critical" "$(pager_incident "$id" | jq -r '.severity')" = Critical
    want "lead@ was told when it turned critical" "$(told "$id" lead)" -ge 1
}
