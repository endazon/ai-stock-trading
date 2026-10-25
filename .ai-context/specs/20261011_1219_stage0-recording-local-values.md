---
title: 経路B（values-local）へ Stage 0 の記録の欄を置き、PoC が実測値と承認値を埋めるだけで回せる形にする（#1219）
type: spec
status: accepted
related_ids: [FR-15, FR-04, ADR-0011, ADR-0033, ADR-0036, ADR-0054, ADR-0064, IADR-0318, IADR-0498, IADR-0524]
author: claude (Claude Code)
created: 2026-10-11
updated: 2026-10-11
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md（決定 3・決定 5）
  - planning:projects/ai-stock-trading/07_adr/ADR-0064_llm-assignment-to-5-5-family-and-stage0-rerun.md（決定 3）
---

# 経路B（values-local）へ Stage 0 の記録の欄を置き、PoC が実測値と承認値を埋めるだけで回せる形にする（#1219）

## 起点

- #1219（Stage 0 記録の最初の見積りの提示）。オーナーは 5.5 系の組（本判断 `claude-sonnet-5-5`・一次 `claude-haiku-5-5`）での Stage 0 の再実施を承認した（#1299 のコメント 2026-10-10）。planning#783 の裁定 8・計画 ADR-0064 決定 3。
- 段取り: 月曜 2026-10-12 の取引で 1 判断あたりのトークン量を実測し、見積りを出し、オーナーの承認額を得る。PoC はその値を values-local へ埋めて記録を回す。

## 計画の確認

- ADR-0033 決定 5: 実行前に見積りを提示し、利用者が承認しない限り実行しない。→ 既定は無効・未承認のままにする。
- ADR-0033 決定 3・ADR-0064 決定 3: 窓はカットオフ `2026-06-30` の翌日以降。→ 期間は PoC が埋める（2026-07-01 以降）。
- ADR-0011・ADR-0054 決定 3: 記録は本番と同じ二段・同じピン。→ 本判断の層の希望モデルは `claude-sonnet-5-5`、一次は割当表に委ねる。

## 現況（origin/develop `c7458ab6` で実測）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 記録の構成キーは `Stage0Recording:{Enabled, From, To, LlmTrainingCutoff, Symbols, VoteCount, DecisionsPerDay, InputTokensPerDecision, OutputTokensPerDecision, ScreeningInputTokensPerDecision, ScreeningOutputTokensPerDecision, ApprovedEstimateJpy, ApprovedVoteCount, OutputPath, Model, ScreeningModel}` | `Stage0RecordingOptions.cs:13-110` |
| 2 | values-local の trade-decision にあるのは `LlmTrainingCutoff=2026-06-30` と `ApprovedEstimateJpy=""` だけ | `values-local.yaml:377`・`:390` |
| 3 | 記録器は起動時に 1 回だけ走る（run-once）。無効なら見積りだけをログに出す | `Hosted/Stage0RecordingService.cs` |
| 4 | 🔴 as-of 入力の基底の供給は `NoAsOfDecisionInputProvider`（常に null）。外側の 3 つの包みは null を素通しする。記録器は null の判断時点を飛ばす → **記録 0 件・LLM 0 回** | `Program.cs:595-603`・`AsOfDecisionInput.cs:337-342`・`Stage0DecisionRecorder.cs:203-205` |
| 5 | backtest の評価の窓は `[今日 − LookbackDays, 今日]`（既定 365）。記録が窓を覆わなければ `RecordingMismatch`、窓にカットオフ以前のバーが入れば `DataCutoff` 未充足 | `Stage0EvaluationOptions.cs:79-80`・`Stage0ReplayEvaluation.cs:194-224`・`Stage0EvaluationService.cs:180-181` |
| 6 | 日足の供給は空（`Backtest__BarData__Provider=""`）で、判定は `NoHistoricalBars` で組まれない（blocked-tasks A-3） | `values-local.yaml:534` |

**PoC の読み（今の構成では記録が 1 件も作られない）は正しい**（#4）。基底の供給は #1308 で起票した。

## 決めたこと

1. values-local の trade-decision に記録の欄を置く。**int / bool の欄は "" にしない**（束縛で例外になり、常駐が落ちる。試験で実測した）。コードの既定と同じ値（`false`・`1`・`0`）を書き、空を許すのは文字列と承認値（`int?`・`decimal?`）だけにする。
2. `Stage0Recording__Model=claude-sonnet-5-5` を置く。空だと見積りの本判断の層が未知モデルの単価（現行 fable の行・$10/$50）で引かれ、約 5 倍に過大になる（`Stage0DecisionRecorder.cs:103`）。一次（`ScreeningModel`）は置かない（割当表の一次のピンで見積る。本番と同じ扱い）。
3. 記録の対象銘柄は backtest の評価銘柄（AAPL・UnitedStates）と同じにする。
4. backtest に `LookbackDays=365`・`IntervalSeconds=86400` を置く（コードの既定と同値＝挙動は変わらない）。PoC が記録を差し込むときに縮める欄である。
5. 本番の values.yaml は変えない。
6. 手順（見積りの式・承認額の入れ方・起動と停止・確かめ方・供給が欠けたときの挙動）は #1219 のコメントに置く（既存の runbook に Stage 0 の記録を扱うものが無く、#1219 が見積りの提示と承認を追う issue であるため）。

## 母集合（規則 6）

- `Stage0Recording__` を values 2 本で引いた: values.yaml は trade-decision に 2 行（`LlmTrainingCutoff`・`ApprovedEstimateJpy`）、report に 1 行（`ApprovedEstimateJpy`）。values-local は trade-decision に 2 行・report に 1 行。→ 足したのは values-local の trade-decision だけ。report の承認額は「trade-decision と同値にする」既存の注記のまま（PoC が両方に入れる）。
- `Backtest__Stage0__` を values-local で引いた: Enabled・LlmTrainingCutoff・Strategy・Recording__Path・Symbols の 6 行。`LookbackDays`・`IntervalSeconds` は無かった。
- helm README・docs/operations に `Stage0Recording` の記述は無い（`grep -rn Stage0Recording deploy/helm/ai-stock-trading/README.md docs/operations` が 0 件）。追随先なし。
- 除外: `docs/blocked-tasks.md` B-7 の「最後に測った時点」は本 PR で測り直していない（記録はまだ走っていない）。更新しない。

## 受け入れ基準と試験

| # | 基準 | 試験（`Stage0RecordingLocalProfileTests`） |
| --- | --- | --- |
| 1 | values-local の欄は env の形のまま束縛でき、書いたままでは記録しない（Enabled=false・未承認・期間なし・出力先なし・トークン 0・Model=sonnet-5-5・カットオフ 2026-06-30・AAPL） | `経路Bの記録の欄は束縛でき_書いたままでは記録しない構成である` |
| 2 | 🔴 否定形: 書いたままの構成で走らせても LLM を 1 回も呼ばず、書き出さない | `経路Bの構成のまま走らせても_LLM_を_1_回も呼ばない` |
| 3 | 🔴 PoC が欄を埋めて承認しても、基底の供給が無ければ記録 0 件・LLM 0 回・0 円（承認ゲートは通る） | `欄を埋めて承認しても_as_of_の基底の供給が無ければ記録は_0_件で_LLM_は_0_回` |
| 4 | 記録の対象銘柄と backtest の評価銘柄が同じ集合 | `経路Bの記録の対象銘柄と_backtest_の評価銘柄は同じ集合である` |
| 5 | backtest に足した 2 欄はコードの既定と同値 | `経路Bの_backtest_の窓と巡回間隔はコードの既定と同値である` |
| 6 | 🔴 本番 values.yaml の trade-decision の記録の欄は従来の 2 つだけで、記録は無効 | `本番の取引判断の記録の欄は従来のままで_記録は無効である` |
| 7 | 本番の既定描画は変わらない | `helm template`（既定）の前後の差分が 0 行 |

## 範囲外

- as-of 入力の基底の供給（#1308）。
- 記録の受け渡し（trade-decision の Pod から backtest の Pod へ）を volume で行う chart の変更。手順は `kubectl cp` で行う（#1219 のコメント）。
- 日足の供給（A-3）。
