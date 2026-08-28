# -*- mode: Python -*-
#
# Watchtower's inner loop. Run from ~/watchtower with:
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

# Bound to the Tailscale interface, so one address works from the Mac Studio and the laptop
# alike. The consequence is that localhost does NOT work, not even from a shell on this
# machine - see CLAUDE.md.
HOST = 'macstudio-von-florian.tail3043f4.ts.net'

def tailnet(host_port, container_port):
    return port_forward(host_port, container_port, host = HOST)

# --- toggles ------------------------------------------------------------------------------

config.define_bool('observability', args = False, usage = 'Prometheus, Grafana, Loki, Alertmanager, collector')
config.define_bool('tracing',       args = False, usage = 'Tempo + the Aspire dashboard')
config.define_bool('agent',         args = False, usage = 'Postgres and the Watchtower pod')
config.define_bool('chaos',         args = False, usage = 'Register the chaos fixtures (still manual-trigger)')
cfg = config.parse()

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
        namespace = 'watchtower-obs',
        release_name = 'watchtower',
        flags = [
            '--version', '81.1.0',
            '--values', 'infra/observability/kube-prometheus-stack.values.yaml',
            '--create-namespace',
        ],
        resource_deps = ['prometheus-community'],
        port_forwards = [
            tailnet(9090, 9090),   # Prometheus
            tailnet(3030, 80),     # Grafana - the Service listens on 80, not 3000
            tailnet(9093, 9093),   # Alertmanager
        ],
        labels = ['observability'],
    )

    helm_resource(
        'loki',
        'grafana-charts/loki',
        namespace = 'watchtower-obs',
        flags = [
            '--version', '6.40.0',
            '--values', 'infra/observability/loki.values.yaml',
        ],
        resource_deps = ['grafana-charts'],
        port_forwards = [tailnet(3100, 3100)],
        labels = ['observability'],
    )

    helm_resource(
        'otel-collector',
        'open-telemetry/opentelemetry-collector',
        namespace = 'watchtower-obs',
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
        namespace = 'watchtower-obs',
        flags = [
            '--version', '0.19.0',
            '--values', 'infra/observability/grafana-mcp.values.yaml',
        ],
        resource_deps = ['grafana-community', 'kube-prometheus-stack'],
        port_forwards = [tailnet(8200, 8000)],
        labels = ['observability'],
    )

    # Alert rules and dashboards are plain CRs and ConfigMaps, picked up by the operator's
    # ruleSelector and Grafana's dashboard sidecar respectively. Editing one is a
    # full-resource replace, not a live sync - that is expected, they are declarative state.
    k8s_yaml(listdir('infra/observability/alerts', recursive = True))
    k8s_resource(
        objects = [
            'watchtower-kubernetes-rules:prometheusrule',
            'watchtower-slo-rules:prometheusrule',
            'watchtower-watchdog:prometheusrule',
            'watchtower-observability-selfcheck:prometheusrule',
        ],
        new_name = 'alert-rules',
        resource_deps = ['kube-prometheus-stack'],
        labels = ['observability'],
    )

# --- traces -------------------------------------------------------------------------------

if tracing:
    helm_resource(
        'tempo',
        'grafana-community/tempo',
        namespace = 'watchtower-obs',
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
        'watchtower/agent',
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

    k8s_yaml(['infra/app/rbac.yaml', 'infra/app/watchtower.yaml'])
    k8s_resource(
        'watchtower',
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
            # pods, including Watchtower's own. Run it alone, deliberately, and clean up.
            labels = ['chaos'],
        )
