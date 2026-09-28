# pager: P22 | - | shared | who is told follows the alert's labels, and what no route owns goes to the fallback
#
# Backlog #141 and #123. The rules are already labelled by the team they belong to. The suite's
# values route team=pager-a to oncall@ and name a fallback route, to dev@, for anything no
# scoped route owns - so an alert with no team label still reaches somebody, and only them.

chats_to() { pager_teams "$1" | jq -r '[.[] | select(.kind == "chat") | .conversation] | unique | join(" ")'; }

scenario() {
    local owned stray a b deadline
    owned=$(pager_name P22)
    stray=$(pager_name P22Stray)

    pager_fire "$owned" team=pager-a provider=delta
    pager_fire "$stray" provider=epsilon
    pager_wait_count "$owned" 1 60 || { fail "the owned alert opened an incident"; return; }
    pager_wait_count "$stray" 1 60 || { fail "the stray alert opened an incident"; return; }
    a=$(pager_first "$owned"); b=$(pager_first "$stray")

    deadline=$(( SECONDS + 90 ))
    while [ "$SECONDS" -lt "$deadline" ]; do
        [ "$(pager_alerts_sent "$a")" -ge 1 ] && [ "$(pager_alerts_sent "$b")" -ge 1 ] && break
        sleep 3
    done

    grep -q oncall <<<"$(chats_to "$a")" && pass "team=pager-a reached its route's recipient" \
        || fail "team=pager-a reached its route's recipient" "chats: $(chats_to "$a")"
    grep -q dev <<<"$(chats_to "$a")" && fail "and nobody else" "chats: $(chats_to "$a")" || pass "and nobody else"
    grep -q dev <<<"$(chats_to "$b")" && pass "an alert no route owns reached the fallback" \
        || fail "an alert no route owns reached the fallback" "chats: $(chats_to "$b")"
    grep -q oncall <<<"$(chats_to "$b")" && fail "and not the scoped route" "chats: $(chats_to "$b")" \
        || pass "and not the scoped route"
}
