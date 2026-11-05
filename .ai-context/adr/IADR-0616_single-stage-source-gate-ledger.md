---
title: IADR-0616 運用段階の正は段階ゲートの台帳とし、設定の口では段階を変えない。遷移の受理で設定の段階をゲートの定義へそろえ、審査が読む段階は段で台帳へ寄せる（審査の数値の値域の検査を足す）
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-10, FR-13, ADR-0008, ADR-0016, IADR-0070, IADR-0140, IADR-0161, IADR-0163, IADR-0134]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-20)
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

> **仮番号**: IADR-0616 は採番時点（2026-11-06）の develop の最大（IADR-0615）＋1 である。マージの前に他の PR が同じ番号を入れたら、その時点の最大＋1 へ付け替える（欠番は許す・再利用はしない）。旧仮番号 IADR-0619 から付け替えた。

# IADR-0616: 運用段階の正を 1 つにする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-05
- 決定者: Claude Code（実装）／ オーケストレーター（段階の統制の正を 1 つにし、段階の変更は必ずゲートを通す。審査の数値の検査を同じ PR に入れてよい、の指示）

## 起点・関連

- issue: #1533
- 前提: IADR-0070（段階ゲートの台帳）・IADR-0140（段階と発注先の 2 軸）・IADR-0161 / IADR-0163（段階の既定発注先の allow-list）・IADR-0134（拒否理由の序数は末尾追加）。
- 実装仕様書: [`.ai-context/specs/20261105_1533_single-stage-source.md`](../specs/20261105_1533_single-stage-source.md)

## コンテキストと課題

審査・サイジングは設定の段階を、段階ゲートは台帳の現段階を読む。設定の段階は `PUT /settings/stage` 1 回で書け、合格基準も承認も経ない。FR-20 は段階遷移を合格・撤退基準と利用者の承認で行うと定める。発注意図の数値の値域も確かめておらず、換算率 0 の新規建ては金額の上限を素通りする。

## 検討した選択肢

1. **台帳を正にし、設定の口を閉じ、遷移で設定をそろえ、審査が読む段階を段で台帳へ寄せる（採用）** — 稼働中の構成で設定と台帳が食い違っていても、段 1 では審査が変わらず、記録で気付ける。
2. 審査を即座に台帳で読む — 稼働中の食い違い（例: 設定だけ上の段階）があると、配備した瞬間に審査の結論が変わる。段で切り替えられない。採らない。
3. 設定の口に合格基準を持たせる — 正が 2 つのまま残る。採らない。

## 決定

1. `PUT /settings/stage` は 409（「段階は段階ゲートの遷移でだけ変えられる」）。`UpdateStage` の本番の呼び手は `SyncStageFromGate` だけ（ソースの走査で固定）。
2. `StageGateService.RequestTransition` は受理の後に `SyncStageFromGate(policy.SettingsFor(遷移先), 承認者, 理由)` を呼ぶ。
3. 構成 `RiskControls:StageSource`（`Report` 既定 / `Enforce`）。`StageGateBoundRiskSettingsStore` を `EfRiskSettingsStore` の外・`SimulatorProfileRiskSettingsStore` の内に挟む。`Report` は設定を返して食い違いを 1 回だけ Warning、`Enforce` は台帳の定義で置き換えて返す。知らない値は起動で止める。
4. `OrderIntentValidation`: 数量 ≦ 0（全注文）、新規建ての価格 ≦ 0・換算率 ≦ 0・損切り価格 ≦ 0・ATR < 0・列挙の外 → `RejectionReason.OrderIntentInvalid`（序数 35・クラス B）。

## 結果

- 段 1 の既定では審査の結論は変わらない（`OrderIntentInvalid` を除く。正しい発注意図では立たない）。
- 設定の口を呼ぶ利用者は 409 を受ける（呼び手はリポジトリにも基盤にも無い）。

## 残余・フォローアップ

1. 段 2 へ上げるのは、段 1 で食い違いのログが出ないことを確かめた後（オーナーの判断）。
2. 台帳の追記と設定の保存は 1 つの取引ではない。
