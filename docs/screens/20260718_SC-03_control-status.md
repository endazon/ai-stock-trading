---
title: 画面仕様書（素案） — SC-03 承認・統制状態参照画面
type: screen
status: Draft
related_ids: [SC-03, FR-10, FR-20, UC-06, UC-07, ADR-0008, ADR-0009]
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

# SC-03 承認・統制状態参照画面【素案】

> 起点: **FR-10**（取引統制）、**FR-20**（段階ゲート）、**UC-06**（設定変更・一時停止・緊急停止）、**UC-07**（稼働状態の確認）。
> 計画リポジトリ `05_screens/` は空のため SC-03 は素案（project-planning#31 で環流）。データ源は RiskManagementService
> `/risk-controls/status`・`/risk-controls/stage-gate`（OwnerOnly）。**参照中心**の画面。

## 画面の位置づけ

platform SPA 認証済みレイアウト配下に feature `sc03-controls` としてマウント（route `controls`・nav「統制状態」）。
破壊的操作（pause/resume・kill switch・段階遷移承認）は **#165 の Discord Bot 側と役割分担**し、本画面には置かない
（[IADR-0084](../adr/IADR-0084_frontend-risk-settings-and-control-status.md) 決定 2・安全既定）。

## アクセス制御

- 利用者（`trading-owner`）限定。`RequireRole anyOf=['trading-owner']`・権限外は `NotFound`（存在秘匿）。
- 実効認可はサーバ側（`/status`・`/stage-gate` = OwnerOnly）。権限外では構成 API を呼ばない。

## 構成要素

1. **統制状態（`RiskStatusView`）**: 3 統制（kill switch・日次損失ロックアウト・一時停止）の on/off、成立中で最優先の統制
   （`activeControl`）、新規建て停止（`newEntriesBlocked`）、ロックアウト解除日、運用段階、当日損益（実現＋含み＋合計）、
   上限使用率の入力（発注額/上限・DD/上限・保有数/上限）。
2. **段階ゲート現況（`StageGateStatus`）**: 現段階・モード/資金上限、昇格評価（`promotion`: 昇格先・可否・未充足基準）、
   撤退評価（`withdrawal`: 到達・停止提案・降格提案段階）。
3. **段階遷移履歴（`StageTransition[]`）**: 承認による昇格・差し戻しを新しい順に一覧（連番・from/to・種別・承認者・理由・日時）。

## データ取得（BFF `/bff/*` 経由・`apiFetch`・すべて読み取り）

| 操作 | 呼び出し | 応答/エラー |
| --- | --- | --- |
| 統制状態 | `GET /risk-controls/status` | `RiskStatusView`。404/失敗=縮退表示 |
| 段階ゲート | `GET /risk-controls/stage-gate` | `StageGateStatus`。失敗時はその領域のみ縮退 |
| 遷移履歴 | `GET /risk-controls/stage-gate/history` | `StageTransition[]`（`stage-gate` の `history` を用いても可） |

## 振る舞い（安全既定）

- **破壊的操作の UI を持たない**（参照専用）。統制の変更入口は #165 の Bot に一元化。
- 数値 enum（`activeControl`/`stage`/`kind`/未充足基準/撤退理由）は表示ラベルへ写像し、未知値はフォールバック表示。
- 取得不能・権限外・BFF 未登録は安全側（縮退・存在秘匿）へ倒す。機微情報は権限外に載せない。
- 各領域（統制状態・段階ゲート・履歴）は独立に縮退する（一方の取得失敗が他方を巻き込まない）。

## スコープ外（後続）

承認・差し戻し操作 UI（#165 Bot 側）、Playwright E2E、platform 合成点（features/BFF）登録。
