---
title: Stage 1 の期間カウントの除外「市場休場」に判定源が無い（祝日を判別できず営業日が過大計上される）
type: plan-feedback
status: resolved
category: 要求の不足
related_ids: [FR-20, FR-12, ADR-0008, ADR-0019, ADR-0022]
source_repo: endazon/ai-stock-trading
source_ref: docs/adr/IADR-0150_stage1-uptime-observation-and-session-hypotheses.md / docs/specs/20260805_385_stage1-trading-day-driver.md / ブランチ feat/FR-20-385-stage1-trading-day-driver
author: endazon (with Claude Code)
created: 2026-08-05
---

# フィードバック: Stage 1 の期間カウントの除外「市場休場」に判定源が無い

> **裁定済み（2026-08-07・質問票 第13回 Q3 案2）。** 計画は判定源を与えるのではなく、
> **「祝日は判別しない。除外しない」「分母と除外の判定に外部カレンダーを用いない」と定めた**
> （計画 06_daytrading-review §4.2「分母と除外の判定源」。planning `06fa163`。環流 project-planning#213 / #217）。
> **本環流が求めた「判定源」は与えられないことが確定した。**
> 実装への反映は endazon/ai-stock-trading#407 / [IADR-0187](../docs/adr/IADR-0187_stage1-holiday-non-detection-arbitration.md)。
> 🔴 **祝日表・休場日リスト・外部カレンダーを足すことは裁定違反である。**


## 種別

要求の不足（確定した除外規則が、除外の判定に必要な入力の出どころを述べていない）。
[20260804_fr20-stage1-session-calendar](20260804_fr20-stage1-session-calendar.md)（半日取引日の判定源）の**兄弟**であり、
供給元を実装した結果として**より具体的な帰結**が判明したため別立てで記録する。

## 起点となる計画書

- 機能要求（FR）: FR-20（段階ゲート）。関連: FR-12
- 関連 ADR: ADR-0008（段階ゲート）／ ADR-0019（moomoo PoC）／ ADR-0022 決定3（**営業日カレンダーを保持しない**）
- 計画書リンク: `planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md` §4.2 ／ `INDEX.md` 決定 34

## 現状（計画書の記述 / As-Is）

§4.2 の表は除外を次のとおり確定している。

| 項目 | 確定値 |
| --- | --- |
| 除外 | **OpenD の停止・ブローカー側の障害・市場休場** |

前 2 者は稼働分数の減少として自然に表れる（実装済み・#385）。**しかし「市場休場」は稼働分数に表れない。**
OpenD は市場が閉じていても接続を保つため、**休場日の probe は成功し続ける**。

## 問題点 / あるべき姿（To-Be）

### 実測で判明したこと（#385 の実装時）

- 本リポジトリの moomoo 取引ポート（`IMoomooTradeClient`）は発注・照会・建玉の 5 メソッドのみで、
  **取引時間・市場状態を照会する口が無い**。ADR-0019 の PoC 7 項目にも含まれていない。
- したがって「今日は市場が開いていたか」を**外部から知る手段が無い**。

### 帰結（実装が採った措置と、残る穴）

- **週末**は曜日の算術（`DayOfWeek`）で外せる。カレンダーではないため誤りようが無い。
- **祝日（米国市場は年 9 日前後）は外せない。** OpenD が稼働していれば営業日 1 日として算入される。
  Stage 1 の 60 営業日に対し **2〜3 日の過大計上**にあたる。
- 実装は祝日表を**発明しない**（ADR-0022 決定3・IADR-0137 決定1 の向きを維持）。
  この穴は実在の祝日を名指ししたテストで可視化してある。

**過大計上は「昇格が早まる」側であり、他の未供給（0 に倒れる）と違って fail-safe ではない。**
影響は限定的だが（昇格には取引件数 100 件と利用者承認も要る）、規則としては §4.2 の除外を満たしていない。

### あるべき姿は次のいずれかを計画側で確定すること

1. **判定源を名指しする**（例: 取引所公表の休場日カレンダー・moomoo OpenAPI の取引時間照会）。
   ADR-0019 の PoC 項目に「その日の通常取引時間・市場状態を照会できるか」を加える形が考えられる。
   半日取引日の判定源（[20260804](20260804_fr20-stage1-session-calendar.md)）と**同一の情報源で同時に解決できる**。
2. **祝日を除外しない旨を明示する**（例: 「休場日に稼働していた日を算入しても、60 営業日の意味は損なわれない」）。
   この場合、実装は現状のままでよい。
3. **構成注入とする**（`MarketCalendar` の `TradeCycle:Holidays:UnitedStates` と同じ形）。
   実装は表を持たず、運用者が与える。**ただし与え忘れは過大計上へ倒れる**ため、
   「未設定なら Stage 1 の営業日を数えない」といった fail-safe の向きも併せて裁定されたい。

## 実装側の暫定対応

- 稼働分数は定期 probe の観測から積む（[IADR-0150](../docs/adr/IADR-0150_stage1-uptime-observation-and-session-hypotheses.md) 決定1・2）。
- 分母は発明せず、**取り得る通常取引時間の仮説すべてで 50% 以上**を要求する（決定3）。
  半日取引日は過少計上（安全側）へ倒れる。
- **祝日は判別しない**（決定5）。限界として IADR・作業仕様書・`docs/blocked-tasks.md`・テストに記録した。

## 影響範囲

- 影響する実装: `Stage1SessionHypotheses`（Risk.Domain）・`BrokerAvailabilityProbeService`（OrderExecution）・
  `BrokerAvailabilityObservedHandler` / `EfStage1TradingDayObservationStore`（Risk）。
- 影響する仕様書: `docs/adr/IADR-0150_*.md`・`docs/specs/20260805_385_*.md`・
  `docs/functional/FR-20_staged-gates.md`・`docs/tests/FR-20_staged-gates-tests.md`・`docs/blocked-tasks.md`。

## 送付

~~未送付。計画リポジトリへ issue として起票する~~ → **送付・裁定済み（2026-08-07）。**
計画側 issue: **project-planning#213 / project-planning#217**（質問票 第13回 Q3。
[20260804](20260804_fr20-stage1-session-calendar.md) と**同一の裁定でまとめて解決された**）。
裁定結果は計画 06_daytrading-review §4.2「分母と除外の判定源」に反映済みであり、
実装側の反映は endazon/ai-stock-trading#407 / IADR-0187 で完了している。
