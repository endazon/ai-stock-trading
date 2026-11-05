{{- /*
ADR-0006, NFR, #1525, IADR-0608 決定2: 自前のイメージの参照を組む単一の部品。

- 引数（dict）: registry・repository・tag・digest（任意。`sha256:` ＋ 64 桁の 16 進）・require（global.image.requireDigest）・what（エラーに出す名前）
- digest があれば `registry/repository:tag@sha256:…`、無ければ従来どおり `registry/repository:tag`。
- 🔴 require が真で digest が無いとき・digest の形が違うときは描画で止める（同じタグが別の中身を指す配備を作らない）。
  require が偽で digest も無い既定の描画は、本部品を入れる前と 1 バイトも変わらない。
*/}}
{{- define "ast.imageRef" -}}
{{- $ref := printf "%s/%s:%s" .registry .repository (toString .tag) -}}
{{- if .digest -}}
{{- if not (regexMatch "^sha256:[0-9a-f]{64}$" (toString .digest)) -}}
{{- fail (printf "%s の digest は sha256: と 64 桁の 16 進で与えてください（受け取った値: %v。IADR-0608）" .what .digest) -}}
{{- end -}}
{{- $ref = printf "%s@%s" $ref .digest -}}
{{- else if .require -}}
{{- fail (printf "global.image.requireDigest=true なのに %s の digest がありません。ノードで書き出した digest のファイルを -f で重ねてください（IADR-0608・手順書）" .what) -}}
{{- end -}}
{{- $ref -}}
{{- end -}}
