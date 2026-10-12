---
title: 経路B（values-local）の Stage 0 の記録の欄へ、オーナー承認（2026-10-13）の値を入れる（#1219）
type: spec
status: accepted
related_ids: [FR-15, FR-04, ADR-0033, ADR-0054, ADR-0064, IADR-0318, IADR-0498, IADR-0524]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md（決定 3・決定 5）
  - planning:projects/ai-stock-trading/07_adr/ADR-0064_llm-assignment-to-5-5-family-and-stage0-rerun.md（決定 3）
---

# 経路B（values-local）の Stage 0 の記録の欄へ、オーナー承認（2026-10-13）の値を入れる（#1219）

## 起点

- #1219 のコメント 2026-10-12T20:23Z（PoC の実測と見積り）と 2026-10-12T22:41Z（**オーナー承認 2026-10-13**: 12 銘柄・多数決 1 回・承認額 1,700 円・窓 2026-07-01〜2026-10-09）。
- **［2026-10-13 追記・オーナー裁定］窓を 2026-09-15〜2026-10-09 へ縮めた。** 承認（2026-10-12T22:41Z）の前提は「窓は 2026-07-01 から」だった。〜2026-09-14 は当時の監視銘柄と確定日報が無く、as-of 入力を再構成できないため、オーナーが裁定で起点を 09-15 へ移した。銘柄・多数決 1 回・トークン量は変えない。
- 欄は #1309（`41cac9ea`。作業仕様書 `20261011_1219_stage0-recording-local-values`）で置いた。本作業は値を入れるだけである。

## 入れる値（承認どおり）

| サービス | 欄 | 値 |
| --- | --- | --- |
| trade-decision | `Stage0Recording__Enabled` | `true` |
| trade-decision | `Stage0Recording__From` / `To` | `2026-09-15` / `2026-10-09`（平日 19 日。裁定で 07-01 起点から縮めた） |
| trade-decision | `Stage0Recording__Symbols__0..11` | TSLA・META・GOOGL・AMZN・AAPL・SMCI・PLTR・NVDA・COIN・MSTR・MARA・MSFT（すべて `UnitedStates`） |
| trade-decision | `VoteCount` / `ApprovedVoteCount` | `1` / `1` |
| trade-decision | 本判断 入力 / 出力 | `3478` / `425` |
| trade-decision | 一次 入力 / 出力 | `2269` / `582` |
| trade-decision | `OutputPath` | `/tmp/stage0/records.json`（#1219 の手順の既定） |
| trade-decision・report | `ApprovedEstimateJpy` | **`437.28`**（下の「承認額の写し方」） |
| backtest | `Backtest__Stage0__Symbols__0..11` | 記録と同じ 12 銘柄（下の「backtest 側の要否」） |

## 承認額の写し方（🔴 1,700 をそのまま入れると承認ゲートを通らない）

- 承認ゲートは**一致**で見る: `decimal.Round(approvedJpy, 2) != decimal.Round(estimate.TotalJpy, 2)` なら `NotApproved`（`Stage0DecisionRecorder.cs` の `ValidateApproval`）。「承認額 ≥ 見積り」ではない。
- 見積り（`Stage0RecordingBudget.Estimate` と同じ式・values-local の単価）。窓 2026-09-15〜2026-10-09: 平日 19 日 × 12 銘柄 = 228 判断時点。
  本判断 228 ×（3.478 × 0.327 ＋ 0.425 × 1.637）＝ 417.931068 円、一次 228 ×（2.269 × 0.0164 ＋ 0.582 × 0.0819）＝ 19.3520472 円、**合計 437.2831152 円**。呼び出し 456 回。
  （参考: 承認時の窓 2026-07-01〜 では平日 73 日・876 判断時点・1,680.0877584 円・1,752 回。PoC の「約 1,681 円」は切り上げ。）
- したがって `1700` を書くと記録器は `NotApproved` で LLM を呼ばずに戻る（試験で固定）。#1219 の手順 3 も「ログの `{TotalJpy}` をそのまま」と書いている。
- 🔴 coordinator からは「`ApprovedEstimateJpy` は 1700 のまま（上限として承認済み）。ただしゲートが見積りとの近さを要求する作りなら報告せよ」と指示を受けた。ゲートは一致を要求するため、1700 では記録が走らない。**値は見積りの写し `437.28` とし、その旨を報告する**（1700 へ戻すのは 2 行の変更で済むが、その場合 #1308 の後も `NotApproved` で記録は 0 回になる）。
- **見積り 437.28 円 ≤ オーナーの承認額 1,700 円**であり、超過の上限（1 判断ぶん ＝ 一次 1 ＋ 本判断 1 ≒ 1.92 円）を足しても 439.2 円で 1,700 円に収まる。よって `437.28` はオーナーの承認の範囲内である。月報 §7 の分母（report）も同値にする。

## backtest 側（`Backtest:Stage0`）の要否

| 欄 | 要否 | 根拠 |
| --- | --- | --- |
| `Strategy=recorded-replay` | 既に入っている（変更なし） | `values-local.yaml` backtest 節 |
| `Symbols` | **要る（12 銘柄へ揃える）** | 再生は記録の銘柄集合と評価ユニバースの**集合一致**を要求する（`Stage0ReplayEvaluation.SameSymbols`）。AAPL 1 銘柄のままだと `RecordingMismatch`。既存試験 `経路Bの記録の対象銘柄と_backtest_の評価銘柄は同じ集合である` も赤になる |
| `LookbackDays` / `IntervalSeconds` / `Recording__Path` | 入れない | #1219 の手順 5 のとおり「記録を渡すとき」に PoC が決める。今は記録 0 件（#1308）・日足なし（A-3）で評価に進まない |

🔴 **発見（本 PR では直さない）**: 評価の窓の終端は常に「今日（UTC）」であり（`Stage0EvaluationOptions.EvaluationWindow`）、再生は `記録の To ≥ 窓の終端` を要求する（`Stage0ReplayEvaluation.Validate`）。承認された `To=2026-10-09` は 2026-10-10 以降のどの評価日でも満たせず、**記録が揃っても `RecordingMismatch` で判定を組まない**。窓を変えるには再見積り・再承認（`To` を評価日まで延ばす）か、評価の終端を構成で与えるコード変更が要る。PR 本文と報告に書いて判断を仰ぐ。

## 前提（本 PR では直さない）

- as-of 入力の基底の供給が未実装（#1308。`Program.cs` の `NoAsOfDecisionInputProvider`）→ 欄を埋めても**記録 0 件・LLM 0 回・0 円で `Completed`**。
- 日足の供給が空（`Backtest__BarData__Provider=""`・blocked-tasks A-3）→ backtest は `NoHistoricalBars`。
- 🔴 `Enabled=true` は trade-decision の**起動のたびに** 1 回走る。今は 0 件で無害だが、#1308 が入った後にこの値のまま Pod が再起動すると、そのたびに見積りの範囲で LLM を呼ぶ。#1308 のマージ後の配備と記録の取り出し後に `false` へ戻す運用（#1219 の手順 4）を PR 本文に明記する。

## 母集合（規則 6・9）

- `Stage0Recording__` を values 2 本で引いた: values-local は trade-decision と report（`ApprovedEstimateJpy`）。values.yaml（本番）は触らない。
- `Backtest__Stage0__Symbols` を全追跡ファイルで引いた（`git grep`）: values-local と `.github/workflows/helm.yml:768`（キーの在否だけを見る。ラベルに「AAPL」とある）。→ ラベルを銘柄名なしへ直す。
- values-local のコメント「経路B は AAPL 1 銘柄」「欄の既定」を引き直し、今回の値に合わせる（規則 10）。
- 除外: `docs/blocked-tasks.md` B-7 は記録がまだ走っていないため更新しない（#1309 と同じ扱い）。`docs/tests/FR-10_risk-controls-tests.md` の AAPL は記録器の単体試験の題材で、values と無関係。

## 受け入れ基準と試験（`Stage0RecordingLocalProfileTests`）

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | values-local の欄が承認値で束縛される（12 銘柄・期間・トークン量・承認値・出力先） | `経路Bの記録の欄は承認値で埋まっている` |
| 2 | 承認値の組の見積りは 437.2831152 円・456 回（平日 19 日）で、オーナーの承認額 1,700 円を超過上限込みで超えない | `承認値の組の見積りはオーナーの承認額_1700_円に収まる` |
| 3 | 🔴 経路B の構成のまま走らせると承認ゲートを通る（`NotApproved` でない）。#1308 が無いので記録 0 件・LLM 0 回 | `経路Bの承認値のまま走らせると承認ゲートを通り_as_of_の基底が無ければ記録は_0_件` |
| 4 | 🔴 否定形: 承認額に 1,700 をそのまま入れると `NotApproved` で LLM を呼ばない | `承認額に_1700_をそのまま入れると承認ゲートを通らない` |
| 5 | 記録の対象銘柄と backtest の評価銘柄が同じ集合（既存） | `経路Bの記録の対象銘柄と_backtest_の評価銘柄は同じ集合である` |
| 6 | 本番 values.yaml は不変（既存） | `本番の取引判断の記録の欄は従来のままで_記録は無効である` |
| 7 | values-local の描画が通り、helm.yml の検査が緑 | `helm template -f values-local.yaml` |

## 範囲外

- #1308（as-of の基底の供給）・A-3（日足）・評価の窓の終端の問題（上の発見）。
