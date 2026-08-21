---
title: LLM 費用の単価をモデル別に解決する（用途別モデル混在への追随）
type: spec
status: accepted
related_ids:
  - FR-04
  - NFR
  - ADR-0011
  - ADR-0014
  - IADR-0055
  - IADR-0114
  - IADR-0122
author: claude
created: 2026-07-31
updated: 2026-07-31
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04: 日報方針とリスク制約の範囲内での売買判断／NFR 費用: 月次上限 15,000 円)
  - planning:projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md (用途別モデル割当・Accepted)
---

# 仕様書: LLM 費用の単価をモデル別に解決する

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#303](https://github.com/endazon/ai-stock-trading/issues/303)
- 機能要求（FR）: FR-04（AI による取引判断＝LLM 呼び出しの発生源）
- 非機能要件（NFR）: 費用（月次上限 ¥15,000 の統制が実態に基づくこと）
- ユースケース（UC）: UC-02（日次の取引判断サイクル）
- 関連 ADR: ADR-0014（用途別モデル割当・Accepted・2026-07-31）、ADR-0011（取引判断のモデル固定）
- 関連 IADR: IADR-0055（LLM 費用計測イベント）、IADR-0114 決定6（現行単価の根拠）、
  IADR-0120（報告書の種別別 purpose）、本作業の決定は IADR-0122

## 目的・背景

`LlmPricing` は **global 単一ペア**（`InputPer1kTokens` / `OutputPer1kTokens`）しか持たない。
2026-07-30 の変更（[microservices-platform#422](https://github.com/endazon/microservices-platform/pull/422)／
MSP IADR-0112、計画側 ADR-0014）で**用途ごとに異なるモデル**が割り当てられたため、単価と実モデルが乖離した。

現行値は opus 基準（¥0.819 / ¥4.093）だが、`trade-decision` は既に `claude-sonnet-5` である。
sonnet-5 の実勢は ¥0.327 / ¥1.637 のため、**約 2.5 倍の過大計上**になっている。
過大計上は月次上限を早く発火させるため安全側ではあるが、統制が実態より早く効いて取引機会を失う。
逆に報告書側（fable-5 は opus の 2 倍単価）は**過小**になる方向で、こちらは危険側である。

構造的な原因は、`PublishingLlmUsageReporter` が DI 時に単価 1 組を受け取るだけで、
**応答が名乗ったモデル（`dto.Model`）を見ていない**ことにある。ゲートウェイは越境ルーティングで
要求と異なるモデルを選び得るため、用途→単価の静的対応では不十分で、**実際に使われたモデル**に基づく
単価解決が要る。

## 対象範囲

- 対象:
  - 応答の実効モデル名（`CompletionApiResponse.Model`）を計上経路まで運ぶ
  - モデル別単価表（`LlmPriceTable`）と、未知モデルの fail-safe
  - 単価の共有配置（`AiStockTrading.Shared.Infrastructure`）— [#282](https://github.com/endazon/ai-stock-trading/issues/282) が再移設なく使えるようにする
  - 経路B（`values-local.yaml`）への単価表投入と、出典・時点・換算率・導入価格の明記
- 対象外:
  - **report-service の費用計上経路そのもの**（[#282](https://github.com/endazon/ai-stock-trading/issues/282)）。本作業は単価解決の器を用意するに留める
  - **実測に基づく月次上限の再ベースライン**（[#243](https://github.com/endazon/ai-stock-trading/issues/243)）。本作業は**構造**、#243 は**実測値**
  - 計画側の月次上限評価（[project-planning#54](https://github.com/endazon/project-planning/issues/54)）
  - 本番 `values.yaml` への単価投入（IADR-0114 決定6 の判断を維持＝変動する外部価格をリポの本番既定に固定しない）
  - `LlmCostIncurred` 契約へのモデル名追加（購読側は金額しか要らないため見送り）
  - SIMULATE / 実弾の閂（IADR-0060 / IADR-0111）には触れない

## 設計

### 1. 実効モデル名の取得経路

基盤の `CompletionApiResponse.Model` は**ゲートウェイが実際に選択したモデル**である
（`microservices-platform` の `Platform.Shared.Contracts/Dtos/CompletionDto.cs`）。
AST は既に部分写像でこれを受け、全量ログ（FR-11 / IADR-0061）に出力している。計上経路だけが見ていない。

```
LlmGateway /complete 応答 (dto.Model)
  → HttpLlmCompletionClient
  → LlmUsage(InputTokens, OutputTokens, Model)      ← Model を追加
  → PublishingLlmUsageReporter
  → LlmPriceTable.Resolve(Model) → LlmPrice
  → LlmPricing.Compute(...) → LlmCostIncurred(amount)
```

`LlmUsage` に `string? Model = null` を追加する（既定 null＝既存の呼び出し・NoOp 経路は非破壊）。

### 2. 単価表の置き場

`LlmPricing` は現在 `TradeDecisionService.Domain` にあるが、サービス間の直接参照は禁止（CLAUDE.md）
のため、#282 で report-service から使えない。**`AiStockTrading.Shared.Infrastructure/Composable/Llm/` へ移設**する。

| 型 | 役割 |
| --- | --- |
| `LlmPrice` | 円/1k トークンの入出力単価ペア（`record struct`） |
| `LlmPriceTable` | モデル名→単価の解決。`Resolve(string? model)` |
| `LlmPricing` | 既存の純関数 `Compute`（移設のみ・ロジック不変） |

`LlmPriceTable.From(entries, fallback)` は**単価を文字列で受ける**。`InvariantCulture` 解析と
fail-safe を 1 箇所へ閉じ込め、共有プロジェクトへ `Microsoft.Extensions.Configuration.Abstractions`
を新規追加せずに済ませるため（構成の読み出しは各サービスの `Program.cs` に残す）。

### 3. 設定形式

```
LlmPricing__PerModel__<model-id>__InputPer1kTokens
LlmPricing__PerModel__<model-id>__OutputPer1kTokens
LlmPricing__InputPer1kTokens   / __OutputPer1kTokens   （既定ペア・従来キー）
```

Kubernetes の env 名は `[-._a-zA-Z][-._a-zA-Z0-9]*` を許容するため、`claude-sonnet-5` のような
ハイフン入りモデル ID をそのままキーにできる。.NET の環境変数プロバイダが `__` を `:` へ写像する。

### 4. 単価表（2026-07 時点・恒久値ではない）

USD→JPY 換算率 **163.71**（システムの為替源 FRED `DEXJPUS` と同一系列・IADR-0107）、小数第 3 位で四捨五入。

| モデル | 公開単価 $/1M（入力/出力） | ¥/1k 入力 | ¥/1k 出力 | 用途（`Llm:Routing:PurposeModels`） |
| --- | --- | --- | --- | --- |
| `claude-fable-5` | 10 / 50 | 1.637 | 8.186 | `report-monthly` |
| `claude-opus-5` | 5 / 25 | 0.819 | 4.093 | `report-weekly`・`default` |
| `claude-opus-4-8` | 5 / 25 | 0.819 | 4.093 | （ADR-0011 が意図する固定先） |
| `claude-sonnet-5` | 2 / 10 ※**2026-08-31 までの導入価格** | 0.327 | 1.637 | **`trade-decision`**・`report-daily` |
| `claude-haiku-4-5` | 1 / 5 | 0.164 | 0.819 | `diagram-coding`（基盤側） |

### 5. fail-safe（過小計上を避ける側へ倒す）

```
Resolve(model):
  1. PerModel に完全一致（大小無視）           → その単価
  2. 一致なし／model が null・空 かつ 表が非空 → 表の成分ごとの最大単価
  3. 表が空                                    → 既定ペア（従来キー・未設定 0）
  * 単価が解析不能・非正の行は表に載せない（→ 2 の未知扱い＝最大単価）。例外は投げない
```

未知モデルを **0 でも既定ペアでもなく最大単価**へ倒すのは、過小計上が危険側だからである。
0 に倒すと月次上限が構造的に効かなくなり、IADR-0114 決定6 が直した問題が未知モデルの形で再発する。
過大計上は統制が実態より早く効くだけで、資金を失う方向ではない。

本番（表が空・既定ペア未設定）では従来どおり ¥0 計上のまま＝**挙動不変**。
金額 0 でも publish して計上経路の健全性を保つ（IADR-0055 根拠）方針も維持する。

### 6. 陳腐化の検知

`claude-sonnet-5` の $2/$10 は **2026-08-31 までの導入価格**であり、為替も変動する。
時限で失敗するテストは無関係な PR の CI を壊すため採らず、
`values-local.yaml` のコメント・本仕様書・IADR-0122 の運用節に再確認期日を明記し、
実測再ベースラインの [#243](https://github.com/endazon/ai-stock-trading/issues/243) に寄せる。

## 受け入れ基準

- [x] 用途／モデルが混在しても、計上額が**実際に使われたモデルの単価**で算出される
      （`HttpLlmCompletionClientTests` が応答の実効モデルの伝播を、`PublishingLlmUsageReporterTests` が単価適用を固定）
- [x] `trade-decision` が `claude-sonnet-5` である現状で、計上額が実勢と一致する（現行の 2.5 倍過大が解消される）
      ＝ in 1000 / out 2000 で **¥3.601**（従来の opus 単価なら ¥9.005）
- [x] `report-*` 3 種別が別モデルへ解決されても、それぞれ正しい単価が引ける（#282 の対応と揃う配置になっている）
      ＝ `Shared.Infrastructure` へ移設済み。fable-5 ¥18.009 / opus-5 ¥9.005 / sonnet-5 ¥3.601 をテストで固定
- [x] 未知モデル・単価未設定は安全側へ倒れ、例外にしない（未知＝最大単価／表が空＝既定ペア／全部未設定＝0）
- [x] `claude-sonnet-5` の 2026-08-31 の導入価格終了に対する更新運用が文書に明記されている
      （`values-local.yaml` コメント／chart README／`docs/operations/operations.md`「LLM 単価の定期見直し」）
- [x] 本番（`values.yaml`）の描画・挙動は不変。SIMULATE / 実弾の閂に変更がない
      （`helm template` の既定描画に `LlmPricing__` が 0 件であることを CI 検査で維持）

## テスト方針

| テスト | 対象 | 内容 |
| --- | --- | --- |
| `LlmPriceTableTests` | 単価解決 | 5 モデルの完全一致 / 大小無視 / 未知→最大 / null・空→最大 / 解析不能・非正の行は未知扱い / 表が空→既定ペア / 全部未設定→0 |
| `LlmPricingTests` | 純関数 | 移設のみ・アサーション不変 |
| `PublishingLlmUsageReporterTests` | 計上額の固定 | `trade-decision`=sonnet-5 で in 1000 / out 2000 → **¥3.601**、`report-monthly`=fable-5 → ¥18.009、未知モデル→fable 単価 |
| `HttpLlmCompletionClientTests` | 実効モデルの伝播 | 応答の `model` が `LlmUsage.Model` に載る |
| `LlmPricingWiringTests` | 構成の写像 | `LlmPricing:PerModel:*` が DI 済みの計上器へ届く |

## 計画書との差異

- 差異: なし。ADR-0014（Accepted）が確定した用途別モデル割当に、計上側を追随させる作業である。
  計画側の月次上限（¥15,000）が新しい割当で妥当かは
  [project-planning#54](https://github.com/endazon/project-planning/issues/54) で評価中であり、本作業は
  「実態に合った金額が積み上がる」ことまでを担う。

## 未決事項

- なし（単価表の値・fail-safe の方針・配置は利用者確認済み）。
