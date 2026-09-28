# pager: P03 | - | shared | two label sets of one rule are two incidents, with different titles
#
# Backlog #132. A rule that names no workload fires once per provider. Each series is its own
# fault and must be its own incident - not a repeat of whichever fired first - and the two titles
# must say which is which, or a person reading the board cannot tell them apart.

scenario() {
    local n titles
    n=$(pager_name P03)

    pager_fire "$n" provider=alpha feed=articles
    pager_fire "$n" provider=beta feed=articles

    pager_wait_count "$n" 2 60 && pass "two incidents opened" \
        || fail "two incidents opened" "$(pager_count "$n") after 60s"

    titles=$(pager_incidents "$n" | jq -r '[.[].title] | unique | length')
    want "their titles differ" "$titles" -ge 2
    pager_incidents "$n" | jq -r '.[].title' | grep -q alpha && pass "a title names its provider" \
        || fail "a title names its provider" "$(pager_incidents "$n" | jq -c '[.[].title]')"
}
