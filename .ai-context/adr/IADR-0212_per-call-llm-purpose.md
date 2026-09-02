---
title: IADR-0212 LLM の用途（purpose）を呼び出しごとの引数にし、二段判断の層を配線で区別する
type: impl-adr
status: Accepted
related_ids: [FR-04, UC-01, NFR, ADR-0014, ADR-0017]
author: 実装エージェント（w1b）
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# IADR-0212: LLM の用途（purpose）を呼び出しごとの引数にし、二段判断の層を配線で区別する

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: 実装エージェント（w1b）／#335・#347

## 起点・関連

- 関連する計画書 ID: FR-04・UC-01・NFR（費用） / ADR-0014 §決定1・ADR-0017 決定1・決定2 / 01_architecture-overview §判断の二段化
- 関連する実装ADR: [IADR-0215](IADR-0215_llm-assignment-table-as-verification-source.md)（割当表）・[IADR-0216](IADR-0216_trade-decision-fallback-ban-enforcement.md)（フォールバック禁止の強制）・[IADR-0218](IADR-0218_llm-cost-scope-by-purpose.md)（費用の対象範囲）・[IADR-0039](IADR-0039_decision-orchestration.md)（二段オーケストレーション）
- 関連する実装仕様書: [20260828 作業仕様書](../specs/20260828_335_347_llm-allocation-and-cost-governance.md)

## コンテキストと課題

[IADR-0215](IADR-0215_llm-assignment-table-as-verification-source.md) が用途別の割当表を、
[IADR-0216](IADR-0216_trade-decision-fallback-ban-enforcement.md) がその表による実効モデルの照合を、
[IADR-0218](IADR-0218_llm-cost-scope-by-purpose.md) が用途による費用の対象範囲判別を導入した。
いずれも **`purpose` を入力に取る**。表は二段判断の層を分けて持っている。

```
trade-decision            → claude-sonnet-5   （フォールバック禁止）
trade-decision-screening  → claude-haiku-4-5  （フォールバック禁止）
```

**しかし実行時の配線では、この 2 つを区別できなかった。** 実測した状態は次のとおりである。

1. `ILlmCompletionClient.CompleteAsync(prompt, model, cancellationToken)` に **purpose を渡す引数が無い**。
2. `TradeDecisionService.Api/Program.cs` は `ILlmCompletionClient` と `ILlmUsageReporter` を
   `LlmGateway:Purpose ?? trade-decision` で**固定して 1 インスタンスだけ**登録していた。
3. `DecisionOrchestrator` はその 1 インスタンスを一次・二次の**両方**で使い、変えていたのは
   `options.PrimaryModel` / `options.SecondaryModel` という**希望値だけ**だった。

希望値は判定に使われない。割当照合（`LlmAssignmentEvaluator.Evaluate(purpose, dto.Model)`）も
基盤 `LlmRouter` のモデル解決も費用の計上区分も、**すべて purpose の側で引かれる**。

### 帰結（これが是正の理由）

`Decision:EnableScreening=true` にすると、一次スクリーニングの呼び出しが `trade-decision` を名乗る。
基盤はその用途に `claude-sonnet-5` を割り当てているため、

- **軽量モデルによる絞り込みという費用統制が成立しない**（一次が本判断と同じモデルへ着地する）。
- 一次に軽量モデルが割り当たった場合は、その応答が本判断の割当（sonnet-5 ピン留め・フォールバック禁止）と
  照合されて**必ず「割当外」と判定され、全サイクルが見送りへ倒れる**。
- 費用計上イベント `LlmCostIncurred` の `Purpose` も両層とも `trade-decision` になり、
  **層別の内訳が取れない**（金額は合うため、症状は内訳の欠落だけで台帳を読むまで気づけない）。

安全側には倒れるが、**スクリーニング機能そのものが成立しない**。
現状は `EnableScreening` の既定が false かつ基盤側が `trade-decision-screening` 未登録のため潜伏していた。

### 🔴 なぜテストが緑だったか（本 ADR の再発防止の的）

`HttpLlmCompletionClientFallbackBanTests` は「スクリーニング層もピン以外なら見送る」を緑にしていた。
しかしそれは **purpose をコンストラクタへ直接渡して**組んだクライアントであり、
**本番の配線では作れない状態**だった。テストは実在しない配線を検証していたのである。
アダプタ単体の粒度では、この種の退行は原理的に捕まらない。

## 検討した選択肢

| # | 案 | 層を区別できるか | Application 層の依存 | 備考 |
| --- | --- | --- | --- | --- |
| 1 | 現状維持 | ❌ | — | スクリーニングが成立しない |
| 2 | keyed DI（`AddKeyedScoped` ＋ `[FromKeyedServices]`）で 2 インスタンス登録 | ✅ | ❌ **DI 属性が Application 層へ入る** | リポジトリ内に前例 0 件。`DecisionOrchestrator` / `TradeDecisionService` は `ILlmCompletionClient` を 1 つ受ける形で DI 解決されており、2 つ目を足すと解決が曖昧になる。属性で解くと層の依存規律（Domain / Application の外部依存ゼロ）を崩す |
| 3 | Application 層に選択子（`ILlmCompletionClientSelector` 等）を新設 | ✅ | ⭕ | 抽象が 1 段増え、`TradeDecisionService` の引数と全テストの fake が入れ替わる。**計画外の抽象化**にあたる |
| 4 | **`purpose` を `CompleteAsync` の引数にし、呼び出しごとに渡す** | ✅ | ⭕ | 変更は署名 1 つと呼び出し 2 箇所。**報告書側（`HttpReportNarrativeDrafter`）が同じ問題を同じ形で解決済み**（IADR-0120 決定1） |

## 決定

**選択肢 4 を採る。** keyed DI は導入しない。

### 決定 1: `purpose` は呼び出しごとの引数にする

`ILlmCompletionClient.CompleteAsync(prompt, model, purpose, cancellationToken)`。
`DecisionOrchestrator` が層に応じて `LlmPurposes.TradeDecisionScreening` / `LlmPurposes.TradeDecision` を渡す。

**用途キーは構成で可変にしない。** ADR-0017 決定1 と 01_architecture-overview が確定させた統制値であり、
運用でずらせる形にすると、ずらした先で割当統制が無音で外れる。

### 決定 2: 用途の解決は egress の 1 箇所に閉じる

`HttpLlmCompletionClient` が `構成の明示上書き（LlmGateway:Purpose） → 呼び出し側の申告 → 安全既定` の順で解決し、
**送信・割当照合・見送りの記録・費用計上のすべてがその 1 値を使う**。

- 構成上書きを残すのは、`LlmGateway__Purpose` を設定済みのデプロイを壊さないためである（報告書側と同じ扱い）。
- 安全既定を `trade-decision` にするのは、**費用上限の対象内**かつ**最も厳しい割当統制**が掛かる側だからである。
  用途不明の呼び出しを対象外・統制外へ倒さない（過小計上を作らない既定・IADR-0218 と同じ向き）。

### 決定 3: 費用計上の用途は計測ごとに受け取る（`LlmUsage.Purpose`）

`LlmUsage` に `Purpose` を**必須の先頭位置引数**として持たせ、`PublishingLlmUsageReporter` から
コンストラクタ引数の purpose を**削除**した。

- **省略可にしない。** 既定値を置くと載せ忘れが静かに通り、`LlmCostIncurred` の対象範囲判別（IADR-0218）が
  誤った区分で積まれる。**書かなければコンパイルが通らない**形にする。
- **計上側が purpose を決めてはならない。** 決めてよいのは用途を知っている egress だけである。
- 報告書サービスの `ILlmUsageReporter` は既に同じ形（`LlmUsage(string Purpose, ...)`）であり、**姉妹実装に揃えた**。

### 決定 4: 🔴 再発防止は composition root を起こすテストに置く

`TradeDecisionService.Api.Tests/LlmPurposeWiringTests.cs` を新設した。
`Program.cs` の DI 登録を実際に起こし、**スクリーニング呼び出しが `trade-decision-screening`、
本判断呼び出しが `trade-decision` でゲートウェイへ届くこと**を、送信された要求本文で観測する。

- スタブは**実ゲートウェイと同じく purpose からモデルを解決して名乗る**（未登録の用途では `DefaultModel` へ
  無音で落ちる挙動・platform IADR-0102 を模す）。したがって用途を取り違えれば**割当外と判定されて
  一次で打ち切られる**という本番の帰結がそのまま再現される。
- 費用計上イベントの `Purpose` も同じ 1 サイクルから観測する（②割当統制と③費用区分は別の事実である）。
- **アダプタのコンストラクタを直接叩くテストは、この種の退行を捕まえられない**ことが実証されている
  （前掲「なぜテストが緑だったか」）。配線の検証は配線を起こしてしか行えない。

## 理由

- ADR-0017 決定1 と 01_architecture-overview が層別の割当を確定させている以上、
  **実装は層を区別できなければならない**。区別できない配線は、計画の割当表を書いたが**適用していない**状態である。
- 選択肢 4 は、層の識別を「DI の構造」ではなく「呼び出しの引数」に置く。二段判断は
  **同一オーケストレータ内の連続した 2 呼び出し**であり、識別の粒度は呼び出しであってインスタンスではない。
- keyed DI は本問題に対して過大である。前例 0 件の DI 機構を入れるのに対し、得られるのは同じ区別だけで、
  **Application 層へ DI 属性を持ち込む代償**が付く。

## 結果

- 良い影響:
  - スクリーニング層が**設計どおり軽量モデルで動く**（費用統制が成立する）。
  - `LlmCostIncurred` が層別の用途で積まれ、月次台帳で一次・二次の内訳が読める。
  - 用途の解決点が 1 箇所（`HttpLlmCompletionClient.ResolvePurpose`）に集約され、送信値と判定値がずれない。
- 悪い影響・トレードオフ:
  - `ILlmCompletionClient` の署名が変わり、テストの fake 6 箇所が追随した。**破壊的変更だが本リポジトリ内に閉じる**。
  - `LlmUsage` の必須引数化により既存の計測呼び出しが全て書き換わった（意図的。決定 3 参照）。
  - **基盤に `trade-decision-screening` が未登録のあいだは、スクリーニングを有効にすると見送りが続く**。
    これは [IADR-0216](IADR-0216_trade-decision-fallback-ban-enforcement.md) が挙げたトレードオフと同一であり、
    本 ADR で新たに生じるものではない（本 ADR はそれを**正しい用途で**検知できるようにした）。
- フォローアップ: 基盤側の `Llm:Routing:PurposeModels` への `trade-decision-screening` 登録は
  microservices-platform の担当であり、本リポジトリからは検知する側に立つ（IADR-0215 と同じ立場）。

> **［2026-09-02 改訂・#571］** 上記フォローアップと「悪い影響・トレードオフ」の `trade-decision-screening`
> 未登録の記述について経過を追記する（本文は書き換えない）。
>
> - microservices-platform 側で `trade-decision-screening`（`claude-haiku-4-5`）の登録 PR が進行し
>   （AST#571・別 worker 担当）、登録が完了次第「潜伏していた」状態は解消する。
> - AST 側は本追記と同日、[IADR-0272](IADR-0272_enable-screening-default-and-config-loader-baseline.md) により
>   `Decision:EnableScreening` の**構成既定**を false → true へ反転した（`DecisionOptionsLoader` の
>   構成読み取りベースラインのみ。`DecisionOrchestrationOptions.Default` レコード自体は本 IADR が導入した
>   層別配線の単体テスト基準値として不変のまま残す）。
> - **現行の既定値は IADR-0272 を正とする。** 本 IADR の決定1〜4（purpose の呼び出しごと引数化・egress
>   1 箇所での解決・`LlmUsage.Purpose` の必須化・composition root テスト）はいずれも改定しない。

## 関連

- Supersedes: なし（[IADR-0039](IADR-0039_decision-orchestration.md) の二段構成・[IADR-0216](IADR-0216_trade-decision-fallback-ban-enforcement.md) の見送り判定は不変。**判定へ渡す用途を層別にしただけ**である）
- Superseded by: なし
