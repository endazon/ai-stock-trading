---
title: 画面仕様書（素案） — SC-02 リスク設定画面（リスク上限の閲覧/変更）
type: screen
status: Draft
related_ids: [SC-02, FR-13, FR-19, FR-20, UC-06, ADR-0007]
issue: 106
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
related_specs:
  - ../specs/20260718_106_frontend-risk-settings-and-controls.md
  - ../adr/IADR-0084_frontend-risk-settings-and-control-status.md
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
2. **ガード（参照）**: 有効な商品種別・市場、禁止銘柄、同日再エントリ禁止、相場操縦パターン禁止を表示（変更は後続）。
3. **段階（参照）**: 現段階・モード（ペーパー/実弾）・資金上限を表示（段階変更は段階ゲート承認フロー＝#165 Bot 側）。
4. **変更履歴**: `SettingsChangeEntry[]` を新しい順に一覧（種別・アクター・理由・前後値・日時）。

## データ取得・更新（BFF `/bff/*` 経由・`apiFetch`）

| 操作 | 呼び出し | 応答/エラー |
| --- | --- | --- |
| 初期表示 | `GET /risk-controls/settings` | `RiskManagementSettings`。404/失敗=縮退表示 |
| 履歴 | `GET /risk-controls/settings/history` | `SettingsChangeEntry[]`。失敗時は履歴領域のみ縮退 |
| 上限保存 | `PUT /risk-controls/settings/limits`（`{limits, reason}`） | 成功=再取得。400=検証、409=競合（DbUpdateConcurrency）＋再取得を促す |

## 振る舞い（安全既定）

- **入力検証**: 空欄・非数値の上限がある間は保存を無効化し該当項目を警告表示。黙って `0` 送信しない。範囲検証はサーバ側 400。
- 理由未入力時は保存不可（送信ボタン無効）。保存成功後は現在値・履歴を再取得。
- 409/400 では破壊的な自動再試行をしない。競合時は「最新を取得して再試行」を促す。
- 取得不能・権限外・BFF 未登録は安全側（縮退・存在秘匿）へ倒す。
- `changeType` 等の数値 enum は表示ラベルへ写像し、未知値はフォールバック表示。

## スコープ外（後続）

ガード・段階の変更 UI、監視銘柄（watchlist）設定、Playwright E2E、platform 合成点（features/BFF）登録。
