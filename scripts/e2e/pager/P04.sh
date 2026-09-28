# pager: P04 | - | shared | a kube-state-metrics alert about a deployment targets the deployment
#
# Backlog #126. kube-state-metrics' own pod name rides along on every series it exports, in the
# `pod` label, and ObjectIdentity took any `pod` label to be the target. So a deployment with no
# replicas available became an incident about the exporter.

scenario() {
    local n id kind name
    n=$(pager_name P04)

    pager_fire "$n" namespace=pager-e2e deployment=checkout job=kube-state-metrics \
        pod=hephaisto-kube-state-metrics-7d9c8b-x2x4q container=kube-state-metrics \
        instance=10.42.0.17:8080

    pager_wait_count "$n" 1 60 || { fail "an incident opened" "none within 60s"; return; }
    id=$(pager_first "$n")
    kind=$(pager_incident "$id" | jq -r '.target.kind')
    name=$(pager_incident "$id" | jq -r '.target.name')

    want "the target is the Deployment" "$kind" = Deployment
    want "the target is named checkout" "$name" = checkout
}
