#!/usr/bin/env bash
# Cluster lifecycle: the inotify workaround, create, port-forward supervision, teardown.

# ---------------------------------------------------------------------------------------
# The inotify limit
# ---------------------------------------------------------------------------------------
# Rancher Desktop's Lima VM ships fs.inotify.max_user_instances=128. A kind node's systemd
# needs far more, and below roughly 8192 it dies during boot with
#
#   Failed to create control group inotify object: Too many open files
#
# which surfaces as a cluster that never becomes ready and a kubelet log nobody thinks to
# read. Raised inside the VM, recorded, and put back by the teardown trap - this is a shared
# machine setting, not ours to keep.
INOTIFY_WANT=8192
INOTIFY_ORIGINAL=""

inotify_raise() {
    local current
    current=$(docker run --rm --privileged --pid=host alpine \
        nsenter -t 1 -m -u -n -i sysctl -n fs.inotify.max_user_instances 2>/dev/null || echo "")

    if [ -z "$current" ]; then
        warn "could not read fs.inotify.max_user_instances; continuing without raising it"
        return 0
    fi

    if [ "$current" -ge "$INOTIFY_WANT" ]; then
        say "fs.inotify.max_user_instances is $current, leaving alone"
        return 0
    fi

    INOTIFY_ORIGINAL="$current"
    say "raising fs.inotify.max_user_instances $current -> $INOTIFY_WANT (restored on exit)"
    docker run --rm --privileged --pid=host alpine \
        nsenter -t 1 -m -u -n -i sysctl -w "fs.inotify.max_user_instances=$INOTIFY_WANT" >/dev/null
}

inotify_restore() {
    [ -n "$INOTIFY_ORIGINAL" ] || return 0
    say "restoring fs.inotify.max_user_instances to $INOTIFY_ORIGINAL"
    docker run --rm --privileged --pid=host alpine \
        nsenter -t 1 -m -u -n -i sysctl -w "fs.inotify.max_user_instances=$INOTIFY_ORIGINAL" >/dev/null 2>&1 \
        || warn "could not restore fs.inotify.max_user_instances; it is still $INOTIFY_WANT"
    INOTIFY_ORIGINAL=""
}

# ---------------------------------------------------------------------------------------
# Create
# ---------------------------------------------------------------------------------------
cluster_create() {
    local node_image
    node_image=$(e2e_node_image "$E2E_K8S_VERSION")
    [ -n "$node_image" ] || die \
        "no pinned node image for Kubernetes $E2E_K8S_VERSION (have: $E2E_NODE_IMAGE_VERSIONS)"

    if kind get clusters 2>/dev/null | grep -qx "$E2E_CLUSTER"; then
        say "cluster $E2E_CLUSTER already exists, reusing it"
        kind export kubeconfig --name "$E2E_CLUSTER" --kubeconfig "$E2E_KUBECONFIG" >/dev/null
    else
        inotify_raise
        say "creating kind cluster $E2E_CLUSTER on Kubernetes $E2E_K8S_VERSION"
        kind create cluster \
            --name "$E2E_CLUSTER" \
            --config "$E2E_DIR/kind.yaml" \
            --image "$node_image" \
            --kubeconfig "$E2E_KUBECONFIG" \
            --wait 120s
    fi

    # From here on every kubectl goes through kc(), which refuses any other context.
    kc cluster-info >/dev/null || die "cluster is up but not answering"
    pass "kind cluster on Kubernetes $E2E_K8S_VERSION"

    say "aliasing the local-path StorageClass"
    kc apply -f "$E2E_DIR/local-path-sc.yaml" >/dev/null

    # infra/observability/*.values.yaml all pin local-path. If this is missing every PVC in
    # the stack sits Pending with no error louder than an event.
    kc get storageclass local-path >/dev/null 2>&1 \
        && pass "local-path StorageClass aliased to kind's provisioner" \
        || fail "local-path StorageClass missing" "the observability PVCs will never bind"

    say "creating namespaces"
    kc apply -f "$REPO/infra/namespaces.yaml" >/dev/null

    # The write Role is only rendered for namespaces carrying this label, so its absence
    # would show up much later as an agent that cannot act where it is supposed to.
    local labelled
    labelled=$(kc get ns "$CHAOS_NS" -o jsonpath='{.metadata.labels.hephaisto\.dev/destructive-actions-allowed}' 2>/dev/null || true)
    [ "$labelled" = "true" ] \
        && pass "chaos namespace carries the destructive-actions-allowed label" \
        || fail "chaos namespace label missing" "got '${labelled:-<none>}'"
}

# ---------------------------------------------------------------------------------------
# Prometheus Operator CRDs
# ---------------------------------------------------------------------------------------
# kube-prometheus-stack.values.yaml sets `crds.enabled: false`, because the dev cluster
# already has them and the chart's own CRD handling fights an operator installed separately.
# A fresh kind cluster has none, so they have to come from somewhere - and `--set
# crds.enabled=true` on the install is the cheapest somewhere, keeping the values file
# unmodified.
#
# This function exists for the case where that is not enough: the chart installs CRDs only on
# first install, so a re-run against a reused cluster needs them to already be there.
crds_present() {
    kc get crd prometheuses.monitoring.coreos.com >/dev/null 2>&1
}

# ---------------------------------------------------------------------------------------
# Port-forwards
# ---------------------------------------------------------------------------------------
# Supervised, because a plain `kubectl port-forward` drops - on a pod restart, on an idle
# timeout, on a stream error - and every assertion after that point then fails for a reason
# that has nothing to do with what it was testing. The Tiltfile solves this with the same
# `until ...; do sleep; done` shape.
PF_PIDS=()

port_forward() {
    local name="$1" ns="$2" target="$3" local_port="$4" remote_port="$5"
    local log="$WORKDIR/pf-$name.log"

    (
        # shellcheck disable=SC2064
        while true; do
            KUBECONFIG="$E2E_KUBECONFIG" kubectl --context "$E2E_CONTEXT" -n "$ns" \
                port-forward "$target" "$local_port:$remote_port" >>"$log" 2>&1 || true
            sleep 3
        done
    ) &
    PF_PIDS+=($!)

    # Wait for it to answer rather than assuming. A forward that never comes up is a
    # diagnosable failure here and an inexplicable one twenty assertions later.
    local deadline=$(( SECONDS + 90 ))
    while [ "$SECONDS" -lt "$deadline" ]; do
        if curl -sS --max-time 3 -o /dev/null "http://127.0.0.1:$local_port/" 2>/dev/null \
           || [ "$(curl -sS --max-time 3 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$local_port/" 2>/dev/null)" != "000" ]; then
            say "port-forward $name -> 127.0.0.1:$local_port"
            return 0
        fi
        sleep 2
    done

    warn "port-forward $name never answered on $local_port; see $log"
    return 1
}

port_forwards_stop() {
    [ ${#PF_PIDS[@]} -gt 0 ] || return 0
    kill "${PF_PIDS[@]}" 2>/dev/null || true
    # The supervisor loop spawns kubectl as a child; killing the subshell alone can orphan it.
    pkill -f "port-forward.*$E2E_CONTEXT" 2>/dev/null || true
    PF_PIDS=()
}

# ---------------------------------------------------------------------------------------
# Teardown
# ---------------------------------------------------------------------------------------
# Installed as a trap on EXIT, so it runs on success, on a failed assertion, on a `set -e`
# abort and on Ctrl-C alike. A teardown that only works on the happy path leaves a kind
# cluster and a raised system-wide inotify limit behind precisely when something went wrong.
teardown() {
    local exit_code=$?
    trap - EXIT INT TERM

    phase "teardown"
    port_forwards_stop

    if [ "${KEEP_CLUSTER:-0}" = "1" ]; then
        say "--keep-cluster: leaving $E2E_CLUSTER up"
        say "  export KUBECONFIG=$E2E_KUBECONFIG"
        say "  kubectl --context $E2E_CONTEXT -n $APP_NS get pods"
        say "  kind delete cluster --name $E2E_CLUSTER   # when you are done"
    else
        if kind get clusters 2>/dev/null | grep -qx "$E2E_CLUSTER"; then
            say "deleting cluster $E2E_CLUSTER"
            kind delete cluster --name "$E2E_CLUSTER" >/dev/null 2>&1 \
                || warn "could not delete the cluster; do it by hand"
        fi
        inotify_restore
        rm -f "$E2E_KUBECONFIG"
    fi

    # The whole point of the dedicated kubeconfig. Say so out loud at the end of every run,
    # because "the harness cannot touch production" is a claim worth re-proving rather than
    # trusting.
    local host_ctx
    host_ctx=$(kubectl config current-context 2>/dev/null || echo "<none>")
    say "your kubeconfig is untouched; current context is still '$host_ctx'"

    if [ -s "$RESULTS" ] && [ "${SKIP_REPORT:-0}" != "1" ]; then
        # The exit status matters to the report, not just to the shell. A run that aborted
        # part-way - a `set -e` trip, a jq error, a Ctrl-C - has recorded no failures, so a
        # reporter that only counts recorded failures happily prints PASSED over a run that
        # never reached its most important phase. That happened, and it is the worst possible
        # bug in a release gate: the one that says yes when it does not know.
        report_render "$exit_code"
    fi

    exit "$exit_code"
}
