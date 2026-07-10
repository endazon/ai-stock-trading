---
title: ポートフォリオ状態の実データ化（OrderApproved/OrderExecuted 台帳射影による IPortfolioStateProvider 実装）
type: spec
status: review
related_ids: [FR-10, FR-05, FR-11, UC-01, UC-02, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: ポートフォリオ状態の実データ化（台帳射影）

> Issue [#12](https://github.com/endazon/ai-stock-trading/issues/12) の後続スライス。リスク管理サービスの
> `IPortfolioStateProvider` を **プレースホルダ（`PlaceholderPortfolioStateProvider`）から実データ供給版へ差し替える**。
> `OrderApproved`（承認済み注文の意図＝銘柄・方向・建玉効果）と `OrderExecuted`（約定）を購読して
> **追記専用の取引台帳（ledger）** に永続化し、台帳＋Clock から `PortfolioState` を純関数で射影する。
> 新規イベント契約は追加しない（`OrderExecuted` は銘柄・方向を持たないが、リスク管理自身が発行した
> `OrderApproved` の `Intent` を `DecisionId` で相関して補完する）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制。保有・損益・当日発注累計・連敗・当日取引銘柄に依存する統制の実データ化）、
  FR-05（発注執行の `OrderExecuted` を消費）、FR-11（監査に資する追記専用台帳）
- ユースケース（UC）: UC-01（定時サイクル）、UC-02（価格変動トリガー）の発注前検証入力
- アーキ概要: `06_technical/01_architecture-overview.md`（リスク管理を独立した強制ポイントに配置。約定イベントの発行）
- ADR: ADR-0003（LLM はリスク統制を上書きできない）
- 関連 IADR: [IADR-0005](../adr/IADR-0005_stage-capital-cap-definition.md)（段階資金上限＝取得額累計）、
  [IADR-0008](../adr/IADR-0008_daily-loss-limit-basis.md)（日次損失＝実現＋含み）、
  [IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md)（建玉効果）、
  [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)（専有 DB 永続化パターン）、本作業で新規
  [IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)
- 対象 Issue: #12（実データ化スライス）。プレースホルダが参照していた「#13/#17 連携」の実データ供給を本スライスで実現する。

## 目的・背景

現状 `IPortfolioStateProvider` は `PlaceholderPortfolioStateProvider` で、`Capital = 初期資金`・その他ゼロを返す。
このため **保有・損益に依存するリスク統制（段階資金上限＝取得額累計・当日発注金額累計・保有銘柄数・日次実現損益・
連敗時縮小・差金決済防止の当日取引銘柄）が「過小適用」**（プレースホルダ自身が起動時警告で自己申告）になっている。
取引システムの安全網が部分的に盲目な状態であり、最優先で塞ぐ。

`OrderExecuted`（FR-05）は `DecisionId, OrderId, Status, FilledQuantity, AveragePrice, ExecutedAt` のみで
**銘柄・売買方向・建玉効果を持たない**。しかしリスク管理サービス自身が発行する `OrderApproved(DecisionId, Intent, ...)`
の `Intent` がそれらを持つ。通常経路（`TradeDecisionMadeConsumer`）・損切り機械執行（`StopLossExecutionService`）とも
`OrderApproved` を出すため、`DecisionId` で相関すれば **両経路を統一的に** 台帳へ記録でき、新規契約は不要。

## 対象範囲

### 純射影（`RiskManagementService.Application`）

- `PortfolioProjection.Project(IReadOnlyList<LedgerFill> fills, DateOnly today, decimal initialCapital)` — 台帳の約定列
  （銘柄・市場・方向・建玉効果・数量・約定単価・約定時刻に補完済み）を畳み込み `PortfolioState` を返す **純関数**。
  - 建玉: **符号付き在庫・平均取得単価法**（銘柄ごとに 1 ネットポジション。Buy=+/Sell=−）。同方向の約定は加重平均で
    取得単価を更新し、反対方向の約定は在庫を減らして実現損益 = `(決済単価 − 取得単価) × 減少数量`（ロングは +、ショートは
    符号反転）を計上する。これは**現物ネッティング口座の会計として経済的に正しい**（同一銘柄でロング・ショートは同時に持てない）。
  - **`PositionEffect` の扱い（IADR-0004 との関係）**: IADR-0004 は発注前**スクリーニング**（`RiskEvaluator.isEntry`）で
    Open/Close を売買方向から分離する決定であり、本射影の**損益会計**とは別関心。現物のみ有効な現段階（信用は無効）では
    ショートエントリー（Sell×Open）は発生せず、符号推論と `PositionEffect` は完全に一致するため会計に差は出ない。
    `LedgerFill.PositionEffect` は監査・将来の**両建て（ロング/ショート別ロット）会計**のため台帳に保持する。信用有効化後の
    別ロット会計は margin フォローアップ（ADR-0007／#50）で対応する（本スライス対象外）。
  - `InvestedCapital` = 建玉中ポジションの `|数量| × 取得単価` の合計（IADR-0005 の段階資金上限＝取得額累計に一致）。
  - `OpenPositionCount` = 建玉数量が 0 でない (銘柄, 市場) の数。
  - `DailyOrderedAmount` = **当日約定分**の約定代金（`数量 × 単価`）合計（IADR-0018 の選択: 発注ではなく約定ベース）。
  - `DailyRealizedPnl` = 当日の Close 約定で計上した実現損益の合計。
  - `ConsecutiveLosses` = 直近から遡って連続する損失（実現 < 0）決済の数（利益決済でリセット）。全履歴の時系列順で判定。
  - `SymbolsTradedToday` = 当日に約定した (銘柄, 市場) の集合（差金決済防止 #26）。
  - `Capital`（当日開始運用資金・固定基準）= `initialCapital + 当日より前の Close 実現損益合計`（当日中は不変。当日実現・含みは含めない）。
  - `UnrealizedPnl = 0` / `DrawdownRatio = 0`（**本スライス対象外**。日次終値マーク＝市場データ連携が必要。IADR-0008 が #12 後続と明記）。
- ポート `IPortfolioLedgerStore`: `AppendApproval(...)` / `AppendFill(...)` / `GetFills()`（承認済み Intent と約定を保持し、相関済み `LedgerFill` を返す）。
- `LedgerPortfolioStateProvider : IPortfolioStateProvider`（`IPortfolioLedgerStore` + `IClock` を用い `Project` を呼ぶ）。
- 取引日境界: Clock の日付を単一取引日タイムゾーン（既定 JST）で解釈（IADR-0018。市場別取引日境界は後続）。

### Worker（`RiskManagementService.Worker`）

- `Composable/Steps/OrderApprovedLedgerConsumer.cs`（`IConsumer<OrderApproved>`）— 承認済み注文の `Intent` を `DecisionId` で台帳に記録。
  MassTransit 再送に対し `DecisionId` で冪等（既存なら無視）。
- `Composable/Steps/OrderExecutedLedgerConsumer.cs`（`IConsumer<OrderExecuted>`）— `Status==Filled` かつ `FilledQuantity>0` の約定を
  `OrderId` で台帳に記録（`DecisionId` で承認 Intent を相関）。`OrderId` で冪等。相関する承認が無ければ警告して無視。
- `Foundation/Persistence`: `ApprovedOrderRow`（PK=DecisionId）・`TradeFillRow`（PK=OrderId, DecisionId）を追加し、
  `EfPortfolioLedgerStore : IPortfolioLedgerStore` を実装。EF Migration を追加。
- `Program.cs`: `IPortfolioStateProvider` を `LedgerPortfolioStateProvider`（scoped）へ差し替え、
  `IPortfolioLedgerStore` を `EfPortfolioLedgerStore`（scoped）で登録、両 Consumer を `AddConsumer` 登録。
  `PlaceholderPortfolioStateProvider` は削除する。

## 受け入れ基準

CI で緑にする範囲（ユニット＋MassTransit テストハーネス＋EF InMemory/WebApplicationFactory）:
- [ ] `Project`: Buy 建て Open → 建玉・`InvestedCapital`・`OpenPositionCount`・`SymbolsTradedToday` に反映される。
- [ ] `Project`: 一部 Close → 平均取得単価で実現損益を計上し、`DailyRealizedPnl` と残建玉が正しい。
- [ ] `Project`: Sell 建て（ショート）Open→Close の実現損益符号が正しい（値下がりで利益）。
- [ ] `Project`: `Capital` は当日より前の実現損益を反映し、当日実現・含みは含めない（当日中不変）。
- [ ] `Project`: `ConsecutiveLosses` は連続損失を数え、利益決済でリセットする。
- [ ] `Project`: `DailyOrderedAmount` は当日約定代金合計、`DailyRealizedPnl` は当日実現損益合計。
- [ ] `OrderApprovedLedgerConsumer`/`OrderExecutedLedgerConsumer`: 台帳に記録され、同一 `DecisionId`/`OrderId` の再送は冪等（重複記録しない）。
- [ ] `LedgerPortfolioStateProvider`: 承認→約定を記録後、`GetCurrent()` が射影した実データを返す。
- [ ] `EfPortfolioLedgerStore`: 追記・相関・冪等が EF（InMemory プロバイダ）で動作する。
- [ ] リスク管理 Worker が起動し、既存の発注前スクリーニングが実データ供給で動作する（WebApplicationFactory）。
- [ ] 既存テストを緑に保つ（プレースホルダ削除に伴う参照の是正を含む）。

実コンテナ前提（CI 既定では実行しない・Testcontainers）:
- [ ] RabbitMQ + PostgreSQL 経由の `OrderApproved`/`OrderExecuted` → 台帳永続化 → 射影の E2E。

## 対象外（後続 PR）

- `UnrealizedPnl`・`DrawdownRatio` の算出（日次終値マーク＝市場データ連携。IADR-0008 の #12 後続）。
- 市場監視 `IPositionStore`・取引判断 `ISizingContextProvider` の実データ化（別サービスの read model。API/イベント連携で後続）。
- 損益集計の最終所有（報告書サービス #14・FR-06/07）。本スライスはリスク管理の判定入力に限定した中間 read model。
- 部分決済・ドテンの注文分解方針（#50）。本スライスは平均取得単価法での建玉・実現損益に限定。
- 市場別（日本株/米国株）の取引日境界。本スライスは単一取引日タイムゾーン（既定 JST）。

## テスト方針

- `PortfolioProjection` は純関数として単体検証（建玉・平均取得単価・実現損益の符号・当日境界・連敗）。DB・Clock 非依存。
- Consumer は MassTransit `ITestHarness` ＋ EF InMemory ストアで記録・冪等を検証。
- `LedgerPortfolioStateProvider` は EF InMemory ストアで承認→約定→射影を検証。
- Worker 起動は `RiskWorkerWebApplicationFactory`（既存）で確認。

## 関連仕様

- 先行: [20260710_risk-management-worker](20260710_risk-management-worker.md)（Slice B・EF 永続化パターン）、
  [20260710_stop-loss-execution](20260710_stop-loss-execution.md)（Close の `OrderApproved` 発行元）、
  [20260710_order-execution](20260710_order-execution.md)（`OrderExecuted` 発行元）
- 実装ADR: [IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)

## 未決事項

- `DailyOrderedAmount` を発注（承認）ベースにするか約定ベースにするか。本スライスは約定ベース（実際に資本が投下された額）を採用（IADR-0018）。
  発注ベースの上限抑止が必要になれば承認台帳から別途集計する。
- 市場別取引日境界・含み損益マーク・他サービスへの read model 供給方式（API か共有射影か）は後続で確定する。
