---
title: 画面仕様書（素案） — SC-02 リスク設定画面（リスク上限の閲覧/変更）
type: screen
status: Draft
related_ids: [SC-02, FR-13, FR-19, FR-20, UC-06, ADR-0003, ADR-0007, ADR-0008]
issue: 106
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
related_specs:
  - ../specs/20260718_106_frontend-risk-settings-and-controls.md
  - ../specs/20260718_196_frontend-watchlist-ui.md
  - ../adr/IADR-0084_frontend-risk-settings-and-control-status.md
  - ../adr/IADR-0086_frontend-guard-edit-ui.md
  - ../adr/IADR-0090_frontend-watchlist-ui.md
---

# SC-02 リスク設定画面（リスク上限の閲覧/変更）【素案】

> 起点: **FR-13**（利用者が設定を変更できる）、FR-19（相場操縦ガード）、FR-20（段階）、**UC-06**。計画リポジトリ `05_screens/`
> は空のため SC-02 は素案（project-planning#33・#31 後続 で環流）。データ源は RiskManagementService `/risk-controls/settings`（OwnerOnly）。

## 画面の位置づけ

platform SPA 認証済みレイアウト配下に feature `sc02-risk-settings` としてマウント（route `settings/risk`・nav「リスク設定」）。
SC-01（FR-17 全体前提条件・ConfigurationService）とは別サービス由来のため独立画面とする（[IADR-0084](../adr/IADR-0084_frontend-risk-settings-and-control-status.md)）。

## アクセス制御

- 表示・変更とも利用者（`trading-owner`）限定。`RequireRole anyOf=['trading-owner']`・権限外は `NotFound`（存在秘匿）。
- 実効認可はサーバ側（`/risk-controls/settings` = OwnerOnly）。権限外では構成 API を呼ばない。

## 構成要素

1. **リスク上限（変更可）**: `RiskLimitSettings` の 8 項目 — 1注文金額上限・1日発注金額上限・保有銘柄数上限・日次損失上限（比）・
   1取引リスク（比）・最大DD（比）・連敗しきい値・連敗縮小係数。数値入力（文字列保持・送信時に数値化）。
2. **ガード（変更可・#188/IADR-0086）**: 有効な商品種別・市場（チェックボックス）、禁止銘柄（追加/削除）、同日再エントリ禁止・
   相場操縦パターン禁止（トグル）を編集。危険な緩和（トグル OFF・禁止銘柄削除・信用の新規有効化）は明示確認を要求（fail-safe）。
3. **段階（参照）**: 現段階・モード（ペーパー/実弾）・資金上限を表示（段階変更は段階ゲート承認フロー＝#165 Bot 側）。
4. **変更履歴**: `SettingsChangeEntry[]` を新しい順に一覧（種別・アクター・理由・前後値・日時）。
5. **監視銘柄（変更可・#196/IADR-0090）**: `MonitoredSymbol[]`（銘柄コード・市場）の一覧・追加・削除。データ源は
   **別サービス** MarketMonitorService `/monitor/watchlist`（取得は OwnerOrService・変更は OwnerOnly）で、リスク設定の取得可否に
   連動せず**独立ロード/縮退**する。追加は理由必須（1 段）。削除は破壊的なため**明示確認**（削除理由必須＋確認ボタン）を要求
   （fail-safe）。市場は数値 enum を写像。監視銘柄の変更履歴（`MonitorSettingsChangeEntry[]`・changeType=追加/削除）を別表で一覧。

## データ取得・更新（BFF `/bff/*` 経由・`apiFetch`）

| 操作 | 呼び出し | 応答/エラー |
| --- | --- | --- |
| 初期表示 | `GET /risk-controls/settings` | `RiskManagementSettings`。404/失敗=縮退表示 |
| 履歴 | `GET /risk-controls/settings/history` | `SettingsChangeEntry[]`。失敗時は履歴領域のみ縮退 |
| 上限保存 | `PUT /risk-controls/settings/limits`（`{limits, reason}`） | 成功=再取得。400=検証、409=競合（DbUpdateConcurrency）＋再取得を促す |
| ガード保存 | `PUT /risk-controls/settings/guard`（`{enabledProductTypes, enabledMarkets, bannedSymbols, preventSameDayReentry, prohibitManipulativeOrderPatterns, reason}`・全置換） | 成功=再取得。危険な緩和は確認必須。400=検証、409=競合＋再取得を促す（#188/IADR-0086） |
| 監視銘柄 一覧 | `GET /monitor/watchlist`（別サービス MarketMonitor・OwnerOrService） | `MonitoredSymbol[]`。404/失敗=独立縮退（「監視銘柄設定は利用できません。」） |
| 監視銘柄 履歴 | `GET /monitor/watchlist/history` | `MonitorSettingsChangeEntry[]`。失敗時は履歴領域のみ縮退 |
| 監視銘柄 追加 | `POST /monitor/watchlist`（`{symbol, market, reason}`） | 成功=再取得。理由必須。400=重複/空/未定義 market、409=競合＋再取得を促す（#196/IADR-0090） |
| 監視銘柄 削除 | `DELETE /monitor/watchlist`（body `{symbol, market, reason}`） | 明示確認（削除理由必須）後に実行。成功=再取得。400=不在、409=競合＋再取得を促す（#196/IADR-0090） |

## 振る舞い（安全既定）

- **入力検証**: 空欄・非数値の上限がある間は保存を無効化し該当項目を警告表示。黙って `0` 送信しない。範囲検証はサーバ側 400。
- 理由未入力時は保存不可（送信ボタン無効）。保存成功後は現在値・履歴を再取得。
- 409/400 では破壊的な自動再試行をしない。競合時は「最新を取得して再試行」を促す。
- 取得不能・権限外・BFF 未登録は安全側（縮退・存在秘匿）へ倒す。
- `changeType` 等の数値 enum は表示ラベルへ写像し、未知値はフォールバック表示。

## スコープ外（後続）

段階の直接変更 UI（段階ゲート承認へ一元化）、監視の変動閾値・収集間隔の変更 UI（`PUT /monitor/settings`・#196 対象外）、
実 BFF の `/monitor/*` プロキシ結線（MSP 側合成点・risk-controls の MSP #287 と同様に別リポ後続）。

> 監視銘柄（watchlist）変更 UI は **#196（IADR-0090）で実装済み**（上表「監視銘柄」）。計画 `05_screens/01_screens.md` は監視銘柄を
> SC-01 の運用パラメータ節に置くが、所有サービス単位に画面を分ける方針（IADR-0084）と #196 の指定に従い SC-02 に載せた（環流対象）。

> **暫定結線の解消（#209/IADR-0095・2026-07-20）**: 従来 TradeDecision の定時サイクルは監視銘柄を構成ファイル（`TradeCycle:Watchlist`）
> から読む暫定実装で、本画面（SC-02）での変更が判断対象に反映されなかった。#209 で TradeDecision は権威源（本画面と同じ
> MarketMonitor `GET /monitor/watchlist`）を **s2s 同期照会**（`OwnerOrService`）するよう恒久化され、**本画面での監視銘柄変更は
> 以後の定時サイクルの判断対象に反映される**。供給不達時は構成ベース（既定 watchlist）へ fail-safe に倒す。詳細は
> [IADR-0095](../adr/IADR-0095_watchlist-authoritative-wiring.md)。
