---
title: 表に無いモデルの計上単価を専用の設定値（既定 $10 / $50）で持ち、単価表から claude-fable-5 の行を外す（#1305）
type: spec
status: accepted
related_ids: [NFR-13, FR-04, ADR-0064, ADR-0037, IADR-0553, IADR-0122, IADR-0499, IADR-0524, IADR-0550, IADR-0055]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0064_llm-assignment-to-5-5-family-and-stage0-rerun.md（決定 6 の表・決定 7・決定 9・実装の現状 R10）
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§6.1 の換算 163.71 円/USD）
  - planning#783（2026-10-10 のコメントの裁定 5）
---

# 表に無いモデルの計上単価を専用の設定値で持ち、単価表から claude-fable-5 の行を外す（#1305）

## 起点

- [#1305](https://github.com/endazon/ai-stock-trading/issues/1305)。計画 ADR-0064 決定 9（追加裁定 2026-10-10）
  「表に無いモデルの応答は、専用の設定値『未知モデルの計上単価』で多めに計上する。既定値は入力 $10 / 出力 $50（$/1M トークン）。
  特定のモデル名には結びつけない。0 には倒さない」・決定 7「実装の単価表の `claude-fable-5` の行（$10 / $50）も外す」・
  決定 6 の表「`claude-fable-5` の行は、専用の設定値の配備と同時に外す」。planning#783 の裁定 5。
- 前提（覆さない）: IADR-0122 の原則（未知モデルを 0 に倒さない・過小計上を危険側とする）、IADR-0499（本番で表が空なら起動しない）、
  IADR-0524 決定 2（禁止モデルの集合）、IADR-0550（取れない入力トークン数は第 2 段）、IADR-0055（計測は best-effort）。
- 直列の前提: #1296（移行段の撤去・PR #1310）はマージ済み（2026-10-10）。

## 現況（origin/develop cc8f0d77。#1376 / IADR-0550 のマージ後）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 未知モデルとモデル名の無い応答は、表の**成分ごとの最大単価**（第 2 段を含む）で計上する。専用の設定値は無い | `Shared.Infrastructure/Composable/Llm/LlmPriceTable.cs:13-16`・`:86-91`・`:127-128` |
| 2 | 上限を担っているのは `claude-fable-5` の行（1.637 / 8.186 円/1k）である | `values-local.yaml:309-310`（report）・`:544-545`（trade-decision） |
| 3 | CI は fable の行の描画を要求している（行を外すと赤） | `.github/workflows/helm.yml:754-756` |
| 4 | 試験は「未知 → 表の最大（fable の値）」を前提にしている | `LlmPriceTableTests`・`LlmPricingWiringTests`（TradeDecision）・`PublishingLlmUsageReporterTests`（TradeDecision） |
| 5 | Stage 0 の見積りも `Resolve` を通す（未知モデルは同じ規則に従う。コード変更は不要） | `Stage0DecisionRecorder.cs` |

## 判断（IADR-0553）

1. **専用の設定値**を `LlmPricing:UnknownModel:InputPer1kTokens` / `:OutputPer1kTokens`（env `LlmPricing__UnknownModel__*`。円/1k）で持つ。
   `PerModel` の外に置き、単価表の行にしない（どのモデル名にも結びつけない）。
2. **既定値は 1.637 / 8.186 円/1k**（$10 / $50 × 163.71 ÷ 1,000 を、他の行と同じく小数第 3 位で四捨五入）。外した fable の行と同じ値であり、
   配備の前後で未知モデルの計上額は変わらない。コードの定数 `LlmPriceTable.DefaultUnknownModelPrice` に持ち、試験で固定する。
3. **入力・出力を独立に解析する。** 未設定（null・空・空白）は既定値（黙って）。書かれていて解析できない・0 以下の成分は、既定値で計上し
   `UnknownModelSettingRejected` を真にする（起動時に WARNING。0 円にも表の最大にも倒さない）。
4. **表の最大と組み合わせない。** 未知モデルの単価は設定値だけで決まり、行を足し外ししても変わらない（決定 9）。
5. **解決の順序は変えない。** 表が空なら従来キー（既定ペア）へ倒れる。専用の設定値は表が非空のときだけ効く。
6. **`IsEffectivelyZero` の意味は変えない。** 専用の設定値だけを構成した状態は「単価がある」と読まない（表が空なら設定値は使われず、
   全呼び出しが既定ペア＝0 円になるため。IADR-0499 の起動拒否はそのまま効く）。
7. 禁止モデル（`claude-fable-5`・`claude-fable-5-1`）の判定は変えない。外すのは単価表の行だけ。禁止モデルが答えた場合の費用は未知モデルの単価になる（値は従来と同じ）。

## やること

1. `LlmPriceTable.From` / `FromRows` に専用の設定値（2 文字列・任意）を足す。`UnknownModelPrice`・`UnknownModelSettingRejected` を公開する。
2. 両サービスの `BuildLlmPriceTable` で `LlmPricing:UnknownModel:*` を読む。起動時、`UnknownModelSettingRejected` なら WARNING を出す
   （文言は `LlmPricingStartupGuard.UnknownModelSettingWarningMessage`）。
3. `values-local.yaml`（report・trade-decision）から fable の 2 行を外し、`LlmPricing__UnknownModel__*` の 2 行（1.637 / 8.186）を置く。
4. `helm.yml`: 両 Deployment で専用の設定値の 2 行の描画を要求し、fable の行が描画されないことを検査する（旧 fable の検査を置き換える）。
5. Helm README・`docs/operations/operations.md`（単価の見直し・本番の単価の投入）を追随する。
6. IADR-0553 を起票し索引へ足す。IADR-0122 決定 3 に日付つき追記（改め）を書く。
7. 試験を追随する（下の受け入れ基準）。

## 受け入れ基準

| # | 基準（issue の受け入れ基準） | 試験 |
| --- | --- | --- |
| 1 | 表が 5.5 系の 3 行だけ・表に無いモデル（`claude-opus-5`・`gpt-5`）→ 専用の設定値 | `LlmPriceTableTests.表に無いモデルは専用の設定値で計上する`・`LlmPricingWiringTests.表に無いモデルは専用の設定値で計上される`（TradeDecision）・ReportService の `LlmPricingWiringTests` |
| 2 | モデル名が null・空・空白 → 1 と同じ単価 | `LlmPriceTableTests.モデル名が無い場合も専用の設定値で計上する`・`PublishingLlmUsageReporterTests.未知のモデルは専用の設定値で計上する(null)` |
| 3 | 設定値なし・表が非空 → 既定 1.637 / 8.186（$10 / $50 × 163.71）。0 に倒さない | `LlmPriceTableTests.専用の設定値が無ければ既定の_10_50_ドルで計上する`・`既定値は_10_50_ドルを_163_71_で換算した値` |
| 4 | 行を足し外ししても未知モデルの単価は変わらない | `LlmPriceTableTests.未知モデルの単価は表の行の足し外しで変わらない`（表の最大より高い行・低い行の両方） |
| 5 | `values-local.yaml` に fable の行が無く、専用の設定値の行がある。運用手順も同じ | `helm.yml` の描画検査（肯定・否定）・`helm template` の手元実行・README・operations の記述 |
| 6 | 否定形: 解析できない・0 以下 → 0 円や表の最大へ黙って倒さず、既定値で計上し起動時に警告 | `LlmPriceTableTests.専用の設定値が不正なら既定値で計上し不正を示す`・`LlmPricingWiringTests.専用の設定値が不正なら起動時に警告する`（TradeDecision）・`LlmPricingStartupWarningTests`（Report） |
| 7 | IADR-0122 に日付つき追記で決定 3 の改めを記録する | IADR-0122 追記・IADR-0553 |
| — | 表が空なら従来キーへ倒れる・`IsEffectivelyZero` は設定値だけでは偽にならない | `LlmPriceTableTests.表が空なら専用の設定値があっても既定ペアへ倒れる`・`IsEffectivelyZero_専用の設定値だけでは真のまま` |

## 母集合（規則 9・10）

`git grep -n -E "最大単価|成分ごとの最大|表の最大|fable.5|fable_5"`（`.ai-context/specs`・`CHANGELOG.md`・`.ai-context/adr` を除く。作業前 cc8f0d77）で引いた。

| 箇所 | 作業後 |
| --- | --- |
| `LlmPriceTable.cs`（ヘッダ・`From`/`FromRows` の doc・未知の算出） | 専用の設定値へ改める |
| `TradeDecisionService/Program.cs:196`・ReportService `Program.cs`（`BuildLlmPriceTable`） | 設定値を読む・コメントを改める |
| 両 `PublishingLlmUsageReporter.cs`（「最大単価」のコメント） | 「未知モデルの計上単価」へ改める |
| `values-local.yaml:309-310`・`:544-545`（fable 行）・`:510`・`:519-522`（コメント） | 行を外し設定値の行を置く。コメントを改める |
| `helm.yml:726`・`:746`（「未知モデル＝最大単価」の文言）・`:754-756`（fable 検査） | 文言を「未知モデルの計上単価」へ。検査を置き換える |
| Helm README `:644`（fable の行）・`:654-655`・`:660`・`:668-669` | 表から外し、設定値の段落を足す |
| `docs/operations/operations.md:591`・`:592`（「最大単価で過大計上」）・本番の投入の手順 3 | 追随する |
| 試験（`LlmPriceTableTests`・TradeDecision の `LlmPricingWiringTests`・`PublishingLlmUsageReporterTests`） | 追随する |

**除外**:
- 禁止モデルの判定（`LlmAssignments.ForbiddenModel*`・その試験・`HttpLlmCompletionClient*Tests` の `claude-fable-5` の応答）: 判定は変えない（issue の注意）。
- `ReportNarrativeTimeouts.cs:9`・`ReportNarrativeTimeoutsTests.cs:10`・`values-local.yaml:292`・`:504` の fable-5: 過去の割当の経緯の記述で単価の行ではない。
- `.ai-context/specs/`・確定済みの IADR（0122 以外）: 凍結記録。IADR-0122 は日付つき追記だけを足す。
- `CHANGELOG.md`: 生成物。

## 残り（本作業の外）

- 本番（配備時の values）への投入は、LLM を有効にする配備のときに運用手順（operations「本番の LLM 単価の投入」）で行う。本作業はチャートの既定（`values.yaml`）を変えない。
- 表に $10 / $50 を超える単価の行を足す場合、未知モデルの計上単価はそれより低くなる（決定 9 で設定値を行から切り離したため）。運用の見直し表に「設定値も見直す」を書く。
