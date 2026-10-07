---
title: IADR-0512 Finnhub の同一鍵の予算（全プロセスの自制レートの合計 ≤ 60 回/分と市場監視の 1 巡回）は、描画済みの chart を読む検査器で判定し、母集合とコードの既定値の複写を 1 つの JSON に置いて C# の試験でコードと突き合わせる
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-03, NFR, ADR-0043, IADR-0275, IADR-0068, IADR-0434, IADR-0437, IADR-0478, IADR-0439, IADR-0058]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 2: (a) 同一鍵の合計 ≤ 60・(b) 1 巡回が巡回間隔に収まる)
---

# IADR-0512: Finnhub の同一鍵の予算を描画済み manifest の検査器で判定する（#1225）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: Claude Code（実装）。予算の値（上限 60・市場監視 12 回/分・巡回 12 要求）は変えない

## 起点・関連

- 起票: [#1225](https://github.com/endazon/ai-stock-trading/issues/1225)（起票元 #1204・第 4 回全体監査 C-6）
- 関連する計画書 ID: FR-01（情報収集）・FR-03（市場監視）・NFR（構成の検査）
- 計画 ADR: ADR-0043 決定 2
- 関連する実装仕様書: [`.ai-context/specs/20261008_1225_finnhub-key-budget-render-check.md`](../specs/20261008_1225_finnhub-key-budget-render-check.md)
- 前提: [IADR-0275](IADR-0275_finnhub-effective-rate-limit-measurement.md)（60 回/60 秒の固定ウィンドウ・同一鍵）、IADR-0068 決定 4（プロセス間の協調はしない）、
  [IADR-0434](IADR-0434_finnhub-cycle-fit-budget-market-monitor-rate.md)（市場監視 12 回/分・合計 57）、IADR-0437（helm.yml の (a)(b) の描画検査の導入）、
  [IADR-0478](IADR-0478_decision-volume-parity-render-check-and-llm-cost-in-nightly-summary.md) 決定 1（描画済み manifest を読む検査器の型）、IADR-0439（`helm-release-drift.js` の YAML の読み）

## 背景

- 超過は「全プロセスの自制レートの合計を 60 以下に保つ」構成でしか防げない。`FinnhubSharedKeyBudgetTests` はコードの既定値の合計（30 ＋ 5 × 4 ＝ 50）だけを固定する。
- `helm.yml` には #1030 で (a)(b) の描画検査（awk）が入っていたが、実測で次が欠けていた:
  陰性対照が無い（抽出が壊れて全部が既定へ落ちても 50 ≤ 60 で緑）・母集合が手書きで、描画に Deployment が無くても既定値で数える・
  コードの既定値（30・5・60）の複写とコードの一致を固定する試験が無い・values と chart README の「57」を見る機械が無い。
- 母集合は grep と描画で引き直した（作業仕様書の表）: 情報収集（`finnhub` と `finnhub-news` が 1 つの限流器を共有）＋ 市況の 4 サービス＝**5 プロセス**、各 1 レプリカ・各 1 限流器。

## 決定

### 決定 1 — 判定は `scripts/check-finnhub-key-budget.js` に置き、`helm.yml` が既定・values-local・陰性対照へ当てる

- 描画済みの manifest を標準入力で読む（IADR-0478 決定 1 と同じ型。YAML の読みは `helm-release-drift.js` を再利用し、外部依存を足さない）。
- (a) 宣言の Deployment の実効の自制レート × `spec.replicas`（無ければ 1）の合計 ≤ 60。(b) 市場監視の自制レート × 巡回間隔 ÷ 60（整数除算）≥ 12。
- 実効値: env が無ければコードの既定、在れば十進整数として読み `max(1, 値)`（限流器の `Math.Max(1, …)` と同じ）。
  **空・整数でない・平文の value でない・同じ名前（大小文字を区別しない）の重複は読めないとして赤**（空を既定へ戻すとは推測しない）。
- **母集合の漂流を赤にする**: 宣言の Deployment が描画に 0 本・2 本以上、または宣言外のワークロードが `Finnhub__ApiKey` / `Finnhub__RequestsPerMinute` / `Finnhub__RateLimitPerMinute` で終わる env を持てば赤。
- 陰性対照（`helm.yml`）: 5 プロセスのそれぞれを合計 61 になる値へ上げた描画（5 件）・巡回間隔 59 秒で 11 要求（1 件）・合計 58 の描画を README の「57」へ当てる（1 件）がすべて**想定の理由で**赤になることを確かめる（赤の理由も文言で照合し、描画の失敗による赤を緑と取り違えない）。
- `helm.yml` の `paths` に検査器と JSON を足す（起動条件が広がるだけ。必須チェックは変えない）。

### 決定 2 — 母集合とコードの既定値の複写は `scripts/finnhub-key-budget.json` の 1 か所にし、C# の試験でコードと突き合わせる

- 情報収集の既定 30 は `InformationCollectionService.Tests` の `FinnhubKeyBudgetDefaultsTests` が `CollectionSourceOptions` と、
  市況の既定 5・消費サービス 4・上限 60 は `FinnhubSharedKeyBudgetTests` が `FinnhubMarketDataOptions` と、
  巡回間隔の既定 60 は `MarketMonitorService.Tests` の `FinnhubKeyBudgetDefaultsTests` が `MonitorOptions` と突き合わせる。
- `FinnhubSharedKeyBudgetTests` の情報収集の定数 30（依存方向のための複写）は JSON から読むよう置き換えた（複写を 1 つ減らす）。

### 決定 3 — 文書の「N ≤ 60 回/分」を描画の合計と突き合わせる

- `--claims <file>` で与えたファイルの「`N ≤ 60 回/分`」の N がすべて描画の合計と一致しなければ赤。1 件も無いファイルも赤（空振りを作らない）。
- chart README の 2 か所（「合計 57/分」「＝ **57**」）をこの書式へ揃えた。`values.yaml` は既定の描画へ、`values-local.yaml` は values-local の描画へ、README は両方へ当てる。

## 却下した代替案

- **awk の検査に陰性対照だけを足す**: 母集合の欠け・重複・読めない値の扱いを helm なしで試験できない（`scripts.repo.test.js` で固定できない）。
- **values.yaml を直接読む検査器（描画しない）**: `extraEnv` の上書き・テンプレートの導出（`$envOverrides`）・`-f` の重ねを再実装することになり、描画と食い違い得る。CI は既に helm を持つ。
- **既定値を検査器の JS に直書きし、C# から JS を正規表現で読む**: JSON の方が C# からも Node からも構造で読める。
- **C# のソースを Node から正規表現で読んで既定値を得る**: 初期化子の書き方が変わると黙って読めなくなる。型をインスタンス化して読む C# の試験の方が確実。
- **文書の数字を検査の出力から生成する**: README・values のコメントは人が書く散文であり、生成物にすると編集の流れが変わる。突き合わせで足りる。

## 結果

- 予算を超える chart の変更（どのプロセスの自制レートを上げても）・母集合の漂流・既定値の複写の陳腐化・文書の数字の食い違いが、いずれも CI で赤になる。
- 試験: `scripts.repo.test.js` の `finnhub-key-budget:` 4 件・C# 3 ファイル（4 件）・`helm.yml` の正例 2・陰性対照 7。
  FR-01 はテスト仕様書を持たない（網羅裁定 #211 の必須範囲外）ため T- 番号は振らない。

## 残余リスク

- README の主張は両プロファイルで同じ合計を前提にする（現況は両方 57）。食い違えば README を書き分け、`--claims` の当て方を見直す。
- 稼働中の Pod の env と、配備時に `--set` で与える値は見ない（`helm-release-drift.js` の領域）。
- 鍵の値は見ない（別アカウントの鍵でも同じ鍵として数える＝保守側）。
- C# のコードの既定値の変更では `helm.yml` は起動しない。.NET の CI の C# 試験が JSON との不一致で赤になり、JSON を直すと `helm.yml` が起動する。
- `TokenBucket` は満杯から始まり連続で補充する（1 分あたり N）ため、固定 60 秒ウィンドウの中では 1 プロセスが設定値 N を超えて送り得る（バースト）。本 IADR は自制レートの設定値の合計だけを見る（実効の上限との関係は IADR-0275 の領域）。独立監査の試算では、5 プロセスが同じ窓へ重なると最大 約 2・57−5 ≈ 109 回になりうる。対策は #1247 で扱う。
