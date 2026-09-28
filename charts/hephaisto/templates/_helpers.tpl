{{/*
Names. `hephaisto` by default rather than the release name, because the RBAC objects, the
NetworkPolicies and the alert rules all refer to each other by name, and an operator reading
`kubectl auth can-i --as=system:serviceaccount:hephaisto:hephaisto` should find what the
documentation says they will. Override with fullnameOverride if you must run two.
*/}}
{{- define "hephaisto.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "hephaisto.fullname" -}}
{{- default (include "hephaisto.name" .) .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "hephaisto.labels" -}}
app.kubernetes.io/name: {{ include "hephaisto.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/part-of: hephaisto
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
{{- end -}}

{{/*
Selector labels. Deliberately NOT including the version or chart labels: those change on
every release, and a Deployment's selector is immutable. Including them makes the second
`helm upgrade` fail with "field is immutable" and the fix is to delete the Deployment.
*/}}
{{- define "hephaisto.selectorLabels" -}}
app.kubernetes.io/name: {{ include "hephaisto.name" . }}
{{- end -}}

{{- define "hephaisto.serviceAccountName" -}}
{{- default (include "hephaisto.fullname" .) .Values.serviceAccount.name -}}
{{- end -}}

{{/*
The labels the Prometheus Operator selects PodMonitors and PrometheusRules by.

This is the single most dangerous value in the chart. Get it wrong and every object is
created successfully, `kubectl get prometheusrule` shows them all present, and Prometheus
never selects any of them: no metrics, no alerts, no incidents - and an agent reporting
itself perfectly healthy because nothing is arriving. There is no error anywhere.

Check it after install with the two commands NOTES.txt prints.
*/}}
{{- define "hephaisto.operatorSelectorLabels" -}}
{{- range $k, $v := .Values.prometheusOperator.selectorLabels }}
{{ $k }}: {{ $v | quote }}
{{- end }}
{{- end -}}

{{/*
Guard for the write Role's namespaces.

`policy.actionableNamespaces` is the list the agent may delete pods in. Refusing outright is
the right failure: a values file that names kube-system is not a typo to be silently dropped,
it is a change someone must see fail. Nothing in the un-charted manifests stopped anyone
editing `namespace: hephaisto-chaos` to `kube-system`; after this, that is a render error.
*/}}
{{- define "hephaisto.validateActionableNamespaces" -}}
{{- $release := .Release.Namespace -}}
{{- $obs := .Values.observabilityNamespace -}}
{{- range .Values.policy.actionableNamespaces -}}
  {{- if hasPrefix "kube-" . -}}
    {{- fail (printf "policy.actionableNamespaces may not contain %q: the agent must never hold delete on a kube-* namespace." .) -}}
  {{- end -}}
  {{- if eq . "default" -}}
    {{- fail (printf "policy.actionableNamespaces may not contain \"default\": it is where unlabelled workloads land, so it is the one namespace whose contents nobody has decided about.") -}}
  {{- end -}}
  {{- if eq . $release -}}
    {{- fail (printf "policy.actionableNamespaces may not contain %q: that is Hephaisto's own namespace, and an agent that can restart itself mid-action loses the transaction that was keeping it honest." .) -}}
  {{- end -}}
  {{- if eq . $obs -}}
    {{- fail (printf "policy.actionableNamespaces may not contain %q: an agent that can delete the Prometheus watching it can make its own failures invisible." .) -}}
  {{- end -}}
{{- end -}}
{{- end -}}

{{/*
Guard for extraEnv.

`extraEnv` is appended LAST in the container spec, because that is what makes it useful: the
agent binds far more configuration than this chart exposes, and an operator has to be able to
set `Llm__Budget__MaxCostUsdPerHour` without the chart growing a value for every options
property.

Last position is also the hazard. Kubernetes takes the last value for a duplicated env name,
so an entry colliding with a chart-managed name does not conflict - it silently WINS, and
`kubectl get deploy -o yaml` shows both, in order, looking entirely reasonable.

Three of these are safety properties rather than settings:

  HEPHAISTO_MODE          shadowing it overrides the configured mode
  HEPHAISTO_SWITCHES_DIR  pointing it elsewhere makes the ConfigMap arm of the kill switch
                          read an empty directory - and an unreadable switch is a switch that
                          is not there
  GEMINI_API_KEY          a literal here is a plaintext credential in `helm get values`, in
  LLM_API_KEY             the release Secret and in the git repo holding your Application,
                          forever. That is precisely what `secrets.llm` exists to avoid. Both
                          are listed because which one an install uses depends on
                          Llm:Provider, and the one nobody remembered to reserve is the one
                          that gets pasted in as a literal.

Every reserved name already has a value that sets it properly, so refusing costs nothing.
*/}}
{{- define "hephaisto.validateExtraEnv" -}}
{{- $reserved := list
      "GEMINI_API_KEY" "LLM_API_KEY" "HEPHAISTO_MODE" "HEPHAISTO_SWITCHES_DIR"
      "ConnectionStrings__hephaisto" "ASPNETCORE_URLS"
      "Grafana__McpUrl" "Grafana__ServiceAccountToken"
      "Cluster__Name" "Web__WebhookToken" "Notifications__MaxPerChannelPerHour" -}}
{{- /* The indexed entries the chart itself emits. Index 0 of DeniedNamespaces is what keeps the
       investigator's read tools out of the coder namespace; an extraEnv entry at the same index
       would silently replace it. Higher indices are the operator's and still work. */ -}}
{{- range $i, $ns := include "hephaisto.selfNamespaces" . | splitList " " -}}
  {{- $reserved = append $reserved (printf "Ingest__SelfNamespaces__%d" $i) -}}
{{- end -}}
{{- if .Values.codeFix.enabled -}}
  {{- $reserved = append $reserved "Kubernetes__DeniedNamespaces__0" -}}
{{- end -}}
{{- range .Values.extraEnv -}}
  {{- if has .name $reserved -}}
    {{- fail (printf "extraEnv may not set %q: the chart manages it, and because extraEnv is appended last a duplicate would silently win rather than conflict. Use the corresponding value instead - mode, secrets.llm, secrets.grafanaMcp, grafanaMcp.url, postgres.* or codeFix.* - or, for an indexed list, the next free index." .name) -}}
  {{- end -}}
  {{- if hasPrefix "CodeFix__" .name -}}
    {{- fail (printf "extraEnv may not set %q: every CodeFix setting is a codeFix.* value, and the chart validates them TOGETHER - the namespace against the RBAC it grants, mode pr against auth. A CodeFix__ entry here would win silently and skip every one of those checks." .name) -}}
  {{- end -}}
  {{- if hasPrefix "OTEL_" .name -}}
    {{- fail (printf "extraEnv may not set %q: the OTEL_* block is derived from otel.endpoint/protocol/environment, and a half-overridden set exports telemetry to two places or to none." .name) -}}
  {{- end -}}
  {{- if has .name (list "Ingest__ClusterName" "Kubernetes__ClusterName" "Investigation__Environment__ClusterName") -}}
    {{- fail (printf "extraEnv may not set %q: the cluster is named once, by cluster.name, which fills all three of the settings that used to name it separately (backlog #139)." .name) -}}
  {{- end -}}
  {{- if hasPrefix "Llm__Prices__" .name -}}
    {{- fail (printf "extraEnv may not set %q: prices are llm.pricing, and the chart numbers the entries - one set here would collide with index 0 of that list." .name) -}}
  {{- end -}}
  {{- if hasPrefix "Investigation__Environment__InScopeNamespaces__" .name -}}
    {{- fail (printf "extraEnv may not set %q: use investigation.inScopeNamespaces." .name) -}}
  {{- end -}}
  {{- if hasPrefix "Policy__AllowedNamespaces" .name -}}
    {{- fail (printf "extraEnv may not set %q: the namespace allowlist is what the write Role is rendered from, so setting it here would let the agent believe it may act somewhere RBAC does not permit. Use policy.actionableNamespaces." .name) -}}
  {{- end -}}
{{- end -}}
{{- end -}}

{{/*
=========================================================================================
Code fixes
=========================================================================================
*/}}

{{/* The coder namespace. One definition, because the Role, the Job env and the refusals must
     never disagree about which namespace holds `create jobs`. */}}
{{- define "hephaisto.codeFixNamespace" -}}
{{- .Values.codeFix.namespace -}}
{{- end -}}

{{/* The coder's ServiceAccount: bound to nothing, token never mounted. */}}
{{- define "hephaisto.codeFixServiceAccountName" -}}
{{- printf "%s-coder" (include "hephaisto.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "hephaisto.codeFixImage" -}}
{{- printf "%s:%s" .Values.codeFix.image.repository (.Values.codeFix.image.tag | default .Chart.AppVersion) -}}
{{- end -}}

{{- define "hephaisto.codeFixEgressName" -}}
{{- printf "%s-coder-egress" (include "hephaisto.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "hephaisto.codeFixProxyUrl" -}}
{{- printf "http://%s.%s.svc:3128" (include "hephaisto.codeFixEgressName" .) (include "hephaisto.codeFixNamespace" .) -}}
{{- end -}}

{{- define "hephaisto.codeFixNugetClaim" -}}
{{- printf "%s-coder-nuget" (include "hephaisto.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* The pod label the coder Job carries - CodeFixJobSpec.AppLabel. Every coder policy selects
     on it, so it is written once. */}}
{{- define "hephaisto.codeFixPodSelector" -}}
app.kubernetes.io/name: hephaisto-coder
{{- end -}}

{{- define "hephaisto.codeFixEgressSelector" -}}
app.kubernetes.io/name: {{ include "hephaisto.codeFixEgressName" . }}
app.kubernetes.io/component: egress-proxy
{{- end -}}

{{/* Namespaces whose signals are the agent's own - escalated, never auto-actionable. Space
     separated, deduplicated, in a stable order. */}}
{{- define "hephaisto.selfNamespaces" -}}
{{- $l := list .Release.Namespace -}}
{{- with .Values.observabilityNamespace -}}{{- $l = append $l . -}}{{- end -}}
{{- if .Values.codeFix.enabled -}}{{- $l = append $l .Values.codeFix.namespace -}}{{- end -}}
{{- join " " (uniq $l) -}}
{{- end -}}

{{/*
The proxy allowlist as squid will accept it: allowlist + extraAllowlist, lower-cased,
deduplicated, and with every entry that another entry already covers DROPPED.

Squid 6 treats an overlap as FATAL - "'api.nuget.org' is a subdomain of '.nuget.org' ... You
need to remove 'api.nuget.org'" - and the pod never starts. Overlaps are natural to write (the
default list has two), so the chart resolves them rather than making every operator learn the
rule from a crash loop. `.example.com` covers `example.com`, every `x.example.com` and every
`.x.example.com`; a name without a leading dot covers only itself.
*/}}
{{- define "hephaisto.codeFixAllowlist" -}}
{{- $all := list -}}
{{- range concat .Values.codeFix.egressProxy.allowlist .Values.codeFix.egressProxy.extraAllowlist -}}
  {{- $all = append $all (lower .) -}}
{{- end -}}
{{- $all = uniq $all -}}
{{- range $d := $all -}}
  {{- $covered := false -}}
  {{- range $w := $all -}}
    {{- if and (hasPrefix "." $w) (ne $d $w) (or (hasSuffix $w $d) (eq (printf ".%s" $d) $w)) -}}
      {{- $covered = true -}}
    {{- end -}}
  {{- end -}}
  {{- if not $covered }}
{{ $d }}
  {{- end -}}
{{- end -}}
{{- end -}}

{{/*
Guard for the code-fix stage. Rendered from the Deployment, so it runs on every install - but
checks nothing unless codeFix.enabled, because a disabled stage renders nothing to protect.

The namespace refusals are the same list as validateActionableNamespaces, for the mirror-image
reason: that list is where the agent may DELETE, this one is where it may CREATE JOBS, and the
two must never meet. A namespace that is both is a namespace where the agent can run arbitrary
code next to the workloads it acts on. RbacSelfCheck refuses to boot on the same condition; this
refuses to render, which is earlier and cheaper.
*/}}
{{- define "hephaisto.validateCodeFix" -}}
{{- if .Values.codeFix.enabled -}}
{{- $ns := .Values.codeFix.namespace -}}
{{- if not $ns -}}
  {{- fail "codeFix.namespace is required when codeFix.enabled is true: it is the one namespace Hephaisto may create Jobs in." -}}
{{- end -}}
{{- if eq $ns "default" -}}
  {{- fail "codeFix.namespace may not be \"default\": it is where unlabelled workloads land, and the coder namespace must hold nothing but coders." -}}
{{- end -}}
{{- if hasPrefix "kube-" $ns -}}
  {{- fail (printf "codeFix.namespace may not be %q: a Job in a kube-* namespace runs beside the control plane." $ns) -}}
{{- end -}}
{{- if eq $ns .Release.Namespace -}}
  {{- fail (printf "codeFix.namespace may not be %q, the release namespace: a coder beside the agent could reach its database and its Service, and create jobs there would put arbitrary code next to the process that holds the kill switch." $ns) -}}
{{- end -}}
{{- if eq $ns .Values.observabilityNamespace -}}
  {{- fail (printf "codeFix.namespace may not be %q, the observability namespace: a coder there could tamper with the telemetry the agent judges itself by." $ns) -}}
{{- end -}}
{{- if has $ns .Values.policy.actionableNamespaces -}}
  {{- fail (printf "codeFix.namespace may not be %q: it is in policy.actionableNamespaces, and the namespace the agent may create Jobs in must never also be one it may act in." $ns) -}}
{{- end -}}
{{- if not .Values.codeFix.image.repository -}}
  {{- fail "codeFix.image.repository is required when codeFix.enabled is true: the coder image is a reviewed chart value, never something the agent chooses." -}}
{{- end -}}
{{- if not .Values.secrets.codeFix -}}
  {{- fail "secrets.codeFix is required when codeFix.enabled is true: the Secret in codeFix.namespace holding the coder's tokens. The chart never creates a Secret, it only references one." -}}
{{- end -}}
{{- if and (eq (lower (toString .Values.codeFix.mode)) "pr") (not .Values.auth.enabled) (not .Values.codeFix.allowUnauthenticatedApproval) -}}
  {{- fail "codeFix.mode pr is refused: auth.enabled is required, because approving a repository write needs an authenticated human, not a typed-in name. (codeFix.allowUnauthenticatedApproval exists for throwaway e2e clusters only.)" -}}
{{- end -}}
{{- if and .Values.codeFix.networkPolicy.enabled (not .Values.codeFix.egressProxy.enabled) -}}
  {{- fail "codeFix.egressProxy.enabled is required while codeFix.networkPolicy.enabled is true: the coder policy allows DNS and the proxy and nothing else, so without the proxy every attempt fails on its first request. Enable the proxy, or turn codeFix.networkPolicy off and control the coder's egress yourself." -}}
{{- end -}}
{{- range .Values.codeFix.repositories -}}
  {{- $host := (urlParse .url).host -}}
  {{- if not (has $host $.Values.codeFix.allowedRepositoryHosts) -}}
    {{- fail (printf "codeFix.repositories entry %q may not map to %q: its host %q is not in codeFix.allowedRepositoryHosts, so the mapping could never pass the host gate and would only ever be declined." .workload .url $host) -}}
  {{- end -}}
{{- end -}}
{{- end -}}
{{- end -}}
