---
title: k8s-local-deploy.sh の discord.bot.* 前回値引き継ぎと、イメージ更新時の rollout restart
type: spec
status: draft
related_ids: [NFR, IADR-0109, IADR-0283]
author: claude (Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs: []
---

# 仕様書: k8s-local-deploy.sh の discord.bot.* 前回値引き継ぎと、イメージ更新時の rollout restart

> 本仕様書は実装着手前に作成する。本作業は計画書由来の機能要求ではなく、ローカル k8s 配備手順
> （`scripts/k8s-local-deploy.sh` / `scripts/k8s-local-images.sh`）の運用上の不具合是正である
> （起点 [#673](https://github.com/endazon/ai-stock-trading/issues/673)）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（実装ADR IADR-0109 / IADR-0283 が関連する既存の実装判断。本書はそれらの延長・穴埋め）
- 計画書リンク: なし
- 無採番 `NFR` の根拠: `traceability.repo.md` の無採番許容2（計画側に対応する非機能要件が無い運用系是正。
  IADR-0283・#626 と同じ扱い）

## 目的・背景

2026-09-03 の実測で判明した 2 つの穴を是正する。

1. **`discord.bot.*` の前回値引き継ぎが無い**: `#626` / `IADR-0283` は `broker.tier` /
   `opend.enabled` に前回値引き継ぎ（`AST_VALUE_KEYS` / `ast_prev_release_value` /
   `resolve_ast_value_overrides`）を導入したが、helm 実行部の `discord.bot.*` 4 行
   （`--set-string discord.bot.guildId="$(helm_escape "${DISCORD_BOT_GUILD_ID:-}")"` 等）は
   引き継ぎ機構の外にあり無条件に上書きする。4 変数を export し忘れて再デプロイすると投入済みの
   ID が空に戻り、Discord Bot が無言で no-op へ落ちる。`#570` の訂正記録（2026-09-02）で実際に
   発生を確認しており、`#263`（IADR-0109）と同型の事故が 2 回目である。
2. **イメージ更新が Pod へ届かない**: `scripts/k8s-local-images.sh` は build/import のみで
   `kubectl rollout restart` を行わず、chart の Pod テンプレートにもイメージ内容のハッシュが
   annotation として無い（`checksum/pipeline` は `files/pipeline.json` のみ）。タグ `:latest` 固定 +
   `imagePullPolicy: IfNotPresent` のため、テンプレートが変わらないサービスはイメージを焼き直しても
   Pod が古いまま残る（OpenD Pod が Helm revision 13→14 を跨いで 25 時間生存した実測あり）。

## 対象範囲

- 対象:
  - `scripts/k8s-local-deploy.sh`（`ast_prev_release_value` の多段ネスト対応・`AST_VALUE_KEYS` への
    discord.bot.* 4 件追加・helm 実行部の書き換え・rollout restart ステップの追加）
  - `scripts/k8s-local-deploy.test.sh`（挙動の固定。discord.bot.* 引き継ぎ・rollout restart 呼び出し）
  - `deploy/helm/ai-stock-trading/README.md` / `scripts/README.md`（挙動の追随）
- 対象外:
  - moomoo-live（実弾）の解禁（既存の閂は変更しない）
  - chart Pod テンプレートへのイメージダイジェスト注釈化（案(b)。本書は案(a) を採用するため対象外。
    理由は「検討した選択肢」参照）
  - Vault / External Secrets 経由の値供給（#24 の射程）

## 設計

### 1. `discord.bot.*` の前回値引き継ぎ

- `ast_prev_release_value` を「`<top> <nested>` の 2 引数・2 階層専用」から「単一のドット区切り
  パス（任意階層）」へ一般化する。`helm get values -o yaml` の出力を awk でインデント幅
  （2 space/階層）とキー名の一致で逐次降下照合し、末端に到達したら値を返す（`broker.tier` /
  `opend.enabled` の既存 2 階層とも後方互換）。
- `AST_VALUE_KEYS` の各エントリへ 3 番目のフィールド（`set` / `set-string`）を追加し、
  `resolve_ast_value_overrides` が `--set` と `--set-string` を使い分けられるようにする
  （`--set-string` の場合のみ `helm_escape` を適用する）。
  ```
  AST_VALUE_KEYS=(
    "broker.tier|BROKER_TIER|set"
    "opend.enabled|OPEND_ENABLED|set"
    "discord.bot.guildId|DISCORD_BOT_GUILD_ID|set-string"
    "discord.bot.channelId|DISCORD_BOT_CHANNEL_ID|set-string"
    "discord.bot.allowedUserIds|DISCORD_BOT_ALLOWED_USER_IDS|set-string"
    "discord.bot.userMapping|DISCORD_BOT_USER_MAPPING|set-string"
  )
  ```
- `helm_escape` の定義を（現状ヘルパー関数群と同じ場所へ）繰り上げ、`AST_DEPLOY_LIB=1` で
  source した場合でも `resolve_ast_value_overrides` から呼べるようにする（現状は実行末尾でのみ
  定義されており、テストからは未定義エラーになる）。
- helm 実行部の 4 行（`--set-string discord.bot.*=...`）を削除し、`"${AST_VALUE_OVERRIDES[@]}"`
  に一本化する。
- `ast_prev_release_value` から読んだ既存値は「エスケープ解除済みの生値」（Helm が内部で保持する
  実際の文字列）である前提に立ち、`resolve_ast_value_overrides` が `--set-string` を組み立てる
  タイミングで一律 `helm_escape` を適用する（env 由来・前回値由来のどちらでも同じ扱いにする）。

### 2. rollout restart（案(a) 採用）

- 新関数 `ast_rollout_restart_workers`（helm 実行の後、`AST_DEPLOY_LIB=1` でも定義される場所に
  置く）が `kubectl get deployment -n "$NS" -o jsonpath=...` で現在の Deployment 名一覧を取得し、
  `opend` を除いて `kubectl rollout restart deployment <name> -n "$NS"` を順に呼ぶ。
- OpenD（`opend` という名前の Deployment）は対象外にする。OpenD は SMS/画像認証済みの moomoo
  セッションを持ち、`Recreate` 戦略・単一レプリカで再起動コストが高い（ADR-0024 決定3/4）ため、
  ローカル配備の便宜のために不要な再起動でセッションを失わせない。
- ハードコードした 11 サービス名の配列ではなく、実クラスタの Deployment 一覧から動的に導出する
  （chart の `.Values.services` にサービスが増減しても本ステップの追随作業が要らない）。

## 検討した選択肢

### rollout restart の方式（#673 タスク記載の(a)/(b)）

| 案 | 内容 | 評価 |
| --- | --- | --- |
| (a) `kubectl rollout restart`（採用） | helm upgrade 後、OpenD を除く Deployment へ再起動を明示的に打つ | 単純・確実。ローカル専用スクリプトの末尾に閉じており、本番 ArgoCD の描画（`values.yaml` のみ）に一切影響しない。挙動が変わらないサービスも再起動するコストはあるが、ローカル開発用途では許容範囲（起動は数秒〜十数秒） |
| (b) Pod テンプレートへイメージダイジェスト注釈 | `k8s-local-images.sh` がビルドしたイメージのダイジェストを算出し、chart の Deployment テンプレートへ `checksum/image` のような annotation として渡す | Helm が自然に差分検知し、変更の無いサービスは再起動されない点で理論上は優れる。しかし `k8s-local-images.sh` にダイジェスト算出＋`helm upgrade` への values 受け渡しの配線が要り、chart 側の Pod テンプレート（本番 ArgoCD 経路と共有）に「ローカル専用の一時イメージダイジェスト」という概念を持ち込むことになる。本番は `values.yaml` のみで描画されるため直接の影響は無いはずだが、テンプレートの共有構造（`deployment.yaml` は local/prod 共通）を変えるため影響評価のコストが (a) より大きい |

**採用: (a)**。理由は前掲のとおり、ローカル専用スクリプトに閉じ、本番描画へ一切触れない点を優先した。

## 受け入れ基準

- [ ] `DISCORD_BOT_GUILD_ID` / `CHANNEL_ID` / `ALLOWED_USER_IDS` / `USER_MAPPING` を export せずに
      再実行しても、前回リリースの値が引き継がれる（helm upgrade に `--set-string
      discord.bot.guildId=<前回値>` 等が渡る）。
- [ ] 明示的な空指定で非空の前回値を消す場合は中断し、`--force-empty-values` でのみ強制できる。
- [ ] 前回リリースが存在しない（新規環境）場合はエラーにならず、chart 既定（空）のまま描画される。
- [ ] カンマ・バックスラッシュを含む `allowedUserIds` / `userMapping` のエスケープが壊れない
      （helm へ渡す直前で 1 回だけエスケープする）。
- [ ] helm upgrade の後、OpenD を除く Deployment へ `kubectl rollout restart` が呼ばれる。
      OpenD（Deployment 名 `opend`）は対象外になる。
- [ ] `scripts/k8s-local-deploy.test.sh` が上記の不変条件を固定する。

## テスト方針

- `scripts/k8s-local-deploy.test.sh` の既存 `helm` スタブ（`get values`）はそのまま使い、
  `given_release_values` に discord.bot.* の 3 階層 YAML を渡すケースを追加する。
- `kubectl` スタブへ `get deployment ... -o jsonpath=...`（canned な Deployment 名一覧を返す）と
  `rollout restart deployment <name> -n <ns>`（呼び出しをログファイルへ記録する）を追加し、
  `ast_rollout_restart_workers` を直接呼んで「opend を除く全件が呼ばれる」ことを検証する。

## 計画書との差異

- 差異: なし（本作業は計画書由来の機能要求ではなく、ローカル配備手順の不具合是正）

## 未決事項

- なし。
