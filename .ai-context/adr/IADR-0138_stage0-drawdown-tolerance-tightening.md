---
title: IADR-0138 Stage 0 の最大 DD 許容値を 0.15 から 0.10 へ厳格化し、運用の DD 停止ラインとの同値性をテストで固定する
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-20, FR-10, ADR-0008, ADR-0018, IADR-0045, IADR-0110, IADR-0127]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0018_risk-defaults-sync-and-stage0-dd.md
---

# IADR-0138: Stage 0 の最大 DD 許容値を 0.15 から 0.10 へ厳格化し、運用の DD 停止ラインとの同値性をテストで固定する

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: 実装（Claude Code）／ 起点 issue [#333](https://github.com/endazon/ai-stock-trading/issues/333)（[#306](https://github.com/endazon/ai-stock-trading/issues/306) を吸収）
- 作業仕様書: [20260804_333_stage-gate](../specs/20260804_333_stage-gate.md)

## コンテキストと課題

`Stage0GateCriteria.Default.MaxDrawdownTolerance` は **0.15** であった。この値の一次記録は
[IADR-0045](IADR-0045_stage0-gate.md)（2026-07-11）であり、当時の表は備考に「前提条件 DD 上限の**緩め**」と
自ら記している。凍結の記録は [IADR-0110](IADR-0110_stage0-criteria-calibration.md) 決定4 で、
「計画書由来であり実装側の自由変数ではない。**変更が要るなら計画側へ環流する**」と明記していた。

**この状態は統制として倒錯していた。** 運用の DD 停止ライン（`RiskLimits.MaxDrawdownRatio`）は **10%** であり、
Stage 0 は**運用停止ラインより 5 ポイント緩い戦略を合格させ得た**。すなわち**検証を通った戦略が、
運用開始と同時に停止条件へ抵触し得る**。ゲートが「合格」と言った意味が運用側で成立しない。

計画側は IADR-0110 が求めた環流（[planning#56](https://github.com/endazon/project-planning/issues/56)）を受け、
ADR-0018 決定2（計画リポ）（2026-08-01）で
**10%（`0.10`）** を確定した。あわせて §4 の Stage 0 合格基準の表現を「最大 DD が**許容内**」から
**「最大 DD ≤ 10%」** へ数値化した（検証不能な表現が実装の裁量を生んだため）。

なお、**0.15 の出所は「計画書の DD 上限」ではない。** 計画 §5 の DD 上限は当時から 10% であり、0.15 は
ADR-0008（計画リポ） の
**旧レンジ「10〜15%」の上限側からの逆算**である。IADR-0110 決定4 の当時の記述はこの点で不正確であった。
同一原因（陳腐化したレンジからの逆算）によるずれは `LosingStreakThreshold`（3 vs 確定値 5）にもあり、
そちらは #329 で是正済みである。

## 検討した選択肢

1. **0.10 へ直すだけ** — 最小だが、次に読む者が再び「Stage 0 は検証だから緩くてよい」と考えて戻す余地が残る。
   実際、0.15 は 2 つの IADR に「据え置く」と書かれたまま 3 週間残った。
2. **0.10 へ直し、値を直書きした退行防止テストを置く** — 戻す変更は検知できるが、
   「運用の停止ラインと同値である」という**この値が満たすべき関係**が固定されない。
   運用側（`RiskLimits.MaxDrawdownRatio`）だけが動いた場合に再び乖離する。
3. **0.10 へ直し、値の固定に加えて「運用の DD 停止ラインと同値」であることもテストで固定する** — 両方向の
   退行を止める。

## 決定

**選択肢 3 を採用する。**

### 決定 1: `MaxDrawdownTolerance` の既定を `0.10` とし、名前付き定数として公開する

`Stage0GateCriteria.MaxDrawdownToleranceDefault = 0.10m` を新設し、`Default` はこれを参照する。
定数の XML ドキュメントに **旧値 0.15 が何であったか（旧レンジの上限側からの逆算）と、なぜ倒錯していたか**を
書き残す。値だけ直してコメントに旧レンジを残すと、次に読む者が再び旧レンジから逆算する
（ADR-0018 §フォローアップが名指しで警告している再発経路である）。

### 決定 2: 退行防止テストは「値」と「運用停止ラインとの同値性」の 2 本立てにする

`Stage0GateCriteriaTests.Stage0の最大DD許容値は10パーセントであり運用の停止ラインと同値である` が

1. `MaxDrawdownTolerance == 0.10m`（**0.15 へ戻す変更を検知する**）
2. `MaxDrawdownTolerance == TradingDefaults.CreateRiskLimits().MaxDrawdownRatio`（**片方だけが動く乖離を検知する**）

の両方を主張する。1 だけでは運用側が動いたときに黙って乖離が復活する。2 だけでは両方を同時に 0.15 へ
動かす変更が通る。

### 決定 3: 「旧許容値の帯（0.10 超〜0.15）」を否定形テストで塞ぐ

ADR-0018 §結果は「既に評価済みのバックテスト結果のうち、**DD が 10〜15% の戦略は不合格へ転じる**」と明記した。
この帯を `[Theory]`（0.11 / 0.13 / 0.15）で不合格側に固定する。境界値テスト（9.9% / 10.0% / 10.1%・
**閾値ちょうどは合格**）とあわせ、「厳格化が実際に効いている」ことを帯として証明する。

### 決定 4: 既存 IADR は書き換えず、追記で改定の経緯を残す

[IADR-0045](IADR-0045_stage0-gate.md)（0.15 採用の一次記録）と
[IADR-0110](IADR-0110_stage0-criteria-calibration.md)（凍結の記録）の本文は改めず、**追記ブロック**を足す
（[IADR-0131 → IADR-0134](IADR-0131_short-selling-controls-fail-closed.md) の前例と同じ形）。
IADR-0110 決定4 の**判断そのもの（較正対象にしない・変更権限は計画側）は維持される**——
改まったのは値であり、決定の構造ではない。

### 決定 5: 実データでの水準確認は本 IADR で扱わない

`MaxDrawdownTolerance` は較正対象ではない（IADR-0110 決定4）。一方、**Stage 0 判定そのものが実過去データ源を
持たない**（ADR-0023（計画リポ）：
Stooq は実質的に取得不能・moomoo OpenAPI の履歴取得は PoC 項目 7）。**この状態では Stage 0 の合格判定を
実施できない**（計画 INDEX 決定 48）。厳格化は正しく実装されるが、**実運用では現時点で一度も発火しない**。
担当は [#382](https://github.com/endazon/ai-stock-trading/issues/382) であり、本 IADR では扱わない。

## 結果

- **良い影響**: Stage 0 が「運用で停止する水準の戦略を合格させる」経路が閉じた。合格の意味が運用側で成立する。
  値と関係の両方が機械的に固定され、2 つの IADR に「据え置く」と書かれたまま残る状態が再発しない。
  計画適合レジストリ（[IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)）の
  `Stage0GateCriteria.MaxDrawdownTolerance` が解消し、登録簿から 1 行削除された。
- **悪い影響 / トレードオフ**: **Stage 0 の合格が難しくなる**（ADR-0018 §結果が明記したとおり）。
  DD が 10〜15% の戦略は不合格へ転じる。ただし、それらは運用開始と同時に停止する戦略であり、
  合格させることに意味が無い。
- **残余リスク**: 上記のとおり Stage 0 判定は実データ源が無く発火しない。**厳格化の実効は #382 の解決に依存する。**

## 関連

- 計画 ADR: ADR-0018（計画リポ）（決定2）／
  ADR-0008（計画リポ）（旧レンジの一次記録・部分改定された）
- 実装 ADR: [IADR-0045](IADR-0045_stage0-gate.md)（0.15 採用の一次記録・追記済み）／
  [IADR-0110](IADR-0110_stage0-criteria-calibration.md)（凍結の記録・追記済み）／
  [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱レジストリ）
- 吸収した issue: [#306](https://github.com/endazon/ai-stock-trading/issues/306)（連敗しきい値 3→5 の是正は #329 で完了済み）
- 仕様書: [作業仕様書 20260804_333](../specs/20260804_333_stage-gate.md)／
  [FR-15 テスト仕様書](../../docs/tests/FR-15_backtest-tests.md)／[FR-20 テスト仕様書](../../docs/tests/FR-20_staged-gates-tests.md)
