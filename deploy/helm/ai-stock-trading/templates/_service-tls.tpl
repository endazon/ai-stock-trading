{{- /*
NFR, NFR-05, IADR-0646: 取引ユニットのサービス間の HTTP の TLS（global.serviceTls）の単一情報源。

段（global.serviceTls.mode）:
  Off    （既定）: 何も変えない（平文の 8080 だけ）。
  Listen : 受け側が平文の 8080 と TLS の口（global.serviceTls.port）の両方で受ける。呼び手は平文のまま。
  Call   : 受け側は両方で受け、呼び手の宛先（env の http://<サービス>-service:8080）を https://<サービス>-service:<port> へ替える。
  Enforce: TLS の口だけで受ける（平文の 8080 を閉じる。probe・Service・NetworkPolicy も TLS の口だけ）。

- ast.serviceTls.mode $          : 検証済みの段（Off / Listen / Call / Enforce）。組み合わせの誤りは描画で止める。
- ast.serviceTls.httpPorts $     : 受け口の番号（空白区切り。Off は "8080"、Listen・Call は "8080 <port>"、Enforce は "<port>"）。
- ast.serviceTls.secretName (dict "root" $ "name" <サービス名>) : サービス証明書の Secret の名前。
- ast.serviceTls.rewrite (dict "root" $ "value" <env の値>) : 呼び手の宛先を TLS の口へ替えた値（Call・Enforce のときだけ替える）。
*/}}
{{- define "ast.serviceTls.mode" -}}
{{- $st := .Values.global.serviceTls | default dict -}}
{{- $m := toString ($st.mode | default "Off") -}}
{{- if or (kindIs "bool" $st.mode) (not (has $m (list "Off" "Listen" "Call" "Enforce"))) -}}
{{- fail (printf "global.serviceTls.mode は \"Off\" / Listen / Call / Enforce のどれかです（受け取った値: %v。YAML では Off を引用符で囲む。IADR-0646）。" $st.mode) -}}
{{- end -}}
{{- if ne $m "Off" -}}
{{- $p := int (toString $st.port) -}}
{{- if or (le $p 0) (eq $p 8080) -}}
{{- fail (printf "global.serviceTls.port は 8080 以外の口の番号です（受け取った値: %v。IADR-0646）。" $st.port) -}}
{{- end -}}
{{- if not $st.secretPrefix -}}
{{- fail "global.serviceTls.secretPrefix が要ります（サービス証明書の Secret の名前の前置き。IADR-0646）。" -}}
{{- end -}}
{{- $ca := .Values.global.infraCa | default dict -}}
{{- if or (not (or $ca.configMap $ca.secretName)) (not $ca.caKey) -}}
{{- fail "global.serviceTls.mode が Off 以外のときは global.infraCa（configMap か secretName と caKey）が要ります（呼び手が受け側の証明書を基盤の CA で確かめる。IADR-0646）。" -}}
{{- end -}}
{{- range $name, $svc := .Values.services -}}
{{- if or $svc.grpcPort $svc.grpcClients -}}
{{- range $k, $on := ($svc.grpcClients | default dict) -}}
{{- if $on -}}
{{- fail (printf "global.serviceTls.mode=%s は gRPC（h2c）と併用できません（services.%s.grpcClients.%s。平文の口を残さない。IADR-0646）。" $m $name $k) -}}
{{- end -}}
{{- end -}}
{{- if $svc.grpcPort -}}
{{- fail (printf "global.serviceTls.mode=%s は gRPC（h2c）の口と併用できません（services.%s.grpcPort。平文の口を残さない。IADR-0646）。" $m $name) -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- if and (eq $m "Enforce") .Values.tradingCycle.cronjob.enabled -}}
{{- fail "global.serviceTls.mode=Enforce と tradingCycle.cronjob.enabled=true は併用できません（CronJob は平文の 8080 を叩く。IADR-0646）。" -}}
{{- end -}}
{{- end -}}
{{- $m -}}
{{- end -}}

{{- define "ast.serviceTls.httpPorts" -}}
{{- $m := include "ast.serviceTls.mode" . -}}
{{- $p := int (toString .Values.global.serviceTls.port) -}}
{{- if eq $m "Off" -}}8080{{- else if eq $m "Enforce" -}}{{ $p }}{{- else -}}8080 {{ $p }}{{- end -}}
{{- end -}}

{{- define "ast.serviceTls.secretName" -}}
{{- printf "%s%s-service-tls" .root.Values.global.serviceTls.secretPrefix .name -}}
{{- end -}}

{{- define "ast.serviceTls.rewrite" -}}
{{- $m := include "ast.serviceTls.mode" .root -}}
{{- $v := toString .value -}}
{{- if has $m (list "Call" "Enforce") -}}
{{- $mm := regexFind "^http://[a-z0-9-]+-service:8080(/|$)" $v -}}
{{- if $mm -}}
{{- $svcName := trimSuffix "-service" (regexReplaceAll "^http://([a-z0-9-]+-service):8080.*$" $mm "${1}") -}}
{{- if hasKey .root.Values.services $svcName -}}
{{- $v = printf "https://%s-service:%v%s" $svcName .root.Values.global.serviceTls.port (trimPrefix (printf "http://%s-service:8080" $svcName) $v) -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- $v -}}
{{- end -}}
