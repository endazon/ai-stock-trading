---
title: IADR-0584 費用統制の費用計上月を JST の年月で求め、既存の行は記録の時刻から JST の年月で引き直す（受容ではなく補正）。勧告ロックの bigint は書式が変わらないので一意・安定のまま
type: impl-adr
status: Accepted
related_ids: [NFR-13, ADR-0057, ADR-0074, IADR-0027, IADR-0034, IADR-0478, IADR-0582]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0057（決定 3。月次 LLM 費用上限の月は JST の暦月）
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§6.1）
related_specs:
  - ../specs/20261024_1461_cost-month-key-jst.md
---

# IADR-0584: 費用計上月を JST の暦月にする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-24
- 決定者: Claude Code（#1461 が実装に委ねた点 —— 移行時の当月行の扱い・勧告ロックの安定性）

## コンテキスト

1. 計画 ADR-0057 決定 3 は、月次 LLM 費用上限の月を JST の暦月と定める。develop `4c84de49` の `CostControlAppService.MonthKey` は
   `clock.UtcNow` の `yyyy-MM`（UTC の年月）で、JST の 1 日 0:00〜9:00 の計上が前月の累計に入っていた。80%・100% の判定、`/costs/state`、
   取引判断の停止の解除（IADR-0582）が同じ月に依存する。
2. 月キーは計上の時点で `cost_entries."Month"` へ書かれる。記録の時刻 `RecordedAt` は計上の時刻そのもの（`EfCostLedger.Record` の `at`）。
3. 勧告ロック（IADR-0034）の bigint は月キー文字列 `yyyy-MM` から `yyyy×100＋MM` で導く。

## 決定

1. **導出**: `MonthKey(instant) = instant.ToOffset(+09:00).ToString("yyyy-MM")`。JST は夏時間を持たないので固定のオフセットで求める
   （報告書サービスの `ReportSchedule.JstOffset` と同じ扱い）。計上・状態・使用量・費用÷資金のすべてが同じ導出を通る。
2. **既存の行は補正する（受容しない）**: EF の移行 `RekeyCostEntriesMonthToJst` が
   `UPDATE cost_entries SET "Month" = to_char("RecordedAt" AT TIME ZONE 'Asia/Tokyo', 'YYYY-MM')`（変わる行だけ）を流す。下げは UTC で引き直す。
   - 理由: 受容すると、配備した月の 1 日 0:00〜9:00 JST の計上が前月に残り、その月の判定が過少になる（上限が上限として働かない向き）。
     記録の時刻が計上の時刻そのものなので、引き直しは決定的で、外部の情報を要らない。
   - 過去の月の行も引き直す（月ごとの境界の 9 時間ぶんが隣の月へ移る）。過去の月の統制の状態はもう使われず、月報の期間集計は監査台帳を
     権威源にしている（IADR-0254）ので、移っても判定・報告書は変わらない。夜間の要約 §13 は当月だけを読む。
   - 手元の PostgreSQL 16 で、境界の前後（14:59:59Z / 15:00:00Z）・年越しの行が上げで JST へ、下げで UTC へ戻ることを確かめた。
3. **勧告ロック**: 月キーの書式は `yyyy-MM` のままなので、`AdvisoryKey` の値は `yyyy×100＋MM` で月ごとに一意・プロセスを跨いで安定である。
   同じ値が UTC 由来の月と JST 由来の月の両方を指すのは、移行の配備中（新旧の版が並ぶ間）だけで、そのときも同じ月キーの計上を直列化するだけで害は無い。
4. **夜間の要約 §13** の月も JST の暦月で選ぶ（同じ台帳を別の月で読むと、当月の累計が食い違う）。

## 結果・残余リスク

- 月次 LLM 費用上限の累計・80%・100% の判定・`/costs/state`・取引判断の停止の解除は、JST の 1 日 0:00 で切り替わる。
- 🔴 **配備の時期**: 月初 0:00〜9:00 JST（UTC と JST の月が食い違う 9 時間）と月末の最後の 9 時間の前後で配備すると、新旧の版が並ぶ間に
  旧版が UTC の月キーで書いた行が移行の後に残り得る（移行は起動時に 1 回だけ流れる）。この窓を避けて配備する。避けられなかったときは移行の SQL を手で流し直す。
- 移行で当月の累計が増える（月初の 9 時間ぶん）ことがある。80%・100% を跨いでも、遷移のイベント（`CostThresholdReached`）は計上のときにしか出ないので
  通知は出ない（状態の照会は即座に新しい累計で答える）。
