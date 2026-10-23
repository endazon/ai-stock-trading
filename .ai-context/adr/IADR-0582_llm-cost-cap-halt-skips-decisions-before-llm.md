---
title: IADR-0582 月次 LLM 費用上限の 100% 到達（Halted）は取引判断の合流点の先頭で費用統制を照会し、定時・価格変動の両方を LLM の前で見送る（理由 LlmCostCapHalted）
type: impl-adr
status: Accepted
related_ids: [NFR-13, FR-02, FR-04, UC-01, UC-02, ADR-0074, ADR-0057, IADR-0031, IADR-0462, IADR-0027]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0074_llm-cost-cap-halt-covers-price-movement-decisions.md（決定 1〜3）
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§6.1）
related_specs:
  - ../specs/20261024_1423_llm-cost-cap-halt-covers-price-movement.md
---

# IADR-0582: 費用上限の停止を取引判断の合流点で効かせる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-24
- 決定者: Claude Code（計画 ADR-0074 フォローアップ 1 が実装に委ねた点 —— 読む場所・見送りの理由の名前・照会の失敗の扱い）

## 起点・関連

- 起点: #1423 M-5。環流 planning#830、裁定 2026-10-23（利用者）→ 計画 ADR-0074（Accepted）。
- 前提（覆さない）: [IADR-0031](./IADR-0031_cost-poller-wiring.md)（費用統制の照会・失敗は Normal）、
  [IADR-0462](./IADR-0462_ledger-position-query-status-and-pre-llm-skips.md)（LLM を呼ぶ前の見送りの出口と台帳の語彙）。

## コンテキスト

1. develop `b427f998` で費用統制の状態（Halted / Throttled）を読むのは情報収集の巡回だけ（Halted なら収集をしない＝定時の判断が起きない）。
   価格変動トリガーの判断は費用統制を見ず、100% 到達後も LLM を呼び続けていた。
2. 定時・価格変動の判断は `TradeDecisionAppService.DecideAsync(DecisionTrigger)` で合流する。
3. 計画 ADR-0074: 100% に達したら定時・価格変動の両方の判断を LLM の前で見送る（新規建ても出口の判断も）。
   LLM を使わない保護は止めない。解除は月替わり（累計が 0 に戻る）で自動。見送りの理由の名前は実装が決める。

## 決定

1. **読む場所は合流点の先頭**とする。方針・サイジング・現在値の照会より前に費用統制を照会し、Halted なら `SkipBeforeLlmAsync` で見送る。
   定時・価格変動を 1 か所で覆い、以降の照会も省ける。価格変動のハンドラ側に置かない（定時の側の取りこぼし＝収集が止まる前に積まれた判断を防ぐ）。
2. **見送りの理由は `LlmCostCapHalted`**（`DecisionForgoneBeforeLlmReason` と `DecisionSkipReason` の末尾。名前は同じ）。
   既存の出口（台帳 `TradeDecisionForgoneBeforeLlm`・計数 `ast_trade_cycle_decision_skips_total`）に載り、監査・夜間の要約・月報の
   「取引判断スキップ回数」に入る。新しいイベント・通知は足さない（100% 到達の通知は既存の Critical がある）。
3. **照会は費用統制の GET `/costs/state`**（情報収集と同じ口・同じ s2s）。`isHalted` が明示的に true のときだけ停止と読む。
   非 2xx・例外・タイムアウト（3 秒）・欠落・不正な本文は「停止していない」へ倒す（IADR-0031 と同じ判断。費用統制の一時障害で
   資金の保護に関わらない判断の全停止をしない）。`CostControl:BaseUrl` 未設定は NoOp（従来どおり）。gRPC の口は足さない（REST で足りる）。
4. **判断のたびに 1 回照会する**（キャッシュしない）。判断は銘柄 × 巡回の頻度で、照会は軽い。月替わりの解除がそのまま効く。
5. helm の取引判断に `CostControl__BaseUrl` を足す（values / values-local）。

## 結果・残余リスク

- 100% 到達の後は、定時・価格変動とも取引判断の LLM 費用が増えない（配備後）。
- 🔴 100% 到達の後は、保有中の銘柄の AI 判断による決済も出ない（ADR-0074 が受け入れたリスク。出口は損切り・利用者の手仕舞い・閉場前の手仕舞い〔有効なら〕）。
- 費用統制が落ちている間は停止を読めず、価格変動の判断は LLM を呼ぶ（fail-open。IADR-0031 と同じ向き）。費用は既存どおり計上される。
- 判断ごとに 1 往復増える（費用統制が遅いと判断が最大 3 秒遅れる）。
- 停止は合流点で見送るだけで、定時の側は従来どおり情報収集が収集を止める（重ねて効く）。
