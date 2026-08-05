---
title: 画面仕様書（素案） — SC-03 承認・統制状態参照画面
type: screen
status: Draft
related_ids: [SC-03, FR-10, FR-20, FR-12, FR-13, UC-06, ADR-0008, ADR-0009, IADR-0140, IADR-0142]
issue: 106
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-08-05
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
related_specs:
  - ../specs/20260718_106_frontend-risk-settings-and-controls.md
  - ../specs/20260805_334_broker-provider-axis.md
  - ../adr/IADR-0140_broker-provider-axis.md
  - ../adr/IADR-0142_stage1-simulate-only-aggregation.md
  - ../adr/IADR-0084_frontend-risk-settings-and-control-status.md
---

# SC-03 承認・統制状態参照画面【素案】

> 起点: **FR-10**（取引統制）、**FR-20**（段階ゲート）、**UC-06**（設定変更・一時停止・緊急停止。本画面は当該統制の状態を閲覧する参照面）。
> 計画リポジトリ `05_screens/` は空のため SC-03 は素案（project-planning#33・#31 後続 で環流）。データ源は RiskManagementService
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
   **発注先（#334）**: 現在の発注先を運用段階の**隣に行を分けて**表示する（1 行に混ぜない。INDEX 決定 46）。
   **本画面は参照専用であり、変更は SC-02 で行う**（導線を置く）。
   内蔵 `paper` 稼働中は画面上部に警告バナー（必須 2 文言）を出し、統制状態のカード類に `paper・参考値` ラベルを付す。
2. **段階ゲート現況（`StageGateStatus`）**: 現段階・**段階の既定発注先**、昇格評価（`promotion`: 昇格先・可否・未充足基準）、
   撤退評価（`withdrawal`: 到達・停止提案・降格提案段階）。
2-2. **Stage 1 の進捗（#334・IADR-0142）**: 経過営業日数 / 目標・取引件数 / 最小件数を表示し、
   **内蔵 `paper` 稼働により算入されなかった営業日数を併記**する（例: 「経過 42 / 60 営業日（`paper` 稼働により 3 日を除外）」）。
   **moomoo `SIMULATE` の約定のみを集計している**旨の注記を置く。閾値は応答（`stage1Criteria`）から取り、画面に直書きしない。
2-3. **発注先の変更履歴（#334・FR-20 (2)）**: 日時・変更前後・変更者・理由を新しい順に一覧。
   設定変更履歴（`/risk-controls/settings/history`）から `changeType == BrokerProviderChanged`（7）だけを絞る。
3. **段階遷移履歴（`StageTransition[]`）**: 承認による昇格・差し戻しを新しい順に一覧（連番・from/to・種別・承認者・理由・日時）。

## データ取得（BFF `/bff/*` 経由・`apiFetch`・すべて読み取り）

| 操作 | 呼び出し | 応答/エラー |
| --- | --- | --- |
| 統制状態 | `GET /risk-controls/status` | `RiskStatusView`。404/失敗=縮退表示 |
| 段階ゲート | `GET /risk-controls/stage-gate` | `StageGateStatus`。失敗時はその領域のみ縮退 |
| 遷移履歴 | `GET /risk-controls/stage-gate/history` | `StageTransition[]`（`stage-gate` の `history` を用いても可） |
| 発注先の変更履歴 | `GET /risk-controls/settings/history` | `SettingsChangeEntry[]` を `changeType == 7` で絞る。失敗時はその領域のみ縮退 |

## 振る舞い（安全既定）

- **破壊的操作の UI を持たない**（参照専用）。統制の変更入口は #165 の Bot に一元化。
- 数値 enum（`activeControl`/`stage`/`kind`/未充足基準/撤退理由）は表示ラベルへ写像し、未知値はフォールバック表示。
- 取得不能・権限外・BFF 未登録は安全側（縮退・存在秘匿）へ倒す。機微情報は権限外に載せない。
- 各領域（統制状態・段階ゲート・履歴）は独立に縮退する（一方の取得失敗が他方を巻き込まない）。

## スコープ外（後続）

承認・差し戻し操作 UI（#165 Bot 側）、Playwright E2E、platform 合成点（features/BFF）登録。
