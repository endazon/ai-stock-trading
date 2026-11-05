---
title: 運用段階の正を段階ゲートの台帳 1 つにし、段階の変更はゲートの遷移だけで行う（審査の数値の値域の検査を併せる。段で切り替える）
type: spec
status: accepted
related_ids: [FR-20, FR-10, FR-13, ADR-0008, ADR-0016, IADR-0616, IADR-0070, IADR-0140, IADR-0161, IADR-0163, IADR-0134]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-20「段階遷移（昇格・差し戻し）は合格・撤退基準に基づき利用者の承認で行う」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: 運用段階の正を 1 つにする（#1533）

## 起点

- #1533。運用段階の値が 2 か所にある。審査・サイジングは設定の段階（`RiskManagementSettings.Stage`）を読み、段階ゲートは台帳（`IStageGateStore`）の現段階を読む。
- FR-20 は「段階遷移（昇格・差し戻し）は合格・撤退基準に基づき利用者の承認で行う」と定める。

## 実測（develop `26d0a18a7`）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | `PUT /risk-controls/settings/stage` は `RiskSettingsService.UpdateStage` で設定の段階（段階・既定の発注先・発注可能額の比率）をそのまま書く。台帳に触れず、合格基準を見ない。比率の値域も確かめない | `UpdateStageSettings/Endpoint.cs`・`RiskSettingsService.cs:168-196` |
| 2 | 審査は設定の段階で判定する（実弾の可否・段階の資金上限・段階別の商品種別） | `RiskEvaluator.cs:103`・`:125`・`:159` |
| 3 | サイジングも設定の段階を読む | `SizingContextService.cs:32`・`:44` |
| 4 | 段階ゲートの遷移（`POST /stage-gate/transition`）は台帳だけに追記し、設定の段階を変えない | `StageGateService.RequestTransition` |
| 5 | `PUT /settings/stage` の呼び手はリポジトリにも基盤にも無い（試験を除く） | `git grep "settings/stage"`（AST・MSP） |
| 6 | 審査は発注意図の数量・価格・換算率の値域を確かめない。換算率 0 は名目額（基準通貨）を 0 に見せ、金額の上限を素通りする | `RiskEvaluator.cs`（`NotionalInBase = Quantity × Price × FxRateToBase`） |

## 決定（IADR-0616）

1. **設定の口は段階を変えない。** `PUT /settings/stage` は 409（正しい口の案内）を返す。`RiskSettingsService.UpdateStage` の本番の呼び手は `SyncStageFromGate` だけ（試験がソースを走査して固定）。
2. **遷移の受理で設定の段階をゲートの定義へそろえる。** `StageGateService.RequestTransition` は台帳へ追記した後、`RiskSettingsService.SyncStageFromGate(policy.SettingsFor(遷移先), 承認者, 理由)` を呼ぶ（変更履歴に「段階」を残す）。
3. **審査が読む段階の正を段で切り替える**（構成 `RiskControls:StageSource`）。読み取りの継ぎ目 `StageGateBoundRiskSettingsStore` を設定ストアの内側に挟む。
   - 段 1 `Report`（既定）: 設定の段階をそのまま返し、台帳の定義と食い違えば Warning を 1 回残す（審査の挙動は変えない）。
   - 段 2 `Enforce`: 台帳の現段階の定義で置き換えて返す。
4. **審査の数値の値域の検査**（`OrderIntentValidation`）。数量 ≦ 0（決済を含む）・新規建てで価格 ≦ 0・換算率 ≦ 0・損切り価格 ≦ 0・ATR < 0・列挙が定義の外を `RejectionReason.OrderIntentInvalid`（序数 35・クラス B）で拒否する。手仕舞いは数量だけを確かめる（FR-10）。

## 段の切り替え（運用）

1. 段 1 のまま配備し、リスク管理のログに「審査が読む運用段階（設定）と段階ゲートの台帳の現段階の定義が食い違っています」が出ないかを見る。
2. 出たら、段階ゲートの遷移で台帳と設定をそろえる（台帳が正）。
3. `RiskControls__StageSource=Enforce` にする。

## 母集合（規則 9）

- 段階を書く: `git grep -n "UpdateStage(" -- backend ':!*/Tests/*'` → `RiskSettingsService`・`UpdateStageSettings/Endpoint.cs`（呼ばなくなる）。
- 段階を読む: `git grep -n "settings.Stage\.\|GetCurrent().Stage" -- backend/Services/RiskManagementService ':!*/Tests/*'` → `RiskEvaluator`・`SizingContextService`・`PositionCloseService`・`MaintenanceMarginReductionService`（いずれも設定ストア経由＝継ぎ目の内側）。
- 台帳の現段階: `StageGateService`・`RiskStatusService`（台帳を直接読む。変更なし）。
- 拒否理由の語彙: `RejectionReason`・`RejectionReasonClassification`・序数の固定の試験・分類の試験。入口の阻止（`EntryStateBlockers`）・gRPC の写像は状態の阻止だけを扱うため対象外（発注意図ごとの理由）。
- 文書: `docs/functional/FR-20_staged-gates.md`（§1-1 を足す）。

## 受け入れ基準

- [x] 設定の口から段階を変えられない（409・試験）
- [x] 遷移の受理で設定の段階がゲートの定義にそろう／拒否では変わらない
- [x] 段 1 は設定の段階で審査する／段 2 は台帳の定義で審査する
- [x] 値域の外の数値は拒否される（新規建て）・決済は数量だけ
- [x] 段 2 の継ぎ目を挟んだ遷移でも、変更履歴の Before は保存されていた段階（`SingleStageSourceTests`。直す前に赤を確認）
- [x] 段 2 で段階以外の設定を保存しても、保存値の段階は動かない（同上。直す前に赤を確認）

## 範囲外（残余）

- 遷移の追記と設定の保存は別の保存である。間で落ちると食い違いが残る（段 1 はログで、段 2 は台帳の定義で読むので審査には影響しない）。
- 画面（SC-02）に段階の編集の口があるかは基盤の画面側の確認が要る（現行の BFF・SPA に呼び手は無い）。
