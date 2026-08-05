---
title: Stage 1 の期間カウント規則に「半日取引日の判定源」が無い（§4.2 の分母をどこから知るか）
type: plan-feedback
status: open
category: 要求の不足
related_ids: [FR-20, FR-12, ADR-0008, ADR-0022]
source_repo: endazon/ai-stock-trading
source_ref: docs/adr/IADR-0137_stage1-trading-day-counting.md / docs/specs/20260804_333_stage-gate.md / ブランチ feat/FR-20-stage-gate
author: endazon (with Claude Code)
created: 2026-08-04
---

# フィードバック: Stage 1 の期間カウント規則に「半日取引日の判定源」が無い

## 種別

要求の不足（確定した判定規則が、判定に必要な入力の出どころを述べていない）。

## 起点となる計画書

- 機能要求（FR）: FR-20（段階ゲート）。関連: FR-12（内蔵 paper との区別）
- ユースケース（UC）: UC-06（段階遷移の承認）
- 関連 ADR: ADR-0008（段階ゲート）／ ADR-0022 決定3（**営業日カレンダーを保持しない**という別件の裁定）
- 計画書リンク: `planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md` §4.2
  「Stage 1 の期間カウント規則（確定）」／ `INDEX.md` 決定 34

## 現状（計画書の記述 / As-Is）

§4.2 の表は分母と基準時刻を次のとおり確定している。

| 項目 | 確定値 |
| --- | --- |
| 分母 | **その日の実際の通常取引時間**（通常日 6.5 時間＝9:30〜16:00 ET／**半日取引日 3.5 時間**＝9:30〜13:00 ET）。固定の 6.5 時間を用いない。プレ／アフターマーケットは含めない |
| 判定の基準時刻 | **米国東部時間**（サマータイムの切替・半日取引日に対応する） |

**しかし「ある日が半日取引日かをどこから知るか」を述べていない。** 判定に必須の入力でありながら、
出典（取引所の公表カレンダー・moomoo OpenAPI の照会・その他）が計画のどこにも無い。

## 問題点 / あるべき姿（To-Be）

- **カレンダーの誤りは、そのまま昇格判定の誤りになる。** 米国の半日取引日は年ごとに変わり
  （感謝祭翌日・クリスマスイブ・独立記念日前日など）、祝日が週末に当たると振替も生じる。
  実装が出典の無い表を抱えると、更新漏れが「半日取引日が算入されない（3.5 時間の日に 105 分稼働しても
  105 ÷ 390 ＝ 26.9%）」という形で静かに昇格を遅らせる。
- **ADR-0022 決定3 は別件（為替の鮮度判定）で「営業日カレンダーを保持しない。カレンダーを持たないため、
  カレンダーの誤りに起因する誤判定が原理的に起きない」と裁定している。** 段階ゲートだけがカレンダーを
  抱えるのは、この裁定と向きが揃わない。
- あるべき姿は次のいずれかを計画側で確定すること。
  1. **判定源を名指しする**（例: moomoo OpenAPI の取引時間照会・取引所公表の休日/半日取引日カレンダー）。
     ADR-0019 の PoC 項目に「その日の通常取引時間を照会できるか」を加える形が考えられる。
  2. **カレンダーを持たない方針を明示する**（ADR-0022 決定3 と同じ向き）。この場合、分母は
     「稼働監視が観測したその日の実際の取引時間」であると計画側に書く。

## 実装側の暫定対応

実装は**値を発明せず、観測入力として受け取る形**を採った（IADR-0137 決定1）。

```csharp
Stage1TradingDayObservation(DateOnly SessionDateEasternTime, int RegularSessionMinutes, int OperationalMinutes)
```

判定（`Stage1DayQualification.Qualifies`）は `OperationalMinutes / RegularSessionMinutes >= 0.50` だけを行い、
カレンダーもタイムゾーン変換も持たない。上記 2 の方針であればこの形がそのまま正であり、
1 の方針が確定した場合は供給側（稼働監視ドライバ）が照会結果を詰める形になる。

**いずれにせよ供給元は未実装である**（日次の稼働分数を記録するドライバが無い）。本フィードバックは
その供給元を実装する前に判定源を確定しておきたい、という趣旨である。

## 影響範囲

- 影響する実装: `Stage1TradingDayObservation` / `Stage1DayQualification`（Risk.Domain）と、
  未実装の稼働監視ドライバ。
- 影響する仕様書: `docs/adr/IADR-0137_stage1-trading-day-counting.md`・
  `docs/specs/20260804_333_stage-gate.md`・`docs/tests/FR-20_staged-gates-tests.md`。

## 送付

未送付。計画リポジトリ（endazon/project-planning）へ issue として起票する。
