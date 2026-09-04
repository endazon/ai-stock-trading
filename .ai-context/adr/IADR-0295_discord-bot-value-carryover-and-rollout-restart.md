---
title: IADR-0295 k8s-local-deploy.sh は discord.bot.* も前回値へ引き継ぎ、helm upgrade 後に OpenD を除く Deployment へ rollout restart する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0109, IADR-0283, IADR-0102, IADR-0111, IADR-0060]
author: claude (Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs: []
related_specs:
  - ../specs/20260903_673_deploy-value-carryover-and-rollout.md
---

# IADR-0295: k8s-local-deploy.sh は discord.bot.* も前回値へ引き継ぎ、helm upgrade 後に OpenD を除く Deployment へ rollout restart する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

## 起点・関連

- 関連する計画書 ID: なし（ローカル配備手順の運用系不具合是正。`NFR` 無採番＝運用系メタ作業に準じる）
- 関連する実装仕様書: [20260903_673_deploy-value-carryover-and-rollout](../specs/20260903_673_deploy-value-carryover-and-rollout.md)
- 関連 IADR: [IADR-0109](IADR-0109_deploy-secret-preservation.md)（ast-secrets の「export し忘れは保持」
  パターンの初出）、[IADR-0283](IADR-0283_deploy-value-preservation-and-kb-realm-fix.md)（`broker.tier` /
  `opend.enabled` への同パターンの拡張。本 IADR は同 IADR が対象外とした `discord.bot.*` を穴埋めする）、
  [IADR-0102](IADR-0102_discord-env-ids-via-values.md)（discord.bot.* の values 経路の先行実装）、
  [IADR-0111](IADR-0111_broker-tier-selection.md) / [IADR-0060](IADR-0060_opend-production-cutover-gates.md)

## コンテキストと課題

[#673](https://github.com/endazon/ai-stock-trading/issues/673) の実測で判明した 2 つの穴を是正する。

1. IADR-0283 は `broker.tier` / `opend.enabled` に前回値引き継ぎを導入したが、`discord.bot.*` の 4 環境
   固有 ID（`guildId` / `channelId` / `allowedUserIds` / `userMapping`）は「空既定＝差し替えなしの設計で
   別種の安全策が既にある」として対象外とした。しかし実際の helm 実行部は次の 4 行で**無条件に**
   `DISCORD_BOT_*` を `--set-string` しており、引き継ぎ機構の外にある:
   ```
   --set-string discord.bot.guildId="$(helm_escape "${DISCORD_BOT_GUILD_ID:-}")"
   --set-string discord.bot.channelId="$(helm_escape "${DISCORD_BOT_CHANNEL_ID:-}")"
   --set-string discord.bot.allowedUserIds="$(helm_escape "${DISCORD_BOT_ALLOWED_USER_IDS:-}")"
   --set-string discord.bot.userMapping="$(helm_escape "${DISCORD_BOT_USER_MAPPING:-}")"
   ```
   4 変数を export し忘れて再デプロイすると投入済みの ID が空へ戻り、Discord Bot が無言で no-op へ落ちる。
   `#570` の訂正記録（2026-09-02）で実際に発生を確認しており、`#263`（IADR-0109）と同型の事故が
   **2 回目**である。
2. `scripts/k8s-local-images.sh` は build/import のみで `kubectl rollout restart` を行わない。chart の
   Pod テンプレートにもイメージ内容のハッシュが annotation として無く（`checksum/pipeline` は
   `files/pipeline.json` のみ）、タグ `:latest` 固定 + `imagePullPolicy: IfNotPresent` のため、
   テンプレートが変わらないサービスはイメージを焼き直しても Pod が古いまま残る（実測: OpenD Pod が
   Helm revision 13→14 を跨いで 25 時間生存）。

## 検討した選択肢

### (1) discord.bot.* の前回値引き継ぎ方式

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A. `discord.bot.*` 専用の別の分岐を新設する | IADR-0283 の `resolve_ast_value_overrides` とは別に、discord.bot.* だけの引き継ぎロジックを書く | ast-secrets（IADR-0109）・broker.tier/opend.enabled（IADR-0283）と合わせて 3 種類目の「保持／明示上書き／明示空値は確認／新規環境」の実装になり、同じ規則を 3 箇所で保守することになる。`--set-string` のエスケープ（IADR-0102）を別経路で扱うため、2 つの経路でエスケープの有無がずれる事故が起きやすい |
| B. `AST_VALUE_KEYS` へ合流させ、`ast_prev_release_value` を任意階層へ一般化する（採用） | 既存の `resolve_ast_value_overrides` の分岐ロジックをそのまま再利用し、`ast_prev_release_value` を「2 階層専用（`<top> <nested>`）」から「任意階層のドット区切りパス」へ一般化する。`--set` と `--set-string` を使い分けるための 3 番目のフィールドを `AST_VALUE_KEYS` の各要素へ追加する | 保守対象が 1 箇所に閉じる。`ast_prev_release_value` の一般化は `broker.tier` / `opend.enabled`（2 階層）の既存呼び出しとも後方互換であることを確認済み（awk のインデント幅・キー名の逐次降下照合で N 階層に対応）。`helm_escape` を `resolve_ast_value_overrides` 内の 1 箇所（`--set-string` の分岐）だけに集約でき、エスケープ漏れの経路が構造的に生まれない |

**採用: B**。理由は保守箇所の一元化と、エスケープ処理を 1 箇所に集約できる点を優先した。

### (2) rollout restart の方式

| 案 | 内容 | 評価 |
| --- | --- | --- |
| (a) `kubectl rollout restart`（採用） | helm upgrade 後、OpenD を除く Deployment へ再起動を明示的に打つ | 単純・確実。ローカル専用スクリプトの末尾に閉じており、本番 ArgoCD の描画（`values.yaml` のみで行われる）に一切影響しない。挙動が変わらないサービスも再起動するコストはあるが、ローカル開発用途では許容範囲（起動は数秒〜十数秒） |
| (b) Pod テンプレートへイメージダイジェスト注釈 | `k8s-local-images.sh` がビルドしたイメージのダイジェストを算出し、chart の Deployment テンプレートへ `checksum/image` のような annotation として渡す | Helm が自然に差分検知し、変更の無いサービスは再起動されない点で理論上は優れる。しかし `k8s-local-images.sh` にダイジェスト算出＋`helm upgrade` への values 受け渡しの配線が要り、chart 側の Pod テンプレート（本番 ArgoCD 経路と共有する `deployment.yaml`）に「ローカル専用の一時イメージダイジェスト」という概念を持ち込むことになり、影響評価のコストが (a) より大きい |

**採用: (a)**。ローカル専用スクリプトに閉じ、本番描画へ一切触れない点を優先した。

対象の絞り込みは実クラスタの `kubectl get deployment` から動的に導出する（ハードコードした 11
サービス名の配列にはしない）。chart の `.Values.services` にサービスが増減しても本ステップの追随作業が
要らないようにするためである。

## 決定

1. `AST_VALUE_KEYS` の各要素へ 3 番目のフィールド（`set` / `set-string`）を追加し、
   `discord.bot.guildId|DISCORD_BOT_GUILD_ID|set-string` ほか 4 件を合流させる。`ast_prev_release_value`
   を「`<top> <nested>` の 2 引数」から「単一のドット区切りパス（任意階層）」へ一般化する
   （`helm get values -o yaml` の出力を awk でインデント幅・キー名の逐次降下照合する）。
2. `resolve_ast_value_overrides` が `set-string` 指定のキーに対して `helm_escape` を適用してから
   `--set-string` を組み立てる（env 由来・前回値由来のどちらでも同じタイミングで 1 回だけ適用する）。
   `helm_escape` の定義を実行末尾からヘルパー関数群（`AST_DEPLOY_LIB=1` でも読み込まれる位置）へ
   繰り上げる。
3. helm 実行部の 4 行の直接 `--set-string discord.bot.*=...` を削除し、`"${AST_VALUE_OVERRIDES[@]}"`
   に一本化する。
4. 新関数 `ast_rollout_restart_workers` を追加し、helm upgrade の直後に呼ぶ。`kubectl get deployment
   -n ai-stock-trading -o jsonpath=...` で現在の Deployment 名一覧を取得し、`opend` を除いて
   `kubectl rollout restart deployment <name> -n ai-stock-trading` を順に呼ぶ。

## 理由

- discord.bot.* の引き継ぎを `broker.tier` / `opend.enabled` と同じ関数へ合流させることで、
  「触らない＝保持・明示空値は確認を挟む」という規則の実装を 1 箇所に保つ。`ast_prev_release_value` の
  一般化は既存 2 階層の呼び出しに対して後方互換であることをテスト（T-626 系列。全件緑）で確認済み。
- OpenD を rollout restart の対象から除くのは、SMS/画像認証済みの moomoo セッションを持ち、
  `Recreate` 戦略・単一レプリカで再起動コストが高いため（ADR-0024 決定3/4）。ローカル配備の便宜のために
  不要な再起動でセッションを失わせない。
- Deployment 一覧を実クラスタから動的に取得する方式は、ハードコードした配列（`k8s-local-images.sh` の
  `MAPPING` のような固定リスト）と比べて、サービスの増減に追随作業が要らない。

## 結果

- 良い影響: `DISCORD_BOT_GUILD_ID` / `CHANNEL_ID` / `ALLOWED_USER_IDS` / `USER_MAPPING` を export し忘れても
  前回の配備状態が保たれる。`k8s-local-images.sh` でイメージを焼き直した後、`k8s-local-deploy.sh` を
  実行すれば Pod テンプレートが変わらないサービスにも新イメージが確実に届く。
- 悪い影響・トレードオフ: rollout restart は変更の無いサービスも含めて毎回全件再起動する（ローカル
  開発用途では許容範囲と判断）。前回値の引き継ぎ対象を増やすたびに `AST_VALUE_KEYS` への追加作業が
  要る（IADR-0283 と同じトレードオフ。自動化していない）。
- フォローアップ: `ast_rollout_restart_workers` は現在の Deployment 一覧全体を対象にするため、将来
  ローカル専用の別サービス（本番に無いもの）を追加した場合はそのまま巻き込まれる想定である
  （現状は問題にならない）。

## 関連

- Supersedes: なし（IADR-0283 の対象外リスクを埋める追加決定であり、IADR-0283 決定1〜3 は不変）
- Superseded by: なし
