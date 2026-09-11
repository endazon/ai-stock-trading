---
title: Stage 0 判定を本番戦略で走らせる（カットオフ日の登録・分割の固定・経路B 有効化）
type: spec
status: done
related_ids: [FR-15, FR-20, FR-04, ADR-0008, ADR-0033, ADR-0036, ADR-0037, IADR-0310, IADR-0318, IADR-0329]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0036_stage0-input-completeness-and-split-fixation.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0037_sonnet5-price-correction-and-cutoff-mapping.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: Stage 0 判定を本番戦略で走らせる（#632）

## 起点

- issue #632（親）。分割先の #688（駆動・バス基盤）と #713（本番戦略＝記録・再生）は着地済みで、
  残っていたのは **計画側の裁定待ちだった 2 点**である。両方とも 2026-09-09 に裁定が下りた。
  - **計画 `ADR-0037` 決定 2**: `claude-sonnet-5` の学習カットオフ日 **`2026-01-31`** を
    `05_trading-assumptions` §6.1 へ登録した。同決定は
    「これで `ADR-0033` 決定 3 の『登録されるまで Stage 0 の合否判定を実行しない』という制約が解ける」
    と明記している。**実装は構成へ値を入れる作業が残っている。**
  - **計画 `ADR-0036` 決定 2 / フォローアップ 3**: ウォークフォワードの IS:OOS 比と PBO 分割数を
    **実装の裁量として追認**し、**「決めたら固定し、根拠を IADR へ残す」**ことを実装へ課した。
    実装は運用値（2:1 / 4）を持つが **IADR が無い**。
- あわせて #713 の是正が**自分の記述を引き直していなかった**（母集合の規則 10）。本番コードの
  複数箇所が「本番戦略（`IBacktestStrategy` 実装）はまだ存在しない」と述べたままで、**#632 の背景が
  引いた自己申告そのものが陳腐化している**。

## 対象範囲

- **対象**: カットオフ日の構成登録／分割の固定と根拠の IADR 化／経路B での定時駆動の有効化／
  評価を走らせなかった verdict の戦略識別子／陳腐化した自己申告の是正／通し試験の本番戦略経路への拡張。
- **対象外**（本 PR では扱わない。理由つき）:
  - **計画 `ADR-0036` 決定 1 の実装**（復元できない as-of 入力項目を記録へ残し、合否の集計から外す）。
    記録器（`TradeDecisionService/RecordStage0Decisions/`）側の契約追加であり、記録実行の承認（B-7）と
    同じ単位で扱うのが自然である。**別 issue へ回す。**
  - **計画 `ADR-0037` 決定 3 の実装**（月報 §7 へ `stage0-recording` の見積り承認額対比）。報告書生成側の作業。
  - **試行の探索（`TrialCount` 最小 20 の充足）**。記録再生は試行 1 本であり、探索は評価対象そのものの
    設計変更になる。**計画の裁定が要る**（本 PR では触れない）。
  - **過去データ源の実値**（`Backtest:BarData:Provider`）。`ADR-0023` 決定 5 の未確認 2 点が未了で
    `docs/blocked-tasks.md` A-3。**既定 `""` のまま据え置く。**
  - **記録の取得実行**。`ADR-0033` 決定 5 の見積り提示 → 利用者承認が要る（B-7）。

## 設計

| # | 対象 | 変更 |
| --- | --- | --- |
| 1 | `values.yaml`（backtest / trade-decision）・`values-local.yaml`・`appsettings.Development.json` | 学習カットオフ日 `2026-01-31` を **`Backtest__Stage0__LlmTrainingCutoff` と `Stage0Recording__LlmTrainingCutoff` の両方**へ登録する。**コード既定にはしない**（`ADR-0033` 決定 3 の「未設定＝未充足」を保つ） |
| 2 | `Stage0ReplayEvaluation` | IS:OOS 比（2:1）と PBO 分割数（4）を **public const として公開し、テストで固定**する。根拠は IADR-0329（`ADR-0036` 決定 2 フォローアップ 3 の履行） |
| 3 | `values-local.yaml`（経路B） | `Backtest__Stage0__Enabled=true`・`Strategy=recorded-replay`・評価銘柄 AAPL。**過去データ源は空のまま**なので verdict は `NoHistoricalBars` で fail-closed だが、**受け側（`BacktestEvaluatedProjectionHandler`）へ実際に 1 通届く**（#632 が「待っているが一通も来ない」と述べた状態を解消する） |
| 4 | `.github/workflows/helm.yml` | 経路B 描画に有効化が在ること／**本番既定描画に `Enabled: "true"` と `Strategy` が漏れていない**ことを検査する |
| 5 | `Stage0EvaluationService.EmptyBarVerdict` | バー 0 本で**評価を走らせなかった**とき、戦略識別子を**名乗らない**（空文字）。従来は評価対象の構成に関わらず `placeholder/no-op` を名乗っており、`Strategy=recorded-replay` の構成下で**走らせていない戦略の名を verdict が運ぶ**。IADR-0310 決定 3 の当該 1 点を改める（IADR-0329） |
| 6 | 本番コード・`docs/` の自己申告 | 「本番戦略は未実装／存在しない」の記述を、**現況（構成で選ぶ・記録が揃えば本物の判定器へ進む）**へ是正する |

### なぜ経路B だけで有効化するのか（本番 values.yaml は不変）

本番既定の fail-safe（`Enabled=false`）は `IADR-0310` 決定 1 の決定であり、**過去データ源と記録が揃うまで
覆さない**。経路B は dogfood の有効化プロファイルであり、既に実DD 供給・日報自動生成などを同じ形で開けている。
**verdict は fail-closed のまま**なので、`BacktestPassed` は `false` のまま＝昇格は止まったままである。

## 受け入れ基準（issue #632 の受け入れ基準を転記）

- [x] 本番構成で Stage 0 判定が実行され、`BacktestEvaluated` が RabbitMQ へ 1 通以上 publish される
      → 経路B の有効化（設計 3）＋通し試験（実 RabbitMQ 疎通は #82 に残る）
- [x] `RiskManagementService` の `IStagePerformanceStore` に `BacktestPassed` が記録され、段階状態から確認できる
      → `Stage0DriverToRiskProjectionTests`（本番戦略経路を追加）
- [x] 🔴 **否定形（最重要）**: 過去データが空のとき合格 verdict を出さない
- [x] 🔴 **否定形**: 駆動を入れたことで Stage 0 未合格でも昇格できる経路が生まれていない
- [x] 起点 ID コメント（FR-15 / FR-20 / ADR-0008）付きのテストを添える

### 本 PR が追加で満たす計画側のフォローアップ

- [x] `ADR-0037` 決定 2 の登録を実装の構成へ反映（カットオフ日 `2026-01-31`）
- [x] `ADR-0036` 決定 2 フォローアップ 3（分割の根拠を IADR へ残し固定する）

## テスト方針

| 見るもの | 対（つい） |
| --- | --- |
| 分割が 2:1 / 4 に固定されている | 陽性: 定数が期待値である／陰性: 定数から**実際に**組まれる窓・行列の形が期待どおり（値だけ合わせて使っていないこと） |
| 走らせなかったときは戦略を名乗らない | 陰性: バー 0 本 → `StrategyId` 空／陽性: バーがある placeholder 走行 → `placeholder/no-op`（既存） |
| 本番戦略の通し | 陽性: 記録が揃えば本物の判定器に到達し verdict が射影される／陰性: 射影されても昇格は拒否され続ける |

## 走査した母集合（規則 2・9・10）

**引いた語（誤りの側から）と結果。除外は理由つきで挙げる。**

1. `LlmTrainingCutoff`（追跡下の全ファイル。`CHANGELOG.md` と `.ai-context/specs/` を除外＝生成物と
   point-in-time 記録）: 30 行 / 12 ファイル。**構成へ値を入れる先は 3 つ**（`values.yaml`・
   `values-local.yaml`・`appsettings.Development.json`）で、残りは型・判定・テスト（据え置き）。
   `.ai-context/adr/IADR-0310` は凍結記録のため据え置く。
2. `本番戦略`（同上・`.ai-context/adr/` も除外＝凍結記録）: 18 行 / 11 ファイル。うち**現況と食い違う**のは
   `PlaceholderStrategy.cs`（2 行）・`Stage0Gate.cs`（1 行）・`Stage0DriverVerdict.cs`（1 行）・
   `Stage0EvaluationOptions.cs`（1 行）・`Program.cs`（2 行）・`appsettings.Development.json`（1 行）・
   `values.yaml`（1 行）・`Stage0DriverVerdictTests.cs`（1 行）・`docs/tests/FR-15_backtest-tests.md`（1 行）。
   残り 7 行は「綴り違いで本番戦略が走らない」等、**現況でも正しい**記述であり据え置く。
3. `PlaceholderStrategy.StrategyId` / `placeholder/no-op`: 8 行 / 6 ファイル。設計 5 で挙動が変わるのは
   `Stage0EvaluationService.cs:237` の 1 箇所と、それを見るテスト 2 箇所
   （`Stage0DriverToRiskProjectionTests.cs:102`）。`docs/functional/FR-15_backtest.md:133` は
   **placeholder 走行の説明**であり据え置く（走行したときは従来どおり名乗る）。
4. `Backtest__` / `Backtest:Stage0`（`deploy/` 配下）: 2 行（`values.yaml` のみ）。**`values-local.yaml` に
   backtest の節そのものが無い**ため、設計 3 では values.yaml の 2 件を写したうえで追加する
   （helm はリストを置換するため。`helm.yml` の「values-local drops no env from prod default」検査）。
5. `OverfittingPartitions` / `InSample`（backend 配下）: `Stage0ReplayEvaluation.cs` の 3 定数と
   `ProbabilityOfBacktestOverfitting.cs` の受け側のみ。**分割を持つ他の場所は無い**（固定の対象は 1 ファイル）。

**除外したもの**: `CHANGELOG.md`（生成物。是正は `scripts/changelog-overrides.json` の系統）、
`.ai-context/specs/`（point-in-time 記録）、`.ai-context/adr/`（凍結記録。本文プロズを後から書き換えない）。

## 計画書との差異

無し。本 PR は `ADR-0036` 決定 2・`ADR-0037` 決定 2 が実装へ委ねた作業の履行であり、
`ADR-0033` / `ADR-0008` の決定は 1 つも覆さない。
