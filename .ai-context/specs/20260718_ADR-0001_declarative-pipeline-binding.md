---
title: 作業仕様書 #22 (PR-A) 取引パイプラインの宣言的バインディング（pipeline.json）
type: work-spec
status: review
related_ids: [ADR-0001, FR-02, FR-04, FR-05, IADR-0028, IADR-0077]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
issue: 22
---

# 作業仕様書 #22 (PR-A): 取引パイプラインの宣言的バインディング

## 起点・関連

- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)（platform 拡張規約への準拠）— 受け入れ基準②
- 計画書 ID: **ADR-0001**（platform 再利用＝可変部品への組み込み）、FR-02/FR-04/FR-05（取引サイクル・判断・執行）
- platform 規約（原典・隣接リポ `../microservices-platform`）: `IADR-0028`（宣言的パイプライン構成）、
  `docs/tech/composable-component-guide.md` §2.1、`IADR-0049`（共通エンベロープ等の段階適用・繰延）
- 実装 ADR: [IADR-0077](../adr/IADR-0077_declarative-pipeline-binding.md)
- 通信仕様: [events-and-ports](../../docs/api/events-and-ports.md)

## 背景

`#22` は 3 つの受け入れ基準（①共通エンベロープ、②宣言的バインディング、③構成情報 API 自己申告）を持つ。
本作業仕様書は **PR-A ＝ 受け入れ基準②** を対象とする。①③は別 PR（後続）で扱う。

platform 側の②規約は確定・利用可能である。検証器 `scripts/validate-pipeline-config.js`（V1〜V6）は移植済みだが、
CI は `--self-test` のみで**自リポの pipeline.json が無い**（`ci.yml` のコメントが復活方針を明記）。取引ドメインの
発行・購読関係はコードに散在し、宣言（単一の正）が無い。

## スコープ（本 PR）

- 取引パイプラインの発行・購読バインディングを `deploy/helm/ai-stock-trading/files/pipeline.json` に宣言する。
- 段は**変換 DAG のみ**を表現する（横断オブザーバ＝監査・通知・射影・市場監視ベースライン更新は含めない。
  根拠は [IADR-0077](../adr/IADR-0077_declarative-pipeline-binding.md)）。
- CI（`ci.yml` の `pipeline-config`）で実 pipeline.json を検証し、`scripts/scripts.test.js` に回帰テストを加える。
- GitOps 適用点として ConfigMap（`templates/configmap-pipeline.yaml`）を公開する。

### 対象外（後続 PR で扱う #22 の残）

- 受け入れ基準③（構成情報 API 自己申告 = `GET /internal/introspection`）— 後続 PR-B。本宣言を実効構成の源泉にする。
- 受け入れ基準①（共通エンベロープ）— platform でも繰延中（`IADR-0049`）。後方互換の契約テストまでを後続 PR-C で扱う。
- 起動時 fail-fast（`IPipelineStep` 全面導入）・ArgoCD 実適用・ステージング昇格ゲート（実基盤依存）。

## 宣言する変換 DAG

| 段 name | service | consumer | input | outputs |
| --- | --- | --- | --- | --- |
| decide-on-price-movement | trade-decision-service | ...TradeDecision.Worker...PriceMovementDetectedConsumer | PriceMovementDetected | TradeDecisionMade |
| decide-on-information | trade-decision-service | ...TradeDecision.Worker...InformationCollectedConsumer | InformationCollected | TradeDecisionMade |
| risk-approve | risk-management-service | ...RiskManagement.Worker...TradeDecisionMadeConsumer | TradeDecisionMade | OrderApproved, OrderRejected |
| risk-stop-loss | risk-management-service | ...RiskManagement.Worker...StopLossTriggeredConsumer | StopLossTriggered | OrderApproved |
| execute-order | order-execution-service | ...OrderExecution.Worker...OrderApprovedConsumer | OrderApproved | OrderExecuted |

sources: `PriceMovementDetected`／`StopLossTriggered`（market-monitor-service）、`InformationCollected`（information-collection-service）。

## 受け入れ基準（本 PR）

- [x] `deploy/helm/ai-stock-trading/files/pipeline.json` が検証器 V1〜V6 に合格する（`node scripts/validate-pipeline-config.js <path>`）。
- [x] CI（`pipeline-config`）が実ファイルを検証する。`scripts/scripts.test.js` に回帰テストがある。
- [x] Helm チャートが ConfigMap を描画する（`helm lint --strict` / `helm template` 緑）。
- [x] C# コード・イベント契約に変更が無い（後方互換・既定挙動不変）。
- [x] IADR-0077・本作業仕様書がある。

## テスト

- `node scripts/validate-pipeline-config.js deploy/helm/ai-stock-trading/files/pipeline.json` → OK。
- `node scripts/scripts.test.js` → 実 pipeline.json 検証を含め全緑。
- `helm lint --strict` / `helm template`（既定・派生）→ 緑。

## トレーサビリティ

- ブランチ: `feat/ADR-0001-declarative-pipeline-binding`
- コミット: `feat(ADR-0001): ...`
- コード/宣言コメント: pipeline.json の由来コメントは README（`files/README.md`）に集約。
