{{- define "starport.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- define "starport.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- define "starport.labels" -}}
app.kubernetes.io/name: {{ include "starport.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- with .Values.commonLabels }}
{{ toYaml . }}
{{- end }}
{{- end -}}
{{- define "starport.backendServiceName" -}}{{ include "starport.fullname" . }}-backend{{- end -}}
{{- define "starport.frontendServiceName" -}}{{ include "starport.fullname" . }}-frontend{{- end -}}
{{- define "starport.backendAccountName" -}}{{ default (include "starport.fullname" .) .Values.serviceAccount.name }}{{- end -}}
{{- define "starport.frontendAccountName" -}}{{ default (printf "%s-frontend" (include "starport.fullname" .)) .Values.frontendServiceAccount.name }}{{- end -}}
{{- define "starport.migrateAccountName" -}}{{ default (printf "%s-migrate" (include "starport.fullname" .)) .Values.database.migrationJob.serviceAccount.name }}{{- end -}}
{{- define "starport.deliveryAccountName" -}}{{ default (printf "%s-delivery" (include "starport.fullname" .)) .Values.delivery.serviceAccount.name }}{{- end -}}
{{- define "starport.processingWorkerAccountName" -}}{{ default (printf "%s-processing-worker" (include "starport.fullname" .)) .Values.processingWorker.serviceAccount.name }}{{- end -}}
{{- define "starport.backendImage" -}}{{ .Values.global.imageRegistry }}/{{ .Values.image.repositoryPrefix }}/starport-backend:{{ default .Chart.AppVersion .Values.image.tag }}{{- end -}}
{{- define "starport.frontendImage" -}}{{ .Values.global.imageRegistry }}/{{ .Values.image.repositoryPrefix }}/starport-frontend:{{ default .Chart.AppVersion .Values.image.tag }}{{- end -}}
{{- define "starport.storageEnv" -}}
- {name: Storage__Mode, value: {{ .Values.config.storage.mode | quote }}}
- {name: Storage__UploadBucket, value: {{ .Values.config.storage.uploadBucket | quote }}}
- {name: Storage__ReleaseBucket, value: {{ .Values.config.storage.releaseBucket | quote }}}
- {name: Storage__SaveBucket, value: {{ .Values.config.storage.saveBucket | quote }}}
- {name: GameDelivery__Enabled, value: {{ .Values.gameContent.enabled | quote }}}
- {name: GameDelivery__RootDomain, value: {{ .Values.gameContent.rootDomain | quote }}}
- {name: GameDelivery__Scheme, value: {{ .Values.gameContent.scheme | quote }}}
- {name: GameDelivery__AppOrigin, value: {{ .Values.gameContent.appOrigin | quote }}}
{{- end -}}
{{- define "starport.databaseEnv" -}}
- name: Database__Host
  value: {{ required "externalDatabase.host is required" .Values.externalDatabase.host | quote }}
- name: Database__Port
  value: {{ .Values.externalDatabase.port | quote }}
- name: Database__Name
  value: {{ .Values.externalDatabase.name | quote }}
- name: Database__Username
  value: {{ .Values.externalDatabase.username | quote }}
- name: Database__Password
  valueFrom:
    secretKeyRef:
      name: {{ .Values.secrets.existingSecret }}
      key: {{ .Values.secrets.databasePasswordKey }}
- name: Database__MaxPoolSize
  value: {{ .Values.externalDatabase.maxPoolSize | quote }}
- name: Database__SslMode
  value: {{ .Values.externalDatabase.sslMode | quote }}
{{- end -}}
