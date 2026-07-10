---
title: サービス間連携 継続（市場監視 IPositionStore → リスク管理の実データ化）
type: spec
status: review
related_ids: [FR-03, FR-10, ADR-0001, ADR-0003, IADR-0018]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: 市場監視の保有ポジションをリスク管理から同期照会する

> Issue [#22](https://github.com/endazon/ai-stock-trading/issues/22)（サービス間連携）の継続。市場監視（#10）の
> `IPositionStore`（現プレースホルダ＝保有なし）を、リスク管理（#12）が #63 取引台帳から射影する**保有ポジション**を
> 同期 API 照会する実装へ差し替え、損切りライン検知に実保有を供給する。IADR-0028/0029 の同期 API 方式・フェイルセーフ既定を踏襲する。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-03（市場監視・損切りライン検知）、FR-10（保有・損切り監視は最後まで維持）
- アーキ概要: 「同期 API 依存は…契約（API）として管理する」／Database per Service（ADR-0001）
- ADR: ADR-0003（損切りは機械的に執行）、IADR-0018（#63 台帳＝OrderApproved Intent と OrderExecuted の相関射影）
- 前提条件（05_trading-assumptions §4/§5）: 1取引リスクは ATR 連動、損切り幅の目安 3%（初期資金の注記）
- 関連 IADR: 本作業で新規 [IADR-0030](../adr/IADR-0030_position-store-sync-api.md)（方式は IADR-0028/0029 を踏襲）
- 対象 Issue: #22（継続）

## コンテキストと課題

`IPositionStore.GetOpenPositions()` は損切りライン検知（`StopLossEvaluator`）へ保有ポジション（`HeldPosition`＝銘柄・市場・
建玉方向・数量・取得単価・**損切り価格**）を供給する。現状はプレースホルダで空（保有なし＝検知対象なし）。

**課題**: #63 台帳（`PortfolioProjection`）は約定列から**銘柄別のネット建玉（銘柄・市場・方向・数量・平均取得単価）**を
射影できるが、**損切り価格を保持しない**。損切り価格の一次情報は取引判断 LLM の `stopLossDistancePerShare`（ATR 連動）だが、
確定済みイベント契約（`OrderIntent`/`OrderApproved`/`OrderExecuted`）にも #63 台帳（`LedgerFill`）にも含まれない（IADR-0018 は
契約最小化のため銘柄/方向のみ補完）。よって損切り価格は現時点で権威データが存在しない。

## 対象範囲

### リスク管理サービス `RiskManagementService`（保有ポジションの所有・公開）

- Application:
  - `PortfolioProjection.ProjectOpenPositions(fills)` を追加（既存の符号付き在庫・平均取得単価法の `Apply` を再利用する純関数）。
    銘柄別ネット建玉 `OpenPosition`（Symbol・Market・Side・Quantity・AverageEntryPrice）を返す（数量 0 は除外）。
  - `OpenPositionView`（Symbol・Market・Side・Quantity・EntryPrice・**StopLossPrice**）と `OpenPositionsService`
    （`IPortfolioLedgerStore` ＋ `IRiskSettingsStore` から導出）。
    - 損切り価格の導出（**近似**）: 既定損切り比率（`TradingDefaults.DefaultStopLossRatio`＝0.03・前提条件 §5 注記）を平均取得単価へ適用。
      - ロング（Buy 建て）: `StopLossPrice = EntryPrice × (1 − ratio)`
      - ショート（Sell 建て）: `StopLossPrice = EntryPrice × (1 + ratio)`
- Worker: `GET /risk-controls/open-positions`（OwnerOnly・既存グループ）→ `IReadOnlyList<OpenPositionView>`。
- DI: `OpenPositionsService` を登録。

### 市場監視サービス `MarketMonitorService`（同期照会）

- `IPositionStore` を**非同期化**（`Task<IReadOnlyCollection<HeldPosition>> GetOpenPositionsAsync(CancellationToken)`）。
- Worker: `HttpPositionStore`（`GET {RiskManagement:BaseUrl}/risk-controls/open-positions` → `HeldPosition` 列へ写像。
  404/非2xx/例外/タイムアウト/不正応答は**空列**＝損切り検知対象なしの安全既定）。5s タイムアウト。
- `Program.cs`: `RiskManagement:BaseUrl` 未設定/不正 URI は従来 `PlaceholderPositionStore`（空）＝安全既定でゲート、設定時のみ Http（解決時に構成を読む）。
- `MarketMonitorService.EvaluateRoundAsync` の呼び出しを `await GetOpenPositionsAsync(...)` に更新。
- `InMemoryPositionStore`・`PlaceholderPositionStore` を非同期化（`Set` は同期のまま）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋fake HttpMessageHandler＋WebApplicationFactory）:
- [ ] `ProjectOpenPositions`: 約定列から銘柄別ネット建玉（数量・平均取得単価）を射影し、数量 0（全決済）は除外する。
- [ ] `OpenPositionsService`: ロングは `Entry×(1−ratio)`、ショートは `Entry×(1+ratio)` で損切り価格を導出する。
- [ ] `GET /risk-controls/open-positions` が OwnerOnly（401）で保有ポジションを返す。
- [ ] `HttpPositionStore`: 200 応答を `HeldPosition` 列に写像する（fake handler）。
- [ ] 404/非 2xx/例外/タイムアウト/不正応答は空列（＝損切り検知対象なし）に倒す。
- [ ] `RiskManagement:BaseUrl` 未設定は `PlaceholderPositionStore`、設定時は `HttpPositionStore`（選択テスト）。
- [ ] 既存テストを緑に保つ（`IPositionStore` の非同期化に追随）。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] 実 RiskManagement への同期照会・service-to-service 認証付き E2E。

## フェイルセーフの方向（明示）

daily-policy/sizing-context の「未取得なら取引しない」と異なり、保有ポジションの空列は**損切り検知を抑止**する（＝
保護が働かない側）。これは損切り価格を知るには保有情報が不可欠で、依存先障害時に取り得る唯一の縮退であり、既存
`PlaceholderPositionStore`（空）と同一の既定を踏襲する。緩和策: 短い監視間隔、プレースホルダ/失敗の警告ログ、リスク管理
側での独立した損切り執行（ADR-0003）。損切り価格の権威データ化（後述）までの過渡的措置。

## 対象外（後続）

- **損切り価格の権威データ化**: 取引判断の `stopLossDistancePerShare`（ATR 連動）を発注/約定パイプラインで永続化し、
  近似（既定比率）を実値へ置換する（契約 or 台帳拡張。IADR-0018 の契約最小化方針の見直しを伴うため別途）。
- service-to-service 認証（`GET /risk-controls/open-positions` は OwnerOnly）。費用 poller の実データ化（#22 の他ステップ）。
- 含み損益/DD の日次終値マーク（IADR-0008 後続）。キャッシュ/リトライ。

## テスト方針

- `ProjectOpenPositions`・`OpenPositionsService` は fake ledger/settings で射影と損切り導出を検証。
- エンドポイントは `RiskWorkerWebApplicationFactory`（TestAuthHandler）で OwnerOnly・応答を検証。
- `HttpPositionStore` は fake `HttpMessageHandler`（200/404/500/タイムアウト）で写像とフェイルセーフを検証。選択は WebApplicationFactory で検証。

## 関連仕様

- 連携元: [20260710_portfolio-projection](20260710_portfolio-projection.md)（#63 台帳）、[20260710_market-monitor-worker](20260710_market-monitor-worker.md)
- 先行: [20260710_daily-policy-wiring](20260710_daily-policy-wiring.md)、[20260710_sizing-context-wiring](20260710_sizing-context-wiring.md)（#22・同期 API 方式）
- 実装ADR: [IADR-0030](../adr/IADR-0030_position-store-sync-api.md)

## 未決事項

- 損切り価格の権威データ化（近似の実値化）・service-to-service 認証・キャッシュ/リトライは #22 の後続で確定する。
