---
title: IADR-0272 Decision:EnableScreening の構成既定を反転し、反転範囲を構成ローダーに限定する
type: impl-adr
status: Accepted
related_ids: [FR-04, UC-01, NFR, ADR-0014, ADR-0017]
author: 実装エージェント（worker）
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md
---

# IADR-0272: `Decision:EnableScreening` の構成既定を反転し、反転範囲を構成ローダーに限定する

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: 実装エージェント（worker）／AST#571

## 起点・関連

- 関連する計画書 ID: FR-04・UC-01・NFR（費用） / ADR-0014 §決定1・ADR-0017 決定1・決定2
- 関連する実装ADR: [IADR-0212](IADR-0212_per-call-llm-purpose.md)（purpose 呼び出しごと引数化・二段判断層別化）・
  [IADR-0215](IADR-0215_llm-assignment-table-as-verification-source.md)（割当表）・
  [IADR-0216](IADR-0216_trade-decision-fallback-ban-enforcement.md)（フォールバック禁止の強制）・
  [IADR-0039](IADR-0039_decision-orchestration.md)（二段オーケストレーション）
- 関連する実装仕様書: [20260902 作業仕様書](../specs/20260902_571_enable-screening-default.md)
- 起点 issue: AST#571（#335 の受け皿。基盤側 `trade-decision-screening` 未登録を解消する 2 issue のうち AST 側）

## コンテキストと課題

[IADR-0212](IADR-0212_per-call-llm-purpose.md) は二段判断の層別 purpose 配線を実現したが、基盤
（microservices-platform）の `Llm:Routing:PurposeModels` に `trade-decision-screening` が未登録のあいだは
`Decision:EnableScreening=true` にしても機能しない（一次が割当外と判定され全サイクル見送り）。このため
`EnableScreening` の既定は false のまま据え置かれていた（IADR-0212 §帰結）。

AST#571 は基盤側登録（microservices-platform 側 PR・別 worker 担当）とセットで、AST 側は基盤登録を前提に
**構成未設定時の既定を true へ反転**することを求めている。

### 実装の現状（着手前の確認）

- `DecisionOrchestrationOptions`（レコード）: `EnableScreening` のプロパティ既定は `false`（`bool` の既定値）。
  `Default` 静的プロパティはこのレコードの素の `new()` であり、`VoteCount=1, EnableScreening=false` を表す。
- `DecisionOrchestrationOptions.Default` は 2 つの独立した文脈で使われている。
  1. `DecisionOptionsLoader.FromConfiguration` の構成未設定時のベースライン（**本番の既定値**）
  2. `DecisionOrchestratorTests` / `TradeDecisionAppService` の「options 未指定」テスト構築子の便宜値
     （**「単発判断」を検証する単体テストの基準ケース**。十数箇所で使用）
- (2) は `EnableScreening` の構成既定とは無関係の関心事（二段オーケストレーションの機構そのものの検証）である。

## 検討した選択肢

| # | 案 | 内容 | 影響範囲 |
| --- | --- | --- | --- |
| 1 | `DecisionOrchestrationOptions.EnableScreening` プロパティの既定値を `true` にする（レコード定義側） | `public bool EnableScreening { get; init; } = true;` | `Default` を使う全箇所（本番既定＋(2) の単体テスト十数箇所）が同時に変わる。**「単発判断」の基準ケースが二段判断の基準ケースへ意味を変える** |
| 2 | `DecisionOrchestrationOptions.Default` 静的プロパティの初期化式を変える | `new() { EnableScreening = true }` | 選択肢 1 と同じ影響範囲（`Default` を参照する箇所は区別できない） |
| 3 | **`DecisionOptionsLoader.FromConfiguration` のベースラインだけを `Default with { EnableScreening = true }` にする** | 構成未設定時の**本番既定**だけが変わる。`Default` レコード自体・(2) の単体テストは無改修 | 変更が構成読み取りの 1 箇所に閉じる。issue が要求する「構成キーの既定反転」と一致する |

## 決定

**選択肢 3 を採る。**

### 決定 1: `DecisionOrchestrationOptions.Default` レコードは変更しない

`Default`（`VoteCount=1, EnableScreening=false`）は**二段オーケストレーション機構そのものの単体テスト基準値**
として、issue #571 の対象外に据え置く。`DecisionOrchestratorTests.既定は単発判断と等価_1回だけ二次プロンプトを
呼ぶ`・`ScreeningContextDegradationTests` 等、`Default` を使う既存テストは無改修のまま緑を維持する。

### 決定 2: 構成既定の反転は `DecisionOptionsLoader.FromConfiguration` のベースライン式に限定する

```csharp
var options = DecisionOrchestrationOptions.Default with { EnableScreening = true };
```

構成 `Decision:EnableScreening` が有効な `bool` 文字列で与えられればそちらが優先される（既存の上書きロジックは
不変）。`Decision:EnableScreening=false` を明示すれば無効化できる fail-safe な上書き経路を維持する。

### 決定 3: 反転は基盤側登録（AST#571 の対）を前提とし、単独では機能を有効化しない

本 IADR は AST 側の構成既定だけを扱う。**基盤（microservices-platform）の `trade-decision-screening` 登録が
別途完了していなければ**、`EnableScreening=true` は IADR-0212 が挙げた「安全側だが機能しない」帰結（全サイクル
見送り）に倒れるだけであり、危険側（誤発注等）には倒れない。反転そのものは安全である。

### 決定 4: helm values に明示行を追加する（挙動は変えない）

`deploy/helm/ai-stock-trading/values.yaml` / `values-local.yaml` の `trade-decision.extraEnv` に
`Decision__EnableScreening: "true"` を明示追加する。構成未設定でも既定は true になるため挙動は変わらないが、
**費用が発生する LLM 呼び出しが 1 段増える変更を helm 側でも可視化する**（IADR-0017 決定4 の可観測性の思想に
倣う）。

## 理由

- issue #571 が要求しているのは「構成キー `Decision:EnableScreening` の既定値」であり、`DecisionOrchestrationOptions`
  レコード自体の意味を変えることではない。選択肢 1・2 はレコードの既定値を変えるため、**本来無関係な単体テスト
  （二段オーケストレーション機構の検証）まで影響範囲に巻き込む**。
- 選択肢 3 は変更を構成読み取りの 1 関数に閉じ込め、`LlmPurposeWiringTests`（composition root を実際に起こし
  `Decision:EnableScreening=true` を明示設定するテスト）・`DecisionOrchestratorTests`・
  `ScreeningContextDegradationTests` のいずれも無改修のまま既存の緑を保つ。
- 基盤側登録が未完了でも安全側（見送り）にしか倒れないため、AST 側の反転を基盤側 PR のマージ前に先行させても
  実害は無い（IADR-0212 と同じ安全性の裏付け）。

## 結果

- 良い影響:
  - 本番の既定挙動が「基盤登録後は二段判断が有効」という意図した状態になる。
  - `DecisionOrchestrationOptions.Default` を使う既存の単体テスト（二段オーケストレーション機構の検証）が
    無改修のまま維持され、変更の意味が構成既定の反転だけに閉じる。
  - helm values の明示行により、費用に影響する既定変更が運用設定からも読める。
- 悪い影響・トレードオフ:
  - `DecisionOrchestrationOptions.Default`（レコードの素の既定値）と `DecisionOptionsLoader` の構成既定
    （実質的な本番既定）が**乖離した状態が恒久化する**。次にこのオプションを読むエンジニアは「`Default` を見ても
    本番の既定は分からない」ことに注意する必要がある（本 IADR とコード内コメントで明示する）。
  - **基盤側 PR（microservices-platform）がマージ・反映されるまでは、本反転は「見送りが増える」以外の
    観測可能な効果を持たない。** 二段判断が実際に機能したことの確認は、基盤側反映後に別セッションが行う
    （作業仕様書 §確認手順）。
- フォローアップ: 基盤側 `trade-decision-screening` 登録完了後、AST の定時サイクル（develop 再デプロイ後）で
  一次スクリーニング（`claude-haiku-4-5`）→ 二次本判断（`claude-sonnet-5`）の 2 段呼び出しが実際に発生することを
  ログ・`LlmCostIncurred` の purpose 別内訳で確認する（作業仕様書参照）。

## 関連

- Supersedes: なし（[IADR-0212](IADR-0212_per-call-llm-purpose.md) の決定1〜4 は不変。本 IADR は同 IADR が
  「フォローアップ」に残した基盤登録待ちの解消を扱う部分改定であり、[IADR-0212](IADR-0212_per-call-llm-purpose.md)
  へ日付付き改訂節を追記した）
- Superseded by: なし
