---
title: IADR-0524 Claude のモデル割当を 5.5 系へ切り替え、移行期間は直前世代を同じ位置で受ける。Stage 0 の両層の照合は受けない。単価表にプロンプト長の第 2 段を持たせる
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-06, FR-15, FR-20, NFR, ADR-0011, ADR-0014, ADR-0015, ADR-0017, ADR-0033, ADR-0037, ADR-0054, IADR-0122, IADR-0215, IADR-0313, IADR-0498]
author: claude (Claude Code)
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning#783（利用者裁定 2026-10-10）
  - planning:projects/ai-stock-trading/07_adr/ADR-0014（§決定1・§決定3）
  - planning:projects/ai-stock-trading/07_adr/ADR-0054（決定1・決定3）
  - planning:projects/ai-stock-trading/07_adr/ADR-0011
  - planning:projects/ai-stock-trading/07_adr/ADR-0037（決定1・決定2）
related_specs:
  - ../specs/20261010_1295_claude-5-5-models.md
---

# IADR-0524: Claude のモデル割当を 5.5 系へ切り替え、移行期間は直前世代を同じ位置で受ける（#1295）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-10
- 決定者: Claude Code（実装）。利用者裁定 2026-10-10（planning#783）を受けた。

## 起点・関連

- 起票: [#1295](https://github.com/endazon/ai-stock-trading/issues/1295)。移行段の撤去: [#1296](https://github.com/endazon/ai-stock-trading/issues/1296)
- 計画: planning#783（全用途を 5.5 系へ・`claude-fable-5-1` も使用しない・Stage 0 は 5.5 系の組で再実施・Haiku 5.5 の単価はプロンプト長で 2 段）
- 前提: IADR-0215（割当表と実効モデルの照合）・IADR-0498（両層の組での Stage 0）・IADR-0122（モデル別単価表と fail-safe）・IADR-0313（スクリーニング予算の導出）
- 仕様書: `.ai-context/specs/20261010_1295_claude-5-5-models.md`

## コンテキスト

- 割当は `claude-opus-5` → `claude-opus-5-5`、`claude-sonnet-5` → `claude-sonnet-5-5`、`claude-haiku-4-5` → `claude-haiku-5-5` へ移る（取引判断の 2 層を含む）。
- 基盤（MSP）の LLM ゲートウェイは**構成したモデル名**を応答に名乗り、本システムの `LlmAssignmentEvaluator` はそれを割当表と**完全一致**で照合する。
  AST だけを先に切り替えると応答は旧 ID を名乗り、取引判断は `Unassigned`（Allowed=false）で全件見送りになる。MSP だけを先に切り替えても同じである。
  2 つのリポジトリの配備を同時刻に揃える手段は無い。
- Stage 0 の合格（実弾解禁の必須ゲート）は「両層の組での通過」であり、組を変えたら再実施する（ADR-0011・ADR-0014 決定 3・ADR-0054 決定 3）。
  照合（`Stage0TwoTierModels`）は評価の `Allowed` を使っていたため、割当表だけを緩めると**旧組の記録が新組の記録として通る**。
- Haiku 5.5 は入力 100,000 トークンを境に単価が 5 倍になる。現行の単価表は 1 モデル 1 段しか持たない。上限側の 1 段に寄せると常時過大計上になる（ADR-0037 決定 1 の趣旨に反する）。

## 決定

### 決定 1: 割当表を 5.5 系にし、移行期間だけ直前世代を同じ位置で受ける

- 割当表（`LlmAssignments.All`）の第 1 候補・フォールバック先を 5.5 系 ID にする。並び・フォールバックの許否は変えない。
- `LlmAssignments.PreviousGenerationAccepted`（5.5 系 ID → 直前世代）を置き、評価は**完全一致の照合で外れたときだけ**直前世代を見る。
  第 1 候補の直前世代は `Primary`、フォールバック先の直前世代は `FallbackFired`（その用途の許否どおり）とし、評価に `PreviousGenerationAccepted = true` の印を付ける。
- 取引判断系は鎖が空なので、直前世代の受け入れはフォールバック先を増やさない。**フォールバック禁止の意味は変わらない**（試験: 取引判断系で Allowed になるのは第 1 候補とその直前世代だけ）。
- 期待値（`ExpectedModel`）は 5.5 系のピンのまま、実効モデル（`EffectiveModel`）は名乗った値をそのまま運ぶ。費用計上（`LlmCostIncurred.Model`）にも名乗った値が載るため、PoC で切り替えを確かめられる。
- **撤去の条件**（#1296）: MSP の用途別割当が 5.5 系へ切り替わって配備され、PoC で各用途の応答の実効モデルが 5.5 系であることを確かめたとき。

### 決定 2: 禁止モデルを集合にし `claude-fable-5-1` を足す

`ForbiddenModels = { claude-fable-5, claude-fable-5-1 }`。禁止の判定は用途によらず最優先で、直前世代の照合より先に行う。

### 決定 3: Stage 0 の両層の照合は直前世代を受けない

- 評価に `MatchesCurrentPin`（`Primary` かつ印なし）を足し、`Stage0TwoTierModels.MatchesPinnedAssignments` はこれを使う。
  旧組・片方の層だけ旧世代の判断は「実効モデル不一致」として判定母集団から外れ、全件が旧組なら判定を組まない（`AllDecisionsExcluded`）。
- 実弾解禁の前提の告知文（`LiveTradingGate.StageZeroTwoTierPrerequisite`・Helm の描画停止文）とカットオーバー手順書の条件 10 を 5.5 系の組へ改める。

### 決定 4: 単価表に任意の第 2 段（プロンプト長）を持たせる

- 行（`LlmPriceRow`）に `LongContextThresholdTokens` と第 2 段の入出力単価を任意で持たせ、構成キーは `LlmPricing:PerModel:<model>:LongContextThresholdTokens` / `LongContextInputPer1kTokens` / `LongContextOutputPer1kTokens`。
- `Resolve(model, inputTokens)` は入力トークン数が閾値を**超える**とき第 2 段を返す（100,000 ちょうどは第 1 段）。計上（両サービスの `PublishingLlmUsageReporter`・Stage 0 の実費）は要求ごとの入力トークン数で、Stage 0 の見積りは 1 回あたりの入力トークン量で引く。
- 第 2 段のキーが 1 つでも書かれていて 3 つ揃って解析できない行は**表に載せない**（未知モデル＝最大単価）。長い要求を第 1 段の安い単価で通さない。
- 未知モデルの最大単価の母集合に第 2 段も含める。第 2 段を持たない行・表が空のとき・`Resolve(model)` の挙動は従来どおり。
- 1 円/1k を下回る単価は小数第 3 位で丸めると過小側へ寄るため有効数字 3 桁で持つ（haiku-5-5 の 0.0164 / 0.0819）。

### 決定 5: 学習カットオフ日とスクリーニング予算の注記

- 学習カットオフ日を `2026-06-30` へ（Jun 2026 を ADR-0037 決定 2 の写し方で月末へ）。駆動側・記録側・開発用 appsettings を同時に変え、Helm の描画検査も同じ値へ。
- スクリーニング予算 150,000 文字は**値を変えない**。導出（haiku-4-5 の 200K コンテキスト）は Haiku 5.5（1M）では効かないため、根拠を費用の段へ書き直す（最悪側の 1.0 文字/トークンでは 100,000 トークンを超え得る）。値の見直しは #1290 の再測定と併せて別に決める。

## 検討した選択肢

| 選択肢 | 採否 | 理由 |
| --- | --- | --- |
| A. AST と MSP を同時刻に切り替える | 不採用 | 2 リポジトリの配備を同時刻に揃える手段が無い。ずれた間は取引判断が全件見送りになる |
| B. 割当表に旧 ID をフォールバック先として足す | 不採用 | 取引判断系は鎖が空でなければならない（ADR-0017 決定 2）。報告書では位置（第 1 候補か第 2 候補か）が狂い、発火の記録が誤る |
| C. **直前世代を同じ位置で受け、印を付ける（本決定）** | 採用 | 位置と許否を変えずに切り替え順の非同期を吸収できる。印で Stage 0 の照合だけを厳格に保てる |
| D. Haiku 5.5 の単価を上限側（$0.50/$2.50）の 1 段で持つ | 不採用 | 一次スクリーニングの大半は 100,000 トークン以下で、常時 5 倍の過大計上になる |

## 結果・影響

- MSP の切り替え前後どちらでも、取引判断・報告書は止まらない。切り替え前の応答は直前世代として受けられ、費用は旧世代の単価行で計上される（Helm の旧行は移行期間だけ残す）。
- 旧組で採った Stage 0 の記録は合否に使えない。Stage 0 は 5.5 系の組で採り直す。窓（2026-07-01 以降）の短さの扱いは planning#783 の裁定待ち。
- Stage 0 の記録の戦略識別子・内容ハッシュは構成モデル名を含むため、5.5 系の記録は別の戦略になる（既存の規則どおり）。

## 試験

| ID | 内容 | 試験 |
| --- | --- | --- |
| （T-ID なし） | 割当表のスナップショット・直前世代の対応表・直前世代の位置と印・プロパティ（取引判断系の許可は第 1 候補とその直前世代だけ）・禁止モデル 2 つ | `LlmAssignmentsTests` |
| T-15-124 | 旧組・片方の層だけ旧世代の記録は両層の照合で不一致／全件が旧組なら判定を組まない／告知文は 5.5 系の組を名乗る | `Stage0DecisionRecordTests`・`Stage0ReplayEvaluationTests`・`LiveTradingGateTests` |
| （T-ID なし） | 第 2 段の境界（100,000 / 100,001）・第 2 段の誤設定の行は未知モデル・未知モデルの最大に第 2 段を含める・構成から計上額まで届く | `LlmPriceTableTests`・`LlmPricingWiringTests` |

変異（`MatchesCurrentPin` → `Allowed`・直前世代を受けない・閾値を `>=`・第 2 段を引かない）でそれぞれ赤になることを確かめた（仕様書 §変異）。

## フォローアップ

1. #1296: MSP の切り替えと PoC の確認の後、直前世代の受け入れ・旧世代の単価行・関連の注記を外す（本 IADR へ日付つき追記で記録する）。
2. Stage 0 を 5.5 系の組で採り直す（planning#783 の裁定 (2) を受けて）。
3. スクリーニング予算の値の見直し（#1290 の Haiku 5.5 での再測定と併せる）。
