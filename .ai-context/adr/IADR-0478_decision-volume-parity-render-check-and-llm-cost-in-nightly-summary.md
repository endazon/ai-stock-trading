---
title: IADR-0478 判断へ渡す出来高の設定は trade-decision と report の一致を chart の描画で検査する。夜間の要約は窓の LLM 費用の円と、当月の月次上限に対する使用率を出す（累計は費用統制の台帳、上限は設定サービスの前提条件から読み、値を複写しない）
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-07, FR-09, NFR, UC-01, ADR-0048, ADR-0023, IADR-0467, IADR-0462, IADR-0218, IADR-0065, IADR-0027, IADR-0058, IADR-0439, IADR-0452, IADR-0463]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0048_decision-volume-from-daily-kline-within-existing-source.md (決定 4: 方針の改訂 LLM へ示す材料)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§6・§6.1: 月次 LLM 費用上限と対象範囲)
---

# IADR-0478: 出来高の 2 か所の設定を描画で検査し、夜間の要約に LLM 費用の円と月次使用率を出す（#1140）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-02
- 決定者: Claude Code（実装）。監査（low）の 4 項目の是正であり、統制の値（リスク統制・取引ガード・費用の上限）は変えない

## 起点・関連

- 起票: [#1140](https://github.com/endazon/ai-stock-trading/issues/1140)（#1118 の独立監査で残った low の 4 項目）
- 関連する計画書 ID: FR-04（判断の材料）・FR-07（方針の改訂）・FR-09 / NFR（費用の統制と可視化）
- 計画 ADR: ADR-0048 決定 4（方針の改訂 LLM へ判断へ渡る材料を示す）・ADR-0023 決定 5（日足の過去データ源）
- 関連する実装仕様書: [`.ai-context/specs/20261001_1140_volume-flag-kline-rate-cost-summary.md`](../specs/20261001_1140_volume-flag-kline-rate-cost-summary.md)
- 前提: [IADR-0467](IADR-0467_decision-volume-from-daily-kline-via-order-execution.md) 決定 6・7（同名の設定を 2 サービスが別々に読む）、
  [IADR-0462](IADR-0462_ledger-position-query-status-and-pre-llm-skips.md)（夜間の要約）、[IADR-0218](IADR-0218_llm-cost-scope-by-purpose.md)（費用の対象範囲を用途で判別）、
  [IADR-0065](IADR-0065_versioned-cost-limits-resolution.md)（月次上限は設定サービスの前提条件から供給）、[IADR-0058](IADR-0058_helm-chart-ci-gate.md)（Helm chart の CI ゲート）
- 4 項目のうち、日足の自制レートの同時運用（項目 2）は IADR-0467 へ、LLM を呼ぶ前の見送りと急変の基準値（項目 3）は IADR-0452 へ、日付つき追記で記録した（本 IADR は決めない）

## 背景

1. **出来高の設定の食い違いに機械検査が無い。** `DecisionVolume:Enabled` は trade-decision（日足を引いて判断へ渡す）と report（方針の改訂 LLM へ「出来高は渡る」と示す）が
   別々に読む。values.yaml の注記と Runbook の手順は「同じ変更で両方を true にする」と書くが、守られているかを見る機械が無い。
   report だけ true だと、判断が確かめられない出来高の条件を方針に書かせる（ADR-0048 決定 4 の逆）。
2. **夜間の要約が LLM の費用を件数でしか出さない。** §1 の種類別の件数に `LlmCostIncurred` が並ぶだけで、円の合計も、月次上限に対する残りも読めない。

## 決定

### 決定 1 — 2 つの値の一致を chart の描画で検査する（`scripts/check-decision-volume-parity.js` ＋ `helm.yml`）

- 判定は描画済みの manifest（`helm template` の出力）を読むスクリプトに置く（YAML の読みは [IADR-0439](IADR-0439_helm-release-drift-read-only-check.md) の `helm-release-drift.js` を再利用し、外部依存を足さない）。
  シェルの awk で書かない —— 判定の分岐（実効値・重複・読めない値）を `scripts.repo.test.js` で単体に試験するため。
- 見るもの: Deployment `trade-decision-service` と `report-service` の `DecisionVolume__Enabled` の**実効値**が等しいこと。実効値はサービスの読みと同じ
  （`bool.TryParse`：前後の空白を許し大小文字を区別しない。キーなし・読めない値は false。env の名前も大小文字を区別しない）。
  両方 true なら、trade-decision の `OrderExecution__BaseUrl` が絶対 URL であること（無い・不正だと trade-decision だけ黙って未提供へ倒れ、report は「渡る」と示し続ける。IADR-0467 の 2026-10-01 追記）。
- 読めない描画は通さない: Deployment の欠け（検査の空振り）・同じ設定の重複（どれが効くか読めない）・平文の value でない値（secretKeyRef 等）。
- `helm.yml` は既定・values-local・全フラグ ON の描画へ当て、**正例**（両方 true＋BaseUrl。既定と values-local）が通ることと、**負例**（report だけ true〔既定・values-local〕・
  trade-decision だけ true・両方 true で BaseUrl なし）が赤になることを確かめる。`helm.yml` の起動条件（`deploy/helm/**`・`helm.yml`）は変えない。

### 決定 2 — 夜間の要約に §12（窓の LLM 費用の円）と §13（当月の累計と月次上限に対する使用率）を足す

- **§12** は監査台帳の `LlmCostIncurred` の `Amount`（円）を、用途 × モデル別の件数・金額と窓の合計で出す。上限の対象かどうかは判別しない
  （用途での判別は費用統制の `LlmCostScope` が唯一の実装であり、SQL へ写すと 2 か所になる）。
- **§13** は監査台帳の外を読む（要約のうちここだけ）:
  - 当月の累計は**費用統制の台帳** `cost_control_svc.cost_entries`（上限の判定 `CostGovernor` が使うのと同じカウンタ）。月は費用統制と同じ UTC の暦月で、
    窓の終端（現在時刻より後なら現在時刻）の月。累計はその月の頭から窓の終端までの計上（`RecordedAt` で切る）。対象（`Llm`）と対象外（`LlmUncapped`）を別の列に出す。
  - 月次上限は**設定サービスの前提条件** `configuration_svc.assumptions` の `costLimits.llm`（費用統制が `AssumptionsCostLimitsProvider` 経由で読むのと同じ行）。
    **値をスクリプトへ複写しない**（利用者が上限を変えると食い違う）。行が無い・数値でない・DB に届かないときは使用率を「不明」と出し、理由を標準エラーへ 1 行出して続ける。
    上限が 0 以下なら割り算せず「統制しない」と出す（`CostGovernor` と同じ扱い）。
  - どちらも `BEGIN TRANSACTION READ ONLY` の中で読み、`ROLLBACK` で終える（§1〜§12 と同じ）。§1〜§12 を先に出し、§13 の失敗で前の節を失わない。終了コードは監査の照会の失敗を優先する。
- `cost_entries.Category` は `CostCategory` の序数で永続化されている（`Llm=0`・`LlmUncapped=3`）。SQL はこの序数を直に書き、
  `scripts.repo.test.js`（T-10-2027）が enum の並びと SQL の序数を突き合わせる（enum 側は「末尾にだけ足す」と定めている）。

## 却下した代替案

| 案 | 採らなかった理由 |
| --- | --- |
| 項目 1 を chart のテンプレートの `fail` にする（描画そのものを止める） | env の配列はサービスごとに values を上書きするリストで、テンプレートの中で 2 サービスの実効値（大小文字・空白・キーなし）を読むと条件が膨らむ。描画の検査は既存の `helm.yml` の形（IADR-0058）にそろう |
| 項目 1 を values.yaml の 1 つのキーから 2 サービスへ配る | 既定描画が変わり（キーを置かない既定の現行動作を崩す）、IADR-0467 の「有効化するときだけ足す」を変える。今回は検査で足りる |
| 項目 4 で上限 15,000 円をスクリプトへ書く・前提条件の既定値（`TradingAssumptionsDefaults`）を読む | 利用者が設定サービスで上限を変えると食い違う（IADR-0065 決定 2 が上限を前提条件から供給する理由と同じ） |
| 項目 4 の当月の累計を監査台帳の `LlmCostIncurred` から数える | 対象範囲の判別（用途）を SQL へ写すことになる（`LlmCostScope` と 2 か所）。費用統制の台帳は判定と同じカウンタである |
| 項目 4 を費用統制の REST（`/costs/state` 等）から読む | 要約は psql だけで動く（トークンの取得が要らない）。DB の読み取りで同じ値が読める |

## 結果

- chart を変える PR で、出来高の設定の食い違いが配備の前に赤になる。`--set` で配備する場合は Runbook の手順で同じ検査を手元で当てられる。
- 夜間の要約で、窓の LLM 費用の円と、当月の月次上限に対する使用率（と残り）が 1 回の実行で読める。
- 試験: T-10-2020〜T-10-2023（判定）・T-10-2024〜T-10-2026（要約の §12・§13。psql スタブと実 PostgreSQL）・T-10-2027（序数の突き合わせ）。`docs/tests/FR-10_risk-controls-tests.md`。

## 残余リスク

- 描画の検査は chart の描画だけを見る。稼働中の Pod の env（`kubectl set env` など描画を経ない変更）は見ない（`helm-release-drift.js` が稼働との差を出す）。
- `helm.yml` は `scripts/check-decision-volume-parity.js` の変更では起動しない（起動条件を変えないため）。判定の変更は `scripts.repo.test.js`（CI の scripts-tests）が試験する。
- §13 の累計は費用統制が計上した時刻（`RecordedAt`）で切る。LLM の呼び出しから計上までの遅れ（メッセージの配送）の分だけ、窓の終端の近くで §12 と食い違い得る。
- §13 は `AST_PSQL` の利用者が `cost_control_svc` と `configuration_svc` を読めることを前提にする（ローカル k3s の `ai` 利用者は読める）。
