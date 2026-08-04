---
title: 作業仕様書 — SC-02 で変更した監視銘柄（watchlist）を TradeDecision の定時サイクルへ結線する（暫定実装の恒久化）
type: work
status: Done
related_ids: [FR-02, FR-13, UC-06, SC-02, IADR-0051, IADR-0088, IADR-0090]
issue: 209
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/05_screens/01_screens.md
related_specs:
  - ../adr/IADR-0095_watchlist-authoritative-wiring.md
  - ../adr/IADR-0088_watchlist-settings-api.md
  - ../adr/IADR-0051_service-to-service-auth.md
  - ./20260718_191_watchlist-settings-api.md
---

# 作業仕様書: 監視銘柄（watchlist）を権威源へ一本化

> 起点: Issue [#209](https://github.com/endazon/ai-stock-trading/issues/209)。実環境構築前監査（2026-07-18・コミット `a48835a`）で
> 「watchlist の結線が暫定実装」（Medium）と検出。#191（設定ストア API）/#197（SC-02 UI）で watchlist の権威源は完成したが、
> TradeDecision は構成ファイル（`TradeCycle:Watchlist`）を読み続けており、**SC-02 での変更が定時サイクルの判断対象に反映されない**。

## 現状（As-Is）

- `ConfigurationWatchlistProvider`（TradeDecision.Worker, `Program.cs` で `IWatchlistProvider` に登録）が
  `TradeCycle:Watchlist` 構成から監視銘柄を供給する暫定実装（ソース冒頭コメントに「暫定実装。実 watchlist 連携は後続」）。
- `InformationCollectedConsumer`（定時系統の合流点）が `IWatchlistProvider.GetWatchlist()` で巡回する。
- watchlist の**権威データ源**は MarketMonitorService の `MarketMonitorSettings.MonitoredSymbols`
  （`MonitorWatchlistService.GetWatchlist()` = `store.GetSettings().MonitoredSymbols`・GET `/monitor/watchlist`・#191/#197）。
  SC-02 UI と API はここを更新するが、TradeDecision は一切読まない → **二重の真実源**。

## スコープ（watchlist 取得経路の是正に閉じる）

- TradeDecision の暫定 watchlist 供給を、権威源（MarketMonitor の watchlist・#191 API）へ**一本化**する。二重の真実源を作らない。
- 供給方式は **s2s 同期照会**（IADR-0051 の sizing-context / open-positions の作法をミラー）。イベント射影は採らない（→ IADR-0095）。
- MarketMonitor 側: GET `/monitor/watchlist` を **`read` サブグループ（`OwnerOrService`）** へ移動し、trading-service にも
  読み取りを開放する。POST/DELETE/`/watchlist/history`/`/settings` は `OwnerOnly` 据え置き（監視設定の変更は利用者のみ＝FR-13・
  [IADR-0088](../adr/IADR-0088_watchlist-settings-api.md) の owner サブグループ認可を維持）。
- TradeDecision 側: `IWatchlistProvider` を非同期化（`ISizingContextProvider` と同型）。`HttpWatchlistProvider`（s2s トークン・
  `MarketMonitor:BaseUrl`）を追加し、`MonitoredSymbol`→`WatchedSymbol` を同形 JSON で逆直列化する。
- **fail-safe フォールバック**（受け入れ基準 2）: `MarketMonitor:BaseUrl` 未設定/不正 → 構成ベース（`ConfigurationWatchlistProvider`＝
  **現行挙動・後方互換**）。照会失敗（非 2xx・timeout・例外）→ 構成 watchlist（既定 watchlist）へ委譲する定義済みの安全側。
- SC-02 画面仕様書に暫定状態の解消を反映する。

## 非スコープ

- 判断スキップ／通知経路（#210・**本 issue の後に着手**予定）。watchlist 取得経路のみに閉じ、イベント/通知には触れない。
- MSP #308／planning #35（別リポ）。
- `Shared.Contracts` の変更（`WatchedSymbol`/`MonitoredSymbol` は同形 JSON で往復。**追加のみの原則**に従い本作業では変更しない）。
- watchlist 変更の**イベント射影**（新イベント→監査 Consumer 追随が必要になり #210 のイベント経路と干渉するため採らない・→ IADR-0095）。

## 受け入れ基準（→ テストへ写像）

- [x] SC-02（または API）で watchlist を変更すると、以後の定時サイクルの判断対象に反映される
      （`HttpWatchlistProvider` が権威源 GET `/monitor/watchlist` を照会し、`MonitoredSymbol`→`WatchedSymbol` を返す）。
- [x] 供給不達時のフォールバック（既定 watchlist）が fail-safe に定義される
      （BaseUrl 未設定/不正 → 構成ベース、照会失敗 → 構成 watchlist へ委譲）。
- [x] MarketMonitor GET `/monitor/watchlist` が `OwnerOrService`（trading-service で s2s 照会可）に開放され、
      変更系（POST/DELETE/history/settings）は `OwnerOnly` のまま。
- [x] SC-02 画面仕様書に暫定状態の解消を反映する。
- [x] 選択中実装の自己申告（IADR-0078 決定4）: introspection に `watchlist` ポートを追加し、http/configuration を判別可能にする。
- [x] `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` 緑・`dotnet format` 済・警告ゼロ。

## 設計判断（→ IADR-0095）

権威源は MarketMonitor（IADR-0088 で確定）。供給方式は s2s 同期照会（IADR-0051 precedent）。詳細・イベント射影を採らない理由・
fail-safe の定義は [IADR-0095](../adr/IADR-0095_watchlist-authoritative-wiring.md)。
