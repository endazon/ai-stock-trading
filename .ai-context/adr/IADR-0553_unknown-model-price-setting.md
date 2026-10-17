---
title: IADR-0553 表に無いモデルの計上単価は単価表の行とは別の設定値（LlmPricing:UnknownModel:*・既定 $10 / $50＝1.637 / 8.186 円/1k）で持ち、単価表から claude-fable-5 の行を外す。不正な値は既定値で計上し起動時に警告する。表が空なら従来キーへ倒れ、IsEffectivelyZero の意味は変えない
type: impl-adr
status: Accepted
related_ids: [NFR-13, FR-04, ADR-0064, ADR-0037, IADR-0122, IADR-0499, IADR-0524, IADR-0550, IADR-0055]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0064_llm-assignment-to-5-5-family-and-stage0-rerun.md（決定 6 の表・決定 7・決定 9・実装の現状 R10）
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§6.1 の換算 163.71 円/USD）
  - planning#783（2026-10-10 のコメントの裁定 5）
related_specs:
  - ../specs/20261017_1305_unknown-model-price-setting.md
---

# IADR-0553: 表に無いモデルの計上単価を専用の設定値で持ち、単価表から claude-fable-5 の行を外す（#1305）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-17
- 決定者: claude が起案（起点 [#1305](https://github.com/endazon/ai-stock-trading/issues/1305)・計画 ADR-0064 決定 9・planning#783 の裁定 5。レビューは PR で受ける）

## 起点・関連

- 計画: **ADR-0064 決定 9**（追加裁定 2026-10-10）「表に無いモデルの応答は、専用の設定値『未知モデルの計上単価』で多めに計上する。
  既定値は入力 $10 / 出力 $50（$/1M トークン）。円換算は 05_trading-assumptions §6.1 のレートによる。特定のモデル名には結びつけない。0 には倒さない」。
  **決定 7**「実装の単価表の `claude-fable-5` の行（$10 / $50）も外す」。**決定 6 の表**「`claude-fable-5` の行は、専用の設定値の配備と同時に外す」。
- 改める IADR: [IADR-0122](IADR-0122_per-model-llm-pricing.md) 決定 3 の上限の取り方（未知モデル＝表の成分ごとの最大単価）。同決定の原則
  （未知モデルを 0 に倒さない・過小計上を危険側とする）は前提として扱い、覆さない（ADR-0064 §既存 IADR の扱い）。
- 前提として覆さない IADR: [IADR-0499](IADR-0499_llm-pricing-unset-refuses-production-start.md)（本番で単価が実質 0 なら起動しない）、
  [IADR-0524](IADR-0524_claude-5-5-models-with-previous-generation-transition.md) 決定 2（禁止モデルの集合）、
  [IADR-0550](IADR-0550_haiku-tier-unknown-input-tokens-high-side.md)（取れない入力トークン数は第 2 段）、IADR-0055（計測は best-effort）。
- 仕様書: `.ai-context/specs/20261017_1305_unknown-model-price-setting.md`

## コンテキスト

- `LlmPriceTable` は、表に無いモデルとモデル名の無い応答を、表の**成分ごとの最大単価**（第 2 段を含む）で計上していた（IADR-0122 決定 3）。
- 経路B（`values-local.yaml`）の表で最大を担っていたのは、禁止モデル `claude-fable-5` の行（$10 / $50 ＝ 1.637 / 8.186 円/1k）である。
  この行は使うためではなく上限値として載っていた（ADR-0064 実測 9）。
- 行を外すだけだと、上限は表の最大（`claude-opus-5-5` の $4 / $20）へ下がる。割当の誤りで $4 / $20 を超えるモデルが答えた要求は過小計上になる
  （ADR-0064 §検討した選択肢 C。採れない）。行を残すと、禁止モデルの行が現行の表に載り続け、決定 7（現行値は 5.5 系だけ）と食い違う（同 B）。

## 決定

### 決定 1: 専用の設定値を `LlmPricing:UnknownModel:*` に置く

- 構成キーは `LlmPricing:UnknownModel:InputPer1kTokens` / `LlmPricing:UnknownModel:OutputPer1kTokens`（円/1k トークン）。
  env 名は `LlmPricing__UnknownModel__InputPer1kTokens` / `__OutputPer1kTokens`（シェル識別子。#817 の制約を満たす）。
- `LlmPricing:PerModel` の外に置く。単価表の行ではないので、どのモデル名にも結びつかない。
- 両サービス（trade-decision・report）の `BuildLlmPriceTable` が読み、`LlmPriceTable.From` / `FromRows` の任意引数で渡す。

### 決定 2: 既定値は 1.637 / 8.186 円/1k（$10 / $50 × 163.71）

- $10 × 163.71 ÷ 1,000 = 1.6371 → **1.637**、$50 × 163.71 ÷ 1,000 = 8.1855 → **8.186**（他の行と同じく小数第 3 位で四捨五入。IADR-0122 決定 4）。
- コードの定数 `LlmPriceTable.DefaultUnknownModelPrice` に持ち、換算の式とあわせて試験で固定する。
- 外した `claude-fable-5` の行と同じ値である。配備の前後で、未知モデルの計上額は変わらない（上限が下がる瞬間を作らない）。
- 入力側の丸め（1.6371 → 1.637）は 0.0001 円/1k の過小である（0.006%）。他の行と同じ丸めの規則に揃え、issue の受け入れ基準 3 の値に一致させた。

### 決定 3: 未知モデルの単価は設定値だけで決まる（表の最大と組み合わせない）

- 表が非空のとき、表に無いモデル・モデル名の無い応答（null・空・空白）は、入力トークン数に依らず設定値で計上する。
- 表の最大との max は取らない。ADR-0064 決定 9「設定値は単価表の行ではなく、どのモデルの行を足し外ししても変わらない」に従う。
- 帰結: 将来 $10 / $50 を超える単価の行を足すと、未知モデルの単価はその行より低くなる。運用の見直し表（`docs/operations/operations.md`）に
  「より高い行を足すときは設定値も上げる」を書く。

### 決定 4: 入力・出力を独立に解析し、不正は既定値で計上して起動時に警告する

- 未設定（null・空・空白）の成分は既定値。黙って使う（既定値は意図した値である）。
- 書かれていて解析できない・0 以下の成分は、**既定値**で計上する（0 円にも表の最大にも倒さない）。`LlmPriceTable.UnknownModelSettingRejected` を真にし、
  両サービスが起動時に `LlmPricingStartupGuard.UnknownModelSettingWarningMessage` の WARNING を出す。
- 起動は止めない（配備でも同じ）。計上は $10 / $50 で続き、費用統制は緩まないため。IADR-0499 の起動拒否は「全呼び出しが 0 円」に限る。
- 成分ごとに独立させるのは、既定ペア（従来キー）の解析と同じ形にするため（正しく書けた側を捨てない）。

### 決定 5: 解決の順序と `IsEffectivelyZero` の意味は変えない

- 順序は従来どおり「一致 → 未知（設定値） → 表が空なら既定ペア（従来キー・未設定 0）」。設定値は表が非空のときだけ効く。
- **専用の設定値だけを構成した状態は「単価がある」と読まない**（`IsEffectivelyZero` は「表が空 かつ 既定ペアが 0」のまま）。
  表が空なら設定値は使われず全呼び出しが 0 円になるため、真のままにして #817 の警告と #1197（IADR-0499）の起動拒否を効かせる。

### 決定 6: 単価表から `claude-fable-5` の行を外す（禁止の判定は変えない）

- `values-local.yaml` の report・trade-decision の `extraEnv` から `LlmPricing__PerModel__claude_fable_5__*` を外し、同じ値の `LlmPricing__UnknownModel__*` を置く
  （既定と同じ値を明示し、描画で読めるようにする）。
- `helm.yml` は両 Deployment で設定値の 2 行を値つきで要求し、`claude_fable_5`（`_1` を含む）の単価行が描画されたら失敗する。旧 fable の行を要求する検査は外した。
- 禁止モデルの判定（`LlmAssignments.ForbiddenModels`）は変えない。禁止モデルが答えた場合の費用は、表に無いモデルとして設定値で計上される（値は従来と同じ）。
- 本番の `values.yaml` は変えない（単価を置かない。IADR-0122 決定 4・IADR-0296）。配備時の values へ足す手順は `docs/operations/operations.md`「本番の LLM 単価の投入」に追記した。

## 検討した選択肢

1. **A: 設定値だけで決める（採用）** — 決定 9 の文言どおり。行の足し外しで黙って変わらない。
2. B: 設定値と表の最大の大きい方 — 過小計上をさらに避けられるが、行を足すと未知モデルの単価が黙って変わる（決定 9「行を足し外ししても変わらない」と食い違う）。
   高い行を足す場面は運用の見直しで設定値を上げれば足りる。採らない。
3. C: 不正な値は起動を止める — 計上は既定値で続き統制は緩まないので、止めるほどの害が無い。IADR-0499 の起動拒否の射程（0 円計上）を広げない。採らない。

## 結果

- 良い影響: 単価表の現行値が 5.5 系の 3 行だけになる（決定 7）。未知モデルの上限が、表のどの行を足し外ししても $10 / $50 のまま変わらない。
- 悪い影響・トレードオフ: 設定項目が 2 つ増える。$10 / $50 を超えるモデルの行を足すときは設定値の見直しが要る（自動では追随しない）。
- 試験の追随: 未知モデルを表の最大で引く前提の試験（Stage 0 の記録器の 4 クラス）は、表に無い層を同じ 1/1 で引くよう設定値を明示した（試験の主題は単価ではない）。

## 関連

- Supersedes: なし（IADR-0122 決定 3 の上限の取り方を改める。IADR-0122 には日付つき追記で記録した）
- Superseded by: なし
