---
title: 作業仕様書 — 監視銘柄（watchlist）設定ストア API の整備（FR-13 残・#188 の前提）
type: work
status: In progress
related_ids: [FR-03, FR-13, FR-11, UC-06]
issue: 191
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
related_specs:
  - ../adr/IADR-0088_watchlist-settings-api.md
  - ../adr/IADR-0084_frontend-risk-settings-and-control-status.md
  - ./20260710_market-monitor-worker.md
---

# 作業仕様書: 監視銘柄（watchlist）設定ストア API の整備

> 起点: Issue [#191](https://github.com/endazon/ai-stock-trading/issues/191)。#188（FR-13 残 UI・PR #192）で
> 「監視銘柄設定は対応するバックエンド API の整備後に着手（別 issue で先行）」として分離起票されたバックエンド整備。
> 本 API が入れば後続で監視銘柄 UI（SC-02）を出せる。

## スコープ（バックエンドに閉じる）

- 監視銘柄（watchlist）の**取得・追加・削除**エンドポイントを、既存のリスク設定 API（Risk `/risk-controls/settings`）の
  作法に揃えて **MarketMonitorService に閉じて**整備する。
- 認可は **owner サブグループ**（`OwnerOnly`＝`trading-owner`）に付与し、**親グループには付けない**。
- **理由必須**・楽観排他・検証(400)/競合(409) を既存設定 API と同型にする。
- 変更履歴は MarketMonitor ローカルの change log（Risk `ISettingsChangeLog` をミラー）に記録する。**新イベントは足さない**
  （→ 中央監査 Consumer は不変・`AuditConsumerCoverageTests` 緑のまま）。
- 既定挙動を壊さない・後方互換（既存 `PUT /monitor/settings` は不変）。

## 非スコープ

- frontend（SC-02）の監視銘柄変更 UI（本 API 整備後に別 issue で着手）→ フォローアップ issue を優先度ラベル付きで起票。
- 段階（stage）の直接変更（段階ゲート承認フロー #20/#165 へ一元化）。
- TradeDecision の暫定 watchlist（`TradeCycle:Watchlist`・Configuration）の置換（別スコープ・後続）。
  → **#209（IADR-0095・2026-07-20）で解消**: TradeDecision は本 API（`GET /monitor/watchlist`）を s2s 同期照会して権威源へ一本化した。
- `Shared.Contracts` の変更（新イベント無し・`Market` は既存。**追加のみの原則**に従い本作業では変更しない）。

## 設計判断（→ IADR-0088）

watchlist の権威データ源は **MarketMonitorService** の `MarketMonitorSettings.MonitoredSymbols`
（EF 単一行 JSON＋Version 楽観排他・`IMonitoredSymbolStore`）。#10 市場監視が直接消費する。
TradeDecision の `TradeCycle:Watchlist`（Configuration）は暫定 stopgap にすぎない。
したがって編集 API は MarketMonitorService に置く（Configuration ではない）。詳細は
[IADR-0088](../adr/IADR-0088_watchlist-settings-api.md)。

## 追加する API（`/monitor` 配下・owner サブグループ）

| メソッド | パス | 概要 | 認可 |
| --- | --- | --- | --- |
| GET | `/monitor/watchlist` | 現在の監視銘柄一覧 | OwnerOrService（#209/IADR-0095 で変更。旧: OwnerOnly） |
| POST | `/monitor/watchlist` | 監視銘柄の追加（body: `symbol, market, reason`） | OwnerOnly |
| DELETE | `/monitor/watchlist` | 監視銘柄の削除（body: `symbol, market, reason`） | OwnerOnly |
| GET | `/monitor/watchlist/history` | 監視銘柄の変更履歴（新しい順） | OwnerOnly |

- **理由必須**: 追加・削除は `reason` 空欄で 400。actor は認証済みトークン名（`preferred_username`）。
- **検証(400)**: 空 `symbol`・未定義 `market`・**重複追加**・**不在削除**は 400（ArgumentException）。
- **競合(409)**: 設定行の Version 楽観排他競合（`DbUpdateConcurrencyException`）は 409。既存 `/monitor/settings` と共通の
  例外フィルタで写像する（新規の写像は増やさない）。
- 認可: 未認証は 401、`trading-owner` を持たなければ 403（owner サブグループ）。

## 受け入れ基準（テストへ写像）

1. `POST /monitor/watchlist` で監視銘柄を追加でき、`GET` に反映・履歴に記録される（理由・actor 付き）。→ 統合/ユニット
2. `DELETE /monitor/watchlist` で監視銘柄を削除でき、`GET` から消え・履歴に記録される。→ 統合/ユニット
3. `reason` 空欄の追加/削除は 400。→ ユニット/統合
4. 重複追加・不在削除・空 symbol・未定義 market は 400。→ ユニット/統合
5. 未認証は 401、非 owner ロールは 403（GET/POST/DELETE すべて）。→ 統合
6. 既存 `PUT /monitor/settings`（閾値・クールダウン・監視銘柄一括置換）は不変（後方互換）。→ 既存テスト緑
7. `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が緑・`dotnet format` 差分なし・警告ゼロ。

## 影響範囲

- 追加（Application）: `MonitorWatchlistService`・`IMonitorSettingsChangeLog`・`MonitorSettingsChangeEntry`・
  `InMemoryMonitorSettingsChangeLog`。
- 追加（Worker）: `EfMonitorSettingsChangeLog`・`MonitorSettingsChangeRow`（DbSet／マッピング／Migration）・
  watchlist エンドポイント。`Program.cs` に DI 追加。
- 変更（Worker）: `MonitorSettingsEndpoints` を親グループ（認可なし・例外フィルタ）＋ owner サブグループ（OwnerOnly）に
  再構成（既存 `/settings` も owner サブグループへ移設・挙動同一）。
- 監査 Consumer・`Shared.Contracts`・既存 `IMonitoredSymbolStore` 契約は不変。

## リスク・留意

- InMemory DB は Version 楽観排他を強制しないため、409 経路のユニット網羅は行わず、既存 `/monitor/settings` と共通の
  例外フィルタ（実証済み）に委ねる。DELETE with body は内部メッシュ API のため許容（IADR-0088 で明示）。
