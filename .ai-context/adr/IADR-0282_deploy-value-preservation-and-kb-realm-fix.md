---
title: IADR-0282 k8s-local-deploy.sh は broker.tier/opend.enabled を個別に前回値へ引き継ぎ、KB レルム名を platform へ是正する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0109, IADR-0100, IADR-0102, IADR-0111, IADR-0060, IADR-0093, IADR-0273, IADR-0274]
author: claude (Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs: []
---

# IADR-0282: k8s-local-deploy.sh は broker.tier/opend.enabled を個別に前回値へ引き継ぎ、KB レルム名を platform へ是正する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: claude（[#626](https://github.com/endazon/ai-stock-trading/issues/626)）

## 起点・関連

- 関連する計画書 ID: なし（ローカル配備手順の運用系不具合是正。`NFR` 無採番＝運用系メタ作業に準じる）
- 関連する実装仕様書: [20260902_626_k8s-local-deploy-values-and-kb-realm](../specs/20260902_626_k8s-local-deploy-values-and-kb-realm.md)
- 関連 IADR: [IADR-0109](IADR-0109_deploy-secret-preservation.md)（ast-secrets の「export し忘れは保持」
  パターンの初出。本 IADR は同パターンを Helm values へ拡張する）、
  [IADR-0100](IADR-0100_route-b-values-local-standing-config.md) /
  [IADR-0102](IADR-0102_discord-env-ids-via-values.md)（values-local.yaml / discord.bot.* の先行実装）、
  [IADR-0111](IADR-0111_broker-tier-selection.md)（`broker.tier` の階層設計）、
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（`opend.enabled` の本番配備経路）、
  [IADR-0273](IADR-0273_msp-mcp-publication-allowlist-drift-detection.md) /
  [IADR-0274](IADR-0274_kb-document-body-forwarding.md)（KB レルム名誤りを残余リスクとして先に記録していた側）

## コンテキストと課題

issue #626 の実測: `scripts/k8s-local-deploy.sh` の `helm upgrade --install` は `--reuse-values` を
使わず、`broker.tier` / `opend.enabled` の env passthrough も無い。そのため一度手動 `--set` で
有効化した環境で本スクリプトを再実行すると両方とも既定へ黙って戻り、`opend.enabled=false` は
OpenD の Deployment（実測では PVC も）を削除する。加えて `values-local.yaml` の
`KnowledgeBase__Auth__Authority` が実在しない realm `microservices-platform` を指しており、KB 保存が
s2s トークン 404 で全件 fail-safe 縮退している（IADR-0274 が残余リスクとして先に指摘していた事象）。

決めるべきことは 2 点: (1) 前回値を引き継ぐ方式（`--reuse-values` か個別引き継ぎか）、(2) OpenD の
PVC 削除をどう防ぐか。

## 検討した選択肢

### (1) 前回値の引き継ぎ方式

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A. `--reuse-values` を足す | Helm に前回リリースの全 values を丸ごと再適用させ、今回指定分だけ上書きさせる | `-f values-local.yaml` との**合成順序**が実測しにくい。`--reuse-values` は「今回のコマンドラインに `-f`/`--set` が無いキーだけ前回値を使う」という仕様だが、**`-f values-local.yaml` は毎回コマンドラインに載る**ため、`values-local.yaml` に書かれているキー（①②③＋Discord＋価格文脈など多数）は常に「今回指定あり」として扱われ前回値を無視する一方、`values-local.yaml` に無いキー（`broker.tier`/`opend.enabled` 含む）は前回値がそのまま残り続ける。**「一度 moomoo-sim にしたら、values-local.yaml を将来 paper 前提の記述へ変えても再現できない」副作用**を実機で確認した（IADR-0100 の「本番バイト等価」前提とは無関係な local 専用ファイルだが、変更の反映という基本動作が壊れる）。加えて Discord ID を誤って `--set-string` し忘れた場合も前回値が永続して「空にできない」事故が起き得る |
| B. `broker.tier`/`opend.enabled` を個別に前回値へ `--set` で補う（採用） | `helm get values` で前回値を読み、env 未設定時だけ `--set key=<前回値>` を明示的に足す | 対象を 2 キーに限定できるため、`values-local.yaml` の変更が反映されなくなる副作用が起きない。ast-secrets（IADR-0109）と同じ「触らない＝保持」パターンを踏襲でき、レビュアーが理解しやすい。欠点は「引き継ぎ対象のキーを都度追加する必要がある」ことだが、対象は `broker.tier`/`opend.enabled` の 2 つのみで拡張の見込みは低い（Discord ID は既に空既定＝差し替えなしの設計で同種の問題を起こしていない） |

**採用: B（個別引き継ぎ）**。`values-local.yaml` の変更が反映されなくなる副作用は、経路B の有効化プロファイル
を明示的に管理する現行運用（IADR-0100）と相容れないため、A は不採用とする。

### (2) OpenD PVC の保護

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A. `opend.enabled` の env passthrough だけ足す | 本 IADR の (1) で解決する範囲に留める | PVC 削除は `opend.enabled` を明示的に `false` にした場合（意図的な無効化）でも起こり得る。前回値保持は「うっかり戻す」事故を防ぐが、「意図して無効化した後、また有効化するときにデバイス信頼を失う」事故は防げない |
| B. PVC に `helm.sh/resource-policy: keep` を付ける（採用） | Helm の標準機能で当該リソースを `helm uninstall` / `--set opend.enabled=false` の再描画いずれでも削除対象から外す | Helm 公式のアノテーションで追加の実装コストが無い。副作用は「明示的に消したいときも手動 `kubectl delete pvc` が要る」ことだが、デバイス信頼という高コストな状態（SMS/画像認証の再実施）を守る方が優先度が高い |

**採用: B**。(1) と組み合わせることで、うっかり戻す事故と意図した無効化の双方から PVC を守る。

## 決定

1. `scripts/k8s-local-deploy.sh` に `AST_VALUE_KEYS`（`broker.tier|BROKER_TIER` / `opend.enabled|OPEND_ENABLED`）
   と `resolve_ast_value_overrides` を追加する。`ast-secrets`（IADR-0109）と同じ「保持 / 明示上書き /
   明示空値での中断（`--force-empty-values` で強制） / 新規環境」の 4 分岐を Helm values に適用する。
   前回値は `helm get values ast -n ai-stock-trading -o yaml` を awk で読む（jq 等の追加依存を持ち込まない。
   本リポの他スクリプトが go-template / awk のみで完結させている慣行に揃える）。
2. `values-local.yaml` の `KnowledgeBase__Auth__Authority` を `http://keycloak:8080/realms/platform` へ
   是正する（実在確認: 隣接クローン `../microservices-platform` の
   `deploy/keycloak/microservices-platform-realm.json` 2 行目 `"realm": "platform"`、および MSP 自身の
   Helm chart 既定値 `global.auth.authority` も同じ realm を指す）。
3. `deploy/helm/ai-stock-trading/templates/opend.yaml` の PersistentVolumeClaim と
   `deploy/opend/k8s/pvc.yaml`（dev 生 manifest）の双方に `helm.sh/resource-policy: keep` を付ける。

## 理由

- ast-secrets で確立済みの「触らない＝保持・明示空値は確認を挟む」パターン（IADR-0109）は、Secret に
  限らず Helm values 全般に適用できる汎用的な安全設計であり、同じ語彙・同じテスト手法
  （`AST_DEPLOY_LIB=1` での関数単位テスト）を再利用できる。
- `--reuse-values` は一見シンプルだが、`-f` で毎回別ファイルを重ねる本スクリプトの運用（IADR-0100）とは
  相性が悪く、「values-local.yaml を直しても反映されない」という別種の事故を生む。個別引き継ぎは対象を
  明示するため、この副作用が起きない。
- `helm.sh/resource-policy: keep` は Helm の標準機能であり、実装コストと副作用（明示的な削除が手動になる）
  のバランスが良い。デバイス信頼の再認証は有人対応が要る高コストな操作であるため、誤って失う経路を
  塞ぐ優先度が高い。

## 結果

- 良い影響: `OPEND_ENABLED` / `BROKER_TIER` を export し忘れても前回の配備状態が保たれる。KB 保存の
  s2s トークン取得が realm 不整合で失敗しなくなる。OpenD の PVC が誤って消えなくなる。
- 悪い影響・トレードオフ: 前回値の引き継ぎ対象を増やすたびに `AST_VALUE_KEYS` への追加作業が要る
  （自動化していない）。PVC を明示的に削除したい場合は `kubectl delete pvc opend-persist -n ai-stock-trading`
  の手動操作が要る（`helm uninstall` では消えない）。
- フォローアップ: `DISCORD_BOT_*` も将来「明示的な空値での中断」が要求されたら本 IADR のパターンを
  適用する（現状は空既定＝差し替えなしの設計で別種の安全策が既にあるため対象外とした）。

## 関連

- Supersedes: なし
- Superseded by: なし
