{{- /*
NFR, IADR-0644: 外への出の共通の部品（templates/networkpolicy-services.yaml・networkpolicy-opend.yaml・deployment.yaml が使う）。

- ast.egressProxy.mode $          : 検証済みの段（Off / Report / Enforce）。値域の外・引用符なしの Off（YAML の偽）は描画で止める。
                                    Report・Enforce は networkPolicy.enabled=true が要る（宣言 networkPolicy.services.<名前>.externalHttps を使うため）。
- ast.egressProxy.applies (dict "root" $ "name" <サービス名>)
                                  : そのサービスへ HTTPS_PROXY・NO_PROXY を渡すなら "true"（段が Off でなく、externalHttps: true の宣言がある）。
- ast.egressProxy.port (dict "root" $ "name" <サービス名>)
                                  : そのサービスの中継の受け口（networkPolicy.services.<名前>.egressProxyPort。IADR-0647）。無い・1024〜65535 の
                                    整数でなければ描画で止める（Off 以外で使うときだけ呼ぶ）。
- ast.egressProxy.url (dict "root" $ "name" <サービス名>) : HTTPS_PROXY の値（http://<host>:<受け口>）。
- ast.egressProxy.validate $      : 受け口の宣言の全体の検め（重なり・externalHttps でないサービスの受け口・旧の url / port）。
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

{{- define "ast.egressProxy.port" -}}
{{- $d := index (.root.Values.networkPolicy.services | default dict) .name | default dict -}}
{{- if kindIs "invalid" $d.egressProxyPort -}}
{{- fail (printf "networkPolicy.egressProxy.mode=%s には networkPolicy.services.%s.egressProxyPort（基盤の中継のこのサービスの受け口）が要ります（IADR-0647）。" (include "ast.egressProxy.mode" .root) .name) -}}
{{- end -}}
{{- $p := toString $d.egressProxyPort -}}
{{- if or (not (regexMatch "^[0-9]{4,5}$" $p)) (lt (atoi $p) 1024) (gt (atoi $p) 65535) -}}
{{- fail (printf "networkPolicy.services.%s.egressProxyPort=%q は 1024〜65535 の整数ではありません（IADR-0647）。" .name $p) -}}
{{- end -}}
{{- $p -}}
{{- end -}}

{{- define "ast.egressProxy.url" -}}
{{- printf "http://%s:%s" .root.Values.networkPolicy.egressProxy.host (include "ast.egressProxy.port" .) -}}
{{- end -}}

{{- define "ast.egressProxy.validate" -}}
{{- $px := .Values.networkPolicy.egressProxy | default dict -}}
{{- if or (hasKey $px "url") (hasKey $px "port") -}}
{{- fail "networkPolicy.egressProxy.url / port は使いません。受け口は送り元ごとに networkPolicy.services.<名前>.egressProxyPort、宛先の名前は networkPolicy.egressProxy.host で与えます（IADR-0647）。" -}}
{{- end -}}
{{- if not $px.host -}}
{{- fail "networkPolicy.egressProxy.host（基盤の外向きの中継の名前）が空です（IADR-0647）。" -}}
{{- end -}}
{{- $seen := dict -}}
{{- range $name, $d := (.Values.networkPolicy.services | default dict) -}}
{{- $d = $d | default dict -}}
{{- if not (kindIs "invalid" $d.egressProxyPort) -}}
{{- if not $d.externalHttps -}}
{{- fail (printf "networkPolicy.services.%s.egressProxyPort は externalHttps: true のサービスだけに書きます（IADR-0647）。" $name) -}}
{{- end -}}
{{- $p := toString $d.egressProxyPort -}}
{{- if hasKey $seen $p -}}
{{- fail (printf "networkPolicy.services.%s と %s の egressProxyPort（%s）が重なっています。受け口は送り元ごとに 1 つです（IADR-0647）。" (index $seen $p) $name $p) -}}
{{- end -}}
{{- $_ := set $seen $p $name -}}
{{- end -}}
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
