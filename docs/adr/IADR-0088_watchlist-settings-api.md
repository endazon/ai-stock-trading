---
title: IADR-0088 監視銘柄（watchlist）設定 API は権威データ源の MarketMonitorService に置き、Risk 設定の作法（owner サブグループ認可・理由必須・楽観排他・ローカル変更履歴）をミラーする
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-13, FR-11, UC-06, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# IADR-0088: 監視銘柄（watchlist）設定 API は MarketMonitorService に置き、Risk 設定の作法をミラーする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-13**（利用者が設定を変更できる）、**FR-03**（市場監視・監視銘柄）、**FR-11**（変更履歴の記録）、
  **UC-06**（設定変更）、ADR-0007（取引ガード・監視設定は利用者のみ変更）。
- 対象 Issue: [#191](https://github.com/endazon/ai-stock-trading/issues/191)（#188 FR-13 残 UI の前提として分離起票）。
- 関連する実装仕様書: [作業仕様](../specs/20260718_191_watchlist-settings-api.md)。
- 前提（develop マージ済み）: #10 市場監視（`IMonitoredSymbolStore`・`MarketMonitorSettings.MonitoredSymbols`・
  EF 単一行 JSON＋Version 楽観排他・IADR-0012 踏襲）、Risk `/risk-controls/settings`（OwnerOnly・理由必須・
  `ISettingsChangeLog`）。

## 背景・課題

FR-13 の残項目のうち**監視銘柄（watchlist）設定**は、設定ストア側に**取得/追加/削除の変更 API が未整備**で、UI（SC-02）を
ブロックしていた。監視銘柄の権威データ源をどのサービスに置き、どの作法で変更 API を提供するかを決める必要がある。

## 決定

### 1. watchlist の編集 API は MarketMonitorService に置く（Configuration ではない）

監視銘柄の権威データ源は MarketMonitorService の `MarketMonitorSettings.MonitoredSymbols` である。#10 市場監視の巡回が
`IMonitoredSymbolStore` から直接読み、EF 単一行 JSON＋Version で永続化・楽観排他される。TradeDecision の
`ConfigurationWatchlistProvider`（`TradeCycle:Watchlist`・Configuration）は「実 watchlist 連携は後続」と明記された**暫定
stopgap** にすぎない。したがって編集 API は MarketMonitorService に閉じて置く。Configuration へ二重の真実源を作らない。

### 2. Risk 設定 API の作法をミラーする

`RiskControlEndpoints` / `RiskSettingsService` と同型にする:

- **owner サブグループ認可**: 親グループ `/monitor` には認可を付けず（例外→HTTP 写像フィルタのみ）、`OwnerOnly` は
  owner サブグループに付与する。これに伴い既存 `/monitor/settings`（GET/PUT）も owner サブグループへ移設する（認可挙動は
  同一・`trading-owner` のみ）。生成AI・自動処理はこのロールを持たないため変更できない。
- **理由必須**: 追加・削除は `reason` 空欄で 400。actor は認証済みトークン名（`preferred_username`）から取り、要求本文では
  受け取らない（なりすまし防止）。
- **検証(400)/競合(409)**: 空 `symbol`・未定義 `market`・重複追加・不在削除は 400（ArgumentException）。設定行の Version
  楽観排他競合（`DbUpdateConcurrencyException`）のみ 409。既存 `/monitor/settings` と**共通の例外フィルタ**で写像し、新規の
  写像分岐は増やさない（不在削除も 409/404 を新設せず 400 に倒す＝写像の単一情報源を保つ）。
- **変更履歴**: MarketMonitor ローカルの `IMonitorSettingsChangeLog`（Risk `ISettingsChangeLog` のミラー・InMemory＋EF・
  追記専用・新しい順）に actor・種別・理由・前後値・日時を記録する。`GET /monitor/watchlist/history` で照会する。

### 3. 新イベントを足さない（中央監査 Consumer 不変）

変更履歴はサービスローカルの change log で満たし、バスへ新イベントを発行しない。これにより AuditService の Consumer 追随は
不要で `AuditConsumerCoverageTests` は緑のまま保たれる。Risk のガード/上限/段階変更も同様にローカル change log で記録し
バスイベントを持たない（既存慣行と一致）。将来 UI で中央監査に載せる要求が出たら、その時点で新イベント＋Consumer 追随を
別 IADR で検討する。

### 4. 追加・削除は個別操作・DELETE は body で理由を運ぶ

UI（1 銘柄ずつの追加/削除）に素直な粒度とし、`POST /monitor/watchlist`（追加）・`DELETE /monitor/watchlist`（削除）を
`{ symbol, market, reason }` の対称な body で受ける。DELETE に body を持たせるのは一般には非推奨だが、理由必須を POST と
対称に扱え、本 API は内部メッシュ限定（Keycloak 認可・ネットワーク分離）であり実害が無いため許容する。`Shared.Contracts`
は新規型・新イベントとも不要（`Market` は既存・DTO は Worker に閉じる）＝**追加のみの原則**を自明に満たす。

## 影響・代替案

- **代替1（却下）**: 既存 `PUT /monitor/settings` に理由/履歴を足して流用。→ 設定一括置換の粒度で UI の追加/削除に合わず、
  既存契約に破壊的変更を与える。個別操作を新設し既存 PUT は後方互換で不変とする。
- **代替2（却下）**: watchlist を Configuration サービスへ移す。→ 権威データ源と消費経路（#10）が MarketMonitor にあり、
  二重の真実源・同期問題を生む。
- **代替3（却下）**: 不在削除を 404・重複追加を 409 に写像。→ 例外フィルタに新分岐が増え写像の単一情報源が崩れる。入力起因の
  失敗は 400 に一元化する。

## 結果

- MarketMonitorService に watchlist の取得/追加/削除/履歴 API が入り、SC-02 の監視銘柄 UI（別 issue）が消費できる。
- 既存挙動・既存テスト・中央監査・`Shared.Contracts` は不変（後方互換）。
