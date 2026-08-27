---
title: 作業仕様書 — claude-sonnet-5 単価の正式確認と期限付き懸念の解消
type: work
status: review
related_ids: [NFR-13, IADR-0122]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04／NFR 費用: 月次上限 15,000 円)
related_specs:
  - ../adr/IADR-0122_per-model-llm-pricing.md
  - ./20260731_303_per-model-llm-pricing.md
---

# 作業仕様書: claude-sonnet-5 単価の正式確認（#243 期限分・2026-08-31）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR-13**「LLM API 費用」の自動統制〈月次上限〉が使う計上単価の鮮度確認）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: 実装 IADR-0122（LLM 費用のモデル別単価解決）決定4・決定5
- 起点 issue: [#243](https://github.com/endazon/ai-stock-trading/issues/243)（コメント 2026-07-31。
  PR #314・IADR-0122 決定5 が本 issue を陳腐化検知の受け皿と定めた）

## 目的・背景

`values-local.yaml` の `LlmPricing__PerModel__claude-sonnet-5__*` は 2026-07 時点の Anthropic 公開単価
（$2/$10 per 1M トークン）を USD→JPY 163.71（FRED `DEXJPUS`・IADR-0107 と同一換算方針）で換算した値
（入力 `0.327`／出力 `1.637` 円/1k トークン）である。この単価は「2026-08-31 までの導入価格」と明記されており、
**本日 2026-08-28 時点で期限まで 3 日**であるため、2026-09-01 以降に適用される正式単価を確認し、
必要なら値を更新するタスクが issue #243 に立っている。

過小計上（本来より安い単価を使い続けること）は月次費用上限（¥15,000）の 80%／100% 判定を実態より遅く
発火させる**費用統制の危険側**であるため、期限内の確認が必要と判断された。

## ★ 母集合（規則 5・7。引いた結果と除外理由をここに書く）

**「`LlmPricing` の設定箇所」を機械的に走査した。**

```
$ grep -rln "LlmPricing" --include=* . | grep -v "/bin/\|/obj/\|\.git/"
```

結果（ビルド成果物 `bin/`/`obj/` 配下を除く）:

| # | ファイル | 内容 | 本作業の対象 |
| --- | --- | --- | --- |
| 1 | `deploy/helm/ai-stock-trading/values-local.yaml` | 経路B（dev）の実投入値・コメント | **対象**（コメントの期限記述を更新） |
| 2 | `deploy/helm/ai-stock-trading/README.md` | 単価表・恒久値ではない旨の説明 | **対象** |
| 3 | `.github/workflows/helm.yml` | values-local.yaml の lint/デプロイ配線 | 対象外（単価の値そのものは持たない） |
| 4 | `backend/Services/TradeDecisionService/.../HttpLlmCompletionClient.cs` | `purpose` 設定（単価ではなくモデル用途割当） | 対象外（本件と無関係） |
| 5 | `backend/Services/TradeDecisionService/.../PublishingLlmUsageReporter.cs` | 単価表を読んで計上する実装コード | 対象外（構造は IADR-0122 で確定済み。値の変更のみが本作業） |
| 6 | `backend/Shared/AiStockTrading.Shared.Infrastructure/Composable/Llm/LlmPricing.cs` / `LlmPrice.cs` | 単価表のドメイン型 | 対象外（同上） |
| 7 | `backend/Shared/AiStockTrading.Shared.Infrastructure.Tests/Llm/LlmPricingTests.cs` | 単価解決ロジックのテスト（具体的な円単価はテストフィクスチャ内の任意値） | 対象外（`values-local.yaml` の実値を検証するテストではない。実測で確認済み） |
| 8 | `backend/Services/TradeDecisionService/tests/TradeDecisionService.Api.Tests/LlmPricingWiringTests.cs` | DI 配線のテスト | 対象外（同上） |
| 9 | `docs/operations/operations.md`「LLM 単価の定期見直し」 | 見直し契機の運用表 | **対象**（`claude-sonnet-5` の行を更新） |
| 10 | `.ai-context/adr/IADR-0122_per-model-llm-pricing.md` | 決定4・決定5・フォローアップ | **対象**（凍結記録のため日付つき追記ブロックで追補。本文は書き換えない） |
| 11 | `.ai-context/specs/20260731_303_per-model-llm-pricing.md` | #303 の作業仕様書（point-in-time） | 対象外（point-in-time の記録であり、当時の記述を書き換えない。`.claude/rules/traceability.repo.md` の凍結規約と同じ扱い） |
| 12 | `.ai-context/adr/IADR-0114_route-b-parity-observed-drawdown-and-official-sources.md` | 決定6 で global 単一ペアを投入した経緯（本件より前の IADR） | 対象外（IADR-0122 が既に決定6 の粒度を更新済みで、本件は値の鮮度確認のみ） |

## 単価確認の手順と結果

### 1. `claude-api` スキルを読み込んだ

キャッシュされた価格表（2026-06-24 時点）は `claude-sonnet-5`: 入力 $2.00/1M・出力 $10.00/1M。
ただし「2026-08-31 までの導入価格」という期限つきの性質はキャッシュ表には現れないため、
公式ページで確認した。

### 2. Anthropic 公式ドキュメントを確認した（2026-08-28）

`https://platform.claude.com/docs/en/about-claude/pricing`（Model pricing テーブル）:

- `claude-sonnet-5`: Base Input Tokens **$2 / MTok**、Output Tokens **$10 / MTok**（変更なし）
- 同ページの注記（id `claude-sonnet-5-introductory-pricing`）:

  > The $2/$10 per million input/output token pricing for Claude Sonnet 5, announced at launch as
  > introductory pricing through August 31, 2026, is now the standard price. The previously scheduled
  > increase to $3/$15 per million input/output tokens on September 1, 2026 will not occur.

**結論**: Anthropic は 2026-08-31 までの「導入価格」を 2026-09-01 以降も**恒久化**すると公式に発表した。
2026-09-01 に予定されていた $3/$15 への改定は**実施されない**。したがって `claude-sonnet-5` の
USD 単価は変わらず、**円換算値（`0.327` / `1.637`）も変更不要**である。

### 3. 補足で一般公開情報を確認した（参考。一次情報ではないため単価の根拠には用いていない）

`WebSearch` の結果、Anthropic 公式アカウント（`@claudeai`）の 2026-08-10 の投稿および複数の技術系ブログが
同じ内容（恒久化・$3/$15 への改定の撤回）を報じており、公式ドキュメントの記載と整合していた。

## 対象範囲

- 対象:
  - `values-local.yaml` のコメント（「2026-08-31 までの導入価格・要再確認」という**期限つきの警告**を、
    「恒久化を確認済み」という記述へ更新する）。**単価の数値そのものは変更しない**（USD 単価が変わって
    いないため）。
  - `deploy/helm/ai-stock-trading/README.md` の単価表・恒久値ではない旨の説明を同様に更新する。
  - `docs/operations/operations.md`「LLM 単価の定期見直し」の該当行を、解消済みとして更新する。
  - `.ai-context/adr/IADR-0122_per-model-llm-pricing.md` 決定4・決定5・フォローアップへ、日付つき追記
    ブロック（`traceability.repo.md` の凍結規約に倣う書式）で確認結果を追補する（本文は書き換えない）。
- 対象外:
  - 単価の数値変更（USD 単価が変わっていないため不要）。
  - 為替レート（163.71）の見直し（本作業のスコープは `claude-sonnet-5` の USD 単価の期限確認のみ。
    為替の乖離は別契機として `operations.md` の表に残す）。
  - #243 の残る本来スコープ（Opus 実運用での出力トークン実測・月次上限の消費見積り再評価）は
    **未着手のまま**とする（今回の対応は同 issue 内の「単価確認・期限分」のみ）。issue 本文にその旨を
    コメントで残すかは判断保留とする（本作業はコード・設定変更を伴うため、issue へのコメントは
    必須要件ではない。必要なら別途 `/plan-feedback` 相当ではなく通常コメントで報告する）。

## 設計

### 更新方針

**数値は変更しない。記述（コメント・表・注記）だけを「期限つきの懸念」から「恒久化確認済み」へ
更新する。** 換算方法（USD→JPY 163.71・小数第 3 位四捨五入）は変更しないため、値の再計算は不要。

### 変更箇所別の方針

1. `values-local.yaml`: `⚠️` の警告コメントを `✅` の確認済みコメントへ差し替え、出典 URL・確認日を明記する。
2. `README.md`: 表の `※導入価格` 注記を外し（恒久価格になったため）、「恒久値ではない」節の
   `claude-sonnet-5` に関する記述を「確認済み・変更なし」へ更新する。**為替・他モデルの単価は今後も
   変動し得るため、「恒久値ではない」という見出し自体は残す**（`claude-sonnet-5` の期限だけが解消した
   のであって、他の懸念が消えたわけではない）。
3. `operations.md`: 「見直し契機」表の `claude-sonnet-5` の行を、解消済みである旨と確認日・出典へ更新する。
4. `IADR-0122`: Accepted の凍結記録であるため本文を書き換えず、決定4・決定5 の直後に
   `［2026-08-28 追記 / #243］` 形式の追記ブロックを置く（`.claude/rules/traceability.repo.md`
   「Superseded / Deprecated な ADR を引用するときの書式」が定める日付つき追記の作法を、
   決定を覆さない事実確認の追記にも援用する）。フォローアップの該当項目も追記で解消済みとする。

## 受け入れ基準

- [x] `claude-sonnet-5` の 2026-09-01 以降の正式単価を一次情報（Anthropic 公式ドキュメント）で確認した
- [x] 確認した単価と現行投入値が一致することを確認し、数値は変更していない
- [x] `values-local.yaml` / `README.md` / `operations.md` / `IADR-0122` の期限つき警告を、確認済みの記述へ更新した
- [x] `IADR-0122` は本文を書き換えず、日付つき追記ブロックで追補した
- [x] コード変更を伴わないため `.NET` のビルド・テストへの影響はない（確認のみ実施）

## テスト方針

本作業はドキュメント・Helm values のコメント更新のみであり、単価の数値・実装コードは変更しない。
`LlmPricingTests` / `LlmPricingWiringTests` は既存のまま green であることを確認する
（回帰が無いことの確認であり、新規テストは追加しない）。

## 計画書との差異

- 差異: なし。

## 未決事項

1. 為替レート（163.71）・他モデルの公開単価は本作業の対象外であり、`operations.md` の見直し契機表に
   引き続き残る。
2. #243 の本来スコープ（Opus 実運用での出力トークン実測・月次上限の消費見積り再評価）は未着手のまま
   issue に残る。
