---
title: IADR-0498 Stage 0 の記録器は本番の DecisionOrchestrator をそのまま使って二段（スクリーニング → 本判断）で記録し、両層の実効モデルを別々に残す。再生は一次の無い記録を評価不能とし、実効モデルがピンと違う判断を判定母集団から外す。両層の組での Stage 0 合格を実弾解禁の前提列挙へ足す
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-20, FR-04, FR-05, NFR, ADR-0054, ADR-0033, ADR-0014, ADR-0011, ADR-0017, ADR-0036, IADR-0318, IADR-0216, IADR-0212, IADR-0215, IADR-0039, IADR-0248, IADR-0313, IADR-0387, IADR-0281, IADR-0111, IADR-0056, IADR-0060]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0054_two-layer-model-assignment-screening-haiku-and-stage0-both-layers.md (決定 3・4、フォローアップ 1・2)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 1・2・5)
  - planning:projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md (決定 3)
related_specs:
  - ../specs/20261007_1196_stage0-two-tier-recording.md
---

# IADR-0498: Stage 0 を本番と同じ二段で記録し、両層の組での合格を実弾解禁の前提にする（#1196）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-07
- 決定者: Claude Code（実装）。起点 [#1196](https://github.com/endazon/ai-stock-trading/issues/1196)（第 4 回全体監査 A-1。#204 の C-8 の未反映分を含む）

## 起点・関連

- 関連する計画書 ID: FR-15（Stage 0）・FR-20（段階ゲート）・FR-04（AI 判断）・FR-05（発注・閂）
- 計画 ADR: **ADR-0054 決定 3・4**（Stage 0 は本番と同じ二段で評価し、両層の組での通過を実弾解禁の必須ゲートにする。記録器が二段になるまで実弾を解禁しない）・
  ADR-0033（評価対象は記録した AI 判断）・ADR-0014 決定 3（モデル変更時の Stage 0 再検証を実弾解禁の必須ゲートとする）・ADR-0011 / ADR-0017 決定 2（ピン留め・フォールバック禁止）・ADR-0036 決定 1（外した範囲を残す）
- 作業仕様書: [`20261007_1196_stage0-two-tier-recording`](../specs/20261007_1196_stage0-two-tier-recording.md)（実測・母集合・試験・変異）
- 改める実装判断: [IADR-0318](./IADR-0318_stage0-ai-decision-record-and-replay.md) 決定 1（記録の契約・戦略 ID のハッシュ）・決定 4（記録の呼び出しは `trade-decision` だけ）・決定 5（見積りの式）。同 IADR へ日付つき追記を置く
- 変えない前提: [IADR-0039](./IADR-0039_decision-orchestration.md)（二段の順序と多数決）・[IADR-0212](./IADR-0212_per-call-llm-purpose.md)（用途は呼び出しごと）・
  [IADR-0216](./IADR-0216_trade-decision-fallback-ban-enforcement.md)（実効モデルの不一致は見送り）・[IADR-0387](./IADR-0387_asof-input-reconstructability-and-population-exclusion.md)（除外の経路と `ExcludedDecisionAltersReplayPath`）・
  [IADR-0111](./IADR-0111_broker-tier-selection.md)（閂 0 は `const false`）

## コンテキスト

計画 ADR-0054（2026-10-07 Accepted）は、スクリーニング（`trade-decision-screening`＝`claude-haiku-4-5`）と本判断（`trade-decision`＝`claude-sonnet-5`）の
層別の割当を ADR に記録し、決定 3 で「Stage 0 は本番と同じ二段を通した判断を評価する。実弾解禁の必須ゲートは両層の組での Stage 0 の通過」と定めた。
決定 4 は「Stage 0 の記録器は本判断しか呼ばない（実現手段は無い）。直るまで実弾を解禁しない」と自認した。

実測（develop `ac8095be`）:

- 記録器（`Stage0DecisionRecorder`）は `trade-decision` だけを全銘柄に多数決回数ぶん呼んでいた。**評価していたのは「スクリーニングを通さず本判断で全銘柄を判断した系」**であり、本番の系ではない。
- 記録の `ModelId` は構成の希望値 1 つだけで、**応答が名乗った実効モデルを層ごとに持たない**。別モデルが答えた判断（IADR-0216 で Hold に倒れる）と、ピンが答えた判断を記録から区別できない。
- 実弾解禁の前提の列挙（閂 0 の例外文・閂 3 の例外文・Helm の外周の fail・cutover runbook の表）のどれにも Stage 0 の項目が無い（#204 C-8・2026-09-02）。

## 検討した選択肢

| # | 二段の実装 | 評価 |
| --- | --- | --- |
| A | **本番の `DecisionOrchestrator` をそのまま使い、渡す LLM 客を包んで呼び出しごとの出力・実効モデルを捕まえる** | 順序・打ち切り・多数決の実装が 1 本のまま。本番の分岐が変われば記録も同じに変わる。**採用** |
| B | 記録器の中で一次 → 二次の分岐を書き直す | 判断経路が 2 本になり、IADR-0318 が避けた「検証したものと本番で走るものがずれ始める」を二段の分岐で作り直す |
| C | `DecisionOrchestrator` に観測用のコールバックを足す | 本番の型に記録の都合の口が増える。A で足りる |

| # | 一次の無い旧記録の扱い | 評価 |
| --- | --- | --- |
| P1 | **記録集合ごと評価不能**（新しい `Stage0GateCheck.ScreeningNotRecorded`。判定器を呼ばない） | 旧記録は別の系を測っている。合格にも、7 条件の判定による不合格にも数えない。**採用** |
| P2 | 判断単位で母集団から外す | 旧記録は全件が一次を持たないため、結局 `AllDecisionsExcluded` になり「範囲を狭めて外した」と読める。理由が別の値で残らない |
| P3 | 旧記録を「一次を通過した」とみなす | 本判断だけの系の成績を二段の系の合格として使うことになる（ADR-0054 決定 3 が禁じたこと） |

| # | 実効モデルがピンと違う判断 | 評価 |
| --- | --- | --- |
| Q1 | **その判断を判定母集団から外す**（as-of 入力の除外と同じ経路。件数を別に残す） | 受け入れ基準 2「その判断は合否から除かれる」そのもの。数量を持てば既存の `ExcludedDecisionAltersReplayPath` で判定を組まない。**採用** |
| Q2 | 記録集合ごと評価不能 | 1 件の一過性の割当外で全体が評価できなくなる。計画は「その判断」を除くと定めた |
| Q3 | 記録器で記録しない | 外した範囲が記録から読めなくなる（ADR-0036 決定 1「外す」は「走らせない」ではない） |

## 決定

### 決定 1: 記録器は本番の `DecisionOrchestrator` で二段を走らせる

- `RecordOneAsync` は、本番の構成（DI の `DecisionOrchestrationOptions`）を引き継ぎ、`EnableScreening=true`（**本番の構成によらず必ず二段**）・
  `VoteCount`＝承認した多数決回数・一次モデル＝`Stage0Recording:ScreeningModel`・二次モデル＝`Stage0Recording:Model` に上書きした
  オーケストレータへ判断を委ねる。**一次で関心なし（Hold）・解析不能なら本判断を呼ばない**（本番の分岐そのもの）。
- 一次のプロンプトは本番の一次の枝と同じ組み立て（`TradeDecisionPromptBuilder.BuildScreening`。本番の構成に予算 `ScreeningContextBudgetChars` があれば
  `ScreeningContextAssembler` の縮退込み）。保有なし・未約定なし・ニュースの状態は不明（二次と同じ。as-of で再構成しない）。
- オーケストレータへ渡す LLM 客を記録器の内側で包み、**呼び出しの直後にその 1 回の計測を切り出して**層（用途）・出力・実効モデル・トークン量を結びつける。
  挙動は変えない。生の判断は捕まえた出力を本番と同じ解析器（`ParseScreening` / `ParseDetailed`）で読み直す（決定的）。多数決はオーケストレータの結果を使う。
- 費用は両層の全呼び出しの合計。費用の付け替え（`stage0-recording`）は一次にも掛かる（IADR-0318 決定 4 の仕組みのまま）。

### 決定 2: 記録の契約に一次と両層の実効モデルを持たせる（後方互換）

- `Stage0ScreeningDecision`（行動・解析不能・根拠・トークン量・`EffectiveModelId`）を新設し、`Stage0DecisionRecord.Screening` に持つ。
  `Stage0RawDecision.EffectiveModelId`（二次の各票）を足す。どちらも既定 null。
- 🔴 **`Screening == null` は「一次を記録していない」**（二段化より前の記録）であり、「一次を通過した」ではない（`AsOfInputs == null` と同じ fail-closed の向き）。
- 実効モデルは捕まえた計測の `Model`（応答が名乗った値）。計測が無い・値が割れる・空なら null（**不明**）。構成の希望値で埋めない。
- 戦略 ID のハッシュは、**一次を持つ記録だけ**一次（行動・解析不能・実効モデル）と各票の実効モデルを含める。旧記録の戦略 ID は変わらない（既存の固定値の試験が守る）。

### 決定 3: 再生は旧記録を評価不能とし、実効モデルがピンと違う判断を外す

- 記録集合に一次の無い記録が **1 件でも**あれば `Stage0GateCheck.ScreeningNotRecorded` で判定器を呼ばない。verdict は不合格固定の経路
  （`Stage0DriverVerdict.RecordingUnusable`）を通り、除外件数は `Unknown(ScreeningNotRecorded)` で名乗らない。
  **これは「判定を走らせていない」側の値であり、7 条件の判定による不合格ではない**（`NoDecisionRecords` と同じ扱い）。`Passed=true` は出ない。
- 一次の実効モデルが `trade-decision-screening` のピン、かつ二次の全票が `trade-decision` のピンと一致しない判断は、`RecordedDecisionReplayStrategy` が注文を写さず母集団から外す。
  照合は `LlmAssignmentEvaluator`（`LlmAssignments`）で行い、構成の希望値とは照合しない。**不明（null）は一致と読まない。**一次で見送った判断（票 0）は一次だけを見る。
- 件数は `Stage0ExclusionSummary.Counted.ModelMismatch`（`Excluded` の内数）として verdict の表示（`Format()`）へ載る。数量を持つ判断を外せば既存の
  `ExcludedDecisionAltersReplayPath`、全件を外せば `AllDecisionsExcluded` で判定を組まない（IADR-0387 の遮断をそのまま使う）。

### 決定 4: 両層の組での Stage 0 合格を解禁前提の列挙へ足す（閂は変えない）

- `LiveTradingGate.StageZeroTwoTierPrerequisite`（告知文の単一の値）を閂 0 の例外文と閂 3（`MoomooBrokerOptions.EnsureSimulate`）の例外文が使う。
  Helm の外周の fail と cutover runbook の前提表（行 #10）にも同じ項目を並べる。**閂 0 の `LiveTradingReleased = false` は 1 文字も変えない。**

### 決定 5: 見積りは判断時点ごとに一次 1 回を足す（過大側）

- `呼び出し回数 ＝ 判断時点数 ×（一次 1 ＋ 多数決回数）`。一次は `Stage0Recording:ScreeningInputTokensPerDecision` / `ScreeningOutputTokensPerDecision` と一次の層の単価
  （希望値が無ければ一次のピン）で円換算する。全件が一次を通ると見るので見積りは過大側に寄り、実績は見積り以下になる。**トークン量の既定値は発明しない**（既定 0）。
- 見積りの式が変わるため、既存の承認値（`ApprovedEstimateJpy`）は一致しなくなり、承認し直すまで記録は実行されない（既定は未承認のままなので配備上の差は無い）。

## 結果・影響

- 計画 ADR-0054 決定 4 の表の「Stage 0 は両層の組で評価する」の現在の実現手段が「無い」から「ある（本 IADR）」になる。**実弾解禁は引き続き閉じている**
  （閂 0 は const、記録の実供給と承認も未了）。
- 旧形式の記録ファイルで回していた評価は、記録を採り直すまで「評価不能（`ScreeningNotRecorded`）」になる。合格は出ない（ADR-0054 決定 4 の暫定手段どおり）。
- 二段化で 1 判断の呼び出しは「一次 1 ＋ 多数決回数」になり、一次で見送った判断は本判断の費用が掛からない。

### 残余リスク

- **判定母集団の外し方は判断単位である。** 一次がピン外で Hold に倒れた判断（IADR-0216）は数量 0 なので外しても経路は歪まないが、二次の一部の票だけがピン外で
  多数決が Buy/Sell になった判断は数量を持つため `ExcludedDecisionAltersReplayPath` で判定全体が止まる（意図どおりの fail-closed。記録を採り直す）。
- **`BacktestEvaluated`（Risk への契約）には「実効モデル不一致」の件数を別の欄で載せていない。** 合計（`ExcludedDecisionCount`）には含まれ、
  内訳は verdict の表示とログにだけ出る（契約の拡張は別件にする。母集団の分母は従来どおり読める）。
- 記録中に一次・二次で割当外が起きると、`HttpLlmCompletionClient` が `TradeDecisionSkipped` を publish する（用途は付け替えない）。日報・月報のスキップ回数には
  Stage 0 の記録中の見送りも数えられる（本判断の層では従来からの挙動。一次の層が加わる）。記録は承認制の run-once であり頻度は低い。
- #1209（最小約定代金・同日再エントリーの統制の再現）は範囲外。記録の判断 → 数量の経路（`SignedQuantity`）の形は保ってあり、後から差し込める。

## 変異で確かめたこと

各受け入れ基準のキー分岐を 1 か所ずつ壊し、対応する試験が赤になることを確かめた（戻して緑）。

| 変異 | 結果 |
| --- | --- |
| M1 記録の二段を無効化（`EnableScreening=false`） | 記録器の試験 52 件 赤 |
| M2 一次 Hold でも本判断へ進む（オーケストレータの打ち切りを外す） | T-15-115 の 3 件 赤 |
| M3a/b/c 一次の実効モデルを捨てる／本判断の実効モデルを捨てる／一次に希望値を入れる | T-15-117 の 2〜3 件 赤 |
| M4 実効モデル不一致を母集団から外さない | T-15-118 の 6 件 赤 |
| M4b 照合が一次の実効モデルを見ない | T-15-120 の 3 件 赤 |
| M5 旧記録を評価へ通す（`ScreeningNotRecorded` を載せない）／一次の有無の判定を常に真にする | T-15-119 の 2 件／T-15-120 の 1 件 赤 |
| M6 戦略 ID が一次を含まない／旧記録の戦略 ID を変える | T-15-120 の 1 件／2 件（既存の固定値の試験を含む）赤 |
| M7 閂 0 の告知から Stage 0 を外す／閂 3 の告知から外す | T-15-122 の 1 件／5 件 赤 |
| M8 見積りが一次を数えない | 見積り・記録器の試験 62 件 赤 |

## 関連

- Supersedes: なし（[IADR-0318](./IADR-0318_stage0-ai-decision-record-and-replay.md) の決定 1・4・5 を部分的に改める。同 IADR の他の決定は有効）
- Superseded by: なし
