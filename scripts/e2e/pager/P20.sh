# pager: P20 | - | shared | the prompt carries the alert's labels, annotations and expression, and not the scrape's own
#
# Backlog #135. For an alert about a pipeline the labels are all there is: which provider, which
# region, and through the generator URL which expression. The scrape's own labels - the
# exporter's address, the Prometheus that evaluated it - are noise at best and a wrong lead at
# worst.

scenario() {
    local n text deadline
    n=$(pager_name P20)

    pager_fire "$n" provider=acme-feed region=eu-central \
        instance=10.0.0.1:9090 job=scrape-job endpoint=http-metrics prometheus=monitoring/k8s \
        "@summary=Too few articles from acme-feed in the last hour"

    deadline=$(( SECONDS + 90 ))
    while [ "$SECONDS" -lt "$deadline" ] && [ "$(pager_llm "$n" | jq length)" -lt 1 ]; do sleep 3; done
    text=$(pager_llm "$n" | jq -r '.[0].text // ""')
    [ -n "$text" ] || { fail "the model was asked" "no request mentions $n"; return; }

    grep -q "acme-feed" <<<"$text" && grep -q "provider" <<<"$text" && pass "the labels are in the prompt" \
        || fail "the labels are in the prompt"
    grep -q "Too few articles" <<<"$text" && pass "the annotation is in the prompt" \
        || fail "the annotation is in the prompt"
    grep -qF "up{job=" <<<"$text" && pass "the rule's expression is in the prompt" \
        || fail "the rule's expression is in the prompt"
    grep -qF "10.0.0.1:9090" <<<"$text" && fail "the scrape's instance is kept out" "the prompt contains it" \
        || pass "the scrape's instance is kept out"
    grep -qF "monitoring/k8s" <<<"$text" && fail "the evaluating Prometheus is kept out" "the prompt contains it" \
        || pass "the evaluating Prometheus is kept out"
}
