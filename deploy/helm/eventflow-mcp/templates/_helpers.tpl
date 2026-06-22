{{- define "eventflow-mcp.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "eventflow-mcp.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name (include "eventflow-mcp.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "eventflow-mcp.labels" -}}
app.kubernetes.io/name: {{ include "eventflow-mcp.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
{{- end -}}

{{- define "eventflow-mcp.selectorLabels" -}}
app.kubernetes.io/name: {{ include "eventflow-mcp.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "eventflow-mcp.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "eventflow-mcp.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "eventflow-mcp.mcpSecretName" -}}
{{- default (printf "%s-auth" (include "eventflow-mcp.fullname" .)) .Values.mcp.existingSecret -}}
{{- end -}}
