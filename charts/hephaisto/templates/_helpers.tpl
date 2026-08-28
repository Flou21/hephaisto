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
