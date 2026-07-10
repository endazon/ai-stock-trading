---
title: サービス間連携 継続（取引判断 ISizingContextProvider → リスク管理の実データ化）
type: spec
status: review
related_ids: [FR-04, FR-10, ADR-0001, ADR-0003, IADR-0017]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# 仕様書: 取引判断のサイジング文脈をリスク管理から同期照会する

> Issue [#22](https://github.com/endazon/ai-stock-trading/issues/22)（サービス間連携）の継続。取引判断（#11）の
> `ISizingContextProvider`（現プレースホルダ＝既定値）を、リスク管理（#12）が設定＋ポートフォリオ状態（#63 台帳）から
> 導出する**サイジング文脈**を同期 API 照会する実装へ差し替える。IADR-0028 の同期 API 方式・フェイルセーフ既定を踏襲する。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-04（判断のサイジング）、FR-10（段階資金上限・日次発注上限・連敗/DD 縮小）
- アーキ概要: 「同期 API 依存（取引判断→検索/認可 等）は…契約（API）として管理する」
- ADR: ADR-0001（Database per Service）、ADR-0003、IADR-0017（サイジング文脈・availableCapital=残枠の小さい方）
- 関連 IADR: 本作業で新規 [IADR-0029](../adr/IADR-0029_sizing-context-sync-api.md)（方式は [IADR-0028](../adr/IADR-0028_daily-policy-sync-api.md) を踏襲）
- 対象 Issue: #22（継続）

## 対象範囲

### リスク管理サービス `RiskManagementService`（サイジング文脈の所有・公開）

- Application: `SizingContextView`（Capital・StageCapitalRemaining・DailyOrderRemaining・ConsecutiveLosses・DrawdownRatio・Mode・Limits）と
  `SizingContextService`（`PortfolioSnapshotBuilder` ＋ `IRiskSettingsStore` から導出）。
  - StageCapitalRemaining = max(0, Stage.CapitalCap − InvestedCapital)（IADR-0005）。
  - DailyOrderRemaining = max(0, Limits.MaxDailyOrderAmount − DailyOrderedAmount)。
  - Capital/ConsecutiveLosses/DrawdownRatio は PortfolioState（#63 台帳の実データ・LedgerPortfolioStateProvider）由来。Mode/Limits は設定由来。
- Worker: `GET /risk-controls/sizing-context`（OwnerOnly・既存グループ）→ `SizingContextView`。

### 取引判断サービス `TradeDecisionService`（同期照会）

- `ISizingContextProvider` を**非同期化**（`Task<SizingContext> GetContextAsync(CancellationToken)`）。
- Worker: `HttpSizingContextProvider`（`GET {RiskManagement:BaseUrl}/risk-controls/sizing-context` → `SizingContext` に写像。404/非2xx/例外/
  タイムアウト/不正応答は**残枠 0 の安全既定**＝availableCapital 0 → 数量 0 → 見送り＝取引しない）。`reports` と同様に短いタイムアウト（5s）。
- `Program.cs`: `RiskManagement:BaseUrl` 未設定/不正 URI は従来プレースホルダ（既定値）＝安全既定でゲート、設定時のみ Http（解決時に構成を読む）。
- `PlaceholderSizingContextProvider` を非同期化して残す。

## 受け入れ基準

CI で緑にする範囲（ユニット＋fake HttpMessageHandler＋WebApplicationFactory）:
- [ ] `SizingContextService`: 段階残枠＝CapitalCap−InvestedCapital、日次残枠＝MaxDailyOrderAmount−DailyOrderedAmount、他は状態/設定由来で導出する（負値は 0 にクランプ）。
- [ ] `GET /risk-controls/sizing-context` が OwnerOnly（401/403）でサイジング文脈を返す。
- [ ] `HttpSizingContextProvider`: 200 応答を `SizingContext` に写像する（fake handler）。
- [ ] 404/非 2xx/例外/タイムアウト/不正応答は残枠 0 の安全既定（＝取引しない）に倒す。
- [ ] `RiskManagement:BaseUrl` 未設定は no-op プレースホルダ、設定時は Http（選択テスト）。
- [ ] 既存テストを緑に保つ（`ISizingContextProvider` の非同期化に追随）。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] 実 RiskManagement への同期照会・service-to-service 認証付き E2E。

## 対象外（後続）

- service-to-service 認証（`GET /risk-controls/sizing-context` は OwnerOnly。サービストークン付与は platform 統合の後続）。
- 市場監視 `IPositionStore`・費用 poller の実データ化（#22 の他ステップ）。UnrealizedPnl/DrawdownRatio の日次終値マーク（市場データ連携・IADR-0008 後続）。キャッシュ/リトライ。

## テスト方針

- `SizingContextService` は fake スナップショット/設定で導出（残枠クランプ）を検証。
- エンドポイントは `RiskWorkerWebApplicationFactory`（TestAuthHandler）で OwnerOnly・応答を検証。
- `HttpSizingContextProvider` は fake `HttpMessageHandler`（200/404/500/タイムアウト）で写像とフェイルセーフを検証。選択は WebApplicationFactory で検証。

## 関連仕様

- 連携元: [20260710_risk-management-worker](20260710_risk-management-worker.md)、[20260710_portfolio-projection](20260710_portfolio-projection.md)（#63 台帳）
- 先行: [20260710_daily-policy-wiring](20260710_daily-policy-wiring.md)（#22 第一歩・同期 API 方式）
- 実装ADR: [IADR-0029](../adr/IADR-0029_sizing-context-sync-api.md)

## 未決事項

- service-to-service 認証・他プレースホルダの実データ化・含み損益マーク・キャッシュ/リトライは #22 の後続で確定する。
