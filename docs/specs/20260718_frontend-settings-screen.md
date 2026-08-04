---
title: 作業仕様書 — フロントエンド新設（frontend/）と設定画面（FR-17 全体前提条件の閲覧/変更）第1スライス
type: work
status: Done（第1スライス。残スライスは #106 に別掲）
related_ids: [FR-13, FR-17, UC-06, ADR-0001]
issue: 106
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
related_specs:
  - ../adr/IADR-0080_frontend-settings-screen.md
  - ../screens/20260718_SC-01_settings.md
---

# 作業仕様書: フロントエンド新設と設定画面（第1スライス）

> 起点 Issue: [#106](https://github.com/endazon/ai-stock-trading/issues/106)（FR-13/FR-17・UC-06）。
> 前提（develop マージ済み）: #22（introspection・pipeline 宣言）・#19（ConfigurationService `/assumptions` API・IADR-0021/0063）。

## 目的

本ユニットに `frontend/` を新設し、platform unit-template 規約に沿って SPA の features 合成点へ組み込める形にする。
第1スライスとして **FR-17 全体前提条件（税・手数料・為替・計算方針・費用上限）の閲覧/変更** の設定画面を実装する。
設計判断は [IADR-0080](../adr/IADR-0080_frontend-settings-screen.md)。バックエンド（`Shared.Contracts`・各 `Program.cs`）は
一切変更しない。`ci.yml` は既存ジョブを保持し `frontend` ジョブを追加のみ。

## スコープ（第1スライス）

1. **`frontend/` scaffold**: `package.json`（`@ai-stock-trading/frontend`・workspaces `*/frontend` で自動認識）、`tsconfig.json`
   （`@foundation`/`@ai-stock-trading` の paths）、`src/features/index.ts`（feature 束ね）、`eslint.config.js`、`vitest.config.ts`、
   テスト setup、テスト/型検査専用 `@foundation` スタブ（IADR-0080 決定 2）。
2. **設定画面 feature（`sc01-settings`, FR-17/UC-06）**: 現在の全体前提条件＋バージョン＋変更履歴の閲覧、`trading-owner` 限定の
   変更フォーム（楽観排他 `ExpectedVersion`・理由必須・400/409 表示）。`RequireRole` で存在秘匿。
3. **CI**: `ci.yml` に `frontend` ジョブ（setup-node → `npm ci` → `typecheck` → `lint` → `vitest run`）を追加。

## スコープ外（後続スライス・`Refs #106`）

- FR-13 の監視銘柄・変動閾値・収集間隔・リスク上限の設定（設定ストア側エンドポイントが未整備＝#19 拡張が前提。バックエンド不可侵）
- #20 段階ゲート昇格承認 UI・統制状態参照（kill switch/pause/lockout）— SC 未定義
- 実ブラウザ Playwright E2E＋実 BFF/Keycloak 疎通（実基盤依存）
- platform SPA 合成点への登録（`src/platform/frontend/src/features/index.ts` への import・vite alias・submodule 登録＝platform リポ側変更）
- 計画リポジトリ `05_screens/` への SC-01 確定反映（`/plan-feedback` で提案・所有者承認）

## BFF 契約（既存・#19 ConfigurationService `/assumptions`。BFF は `/bff/*` で合成）

| メソッド | パス（apiFetch 相対） | 認可 | 用途 |
| --- | --- | --- | --- |
| GET | `/assumptions` | OwnerOrService | 現在値＋version（`VersionedAssumptions`） |
| GET | `/assumptions/history` | OwnerOnly | 変更履歴（`AssumptionsChangeEntry[]`・新しい順） |
| PUT | `/assumptions` | OwnerOnly | 更新（`{assumptions, expectedVersion, reason}`）。成功=更新後 version、400=検証、409=競合 |

応答型（camelCase）:
- `VersionedAssumptions`: `{ assumptions: { capitalGainsTaxRate, japanCommission:{rate,minimum,cap}, unitedStatesCommission:{...},
  fxSpreadRatio, minimumExpectedProfitMultiple, costLimits:{total,llm,infrastructure,data} }, version, isResolved }`
- `AssumptionsChangeEntry`: `{ actor, reason, changedAt, version, before?, after? }`

## 受け入れ基準（テストへ写像・第1スライス完了）

- [x] `frontend/` が unit-template 規約で作成され、`npm run typecheck`／`npm run lint`／`npm run test`（12/12）が緑（自己完結・platform 非依存）。CI `frontend` ジョブでも緑。
- [x] 設定画面が `trading-owner` に許可され、権限外は `NotFound`（存在秘匿）で構成 API を呼ばない（`access.test.tsx`）
- [x] 現在の前提条件＋version が描画される（`SettingsPage.test.tsx`）
- [x] 変更フォーム送信が PUT `/assumptions` を `{assumptions, expectedVersion, reason}` で呼ぶ（理由未入力は送信不可）
- [x] 空欄・非数値の財務パラメータは保存を無効化し警告する（黙って 0 送信しない・安全既定。レビュー 🟡 対応）
- [x] 競合（409）・検証エラー（400）でメッセージを表示し、破壊的な自動再試行をしない
- [x] 変更履歴が新しい順に一覧される。履歴取得失敗時はその領域のみ縮退する
- [x] `ci.yml` の既存ジョブが不変で、`frontend` ジョブが追加されている

> 後続スライスの受け入れ基準（FR-13 設定・#20 承認/統制状態参照・Playwright E2E・platform 合成点登録・SC-01 確定反映）は #106 に別掲。

## 安全既定（fail-safe）

- 認可は表示制御のみ。実効境界はサーバ側（403/404）。フロントは `/bff/*` 経由のみで各サービスを直接叩かない。
- BFF スタブ/実装は本スライス対象外。テストは `apiFetch` をモックし実疎通に依存しない。秘匿情報は扱わない。
- 取得失敗・権限外は安全側（縮退表示・存在秘匿）へ倒す。
