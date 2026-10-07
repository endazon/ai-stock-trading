---
title: Stage 0 の記録器を本番と同じ二段（スクリーニング→本判断）にし、両層の実効モデルを記録し、両層の組での合格を実弾解禁の前提列挙へ入れる（#1196）
type: spec
status: accepted
related_ids: [FR-15, FR-20, FR-04, FR-05, NFR, ADR-0054, ADR-0033, ADR-0014, ADR-0011, ADR-0017, ADR-0036, IADR-0318, IADR-0216, IADR-0212, IADR-0215, IADR-0039, IADR-0248, IADR-0313, IADR-0387, IADR-0111, IADR-0056, IADR-0498]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0054_two-layer-model-assignment-screening-haiku-and-stage0-both-layers.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md
---

# 仕様書: Stage 0 の記録器の二段化・両層の実効モデル・実弾解禁の前提列挙（#1196）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-15**（バックテスト＝Stage 0 の必須ゲート）・**FR-20**（段階ゲート）・FR-04（AI 判断）・FR-05（発注）
- 計画 ADR: **ADR-0054 決定 3・4・フォローアップ 1・2**（2026-10-07 Accepted）・ADR-0033（Stage 0 の評価対象は記録した AI 判断）・
  ADR-0014 決定 3（モデル変更時の Stage 0 再検証を実弾解禁の必須ゲートとする）・ADR-0011（ピン留め）・ADR-0017 決定 2（フォールバック禁止）・
  ADR-0036 決定 1（外した範囲を残す）
- 関連 IADR: IADR-0318（Stage 0 の記録・再生）・IADR-0216（実効モデルの一致による取引判断のフォールバック禁止）・IADR-0212（用途は呼び出しごと）・
  IADR-0039（二段オーケストレーション）・IADR-0248（解析不能と見送りの区別）・IADR-0313（スクリーニング入力の予算）・IADR-0387（as-of 入力の除外）・
  IADR-0111 / IADR-0056（閂 0 と解禁前提）。**新規 IADR-0498**（本件の設計判断）
- 起票: [#1196](https://github.com/endazon/ai-stock-trading/issues/1196)（第 4 回全体監査 A-1）。#204 の C-8（2026-09-02）の未反映分を含む
- ブランチ: `feat/FR-15-1196-stage0-two-tier`
- 基点コミット: `origin/develop` `ac8095be`（計画 ADR レンジ `ADR-0001..0055` の引き直し後）
- 範囲外: #1209（記録器へ最小約定代金・同日再エントリーの統制を再現する。PR #1191 が未マージ）。本件では触らないが、
  後から差し込めるよう「判断 → 記録」の経路（`RecordOneAsync`）の形は保つ

## 目的・背景

計画 ADR-0054 決定 3 は「Stage 0 は本番と同じ二段（`trade-decision-screening` → `trade-decision`）で走らせ、
両層の組（`claude-haiku-4-5` ＋ `claude-sonnet-5`）での通過を実弾解禁の必須ゲートにする」と定め、決定 4 で
「現行の記録器は本判断しか呼ばない。直るまで実弾を解禁しない」と自認した。実装側には対応が無く、
解禁前提の列挙（`LiveTradingGate`・cutover runbook）にも Stage 0 の項目が無い（#204 C-8 の指摘）。

## 調査（基点コミット）

| 事実 | 出典 |
| --- | --- |
| 記録器は `LlmPurposes.TradeDecision` だけを `VoteCount` 回呼ぶ（スクリーニング段が無い） | `Features/TradeDecision/RecordStage0Decisions/Stage0DecisionRecorder.cs:291-298` |
| 記録の `ModelId` は構成の希望値（`options.Model`）1 つだけで、応答が名乗った実効モデルを持たない | 同 `:331`・`Shared.Contracts/Backtest/Stage0DecisionRecord.cs:79-93` |
| 本番の二段は `DecisionOrchestrator.DecideAsync`（一次は `ParseScreening` で方向だけ読み、関心なし・解析不能なら二次を呼ばない） | `Features/TradeDecision/DecideTrade/DecisionOrchestrator.cs:30-114` |
| 本番の一次プロンプトは、予算（`ScreeningContextBudgetChars`。構成既定 150,000）があれば `ScreeningContextAssembler` で参考情報を縮退して載せ、無ければ参考情報なし | `TradeDecisionAppService.cs:444-466`・`DecisionOptionsLoader.cs:18-88` |
| 実効モデルは `HttpLlmCompletionClient` が `LlmUsage.Model` として計測へ渡す。割当外なら本文を捨てて Hold を返し、`TradeDecisionSkipped`（用途つき）を publish する | `HttpLlmCompletionClient.cs:191-232`・`:276-290` |
| 記録中の計測は `Stage0RecordingUsageCollector` が捕まえる（元の用途・実効モデルのまま。publish は `stage0-recording` へ付け替え） | `Stage0RecordingUsageCollector.cs:52-61` |
| 解禁前提の列挙は 3 点（リスク統制・監査・上限／Vault／Reserved 滞留）。Stage 0 が無い | `OrderExecutionService/Infrastructure/ExternalServices/LiveTradingGate.cs:34-40` |
| 同じ列挙の写しが 2 か所ある（閂 3 の例外文・Helm の外周の fail） | `MoomooBrokerOptions.cs:118-121`・`deploy/helm/ai-stock-trading/templates/deployment.yaml:15` |
| runbook の前提表は 9 行で Stage 0（モデル再検証）の行が無い | `docs/operations/live-trading-cutover-runbook.md:107-117` |
| 記録の永続化は JSON ファイル（`FileStage0DecisionRecordSink` / `FileStage0DecisionRecordSource`）。**DB スキーマは無い → EF マイグレーション不要** | `Stage0DecisionRecordJson.cs` |
| フォローアップ 2: 日報のスキップ回数は `TradeDecisionSkipped` の全件数（用途で絞らない）。スクリーニングで割当外・モデル不可の見送りは `effectivePurpose`＝`trade-decision-screening` で publish される → **含む** | `HttpLlmCompletionClient.cs:139-147, 214-232, 278-290`・`ReportService/Infrastructure/ExternalServices/HttpLlmUsageRecordSource.cs:106-125`（用途の絞り込み無し）・`ReportService/Domain/LlmUsageRecord.cs:192`・既存試験 `HttpLlmCompletionClientFallbackBanTests.スクリーニング層もピン以外なら見送る` |

### 母集合の引き直し（規則 9・10）

誤りの側の文字列で全文書を走査した（`git grep`、`.ai-context/specs` は凍結記録のため除外）。

| 走査語 | ヒット | 扱い |
| --- | --- | --- |
| `発注予約 Reserved 滞留の自動リコンサイル`（解禁前提の列挙の写し） | `LiveTradingGate.cs:38`・`MoomooBrokerOptions.cs:121`・`templates/deployment.yaml:15` | **3 か所とも Stage 0 の項目を足す**（1 か所だけ直すと読む面ごとに前提が違う） |
| 解禁前提の人が読む表 | `docs/operations/live-trading-cutover-runbook.md` の前提表 | **行 #10 を足す**。`operations.md` の 13 項目は OpenD 本番切替の表であり Stage 0 の項目の置き場ではない（runbook の #1・#8・#9 も同表に無い）ので除外 |
| `記録器`（docs） | `docs/blocked-tasks.md` B-7・`docs/functional/FR-15_backtest.md`・`docs/tests/FR-15_backtest-tests.md` | 機能仕様書の「評価しない条件」と除外の節へ 2 行足す。テスト仕様書へ T-15-115〜を足す。B-7 は「記録の実行に承認と実 LLM が要る」の記述で本件により誤りにならない（見積りの式が変わるだけ）→ 再測定手順に一次の項を足す |
| `本判断だけ`・`本判断しか` | 0 件（コード・docs） | — |
| `new Stage0DecisionRecord(`・`Stage0RawDecision(`（構築箇所） | 記録器 1・試験 6 ファイル | 試験の記録はすべて「一次なし（旧記録）」になる → 評価へ進む肯定形の試験の記録へ一次と実効モデルを足す |
| `Stage0Recording__*`（構成） | Helm values（`LlmTrainingCutoff`・`ApprovedEstimateJpy`） | 新しい構成キー（一次のモデル・トークン量）は既存のトークン量と同じく values へ置かない（既定は記録の実行不能） |

**この変更で新たに誤りになる自分の記述**（規則 10）: IADR-0318 決定 4「記録の LLM 呼び出しは `LlmPurposes.TradeDecision` を名乗る」・
決定 5「見積り＝銘柄×営業日×1 日回数×多数決」・決定 1「戦略 ID のハッシュ」の 3 点。本文は凍結のため日付つき追記で改める。
`Stage0DecisionRecorder` の型コメント（判断経路の列挙）・`Stage0RecordingBudget` の式のコメントも改める。

## 設計

### 1. 記録器は本番の `DecisionOrchestrator` をそのまま使う（複製しない）

- `RecordOneAsync` は本番と同じ入力で一次・二次のプロンプトを組み、**`DecisionOrchestrator` に順序を任せる**
  （`EnableScreening=true` 固定・`VoteCount`＝承認した多数決回数・一次モデル＝`ScreeningModel`・二次モデル＝`Model`）。
  一次で関心なし（Hold）・解析不能なら二次を呼ばない —— 本番の分岐そのものである。
- 各呼び出しの層・出力・実効モデル・トークン量は、オーケストレータへ渡す `ILlmCompletionClient` を記録器の内側で包んで捕まえる
  （呼び出しの直後に計測を切り出す）。生の判断は捕まえた出力を本番と同じ解析器（`ParseScreening` / `ParseDetailed`）で読み直す（決定的）。
  多数決の結果はオーケストレータの返り値を使う。
- 一次のプロンプトの形は本番の構成（`DecisionOrchestrationOptions.ScreeningContextBudgetChars`）に従う（予算があれば縮退込み）。
  記録器はそのために本番の `DecisionOrchestrationOptions` を受け取る（DI の単一の値）。ニュースの状態は二次と同じく「不明」（as-of で再構成しない）。
- 費用は一次・二次の全呼び出しの合計（見積り超過の停止判定も同じ）。

### 2. 記録の契約に両層の実効モデルを持たせる（後方互換）

- `Stage0RawDecision` に `EffectiveModelId`（既定 null）を足す（二次の各票）。
- `Stage0ScreeningDecision`（行動・解析不能・根拠・トークン量・`EffectiveModelId`）を新設し、`Stage0DecisionRecord.Screening`（既定 null）に持つ。
- **null は「一次を記録していない」（二段化より前の記録）である。** JSON で欠落すれば null へ復元される。
- 実効モデルは捕まえた計測の `Model`（応答が名乗った値）。計測が無い（送信できなかった等）・複数の値が混ざるときは null（＝不明）。
- 戦略 ID のハッシュは、一次を持つ記録だけ一次と各票の実効モデルを含める（旧記録の戦略 ID を変えない）。

### 3. 再生側の扱い

| 条件 | 扱い |
| --- | --- |
| 記録集合に一次の無い記録が 1 件でもある | 🔴 **評価不能**。新しい `Stage0GateCheck.ScreeningNotRecorded` で判定器を呼ばない（合格にも 7 条件の判定にも数えない）。除外件数は「不明（一次を記録していない）」 |
| 一次の実効モデルが `trade-decision-screening` のピンと違う、または二次のどれかの票が `trade-decision` のピンと違う（不明を含む） | その判断を**判定母集団から外す**（as-of 入力の除外と同じ経路。数量を持てば `ExcludedDecisionAltersReplayPath` で判定を組まない）。件数は除外の集計に「実効モデル不一致」として別に載せる |

照合の基準は `LlmAssignmentEvaluator`（`LlmAssignments`）であり、構成の希望値ではない。

### 4. 解禁前提の列挙

`LiveTradingGate` の例外文と型コメント・閂 3 の例外文・Helm の fail・runbook の表へ
「両層の組（スクリーニング `claude-haiku-4-5` ＋ 本判断 `claude-sonnet-5`）での Stage 0 合格」を足す。
**閂 0（`const false`）は変えない。**

### 5. 見積り（ADR-0033 決定 5）

`呼び出し回数 ＝ 判断時点数 ×（一次 1 ＋ 多数決回数）`（全件が一次を通る＝過大側）。一次は
`ScreeningInputTokensPerDecision` / `ScreeningOutputTokensPerDecision` と一次モデルの単価（未指定なら `LlmAssignments` のピン）で円換算する。
**トークン量の既定値は発明しない**（未設定は 0）。見積りの値が変わるため、承認値は引き直しになる（既定は未承認のまま）。

### 変えないもの

判断の順序・多数決（同数・空は Hold）・カットオフ窓（`values.yaml` の `2026-01-31`）・閂 0〜4・本番の判断経路・費用の付け替え。

## 受け入れ基準と試験（T-ID は develop の最大 T-15-114 の次から。開いている PR #1191・#1205 は T-15 を使っていない）

| ID | 受け入れ基準 | 内容 |
| --- | --- | --- |
| T-15-115 | 1 | 一次 → 二次の順に呼び、一次が Hold なら二次を呼ばない（呼び出しの用途の並びで確かめる）。一次の解析不能も打ち切る |
| T-15-116 | 1 | 一次のプロンプトは本番と同じ組み立て（予算あり＝縮退込み）で、二次は従来どおり。費用は両層の合計 |
| T-15-117 | 2 | 記録に一次の実効モデルと二次の各票の実効モデルが別々に残る（応答が名乗った値。希望値ではない） |
| T-15-118 | 2 | 再生: 一次か二次のどちらかの実効モデルがピンと違う（不明を含む）判断は母集団から外れ、件数が「実効モデル不一致」として残る |
| T-15-119 | 5 | 再生: 一次の無い記録（旧記録）を含む記録集合は評価不能（`ScreeningNotRecorded`）で判定器を呼ばず、除外件数も名乗らない |
| T-15-120 | 2・5 | 契約: 一次・実効モデルは JSON 往復で落ちず、欠けた旧 JSON は null（評価不能）へ復元される。旧記録の戦略 ID は変わらない |
| T-15-121 | — | 見積りは一次の呼び出し・単価を含む（判断時点 ×（1 ＋ 多数決））。実行せずに取得できる |
| T-15-122 | 3 | 解禁前提の列挙（閂 0・閂 3 の例外文）に両層の組での Stage 0 合格が並ぶ。閂 0 は未解禁のまま |

受け入れ基準 4 は IADR-0318 の日付つき追記（フォローアップ 2 の確認結果）で満たす（試験ではなく記録）。

## 変異（各受け入れ基準のキー分岐）

M1 一次を呼ばない（`EnableScreening=false`）／M2 一次 Hold でも二次を呼ぶ／M3 実効モデルを片方の層しか残さない／
M4 実効モデル不一致を母集団から外さない／M5 旧記録を評価へ通す（評価不能にしない）／M6 戦略 ID が一次を含まない／M7 解禁前提から Stage 0 を外す。
結果は PR 本文と IADR-0498 に記録する。

## 配備

取引判断サービス（記録器。既定は実行不能のまま）・バックテストサービス（再生）・注文執行サービス（例外文のみ）。
旧形式の記録ファイルを使っていた評価は「評価不能」へ変わる（記録を採り直すまで合格は出ない＝計画 ADR-0054 決定 4 の暫定手段どおり）。
