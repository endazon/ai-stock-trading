---
title: 仕様書: Stage 0 の評価対象を AI 判断そのものにする（記録・再生方式）
type: spec
status: draft
related_ids: [FR-04, FR-15, FR-20, NFR, ADR-0003, ADR-0008, ADR-0011, ADR-0014, ADR-0018, ADR-0033, ADR-0034, IADR-0043, IADR-0045, IADR-0055, IADR-0089, IADR-0105, IADR-0212, IADR-0248, IADR-0281, IADR-0296, IADR-0304, IADR-0310, IADR-0318]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0018_risk-defaults-sync-and-stage0-dd.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0034_short-sell-inclusion-observed-and-strategy-change.md
---

# 仕様書: Stage 0 の評価対象を AI 判断そのものにする（記録・再生方式）

> 対象 issue: [#632](https://github.com/endazon/ai-stock-trading/issues/632) の残件（駆動経路は
> [#688](https://github.com/endazon/ai-stock-trading/issues/688) / IADR-0310 で完了済み）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-04**（AI による売買判断）・**FR-15**（バックテスト＝Stage 0 の必須ゲート）・
  **FR-20**（段階的展開のゲート）・NFR（LLM 費用）
- ユースケース（UC）: UC-06（設定変更・段階遷移承認）
- 画面（SC）: なし
- 関連 ADR: **ADR-0033**（Stage 0 の評価対象は AI 判断そのもの・記録再生方式。2026-09-05 裁定）・
  ADR-0008（段階ゲートとバックテスト）・ADR-0011（モデルピン留め）・ADR-0014（モデル割当）・
  ADR-0018（Stage 0 の DD 閾値）・ADR-0003（全量ログ・不確実なら取引しない）・ADR-0034（空売り「含む」の判定）
- 関連 IADR: IADR-0043（`IBacktestStrategy` の純関数契約）・IADR-0045（Stage 0 判定器）・IADR-0089（verdict 供給）・
  IADR-0105（過去データ源）・IADR-0212（用途キー）・IADR-0248（解析不能と見送りの区別）・IADR-0281 / IADR-0304
  （StrategyId と空売りの観測）・IADR-0310（駆動とプレースホルダ verdict）・**IADR-0318**（本作業の実装 ADR）
- 計画書リンク: 隣接クローンを読み取り専用で参照（`project-planning/projects/ai-stock-trading/`）

## 目的・背景

ADR-0033 は Stage 0 の評価対象を **FR-04 の AI 判断そのもの**と定め、**記録・再生方式**で評価すると決めた。
IADR-0310（#688）は駆動経路までを作り、評価対象はプレースホルダ戦略（注文を出さない）に留めた。
本作業はその評価対象を実装する ——「記録」（取引判断サービス）と「再生」（バックテストサービス）である。

**LLM 呼び出しは記録の側にだけ置く。** 再生は純関数であり、`IADR-0043` の契約を覆さない（ADR-0033 決定2）。

## 対象範囲

- 対象:
  - 記録の契約（`AiStockTrading.Shared.Contracts.Backtest`）: 判断 1 件・記録集合・JSON 直列化・戦略 ID の導出
  - 再生（BacktestService）: `RecordedDecisionReplayStrategy`（純関数）・記録の供給ポートとファイル実装・
    駆動の戦略選択（`placeholder` / `recorded-replay`）・記録が整合するときだけ**本物の** `Stage0GateService` へ到達させる
  - 記録（TradeDecisionService）: `Stage0DecisionRecorder`・as-of 入力ポート・多数決・費用見積り（純関数）・
    承認ゲート・見積り超過での停止・既定で走らない run-once 常駐
- 対象外:
  - as-of 入力（過去時点の価格・ニュース・開示）の**実供給**。既定実装は「入力なし」であり、実供給は残件
  - 実 LLM を用いた記録の実行（利用者の見積り承認が要る。ADR-0033 決定5）
  - LLM 学習カットオフ日の値そのもの（計画側 `05_trading-assumptions §6.1` への登録が要る）
  - 試行台帳の**探索**（記録再生戦略はパラメータ探索を持たないため試行は 1 本である）

## 設計

### 1. 記録の契約（Shared.Contracts / `Backtest/`）

| 型 | 役割 |
| --- | --- |
| `Stage0DecisionAction` | `Hold` / `Buy` / `Sell`。契約側の判断表現（`TradeDecisionService.Domain.TradeAction` へ依存しない） |
| `Stage0RawDecision` | **各回の生の判断**（試行番号・行動・根拠・参照価格・損切り幅・入出力トークン・解析不能フラグ） |
| `Stage0DecisionRecord` | 1 判断時点分（銘柄・市場・AsOf・入力の指紋・モデル ID・多数決回数・生の判断の全量・多数決結果・符号付き数量・費用） |
| `Stage0DecisionRecordSet` | 期間・銘柄集合・カットオフ日・作成時刻・モデル ID・**戦略 ID**・記録の並び |
| `Stage0StrategyIdentity` | 戦略 ID の導出（`ai-decision-replay/<model>/<recordset-hash>`） |
| `Stage0DecisionRecordJson` | `System.Text.Json` の共有オプション（列挙は文字列・両サービスで同一） |

- **生の判断を欠落させない**（ADR-0003 の全量ログ）。多数決結果だけを残す形は採らない。
- 戦略 ID のハッシュは**内容だけ**から採る（作成時刻を含めない）。同じ記録を読み直しても同じ戦略 ID になり、
  `BacktestEvaluated.StrategyId`（IADR-0281 の「戦略の同一性」）が記録の同一性と一致する。

### 2. 再生（BacktestService）

- `Domain/RecordedDecisionReplayStrategy` は `IBacktestStrategy` の純関数実装である。
  - `AsOf` に対応する記録を引き、`SignedQuantity != 0` のものだけ `BacktestOrder` へ写す。
  - **記録集合の期間外の `AsOf` では 1 件も発注しない**（境界は両端含む）。記録の無い日も無発注。
- `IStage0DecisionRecordSource`（既定 `NoStage0DecisionRecordSource`＝常に記録なし）と
  `FileStage0DecisionRecordSource`（`Backtest:Stage0:Recording:Path` の JSON）。
- 駆動 `Hosted/Stage0EvaluationService` に戦略選択 `Backtest:Stage0:Strategy` を足す。
  - `placeholder`（既定）: #688 のまま**一切変えない**。
  - `recorded-replay`: 記録が**存在し・期間が構成の評価期間を覆い・銘柄集合が構成と一致し・
    カットオフ日が構成と一致する**ときだけ、本物の `Stage0GateService` へ到達する。
- **否定形（最重要）**: 記録なし／期間不整合／銘柄不整合／カットオフ日未構成／カットオフ日の不一致 のいずれかは
  `Stage0DriverVerdict` の不合格固定へ倒し、`FailedChecks` に `NoDecisionRecords` または `RecordingMismatch` を載せる。
- 評価文脈（`Stage0GateContext`）の組み立ては純関数 `Stage0ReplayGateContextBuilder` に閉じる。
  - コスト 2 倍感度: 同一の記録を `CostSensitivity.Doubled` で再走行した総リターン。
  - ウォークフォワード: 記録期間を **2:1** で IS / OOS に割り、OOS 窓のバーだけで再走行した総リターン。
    記録再生戦略はパラメータ探索を持たないため、窓の役割は「後半で確かめる」ことに限られる。
  - 試行台帳: **1 本**（記録そのもの）。`MinTrials=20` を満たさないため、現時点の合否は必ず不合格になる。
    これは**仕様**であり、探索の実装が入るまで変わらない。
  - PBO 行列: 戦略候補は「記録した AI 判断」と「何もしない」の 2 本。後者の各ブロック成績は定義から 0 である。
    ブロックは日次リターン、分割数は 4（`ProbabilityOfBacktestOverfitting` の要件＝偶数・2 以上・ブロック数以上）。

### 3. 記録（TradeDecisionService / `Features/TradeDecision/RecordStage0Decisions/`）

- **ルックアヘッド排除を型で担保する**: `AsOfDecisionInput` は日付を持つ入力（日報方針・参照価格・参考情報）を
  すべて `AsOf` で切る。`AsOf` より後の日付を渡すと例外か除外になり、**構造的に未来を渡せない**。
  - 参考情報は `PublishedAt` が `AsOf` 以前のものだけを残す。**日付不明は除外する**（保守側）。
  - 落とした件数は記録へ残す（黙って捨てない）。
- プロンプトは**本番と同じ経路**（`TradeDecisionPromptBuilder.Build`）で組む。
- LLM は `ILlmCompletionClient` を `VoteCount` 回呼ぶ。**用途（purpose）は `trade-decision` のまま**である
  （ADR-0011 の「検証したモデルと本番モデルの一致」を守るため。詳細と費用区分の分離は IADR-0318 決定4）。
- 多数決は本番と同じ `DecisionAggregator`（同数は Hold へ倒す・空も Hold）。
- **費用**: `Stage0RecordingUsageCollector` が記録中の計測を捕まえ、**用途を `stage0-recording` へ付け替えて**
  publish する。月次上限（15,000 円・取引判断サイクル対象）の判定 `LlmCostScope.IsGoverned` は偽になる。
- **承認ゲート**: `Stage0Recording:ApprovedEstimateJpy` と `ApprovedVoteCount` が見積りと一致しない限り実行しない。
  既定は未承認であり、**LLM は 1 回も呼ばれない**。
- **超過停止**: 実績（円）が見積り額を超えたら、その時点で打ち切り、**途中までの記録を保存**して報告する。
- 起動は `Hosted/Stage0RecordingService`（run-once・**既定無効**）。無効でも見積りはログへ出す（ADR-0033 決定5 の「提示」）。

## 受け入れ基準

- [ ] 記録の契約が JSON で往復でき、生の判断が欠落しない
- [ ] 再生戦略が純関数である（同一入力→同一出力・期間外は無発注・記録なし日は無発注）
- [ ] `placeholder` の挙動が #688 から 1 バイトも変わらない（既存テストが緑のまま）
- [ ] `recorded-replay` で記録が整合するときだけ本物の `Stage0GateService` に到達する
- [ ] 記録なし・期間不整合・銘柄不整合・カットオフ未構成・カットオフ不一致のいずれも合格 verdict を出さない
- [ ] 多数決の境界（同数→Hold・N=1・N=3）が本番と同一規則である
- [ ] 費用見積りが境界値で正しく、承認しない限り LLM が 1 回も呼ばれない
- [ ] 見積り超過で停止し、途中までの記録が残る
- [ ] Stage 0 記録の費用が月次上限の区分へ混ざらない

## テスト方針

3 点セット（境界値テーブル・プロパティベース・**否定形**）で書く。統制系（費用の承認ゲート・超過停止・
合格 verdict を出さない条件）は否定形（陽性対照）を必ず置き、変異試験で load-bearing を実測する。

## 計画書との差異

- **多数決の回数・見積り額の承認値は計画に無い**（ADR-0033 決定4・決定5 が「利用者が承認する」と定めた手続き）。
  実装は構成値として受け、**既定は未承認**（実行不能）にする。
- **カットオフ日の値は計画に無い**（`05_trading-assumptions §6.1` への登録が残件）。未設定は未充足へ倒す。
- **1 判断あたりのトークン量の前提が計画に無い**。見積り関数の引数として受け、既定値を発明しない。
