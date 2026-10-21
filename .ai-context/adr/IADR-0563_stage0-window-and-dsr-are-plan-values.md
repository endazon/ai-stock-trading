---
title: IADR-0563 Stage 0 の評価の窓の長さ（365 暦日）と DSR の合格の閾値（0.95）を計画の値として記録し、コードの既定（公開定数 LookbackDaysPlanValue・MinDeflatedSharpeDefault）と判定用の構成（appsettings*.json・values*.yaml。PoC の values-local は対象外）を計画の値と突き合わせる試験を置く
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-20, ADR-0069, ADR-0033, ADR-0064, ADR-0036, ADR-0039, ADR-0018, IADR-0110, IADR-0138, IADR-0329, IADR-0337, IADR-0528, IADR-0554]
author: claude (Claude Code)
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0069_stage0-evaluation-window-365-days-and-dsr-threshold.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§6.1）
  - planning#806（裁定 2026-10-21）
---

# IADR-0563: Stage 0 の評価の窓 365 暦日と DSR 0.95 は計画の値である（#1307）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-21
- 決定者: claude が起案（起点 #1307・裁定 planning#806・計画 ADR-0069。レビューは PR で受ける）

## 起点・関連

- 計画: **ADR-0069 決定 1・2・5・フォローアップ 1**（評価の窓の長さ 365 暦日・DSR ≥ 0.95 を計画の基準とする・PoC の 24 日の窓は判定用でない）。FR-15、FR-20。
- 前提として覆さない IADR: [IADR-0110](IADR-0110_stage0-criteria-calibration.md)（DSR 0.95 の較正。標本長 252 営業日）、
  [IADR-0329](IADR-0329_stage0-split-fixation-cutoff-registration-and-route-b-enablement.md)（IS:OOS 2:1・PBO 分割 4）、
  [IADR-0337](IADR-0337_pbo-not-evaluable-without-search.md)（試行 1 本の PBO は評価不能）、
  [IADR-0528](IADR-0528_stage0-asof-base-input-supply.md) 決定 9（窓の終端の固定）、[IADR-0554](IADR-0554_trial-ledger-per-period-sharpe.md)（日次の Sharpe）。
- 仕様書: `.ai-context/specs/20261021_1307_stage0-window-plan-values.md`
- 残り（窓が届いた後の報告）: #1405

## コンテキスト

- 評価の窓の長さ `LookbackDays = 365` はコードの既定値として在るだけで、**それを決めた IADR は無かった**（ADR-0069 §実装の現状）。
- DSR の閾値 0.95 は `Stage0GateCriteria.Default` の直書きで、一次記録は IADR-0110（実装）にあった。計画には閾値の文が無かった（ADR-0069 実測 5）。
- ADR-0069 が両者を計画の値として登録し、「構成値（`LookbackDays`・`MinDeflatedSharpe`）を計画の値と突き合わせる試験を置く（PoC の判定用でない構成は除く）」を実装へ求めた（フォローアップ 1）。

## 決定

### 決定 1: 両値の正本は計画である。実装はコードの公開定数で受け、値を動かさない

- `Stage0EvaluationOptions.LookbackDaysPlanValue = 365`（暦日）を足し、`LookbackDays` の既定をこの定数にする。
- `Stage0GateCriteria.MinDeflatedSharpeDefault = 0.95` を足し、`Stage0GateCriteria.Default.MinDeflatedSharpe` をこの定数にする。
- 先例は ADR-0039 決定 2 の `MinTrialsDefault`（IADR-0337 決定 2）と ADR-0018 の `MaxDrawdownToleranceDefault`（IADR-0138）である。**変更が要るなら計画へ環流する**（ADR-0069 決定 1「窓の長さを変えるときは、計画を改める。実装の構成だけで変えない」）。
- 窓の取り方 `[終端 − 365 暦日, 終端]`（`EvaluationWindow`）と分割（IS:OOS 2:1・PBO 4。ADR-0036 決定 2）は変えない。

### 決定 2: 判定用の構成を列挙で取り、計画の値と突き合わせる。PoC の構成だけを名前で除く

`Stage0EvaluationWindowPlanValueTests` が見るもの:

| 対象 | 見方 |
| --- | --- |
| コードの既定 | `LookbackDaysPlanValue == 365`・`new Stage0EvaluationOptions().LookbackDays == LookbackDaysPlanValue`・`MinDeflatedSharpeDefault == 0.95`・`Default.MinDeflatedSharpe == MinDeflatedSharpeDefault` |
| 窓の届く日 | カットオフ 2026-06-30 で、終端 2027-07-01 の窓は 2026-07-01 起点（カットオフより後）、終端 2027-06-30 の窓はカットオフ当日を含む（ADR-0069 決定 1・3） |
| `backend/Services/BacktestService/appsettings*.json` | **列挙して**、`appsettings.json` に重ねて束縛し、`LookbackDays == 365` |
| `deploy/helm/ai-stock-trading/values*.yaml` | **列挙して**、`services.backtest.extraEnv` の `Backtest__Stage0__LookbackDays` が無い（既定が効く）か `365` |
| 除外 | `values-local.yaml` だけ（PoC の経路の確認用の 24 日。ADR-0069 決定 5）。除外の名前が実在しないと赤になる（表が古くなったことを黙らせない） |
| 否定形 | `values-local.yaml` は実際に 365 と違う値を書いており、同じ読み方で読める（除外が無ければ検知される） |
| DSR の構成 | `MinDeflatedSharpe` はどの構成（values*.yaml・appsettings*.json・docker-compose.yml）にも書かない。構成の口が無いことを固定する |

- **母集合をファイルの列挙で取る**のは、values*.yaml・appsettings*.json が増えたときに黙って検査の外へ出さないためである（規則 9）。
- 本番（`values.yaml`）は値を書かず既定が効く（ADR-0069 実測 7）。本番を書き換えない。

## 検討した選択肢

1. **公開定数 ＋ 構成の列挙による突き合わせ（採用）** — 先例（`MinTrialsDefault`）と同じ作法。PoC の除外は名前で 1 件だけ持つ。
2. 起動時に構成値が 365 と違えば警告する（不採用） — PoC の 24 日は正当な構成であり、毎起動で警告が出る。判定用かどうかは構成の外（利用者の承認）で決まるため、起動時には区別できない。ADR-0069 決定 7 の「365 暦日に満たない窓の評価を合格の根拠にしない」は人の手順（昇格の承認）のままとする。
3. `LookbackDays` を構成から外し定数にする（不採用） — PoC の経路の確認（ADR-0069 決定 5 が改めないとした構成）ができなくなる。

## 結果

- 計画の値と構成値がずれれば試験が赤になる（変異で確かめた: values.yaml に `30`・appsettings.Development.json に `200`・`Default` を 0.9 にすると、それぞれ該当の試験が赤）。
- 🔴 判定器は窓の長さを見ない。365 暦日に満たない窓（PoC の 24 日）でも評価の結果は出る。それを合格の根拠にしないのは人の手順である（ADR-0069 決定 7 の表のとおり。本 IADR は機械の検査を足さない）。
- 窓が届いた後（2027-07-01 以降）の環流報告は #1405 に残す。
