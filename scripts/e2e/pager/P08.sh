# pager: P08 | - | shared | a resolve with no incident opens nothing
#
# Backlog #129. A resolve for an alert the agent never saw - it was down, or it was installed
# after the alert fired - is not a fault. Opening an incident for it pages a person for the
# fault having gone away.

scenario() {
    local n
    n=$(pager_name P08)

    pager_resolve "$n" namespace=pager-e2e deployment=billing
    want "the webhook accepted the resolve" "$PAGER_CODE" -lt 300
    sleep 15
    want "nothing opened" "$(pager_count "$n")" -eq 0
    want "the model was not asked" "$(pager_llm "$n" | jq length)" -eq 0
}
