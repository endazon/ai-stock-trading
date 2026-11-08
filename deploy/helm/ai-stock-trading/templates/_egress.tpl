{{- /*
NFR, IADR-0644: 外への出の共通の部品（templates/networkpolicy-services.yaml・networkpolicy-opend.yaml・deployment.yaml が使う）。

- ast.egressProxy.mode $          : 検証済みの段（Off / Report / Enforce）。値域の外・引用符なしの Off（YAML の偽）は描画で止める。
                                    Report・Enforce は networkPolicy.enabled=true が要る（宣言 networkPolicy.services.<名前>.externalHttps を使うため）。
- ast.egressProxy.applies (dict "root" $ "name" <サービス名>)
                                  : そのサービスへ HTTPS_PROXY・NO_PROXY を渡すなら "true"（段が Off でなく、externalHttps: true の宣言がある）。
- ast.egressProxy.noProxy $       : NO_PROXY の値（クラスタの中の名前だけ。外の名前は入れない）。
- ast.publicEgress.except $       : 外への ipBlock の except（except ＋ extraExcept）。extraExcept が空・CIDR でない要素があれば描画で止める
                                    （ノード自身の公開アドレスを外し忘れたまま描かない。IADR-0644 決定 3）。
*/}}
{{- define "ast.egressProxy.mode" -}}
{{- $m := (.Values.networkPolicy.egressProxy | default dict).mode -}}
{{- if kindIs "bool" $m -}}
{{- fail "networkPolicy.egressProxy.mode は文字列で書きます（引用符なしの Off は YAML の偽になります。\"Off\" と書く。IADR-0644）。" -}}
{{- end -}}
{{- $m = toString ($m | default "Off") -}}
{{- if not (has $m (list "Off" "Report" "Enforce")) -}}
{{- fail (printf "networkPolicy.egressProxy.mode=%q は受けません（Off / Report / Enforce。IADR-0644）。" $m) -}}
{{- end -}}
{{- if and (ne $m "Off") (not .Values.networkPolicy.enabled) -}}
{{- fail (printf "networkPolicy.egressProxy.mode=%s には networkPolicy.enabled=true が要ります（中継へ寄せるサービスは networkPolicy.services.<名前>.externalHttps で決まるため。IADR-0644）。" $m) -}}
{{- end -}}
{{- $m -}}
{{- end -}}

{{- define "ast.egressProxy.applies" -}}
{{- $root := .root -}}
{{- if ne (include "ast.egressProxy.mode" $root) "Off" -}}
{{- $d := index ($root.Values.networkPolicy.services | default dict) .name | default dict -}}
{{- if $d.externalHttps -}}true{{- end -}}
{{- end -}}
{{- end -}}

{{- define "ast.egressProxy.noProxy" -}}
{{- $np := .Values.networkPolicy -}}
{{- $names := list "localhost" "127.0.0.1" ".svc" ".cluster.local" -}}
{{- range $ns := list .Values.namespace.name $np.infra.namespace $np.llmGateway.namespace -}}
{{- $names = append $names (printf ".%s" $ns) -}}
{{- end -}}
{{- range $name, $_ := .Values.services -}}
{{- $names = append $names (printf "%s-service" $name) -}}
{{- end -}}
{{- if .Values.infraAliases.enabled -}}
{{- range $a := .Values.infraAliases.services -}}
{{- $names = append $names $a -}}
{{- end -}}
{{- end -}}
{{- if .Values.opend.enabled -}}
{{- $names = append $names "opend" -}}
{{- end -}}
{{- range $x := ($np.egressProxy.noProxyExtra | default list) -}}
{{- $names = append $names (toString $x) -}}
{{- end -}}
{{- join "," (uniq $names) -}}
{{- end -}}

{{- define "ast.publicEgress.except" -}}
{{- $pe := .Values.networkPolicy.publicEgress -}}
{{- $extra := $pe.extraExcept | default list -}}
{{- if not $extra -}}
{{- fail "networkPolicy.publicEgress.extraExcept が空です。外への出（ipBlock）を描くときは、ノード自身の公開アドレス（/32）を配備の値で与えます（--set-json 'networkPolicy.publicEgress.extraExcept=[\"<アドレス>/32\"]'。値はリポジトリに書かない。手順は docs/operations/cloud-network-policy-runbook.md。IADR-0644）。" -}}
{{- end -}}
{{- range $c := $extra -}}
{{- if not (regexMatch "^([0-9]{1,3}\\.){3}[0-9]{1,3}/([0-9]|[12][0-9]|3[0-2])$" (toString $c)) -}}
{{- fail (printf "networkPolicy.publicEgress.extraExcept の %q は IPv4 の CIDR（a.b.c.d/nn）ではありません（IADR-0644）。" (toString $c)) -}}
{{- end -}}
{{- end -}}
{{- join ", " (concat ($pe.except | default list) $extra) -}}
{{- end -}}
