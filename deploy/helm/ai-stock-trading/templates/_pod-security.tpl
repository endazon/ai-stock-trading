{{- /*
NFR, IADR-0609, #1526: Pod とコンテナのセキュリティ設定（多層防御・最小権限）の単一情報源。値は values.yaml の podSecurity。

段（podSecurity.stage。Worker と CronJob に効く。OpenD は opend.podSecurity.stage で templates/opend.yaml が組む）:
  1（既定）: 動作を変えない。Worker は runAsUser: 0 を明示する（イメージは USER $APP_UID＝非 root。
             発注執行の鍵の mount 0400 と Stage 0 の PVC の既存ファイルが root を前提にしているため、段を上げるまで root のまま）。
  2      : root のまま seccomp RuntimeDefault・権限昇格なし・ケーパビリティを全て落とす。
  （段に依らず）: SA トークンを置かない（automountServiceAccountToken: false）。AST の Pod は k8s API を使わない
             （serviceAccountName も Role も無い。メッシュのサイドカーは自前の projected token を使う）。
  3      : 2 に加えて非 root（runAsNonRoot・uid / gid・fsGroup）。鍵の mount は 0440（fsGroup のグループ読み）。

- ast.podSecurity.stage                 : 検証済みの段（1〜3 の整数）。値域の外は描画で止める。
- ast.podSecurity.validate              : 段・readOnlyRootFilesystem・Pod Security Admission の水準の組み合わせを検める（deployment.yaml の冒頭で呼ぶ）。
- ast.pod.security (dict "root" $ "uid" <uid> "gid" <gid> "fsGroup" <bool> "pinRoot" <bool>)
                                        : Pod spec の automountServiceAccountToken と securityContext（無ければ空）。
- ast.container.security $              : コンテナの securityContext（段 1 は空）。
- ast.podSecurity.readOnlyRoot $        : readOnlyRootFilesystem を描くなら "true"、描かないなら空。
- ast.podSecurity.secretMode (dict "root" $ "mode" <既定のモード>) : 段 3 なら 0440（288）、それ以外は既定のモード。
*/}}
{{- define "ast.podSecurity.stage" -}}
{{- $raw := (.Values.podSecurity | default dict).stage -}}
{{- $s := toString $raw -}}
{{- if not (has $s (list "1" "2" "3")) -}}
{{- fail (printf "podSecurity.stage は 1 / 2 / 3 のどれかです（受け取った値: %v。IADR-0609）" $raw) -}}
{{- end -}}
{{- $s -}}
{{- end -}}

{{- define "ast.podSecurity.validate" -}}
{{- $ps := .Values.podSecurity -}}
{{- $st := int (include "ast.podSecurity.stage" .) -}}
{{- if and $ps.readOnlyRootFilesystem (lt $st 2) -}}
{{- fail "podSecurity.readOnlyRootFilesystem=true は podSecurity.stage が 2 以上のときだけ使えます（IADR-0609）。" -}}
{{- end -}}
{{- $psa := $ps.admission -}}
{{- range $k := list "warn" "audit" -}}
{{- if not (has (index $psa $k | toString) (list "privileged" "baseline" "restricted")) -}}
{{- fail (printf "podSecurity.admission.%s は privileged / baseline / restricted のどれかです（受け取った値: %v。IADR-0609）" $k (index $psa $k)) -}}
{{- end -}}
{{- end -}}
{{- $enf := $psa.enforce | default "" | toString -}}
{{- if not (has $enf (list "" "privileged" "baseline" "restricted")) -}}
{{- fail (printf "podSecurity.admission.enforce は 空 / privileged / baseline / restricted のどれかです（受け取った値: %v。IADR-0609）" $enf) -}}
{{- end -}}
{{- if eq $enf "restricted" -}}
{{- $ost := int (toString (.Values.opend.podSecurity | default dict).stage) -}}
{{- if or (lt $st 3) (and .Values.opend.enabled (lt $ost 3)) -}}
{{- fail (printf "podSecurity.admission.enforce=restricted には podSecurity.stage=3（受け取った値: %d）と、opend.enabled のとき opend.podSecurity.stage=3（受け取った値: %d）が要ります。満たさない Pod は作れなくなります（IADR-0609）。" $st $ost) -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "ast.pod.security" -}}
{{- $st := int (include "ast.podSecurity.stage" .root) -}}
automountServiceAccountToken: false
{{- if ge $st 3 }}
{{- $uid := int (default 0 .uid) -}}
{{- $gid := int (default 0 .gid) -}}
{{- if or (le $uid 0) (le $gid 0) -}}
{{- fail (printf "段 3 の runAsUser / runAsGroup は 1 以上の整数で与えてください（受け取った値: %v / %v。IADR-0609）" .uid .gid) -}}
{{- end }}
securityContext:
  runAsNonRoot: true
  runAsUser: {{ $uid }}
  runAsGroup: {{ $gid }}
  {{- if .fsGroup }}
  fsGroup: {{ $gid }}
  {{- end }}
  seccompProfile:
    type: RuntimeDefault
{{- else if or (ge $st 2) .pinRoot }}
securityContext:
  {{- if .pinRoot }}
  runAsUser: 0
  {{- end }}
  {{- if ge $st 2 }}
  seccompProfile:
    type: RuntimeDefault
  {{- end }}
{{- end }}
{{- end -}}

{{- define "ast.container.security" -}}
{{- $st := int (include "ast.podSecurity.stage" .) -}}
{{- if ge $st 2 }}
securityContext:
  allowPrivilegeEscalation: false
  capabilities:
    drop:
      - ALL
  {{- if .Values.podSecurity.readOnlyRootFilesystem }}
  readOnlyRootFilesystem: true
  {{- end }}
{{- end }}
{{- end -}}

{{- define "ast.podSecurity.readOnlyRoot" -}}
{{- if and .Values.podSecurity.readOnlyRootFilesystem (ge (int (include "ast.podSecurity.stage" .)) 2) -}}true{{- end -}}
{{- end -}}

{{- define "ast.podSecurity.secretMode" -}}
{{- if ge (int (include "ast.podSecurity.stage" .root)) 3 -}}288{{- else -}}{{ .mode }}{{- end -}}
{{- end -}}
