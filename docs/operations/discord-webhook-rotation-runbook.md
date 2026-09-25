---
title: 運用 Runbook — Discord Webhook の再発行（漏洩時の失効）と蓄積分の後始末
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
---
<!-- trace:
ids: [NFR, FR-09]
adrs: []
iadrs: [IADR-0109, IADR-0121, IADR-0333, IADR-0341]
specs: [20260926_318_discord-webhook-rotation]
issues: [#318, #289, #311, #313, #751, #760, #795]
-->
<!-- 起点 ID・関連 ADR/IADR・仕様書名・修飾付き issue 参照は本文へ書かず、上の trace ブロックへ入れる（scripts/check-trace-blocks.js が検査する） -->

# 運用 Runbook: Discord Webhook の再発行（漏洩時の失効）と蓄積分の後始末

> 運用仕様書（`docs/operations/`）の下位にあたる手順書である。
> **Discord Webhook の URL はそれ自体が資格情報である**（知っていれば認証なしで当該チャンネルへ投稿できる）。
> **失効させる手段は、その Webhook を Discord 側で削除することだけである。** ログから出力を止めても、既に見た者の手元の URL は生きている。
>
> 🔴 **本書の手順 1〜3 は利用者が行う。** Discord の管理画面の操作と、秘密情報の入力は AI が代行しない。

## この手順を実行する条件（いつ走らせるか）

- Webhook の URL がログ・トレース・チャット・スクリーンショット・issue などに平文で出た（または出た疑いがある）とき。
- Discord サーバーの管理者が入れ替わったとき。
- 過去に一度もこの手順を実行していない現行の Webhook を使い続けているとき（アプリログへの平文出力が止まる前から使っている URL は、漏洩済みとして扱う）。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | Discord サーバーの「ウェブフックの管理」（または管理者）。基盤の「秘密情報・接続設定の管理」画面へのログイン |
| 必要なツール | ブラウザ、`kubectl`（確認のみ）、`curl`（確認のみ） |
| 所要時間の目安 | 10 分（新旧の差し替えのあいだ通知が数十秒止まる） |
| 実行者 | **利用者本人** |

### 現在の配線（2026-09-26 に読み取りのみで実測）

| 項目 | 値 |
| --- | --- |
| URL を読む箇所 | notification-service の環境変数 `Notifications__Discord__WebhookUrl` ← Secret `ast-secrets` のキー `discord-webhook-url`（`optional: true`） |
| `ast-secrets` の所有者 | **ExternalSecret `ast-secrets`**（`ClusterSecretStore/vault-backend`・更新間隔 1h・`SecretSynced`）。値の正は **Vault の KV `ai-stock-trading/app-secrets` のプロパティ `discord-webhook-url`** |
| 反映 | notification-service に `secret.reloader.stakater.com/reload: ast-secrets` が付いており、Secret が変わると Reloader が再起動する |
| ログへの出力 | 送信先は `https://discord.com/***` と秘匿されて出る（実測。平文の URL の形に一致する行は 0 件） |
| トレースへの出力 | エクスポータの手前で `url.full` を scheme と host まで落とすプロセッサが入っている |

🔴 **`kubectl create secret ... | kubectl apply` で `ast-secrets` を書き換えない。** `ast-secrets` は ExternalSecret が所有しており、
次の同期（最長 1 時間）で Vault の値に戻される。書き換えた直後だけ新しい URL で動き、しばらくして**古い URL に戻る**という、
最も気づきにくい壊れ方をする。値は必ず Vault 側（下の手順 2）で変える。

## 手順

### 手順 1: Discord で新しい Webhook を作る（旧い方はまだ消さない）

1. Discord で通知先のサーバーを開き、**サーバー設定 → 連携サービス → ウェブフック**を開く。
2. **新しいウェブフック**を作り、通知先チャンネル（現行と同じチャンネル）を選ぶ。名前は旧いものと区別できるようにする（例: 末尾に作成日）。
3. **ウェブフック URL をコピー**する。**どこにも貼らない**（チャット・issue・メモ・端末の履歴に残さない）。

### 手順 2: 新しい URL を Vault へ書く（画面から）

1. 基盤の**「秘密情報・接続設定の管理」画面**を開き、ai-stock-trading の `discord-webhook-url` を選ぶ。
2. 手順 1 の URL を貼って保存する。保存すると基盤の BFF が ExternalSecret `ast-secrets` を即時同期させ、Reloader が notification-service を再起動する。

**画面が使えないときのフォールバック**（Vault CLI。値は標準入力から渡し、コマンド行と履歴に残さない）:

```bash
# 🔴 put ではなく patch。put は ai-stock-trading/app-secrets の他のキー（API 鍵群）を全部消す
vault kv patch ai-stock-trading/app-secrets discord-webhook-url=-    # ← 実行後に URL を貼って Enter、Ctrl-D
kubectl -n ai-stock-trading annotate externalsecret ast-secrets force-sync="$(date +%s)" --overwrite
```

### 手順 3: 新しい URL で届くことを確かめてから、旧い Webhook を削除する

1. 下の「確認」の 1〜3 を満たすことを確かめる。
2. Discord の**サーバー設定 → 連携サービス → ウェブフック**で、**旧い Webhook を削除**する。**これで漏洩した URL は失効する**（本書の主目的）。
3. 下の「確認」の 4 で、旧 URL が無効になったことを確かめる。

### 手順 4: 蓄積分の後始末（判断つき）

**旧 URL が失効した時点で、どこかに残っている旧 URL は投稿の能力を失う。** したがって後始末は「漏洩の解消」ではなく「衛生」であり、
消さずに保持期間の経過を待つのが既定である。消すかどうかは利用者が決める。

| 蓄積先 | 現況（2026-09-26 実測） | 既定の扱い |
| --- | --- | --- |
| ログ基盤（Loki）・トレース基盤（Tempo） | **現行クラスタに Pod も PVC も無い。** 基盤の namespace は 10 日前に作り直され、PV の回収方針は `Delete`。当時のデータはボリュームごと消えた可能性が高い（中身は確認していない）。可観測性データは基盤の切替計画でも破棄の裁定が出ている | 対応不要 |
| otel-collector のコンテナログ（エクスポータは debug のみ） | 現行と直前のコンテナのログに、平文の Webhook URL の形に一致する行は 0 件 | 対応不要（kubelet のログローテーションで流れる） |
| notification-service のコンテナログ | 同上 0 件（送信先は `***` で出る） | 対応不要 |
| GitHub（issue / PR の本文とコメント・コード） | 出所の issue と PR の本文・コメントに、URL の形に一致する文字列は 0 件。コード検索も 0 件 | 対応不要 |
| 端末の履歴・AI セッションの記録・手元のメモ | **AI は調べていない**（利用者の端末上の私的な記録であるため） | 利用者が判断する。探すなら下のコマンド |
| Rancher Desktop の名前なしボリューム（過去の compose 構成の残り得るもの） | 5 件あるが中身は確認していない | 利用者が判断する。消すと戻らない |

端末側を探すとき（**件数だけを出し、中身は表示しない**）:

```bash
grep -rEc 'discord(app)?\.com/api/webhooks/[0-9]{15,}/[A-Za-z0-9_-]{20,}' <探すディレクトリ> 2>/dev/null | grep -v ':0$'
```

## 確認（この手順が成功したと言える条件）

```bash
# 1) 同期された（LAST SYNC が手順 2 の後になっている）
kubectl -n ai-stock-trading get externalsecret ast-secrets

# 2) notification-service が手順 2 の後に再起動した（AGE が新しい）
kubectl -n ai-stock-trading get pods | grep notification-service

# 3) 新しい URL で投稿できる（利用者の端末で。URL は read -s で受け、画面と履歴に残さない）
read -rs NEW_URL && curl -s -o /dev/null -w '%{http_code}\n' -H 'Content-Type: application/json' \
  -d '{"content":"[検証] Webhook 再発行の疎通確認です（取引の通知ではありません）"}' "$NEW_URL"; unset NEW_URL
#    → 204

# 4) 旧い URL が失効した（手順 3 の後）
read -rs OLD_URL && curl -s -o /dev/null -w '%{http_code}\n' "$OLD_URL"; unset OLD_URL
#    → 404（Unknown Webhook）。200 なら旧い Webhook がまだ残っている

# 5) 新しい URL がログに平文で出ていない（件数だけ）
kubectl -n ai-stock-trading logs deploy/notification-service | grep -cE 'discord(app)?\.com/api/webhooks/[0-9]{15,}/'
#    → 0
```

次の通常の通知（報告書の確定・kill switch 等のイベント）が実チャンネルに届き、notification-service のログに
`HTTP 応答: 204 POST https://discord.com/***` が出れば、アプリの経路でも新しい URL が使われている。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| 確認 3 が 401 / 404 | コピーした URL が途中で切れている・別の Webhook を消した | Discord の画面で新しい Webhook が残っているか確かめ、URL をコピーし直して手順 2 からやり直す |
| 確認 1 の LAST SYNC が古いまま | 画面の保存が同期を起こせなかった | フォールバックの `force-sync` 注釈を打つ。それでも同期しないなら `kubectl -n ai-stock-trading describe externalsecret ast-secrets` のイベントを見る |
| 数十分後に通知が旧い URL へ飛んでいる／届かなくなった | `ast-secrets` を `kubectl` で直接書き換えた（ExternalSecret が Vault の値へ戻した） | Vault 側に新しい URL を書く（手順 2）。`kubectl` で Secret を触らない |
| 旧い Webhook を消したら通知が止まった | 手順 2 が反映される前に消した | 手順 2 をやり直す。旧 URL は復活させない（新しく作る） |

## 記録

- 実施日と、確認 3・4 の HTTP ステータスを #318（または同種の事故の issue）へ残す。**URL そのものは書かない。**
- 手順 4 で消したもの・消さないと決めたものと理由を同じ issue に残す。

## 限界（この手順で担保できないこと）

- 旧 URL が失効する前に第三者が投稿していたかは、本書では分からない（Discord の監査ログで Webhook 経由の投稿を確かめる）。
- 新しい URL の再漏洩を防ぐのはアプリ側の秘匿（ログ・トレース）であり、本書ではない。秘匿を外す変更が入れば再び漏れる。
- Bot トークン（`discord-bot-token`）は別の資格情報である。漏洩したなら Discord Developer Portal の **Reset Token** で失効させ、
  同じく手順 2 の要領で `discord-bot-token` を書き換える（本書の手順 1・3 は Webhook 専用）。
