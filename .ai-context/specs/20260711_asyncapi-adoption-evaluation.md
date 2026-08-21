---
title: 通信契約の AsyncAPI 採用可否の評価（IADR-0009 の再検討トリガ）
type: spec
status: review
related_ids: [FR-04, FR-05, ADR-0001, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 仕様書: 通信契約の AsyncAPI 採用可否の評価（IADR-0009 の再検討トリガ）

> Issue [#51](https://github.com/endazon/ai-stock-trading/issues/51)（`Refs #51`）。
> [IADR-0009](../adr/IADR-0009_async-contract-format.md) がフォローアップとして明記した「契約が増え形式化の便益がコストを上回った時点で
> AsyncAPI 移行を再検討する」というトリガの**再評価**を行い、結論を新規実装 ADR（**IADR-0037**）として確定する**設計文書タスク**である。
> 本タスクはコード変更を伴わない（ドキュメントのみ）。実コード（契約ガードテスト等）の実装は後続タスクへ切り出す。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求: FR-04（取引判断）/ FR-05（発注執行）。非同期イベント契約が両 FR のサービス間連携の基盤。
- 技術検討 / ADR: `01_architecture-overview.md`、ADR-0001（platform 再利用・イベント連携）、ADR-0002（証券会社アダプタ）。
- 実装 ADR: [IADR-0009](../adr/IADR-0009_async-contract-format.md)（非同期契約の記述形式＝Markdown・AsyncAPI は現段階不採用）。
- 対象 Issue: #51（派生元 #34 / PR #45）。関連 platform イベント規約 #22。

## 評価の背景（IADR-0009 以降の変化）

| 観点 | IADR-0009 時点（2026-07-09） | 現在（2026-07-11） |
| --- | --- | --- |
| 非同期イベント契約数 | 4 | **10** |
| 契約の単一情報源 | 共有 C# `record`（`AiStockTrading.Shared.Contracts`） | 変わらず（共有 C# `record`） |
| 発行/購読の言語 | すべて .NET・同一ソリューション | 変わらず（非 .NET / 外部購読者なし） |
| 契約テスト/コード生成のニーズ | 未顕在 | 未顕在（外部・多言語消費者が不在） |
| platform（IADR-0001 整合先） | AsyncAPI 不採用（MassTransit `MessageUrn` + URN 回帰テスト） | 変わらず（AsyncAPI 不採用のまま） |

- **現状の 10 イベント**: `TradeDecisionMade` / `OrderApproved` / `OrderRejected` / `OrderExecuted` / `PriceMovementDetected` /
  `StopLossTriggered` / `InformationCollected` / `CostThresholdReached` / `AssumptionsChanged` / `ReportConfirmed`。
- 契約数は増えたが、**契約の性質**（共有アセンブリの C# 型が権威・発行/購読が同一 DLL を参照）は不変であり、AsyncAPI の主便益
  （言語中立の機械可読契約・多言語コード生成・組織横断の契約公開）を享受する消費者が存在しない。

## 評価対象・成果物（スコープ）

本タスクは以下の**ドキュメントのみ**を成果物とする。

1. **IADR-0037**（`docs/adr/IADR-0037_async-contract-format-reevaluation.md`）: 案比較（3 案）・推奨・根拠・再採用トリガの明文化。
2. `docs/adr/README.md` の一覧に IADR-0037 を追記。
3. `docs/adr/IADR-0009_async-contract-format.md` の「関連」にフォローアップ結果（IADR-0037）へのリンクを追記（履歴不変・追記のみ）。
4. `docs/api/events-and-ports.md` を現状の 10 イベントに同期（IADR-0037 が「契約管理の継続先」と名指しするため、未掲載だった
   `InformationCollected` / `CostThresholdReached` / `AssumptionsChanged` / `ReportConfirmed` の 4 件を運用・ライフサイクル
   イベント表として追記。決定の前提「Markdown で人間可読な契約が管理されている」を実体化する）。

**スコープ外（後続タスクへ切り出す）**: 推奨に含まれる「軽量な契約ガード（`MessageUrn` 回帰テスト）」の実コード実装。設計文書タスクの
範囲を超えるため、IADR で後続として明記し別 issue/PR 化する。

## 受け入れ基準

- [x] IADR-0037 が **2 案以上**（現状 Markdown / AsyncAPI 即採用 / C# 型からの AsyncAPI 生成）を評価軸ごとに比較している。
- [x] IADR-0037 が推奨（結論）と根拠を明示し、現状のイベント契約（`Shared.Contracts.Events`・MassTransit `MessageUrn`）との整合を踏まえている。
- [x] IADR-0037 が「再採用トリガ」を観測可能な条件として明文化している（IADR-0009 の曖昧なトリガを具体化）。
- [x] IADR-0009 との関係（Supersede か否か）が明示されている。
- [x] `docs/adr/README.md` の一覧に IADR-0037 が昇順・欠番なしで追記されている。
- [x] 起点 ID（FR-04/FR-05・ADR-0001・IADR-0009）と Issue #51 が各文書に記載され、トレーサビリティが保たれている。
- [x] `docs/api/events-and-ports.md` が現状の 10 イベントを網羅している（IADR-0037 の前提を実体化）。

## 関連仕様

- 通信仕様書: [events-and-ports](../../docs/api/events-and-ports.md)（非同期イベント契約・ポート契約）。
- 実装 ADR: [IADR-0009](../adr/IADR-0009_async-contract-format.md)（本評価の起点）、[IADR-0001](../adr/IADR-0001_repo-structure-and-stack.md)（platform 整合の制約）。

## 未決事項

- 契約ガード（`MessageUrn` 回帰テスト）の実装は後続 issue で行う（IADR-0037 のフォローアップ）。
