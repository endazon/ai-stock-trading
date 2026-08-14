---
title: IADR-0122 LLM 費用は応答が名乗った実効モデルの単価で計上し、未知モデルは最大単価へ倒す
type: impl-adr
status: Accepted
related_ids:
  - FR-04
  - FR-11
  - NFR
  - ADR-0010
  - ADR-0011
  - ADR-0014
  - IADR-0055
  - IADR-0107
  - IADR-0114
  - IADR-0120
author: claude
created: 2026-07-31
updated: 2026-07-31
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04／NFR 費用: 月次上限 15,000 円)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md (用途別モデル割当・Accepted)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md (取引判断の LLM モデル固定・Accepted)"
---

# IADR-0122: LLM 費用のモデル別単価解決

- 状態: Accepted
- 日付: 2026-07-31
- 決定者: claude（実装）／利用者（単価表の値・fail-safe の方針を指定）

## 起点・関連

- 起点 issue: [#303](https://github.com/endazon/ai-stock-trading/issues/303)
- 仕様書: `docs/specs/20260731_303_per-model-llm-pricing.md`
- 基盤側: [microservices-platform#422](https://github.com/endazon/microservices-platform/pull/422)（MSP/IADR-0112。
  `Llm:Routing:PurposeModels` に `report-monthly`/`report-weekly`/`report-daily` を追加し、`trade-decision` を
  `claude-sonnet-5` へ改定）。計画側は `ADR-0014`（Accepted・2026-07-31・[project-planning#50](https://github.com/endazon/project-planning/issues/50)）。
- 役割の分担（重複起票を避けるための相互参照）:
  - 本 IADR = **構造**（実効モデルに基づく単価解決）
  - [#243](https://github.com/endazon/ai-stock-trading/issues/243) = **実測値**（Opus 5 化に伴う実測と月次上限の再ベースライン）
  - [#282](https://github.com/endazon/ai-stock-trading/issues/282) = **経路**（report-service に費用計上経路が無い）
  - [project-planning#54](https://github.com/endazon/project-planning/issues/54) = **計画**（3 種別で別モデルを使う前提の月次上限の妥当性）
- 前提の更新: IADR-0114 決定6 は「per-model 化は本作業の範囲外」と明記して global 単一ペアを投入した。
  本 IADR はその宿題を解く（決定6 の**単価の出典・換算・経路B 限定という方針は維持**し、粒度だけを変える）。
- **採番の経緯**: 当初 `IADR-0121` で起票したが、並行 PR [#311](https://github.com/endazon/ai-stock-trading/pull/311)
  （`IADR-0121_credential-bearing-uri-log-redaction`）が先に develop へマージされて同番号を確保したため、
  **本 IADR を 0122 へ改番した**（[[IADR-0120]] と同じ事象・同じ解き方）。IADR 番号は develop へマージされた
  時点で確定するため、open な PR が並走すると着手時の「最大+1」では衝突する。先着側を動かすと連鎖するので、
  後着の本 PR 側を動かした。ブランチ名 `feat/IADR-0121-per-model-llm-pricing` は
  IADR-0120（`feat/IADR-0117-...`）の先例に倣い改名していない（PR の同一性を保つため）。

## コンテキストと課題

`LlmPricing` は global 単一ペア（`InputPer1kTokens` / `OutputPer1kTokens`）しか持たず、
`PublishingLlmUsageReporter` は DI 時にその 1 組を受け取るだけで、**応答が名乗ったモデルを見ていない**。

用途別モデル割当（ADR-0014）が確定した結果、単価と実モデルが乖離した。

| 用途 | 実効モデル | 現行値（opus 基準）の誤差 |
| --- | --- | --- |
| `trade-decision` | `claude-sonnet-5` | **約 2.5 倍の過大計上** |
| `report-monthly` | `claude-fable-5` | 約 1/2 の過小計上（危険側・ただし #282 により現状は計上経路そのものが無い） |
| `report-weekly` | `claude-opus-5` | 一致 |
| `report-daily` | `claude-sonnet-5` | 約 2.5 倍の過大計上 |

過大計上は月次上限（¥15,000）を早く発火させるため安全側だが、統制が実態より早く効いて取引機会を失う。
過小計上は上限を素通りさせるため危険側である。

さらに、ゲートウェイは越境ルーティング（ADR-0010）で**要求と異なるモデル**を選び得る。
AST 側で「用途→単価」を静的に対応付けても実際の呼び出しとずれ得るため、
基準にできるのは**応答が名乗ったモデル**だけである（IADR-0111 と同じ考え方＝モデル名はゲートウェイの報告値のみを根拠とする）。

## 検討した選択肢

| # | 案 | 実態への追随 | 影響範囲 | 評価 |
| --- | --- | --- | --- | --- |
| 1 | **応答の `Model` から単価を引く**（モデル別単価表） | ○ 越境ルーティングにも追随 | 中（計上経路と設定） | **採用** |
| 2 | 用途ごとの単価を静的に持つ | △ 実ルーティングとずれ得る | 小 | 次善。ずれたときに検知できない |
| 3 | 費用計算を基盤（LlmGateway）側へ寄せ、AST は金額を受け取る | ◎ 責務としては最も筋が良い | 大（契約変更・全呼び出し側） | 見送り。#303 の射程を超える |

## 決定

### 決定1: 実効モデル名を計上経路まで運ぶ

`LlmUsage` に `string? Model` を追加し、`HttpLlmCompletionClient` が `CompletionApiResponse.Model`
（＝ゲートウェイが実際に選択したモデル）を渡す。既定 `null` のため既存の呼び出し・NoOp 経路は非破壊。

要求側の `Model`（`Decision:PrimaryModel` / `SecondaryModel`。現状いずれも未設定＝null）は**希望値**であり
計上の根拠にしない。根拠は応答の報告値のみとする。

### 決定2: 単価表は `AiStockTrading.Shared.Infrastructure` に置く

`LlmPricing` を `TradeDecisionService.Domain` から
`AiStockTrading.Shared.Infrastructure/Composable/Llm/` へ移設し、`LlmPrice` / `LlmPriceTable` を新設する。

サービス間の直接参照は禁止（CLAUDE.md）のため、取引判断サービスの Domain に置いたままでは
#282 で report-service から使えない。**単価は 1 つの真実で持つべき値**であり、#282 で二重定義するくらいなら
本作業で共有側へ移す。移設に伴うロジック変更は無く、既存テストのアサーションもそのまま移す。

`LlmPriceTable.From(...)` は単価を**文字列で**受け、`InvariantCulture` 解析と fail-safe を 1 箇所へ閉じ込める。
共有プロジェクトへ構成パッケージを持ち込まないため、構成の読み出し自体は各サービスの `Program.cs` に残す。

### 決定3: 未知モデルは 0 でも既定ペアでもなく「最大単価」へ倒す

```
1. PerModel に完全一致（大小無視）           → その単価
2. 一致なし／model が null・空 かつ 表が非空 → 表の成分ごとの最大単価
3. 表が空                                    → 既定ペア（従来キー・未設定 0）
* 単価が解析不能・非正の行は表に載せない（→ 2 の未知扱い＝最大単価）。例外は投げない
```

「安全側」を **0 に倒す**方向で解釈しないことを明示する。費用統制における危険側は**過小計上**であり、
0 に倒すと月次上限が構造的に効かなくなる（IADR-0114 決定6 が直したのはまさにこの状態で、
未知モデルという形で再発させない）。過大計上は統制が実態より早く効くだけで、資金を失う方向ではない。

例外を投げない点は IADR-0055 の「計測は best-effort＝LLM 応答を壊さない」を維持する。

### 決定4: 単価表は経路B（`values-local.yaml`）にのみ投入する

本番 `values.yaml` へは置かない（IADR-0114 決定6 の判断を維持）。
為替・公開単価は変動し、`claude-sonnet-5` の $2/$10 は **2026-08-31 までの導入価格**である。
変動する外部価格をリポジトリの本番既定に固定すると陳腐化が検出されない。

投入値（USD→JPY 換算率 **163.71**＝FRED `DEXJPUS` と同一系列・IADR-0107、小数第 3 位で四捨五入）:

| モデル | $/1M（入力/出力） | ¥/1k 入力 | ¥/1k 出力 |
| --- | --- | --- | --- |
| `claude-fable-5` | 10 / 50 | 1.637 | 8.186 |
| `claude-opus-5` | 5 / 25 | 0.819 | 4.093 |
| `claude-opus-4-8` | 5 / 25 | 0.819 | 4.093 |
| `claude-sonnet-5` | 2 / 10（導入価格・〜2026-08-31） | 0.327 | 1.637 |
| `claude-haiku-4-5` | 1 / 5 | 0.164 | 0.819 |

表に無いモデル（`claude-sonnet-4-6` / `gpt-5` 等。現行の用途割当では選ばれない）は決定3 により
最大単価（fable-5）で計上される。**意図した過大計上**であり、必要になった時点で行を足す。

### 決定5: 陳腐化の検知は時限テストではなく文書と #243 で行う

期日を過ぎると失敗するテストは、無関係な PR の CI を落とすため採らない。
`values-local.yaml` のコメント・作業仕様書・本 IADR に **2026-08-31 の再確認期日**と
出典・時点・換算率を明記し、実測再ベースラインの #243 に寄せる。

## 理由

- 越境ルーティングを持つ以上、単価の根拠にできるのは応答の報告値だけである（決定1）。
- 単価は費用統制の入力であり、サービスごとに別の表を持つと不整合が統制の穴になる（決定2）。
- 「安全側＝0」は費用統制では逆であり、明文化しないと将来の変更で 0 へ倒される（決定3）。
- 外部価格は変動する。リポジトリの本番既定に固定した瞬間、更新されないまま参照され続ける（決定4・決定5）。

## 結果

- 良い影響:
  - `trade-decision` の計上額が実勢（sonnet-5）と一致し、約 2.5 倍の過大計上が解消される。
    月次上限の 80% / 100% 判定が実態に基づくようになる。
  - #282 で report-service に計上経路を足す際、単価解決は共有側をそのまま使える（再移設が不要）。
  - 未知モデルが 0 円で素通りしなくなる。
- 悪い影響・トレードオフ:
  - 設定項目が増える（モデル数 × 2）。経路B のみで、本番の描画は不変。
  - 表に無いモデルは最大単価で過大計上される（意図した安全側）。
  - `LlmPricing` の名前空間が変わる（`AiStockTrading.TradeDecision.Domain` →
    `AiStockTrading.Shared.Infrastructure.Composable.Llm`）。参照箇所は計上経路のみ。
- フォローアップ:
  - **2026-08-31**: `claude-sonnet-5` の導入価格終了。単価と換算率を再確認する（#243 と併せて）。
  - #282 の対応時に report-service へ同じ解決経路を配線する。
  - `LlmCostIncurred` にモデル名を載せて台帳をモデル別に集計する案は見送った（購読側は金額しか要らない）。
    モデル別の実消費を見たくなったら #243 の実測と併せて再検討する。

## 関連

- Supersedes: なし（IADR-0114 決定6 の粒度のみを更新する。出典・換算・経路B 限定という方針は維持）
- Superseded by: なし
