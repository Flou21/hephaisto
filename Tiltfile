# -*- mode: Python -*-
#
# Hephaisto's inner loop. Run from ~/hephaisto with:
#
#     tilt up --port 10351
#
# Port 10351 because the ~/dev workspace already runs a detached Tilt on 10350. The two
# share one k3s cluster but no ports and no resources.
#
# NEVER run a bare `tilt down`: it helm-uninstalls the observability stack and takes
# Grafana's PVC with it. Every dashboard and datasource here is declarative so that losing
# the PVC costs nothing - which only holds as long as nobody creates dashboards by hand.

# Tilt refuses to deploy to a context it does not recognise as local, and k3s under Rancher
# Desktop is not on its allowlist. Both spellings appear: this machine's kubeconfig calls it
# studio-rancher-desktop, the laptop calls the same cluster rancher-desktop.
allow_k8s_contexts(['studio-rancher-desktop', 'rancher-desktop'])

load('ext://helm_resource', 'helm_resource', 'helm_repo')
load('ext://namespace', 'namespace_create')

# HOST and HOST_IP are per-machine and are read from tilt_config.json, which is NOT tracked
# - see tilt_config.sample.json. They are assigned just below config.parse().

def tailnet(host_port, container_port):
    return port_forward(host_port, container_port, host = HOST)

# --- why some forwards are local_resources and not port_forwards ---------------------------
#
# A Tilt resource forwards to ONE pod. helm_resource makes the whole Helm release a single
# resource, so a chart that deploys several distinct servers - kube-prometheus-stack is
# Prometheus AND Grafana AND Alertmanager - can only ever have one of them reachable through
# port_forwards. Declaring three there is silently wrong: Tilt binds all three host ports to
# whichever pod it selected, so :9090 works and :3030 and :9093 answer nothing at all. The
# same bites Loki, whose release includes loki-0 and loki-gateway on different ports.
#
# So: one port_forward per resource for its primary pod, and an explicit `kubectl
# port-forward` against the SERVICE for the rest. Forwarding to a Service also survives the
# pod restarts that a helm upgrade causes, which the pod-bound version does not.
def svc_forward(name, namespace, service, host_port, service_port, deps = []):
    # Retry forever rather than exiting. kubectl port-forward dies immediately if the Service
    # does not resolve yet, and helm_resource reports ok as soon as helm returns - which is
    # before the operator has created the Services this forwards to. Without the loop, grafana
    # and alertmanager come up dead on every fresh `tilt up` and need a manual trigger, while
    # loki happens to win the race and works. It also reconnects across the pod restarts a
    # helm upgrade causes.
    return local_resource(
        name,
        serve_cmd = 'until kubectl -n %s port-forward --address %s svc/%s %d:%d; do sleep 5; done' % (
            namespace, HOST_IP, service, host_port, service_port),
        resource_deps = deps,
        labels = ['forwards'],
        auto_init = True,
    )

# --- toggles ------------------------------------------------------------------------------

# Where the port-forwards bind. The default keeps a fresh clone working with no config at
# all. Set them in tilt_config.json to bind to something every machine on your network can
# reach - a VPN or Tailscale interface, say - and then one address works from everywhere.
# The tradeoff is that localhost then does NOT work, not even from a shell on this machine.
config.define_string('host',    args = False, usage = 'Hostname the port-forwards bind to')
# kubectl only accepts an IP or `localhost` for --address, never a hostname, so the
# svc_forward calls need the address `host` resolves to.
config.define_string('host-ip', args = False, usage = 'The IP that `host` resolves to')

config.define_bool('observability', args = False, usage = 'Prometheus, Grafana, Loki, Alertmanager, collector')
config.define_bool('tracing',       args = False, usage = 'Tempo + the Aspire dashboard')
config.define_bool('agent',         args = False, usage = 'Postgres and the Hephaisto pod')
config.define_bool('chaos',         args = False, usage = 'Register the chaos fixtures (still manual-trigger)')
cfg = config.parse()

HOST    = cfg.get('host', 'localhost')
HOST_IP = cfg.get('host-ip', '127.0.0.1')

observability = cfg.get('observability', True)
tracing       = cfg.get('tracing', True)
agent         = cfg.get('agent', True)
chaos         = cfg.get('chaos', False)

# --- namespaces ---------------------------------------------------------------------------

k8s_yaml('infra/namespaces.yaml')

# --- helm repos ---------------------------------------------------------------------------
#
# Every chart is pinned to an exact version. A helm_resource without --version resolves
# "latest", which will silently major-upgrade Prometheus on some future `tilt up` and leave
# you debugging a stack you did not change.

if observability or tracing:
    helm_repo('prometheus-community', 'https://prometheus-community.github.io/helm-charts', labels = ['repos'])
    helm_repo('grafana-charts',       'https://grafana.github.io/helm-charts',              labels = ['repos'])
    # Distinct repo, not a mirror. The Tempo and grafana-mcp charts migrated here after
    # 2026-01-30; grafana/tempo tops out at 1.24.4 (deprecated) and grafana/grafana-mcp at
    # 0.3.1, so the pinned versions below simply do not exist in grafana-charts.
    helm_repo('grafana-community',    'https://grafana-community.github.io/helm-charts',    labels = ['repos'])
    helm_repo('open-telemetry',       'https://open-telemetry.github.io/opentelemetry-helm-charts', labels = ['repos'])

# --- observability stack ------------------------------------------------------------------

if observability:
    helm_resource(
        'kube-prometheus-stack',
        'prometheus-community/kube-prometheus-stack',
        namespace = 'hephaisto-obs',
        release_name = 'hephaisto',
        flags = [
            '--version', '81.1.0',
            '--values', 'infra/observability/kube-prometheus-stack.values.yaml',
            '--create-namespace',
        ],
        resource_deps = ['prometheus-community'],
        # Prometheus only. Grafana and Alertmanager are separate pods in this same release
        # and get their own Service forwards below - see the comment on svc_forward.
        port_forwards = [tailnet(9090, 9090)],
        labels = ['observability'],
    )

    svc_forward('grafana-forward', 'hephaisto-obs', 'hephaisto-grafana',
                3030, 80, deps = ['kube-prometheus-stack'])
    svc_forward('alertmanager-forward', 'hephaisto-obs',
                'hephaisto-kube-prometheus-alertmanager',
                9093, 9093, deps = ['kube-prometheus-stack'])

    helm_resource(
        'loki',
        'grafana-charts/loki',
        namespace = 'hephaisto-obs',
        flags = [
            '--version', '6.40.0',
            '--values', 'infra/observability/loki.values.yaml',
        ],
        resource_deps = ['grafana-charts'],
        # No port_forwards: this release has loki-0 (3100) and loki-gateway (80), and Tilt
        # would bind 3100 to whichever it selected - it picked the gateway, so :3100 answered
        # nothing.
        labels = ['observability'],
    )

    svc_forward('loki-forward', 'hephaisto-obs', 'loki', 3100, 3100, deps = ['loki'])

    helm_resource(
        'otel-collector',
        'open-telemetry/opentelemetry-collector',
        namespace = 'hephaisto-obs',
        flags = [
            '--version', '0.171.0',
            '--values', 'infra/observability/otel-collector.values.yaml',
        ],
        # Depends on its exporters existing first: a collector that starts before Tempo and
        # Loki spends its first minutes logging connection refused, which looks alarming and
        # is not.
        resource_deps = ['open-telemetry', 'kube-prometheus-stack', 'loki'] + (['tempo'] if tracing else []),
        port_forwards = [tailnet(4317, 4317), tailnet(4318, 4318)],
        labels = ['observability'],
    )

    helm_resource(
        'grafana-mcp',
        'grafana-community/grafana-mcp',
        namespace = 'hephaisto-obs',
        flags = [
            '--version', '0.19.0',
            '--values', 'infra/observability/grafana-mcp.values.yaml',
        ],
        resource_deps = ['grafana-community', 'kube-prometheus-stack'],
        port_forwards = [tailnet(8200, 8000)],
        labels = ['observability'],
    )

    # Datasources and dashboards are ConfigMaps picked up by Grafana's sidecars, and the
    # alert rules are CRs picked up by the operator's ruleSelector. Editing one is a
    # full-resource replace, not a live sync - that is expected, they are declarative state.
    #
    # The datasource ConfigMap is NOT optional. The values file sets
    # grafana.sidecar.datasources.defaultDatasourceEnabled: false so the chart does not
    # provision its own Prometheus datasource and fight this file over the `prometheus` uid.
    # Leaving this un-applied therefore does not fall back to a default - it leaves Grafana
    # with ZERO datasources, an empty Explore, and every dashboard panel showing "Datasource
    # not found". Nothing logs an error, because from Grafana's point of view it was simply
    # never told about any.
    k8s_yaml('infra/observability/grafana-datasources.yaml')
    k8s_yaml('infra/observability/dashboards/hephaisto-dashboard-configmap.yaml')
    k8s_yaml(listdir('infra/observability/alerts', recursive = True))
    k8s_resource(
        objects = [
            'hephaisto-kubernetes-rules:prometheusrule',
            'hephaisto-slo-rules:prometheusrule',
            'hephaisto-watchdog:prometheusrule',
            'hephaisto-observability-selfcheck:prometheusrule',
        ],
        new_name = 'alert-rules',
        resource_deps = ['kube-prometheus-stack'],
        labels = ['observability'],
    )

    k8s_resource(
        objects = [
            'hephaisto-datasources:configmap',
            'hephaisto-dashboard:configmap',
        ],
        new_name = 'grafana-provisioning',
        resource_deps = ['kube-prometheus-stack'],
        labels = ['observability'],
    )

# --- traces -------------------------------------------------------------------------------

if tracing:
    helm_resource(
        'tempo',
        'grafana-community/tempo',
        namespace = 'hephaisto-obs',
        flags = [
            '--version', '2.3.0',
            '--values', 'infra/observability/tempo.values.yaml',
        ],
        resource_deps = ['grafana-community'],
        port_forwards = [tailnet(3200, 3200)],
        labels = ['observability'],
    )

    # Not a replacement for Tempo - Tempo is the durable system of record and this is
    # in-memory only. It earns its place by rendering gen_ai.* semconv spans natively:
    # model, token counts, prompts and tool arguments, with no panel to build.
    k8s_yaml('infra/observability/aspire-dashboard.yaml')
    k8s_resource(
        'aspire-dashboard',
        port_forwards = [tailnet(18888, 18888)],
        labels = ['observability'],
    )

# --- the agent ----------------------------------------------------------------------------

if agent:
    k8s_yaml('infra/app/postgres.yaml')
    k8s_resource('postgres', port_forwards = [tailnet(5433, 5432)], labels = ['agent'])

    # disable_push=True builds straight into this node's docker daemon. tilt_config.json in
    # ~/dev learned this the hard way; here there is no registry path at all, so there is
    # nothing to accidentally re-enable.
    custom_build(
        'hephaisto/agent',
        'docker build -t $EXPECTED_REF -f Dockerfile.dev .',
        deps = ['src', 'Directory.Build.props', 'Directory.Packages.props', 'Dockerfile.dev'],
        disable_push = True,
        live_update = [
            # A plain source edit syncs and dotnet watch hot-reloads. Only a .csproj or a
            # package change needs the full rebuild that fall_back_on forces.
            fall_back_on(['Directory.Packages.props', 'Directory.Build.props']),
            sync('src', '/app/src'),
        ],
    )

    k8s_yaml(['infra/app/rbac.yaml', 'infra/app/hephaisto.yaml'])
    k8s_resource(
        'hephaisto',
        port_forwards = [tailnet(8100, 8080)],
        resource_deps = ['postgres'] + (['kube-prometheus-stack'] if observability else []),
        labels = ['agent'],
    )

# --- chaos --------------------------------------------------------------------------------
#
# Every fixture is auto_init=False and manual-trigger, so `tilt up` never brings up a
# deliberately broken cluster by accident. You break things on purpose, one at a time, and
# watch what the agent makes of it.

if chaos:
    for f in listdir('infra/chaos'):
        if not f.endswith('.yaml'):
            continue

        # The resource name is the workload name inside the manifest, which is the file's
        # basename - c1-oomkill, not a 'chaos-' prefix. Deriving a different name here makes
        # k8s_resource fail with "unknown resource" at load time.
        name = os.path.basename(f).replace('.yaml', '')
        k8s_yaml(f)
        k8s_resource(
            name,
            auto_init = False,
            trigger_mode = TRIGGER_MODE_MANUAL,
            # c9-memhog drives the whole node into memory pressure and will evict unrelated
            # pods, including Hephaisto's own. Run it alone, deliberately, and clean up.
            labels = ['chaos'],
        )
