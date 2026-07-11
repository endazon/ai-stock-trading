---
title: IADR-0037 取引判断の多数決はドメイン純関数・二段（一次スクリーニング→二次多数決）はアプリのオーケストレータ・モデル選択はポート引数でゲートウェイへ委譲
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-11, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# IADR-0037: 取引判断の多数決・二段オーケストレーションの構成

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-04（AI 判断・根拠記録）、FR-11（入出力・根拠の記録）、ADR-0003（方針階層＋独立リスク管理・不確実なら Hold）
- 技術検討: `06_technical/01_architecture-overview.md` L128（構造化出力＋同一入力複数回実行＋多数決で安定化）／L129（軽量スクリーニング→高性能本判断の二段化で費用統制）／L34（モデル選択・二段判断はゲートウェイ構成）
- 対象 Issue: [#11](https://github.com/endazon/ai-stock-trading/issues/11)（残スライスあり・クローズしない）
- 関連する実装仕様書: [20260711_decision-orchestration](../specs/20260711_decision-orchestration.md)
- 関連 IADR: [IADR-0017](IADR-0017_trade-decision-structure.md)（多数決・二段を「対象外（後続）」と明記＝本 IADR がその後続）

## コンテキストと課題

LLM の売買判断は非決定的で、同一入力でも出力が揺れる（`06_daytrading-review.md` §3.1 の実証）。計画書は非決定性対策として
「同一入力の複数回実行＋多数決」（L128）と、費用統制として「軽量モデルのスクリーニング→高性能モデルの本判断」の二段化（L129）を
要求する。IADR-0017（Slice A）はこの 2 つを「対象外（後続）」とし単発判断のみ実装した。本 IADR はその後続として、多数決・二段の
**オーケストレーションロジック**を、実 LLM 不在でも CI で決定的に検証できる形で実装する構成を決める。論点は 3 つ:

1. 多数決の集約をどこに置き、タイ・数値をどう扱うか。
2. 二段判断（一次→二次）をどう構成し、費用統制（二次スキップ）をどう実現するか。
3. 一次=軽量／二次=高性能のモデル選択をどう表現するか（L34「ゲートウェイ構成」との整合）。

## 検討した選択肢

1. **多数決を判断サービス内にインラインで実装** — 純関数として切り出さないと単体検証しづらく、責務が肥大化する。
2. **多数決＝ドメイン純関数＋二段＝アプリのオーケストレータ。モデル選択はポート引数でゲートウェイへ委譲**（採用）。
3. **モデル選択をサービス側で解決（モデル別クライアントを複数注入）** — L34「モデル選択はゲートウェイ構成」に反し、サービスが
   モデル実体に結合する。

## 決定

選択肢 2 を採用する。

- **多数決＝ドメイン純関数**: `DecisionAggregator.Aggregate(IReadOnlyList<LlmDecision>) : DecisionVoteResult`。
  - 最多得票の `TradeAction` を採り、**同数タイ・空入力は安全側 `Hold`**（ADR-0003「不確実なら取引しない」）。
  - 数値（`ReferencePrice`/`StopLossDistancePerShare`/`Rationale`）は、勝利票の中から**参照価格の下側中央値を持つ代表票を
    一体で採用**する。合成値を作らず、実在する 1 票の根拠（FR-11）を保つ。決定的順序でソートし中央を選ぶ（同値でも再現的）。
  - 監査用に `TotalVotes`／`AgreementVotes`（勝利 action の得票数）を返す（FR-11）。
- **二段＝アプリのオーケストレータ**: `DecisionOrchestrator.DecideAsync(screeningPrompt, decisionPrompt)`。
  - 一次スクリーニング（`EnableScreening` 時）: 軽量モデルで 1 回判断。`Hold` なら**二次を呼ばず打ち切り**（費用統制）。
  - 二次本判断: 高性能モデルで `VoteCount` 回実行し、各出力を `TradeDecisionParser.Parse` → `DecisionAggregator.Aggregate`。
  - 集約結果と票数・スクリーニング可否を `OrchestratedDecision` で返し、判断サービスが FR-11 ログに残す。
- **モデル選択＝ポート引数**: `ILlmCompletionClient.CompleteAsync(prompt, model?, ct)` に**モデル識別子の任意引数**を足す
  （後方互換の既定 `null`）。一次=`PrimaryModel`／二次=`SecondaryModel` をゲートウェイへ渡すのみで、**実モデル解決はゲートウェイの
  後続作業**とする（L34 と整合）。
- **既定は現行挙動と等価**: `DecisionOrchestrationOptions.Default`＝`VoteCount=1`・`EnableScreening=false`・モデル未指定。
  判断サービスはオプション未注入なら `Default` を使い、単発判断（IADR-0017）と完全に等価に振る舞う（回帰なし）。
- **スクリーニングのスキーマ再利用**: `BuildScreening` は本判断と同じ JSON スキーマを出力させ、`TradeDecisionParser` を共有する
  （絞り込みは「`Hold` なら見送り」で表現）。新スキーマ・新パーサを増やさない。

## 理由

- 多数決を純関数に切り出すことで、fake なしで全ケース（タイ・割れ・空・数値集約）を決定的に単体検証でき、責務が明確。
- タイ・空を `Hold` に倒すのは ADR-0003 の「不確実なら取引しない」を集約段でも貫くため。代表票の一体採用は、合成した参照価格と
  損切り幅が実在しない組み合わせ（下流の損切り価格が権威データ・IADR-0035）になる事故を避け、FR-11 の根拠追跡を保つ。
- 二段をアプリのオーケストレータに置き、一次 `Hold` で二次をスキップすることが費用統制（L129・#23/#79 と連動）の実体。
- モデル選択をポート引数に留めゲートウェイへ委譲するのは L34 の方針どおりで、サービスをモデル実体から切り離す。
- 既定を現行等価にすることで、実 LLM・費用パラメータが確定するまで安全に無効化でき、既存テストの回帰を避けられる。

## 結果

- 良い影響: 実 LLM 不在でも多数決・二段のオーケストレーションを CI で緑に検証。非決定性（票割れ）とスクリーニング打ち切りを
  fake で再現。既定オフで安全に段階導入できる。
- 悪い影響・トレードオフ: `VoteCount` 回の LLM 呼び出しは費用・レイテンシを増やす（費用統制 #23/#79 と併せて回数を調整）。
  二段のスクリーニングは 1 回分の追加費用（ただし二次スキップで回収）。モデル選択は識別子の受け渡しのみで、実効はゲートウェイ実装に依存。
  中央値の代表票採用は「多数決の中の 1 票」を選ぶため、票が僅差で割れると採用票が変わり得る（決定的だが票構成に敏感）。
- フォローアップ: 実 LLM クライアント（`/complete`）＋ゲートウェイのモデル解決、`VoteCount`・二段しきい値の実値決定（#23/#79）、
  複数銘柄の一括スクリーニング（絞り込み）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0017](IADR-0017_trade-decision-structure.md)（本 IADR がその「後続（多数決・二段）」）、[IADR-0035](IADR-0035_stop-loss-authoritative.md)（損切り価格が権威データ＝代表票一体採用の根拠）
