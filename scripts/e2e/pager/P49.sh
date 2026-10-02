# pager: P49 | kubectl | shared | a workload the watcher reported heals, and its incident closes without anybody touching it
#
# Backlog #158. The Kubernetes watcher only ever said that something was wrong, so an incident
# it opened ended by hand or never: production held 326 open ones, a crash loop of the week
# before among them. Once every pod of the workload has run cleanly for incidents.healedAfter
# (seconds here, values-pager.yaml) the watcher reports it healed and the incident closes, as
# hephaisto/watcher - not as a person, and not as a resolution the agent could claim.
#
# A Deployment of its own, named for the run, whose container exits at once. The stand-in's
# image, because every install the suite runs on already has it; nothing is pulled.

p49_incident() {
    _pager_curl "$PAGER_API/api/incidents?state=any&limit=100&namespace=hephaisto-chaos" \
        | jq -r --arg n "$1" '[.[] | select(.ownerName == $n)] | sort_by(.openedAt) | .[0].id // empty'
}

scenario() {
    local name="pager-heal-${PAGER_RUN}" id="" deadline

    pager_kc apply -f - >/dev/null <<YAML
apiVersion: apps/v1
kind: Deployment
metadata:
  name: $name
  namespace: hephaisto-chaos
  labels: {app.kubernetes.io/name: $name, app.kubernetes.io/part-of: hephaisto-e2e}
spec:
  replicas: 1
  selector: {matchLabels: {app.kubernetes.io/name: $name}}
  template:
    metadata:
      labels: {app.kubernetes.io/name: $name}
    spec:
      terminationGracePeriodSeconds: 1
      containers:
        - name: app
          image: hephaisto/notification-receiver:dev
          imagePullPolicy: IfNotPresent
          command: ["/bin/sh", "-c", "echo 'FATAL cannot reach the database'; exit 1"]
          resources:
            requests: {cpu: 5m, memory: 8Mi}
            limits: {memory: 32Mi}
          securityContext:
            runAsNonRoot: true
            runAsUser: 1654
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
YAML

    deadline=$(( SECONDS + 180 ))
    while [ "$SECONDS" -lt "$deadline" ]; do
        id=$(p49_incident "$name" 2>/dev/null || true)
        [ -n "$id" ] && break
        sleep 3
    done

    if [ -z "$id" ]; then
        pager_kc -n hephaisto-chaos delete deploy "$name" --wait=false >/dev/null 2>&1
        fail "the watcher opened an incident for the crash loop" "none for $name within 180s"
        return
    fi
    pass "the watcher opened an incident for the crash loop"

    pager_wait_settled "$id" 120 || fail "the investigation ended" "state $(pager_state "$id")"
    want "it is open while the workload crashes" "$(pager_state "$id")" != Closed

    # The fix a person would deploy: the same Deployment, with a container that stays up.
    pager_kc -n hephaisto-chaos patch deploy "$name" --type=json \
        -p '[{"op":"replace","path":"/spec/template/spec/containers/0/command","value":["/bin/sh","-c","sleep 3600"]}]' >/dev/null

    if pager_wait_state "$id" 180 Closed; then
        pass "the incident closed once the workload had run cleanly"
    else
        fail "the incident closed once the workload had run cleanly" "state $(pager_state "$id") after 180s"
    fi
    want "closed by the watcher, not by a person" "$(pager_incident "$id" | jq -r '.closedBy')" = hephaisto/watcher
    want "and nothing reopened it" "$(p49_incident "$name")" = "$id"

    pager_kc -n hephaisto-chaos delete deploy "$name" --wait=false >/dev/null 2>&1
}
