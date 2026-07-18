---
title: 作業仕様書 — フロントエンド T1 残スライス（FR-13 リスク設定画面・#20 承認/統制状態参照画面）
type: work
status: In progress
related_ids: [FR-10, FR-13, FR-19, FR-20, UC-06, UC-07, SC-02, SC-03, ADR-0007, ADR-0008, ADR-0009]
issue: 106
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
related_specs:
  - ./20260718_frontend-settings-screen.md
  - ../adr/IADR-0080_frontend-settings-screen.md
  - ../adr/IADR-0084_frontend-risk-settings-and-control-status.md
  - ../screens/20260718_SC-02_risk-settings.md
  - ../screens/20260718_SC-03_control-status.md
---

# 作業仕様書: フロントエンド T1 残スライス（FR-13 リスク設定・#20 承認/統制状態参照）

> 起点: Issue [#106](https://github.com/endazon/ai-stock-trading/issues/106) の T1 残スライス。第1スライス 1a（設定画面
> SC-01 / FR-17 全体前提条件・[IADR-0080](../adr/IADR-0080_frontend-settings-screen.md)・PR #185）はマージ済み。本作業は
> **1b: FR-13 個別設定画面（リスク上限の閲覧/変更）** と **1c: #20 承認・統制状態の参照画面** を実装する。

## スコープ

### 対象（本 PR）

- **1b（FR-13/FR-19/FR-20）リスク設定画面（SC-02・素案）**: リスク管理サービスの設定を利用者（`trading-owner`）が閲覧し、
  **リスク上限（`RiskLimitSettings` の 8 項目）** を変更する。ガード（`Guard`）・段階（`Stage`）は参照表示に留める（変更は後続）。
  変更履歴（`SettingsChangeEntry[]`）を新しい順に表示する。
- **1c（FR-10/FR-20/UC-06/UC-07）承認・統制状態参照画面（SC-03・素案）**: 3 統制（kill switch・日次損失ロックアウト・一時停止）
  の現況、運用段階、当日損益・上限使用率・保有ポジションを表示し、段階ゲート（現段階・設定・遷移履歴・昇格評価・撤退評価）を
  参照表示する。**参照中心**。破壊的操作（pause/resume・kill switch・段階遷移承認）は **#165 の Discord Bot 側と役割分担** し、
  本画面には置かない（安全既定）。

### 対象外（後続・フォローアップ issue へ分離）

- **1d Playwright E2E**（実ブラウザ）＋実 BFF/Keycloak 疎通 → 実基盤依存として後続へ分離（[IADR-0080](../adr/IADR-0080_frontend-settings-screen.md) 決定 3 と同方針）。
- ガード（監視市場/銘柄/禁止銘柄）・段階設定の**変更 UI**（`PUT /risk-controls/settings/guard`・`/settings/stage`）。
- 段階ゲート**承認・差し戻し操作 UI**（`POST /risk-controls/stage-gate/transition`。#165 の Bot 側で駆動）。
- 監視銘柄（watchlist）の設定 UI（設定ストア側にエンドポイント未整備）。
- platform SPA の features 合成点・BFF `/bff/risk-controls/*` 合成点への登録（platform リポ側変更）。

## 前提（develop マージ済みの既存契約に準拠。バックエンド無改修）

本作業は `frontend/`＋`ci.yml` に閉じる（バックエンドは触らない。#166 が Risk を同時に触るため非干渉）。以下の既存契約を消費する:

| 用途 | エンドポイント | 認可 | 応答型（camelCase・enum は数値） |
| --- | --- | --- | --- |
| リスク設定 取得 | `GET /risk-controls/settings` | OwnerOnly | `RiskManagementSettings { guard, limits, stage }` |
| リスク設定 履歴 | `GET /risk-controls/settings/history` | OwnerOnly | `SettingsChangeEntry[]`（`changeType` は数値 enum） |
| リスク上限 変更 | `PUT /risk-controls/settings/limits` | OwnerOnly | body `{ limits, reason }` → 更新後の設定。競合は 409、検証は 400 |
| 稼働状態 集約 | `GET /risk-controls/status` | OwnerOnly | `RiskStatusView`（`activeControl`/`stage` は数値 enum） |
| 段階ゲート 現況 | `GET /risk-controls/stage-gate` | OwnerOnly | `StageGateStatus { currentStage, currentSettings, history, promotion, withdrawal }` |
| 段階ゲート 履歴 | `GET /risk-controls/stage-gate/history` | OwnerOnly | `StageTransition[]` |

- **enum は HTTP JSON では数値**（Risk Worker は `JsonStringEnumConverter` を HTTP 応答に設定していない）。フロントは
  数値 → 表示ラベルの写像を持ち、**未知値は安全側のフォールバック表示**にする。
- BFF は `apiFetch`（`@foundation/api/apiClient`・`/bff/*` プレフィックス）経由でのみ呼ぶ（[IADR-0080](../adr/IADR-0080_frontend-settings-screen.md)）。
- BFF `/bff/risk-controls/*` のプロキシ登録は platform 合成点側（後続）。**未登録の間は 404/失敗として安全側に縮退**（画面は
  存在するが「利用できません」を表示）する。第1スライス（`/bff/assumptions`）と同じ扱い。

## アクセス制御（表示制御・実効認可はサーバ側）

- 両画面とも利用者（`trading-owner`）限定。`RequireRole anyOf=['trading-owner']` でラップし、権限外は `NotFound`（存在秘匿・
  IADR-0009）。権限外では構成 API を呼ばない。実効認可はサーバ側（OwnerOnly の 403/404）。

## 受け入れ基準（テストへ写像する）

### 1b リスク設定画面（SC-02）

1. `trading-owner` で `GET /risk-controls/settings` を表示し、リスク上限 8 項目の現在値を入力欄に反映する。ガード・段階は参照表示。
2. リスク上限のいずれかが空欄・非数値の間は保存を無効化し、該当項目を警告表示する（黙って 0 送信しない・安全既定）。
3. 理由未入力時は保存不可（送信ボタン無効）。
4. 保存は `PUT /risk-controls/settings/limits`（`{limits, reason}`）を呼び、成功後に現在値・履歴を再取得する。
5. 409（競合）・400（検証）は**破壊的な自動再試行をせず**メッセージ表示する（409 は「最新を取得して再試行」を促す）。
6. 権限外（非 owner）は `NotFound` を描画し、設定 API を呼ばない（存在秘匿）。
7. 変更履歴を新しい順に一覧し、`changeType` を種別ラベルへ写像する。取得失敗はその領域のみ縮退。

### 1c 承認・統制状態参照画面（SC-03）

1. `trading-owner` で `GET /risk-controls/status` を表示し、3 統制の状態・成立中の最優先統制・新規建て停止の有無・段階・
   当日損益・上限使用率（発注額/DD/ポジション）を表示する。
2. `GET /risk-controls/stage-gate` を表示し、現段階・モード/資金上限・昇格評価（可否・未充足基準）・撤退評価（到達・停止提案）を表示する。
3. 段階ゲート遷移履歴を新しい順に一覧し、`kind`（昇格/差し戻し）・from/to 段階を写像する。
4. **破壊的操作の UI を持たない**（参照中心・#165 と役割分担）。
5. 数値 enum（`activeControl`/`stage`/`kind`/`changeType` 等）は表示ラベルへ写像し、未知値はフォールバック表示（安全側）。
6. 権限外は `NotFound`（存在秘匿）。取得失敗・BFF 未登録は「利用できません」に縮退。

## 検証（完了前）

`cd frontend && npm ci && npm run typecheck && npm run lint && npm run test`（Vitest）が緑。CI は既存 `frontend` ジョブ
（`ci.yml`）で自動実行（Node のみ・Docker 不要）。実ブラウザ E2E・実 BFF/Keycloak 疎通は後続スライスへ分離。

## トレーサビリティ

- ブランチ: `feat/FR-13-frontend-risk-settings-and-controls`
- コミット: `feat(FR-13,FR-20,UC-06): ...` 等（起点 ID を併記）
- コード: 各 feature/コンポーネントに起点 ID をコメント
- PR: `Refs #106`（残スライス・後続分離を明記）

## 計画環流

計画リポジトリ `05_screens/` は空（SC 未定義）。本作業は **SC-02（リスク設定）・SC-03（統制状態参照）** を素案として実装し、
`/plan-feedback`（project-planning#31 で SC 定義を環流中）で確定を提案する。未確定部分は確定済み契約の範囲で実装し、素案は
draft 継続とする。
