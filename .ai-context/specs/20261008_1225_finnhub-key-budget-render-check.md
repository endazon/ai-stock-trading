---
title: Finnhub の同一鍵を使う全プロセスの自制レートの合計 ≤ 60 回/分を、描画した chart で検査器が判定し、陰性対照と既定値の複写の固定を足す（#1225）
type: spec
status: accepted
related_ids: [FR-01, FR-03, NFR, ADR-0043, IADR-0512, IADR-0275, IADR-0068, IADR-0434, IADR-0437, IADR-0478, IADR-0058]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 2: (a) 同一鍵の合計 ≤ 60・(b) 1 巡回が巡回間隔に収まる)
---

# Finnhub の同一鍵の予算を描画で検査する（#1225）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（情報収集）・FR-03（市場監視の巡回）・NFR（構成の検査）
- 計画 ADR: ADR-0043 決定 2（(a) 同一鍵の自制レートの合計 ≤ 60 回/分・(b) 1 巡回が巡回間隔に収まる）
- 関連する実装ADR: IADR-0275（60 回/60 秒の固定ウィンドウの実測・同一鍵）・IADR-0068 決定 4（プロセス間の協調はしない）・
  IADR-0434 / IADR-0437（市場監視 12 回/分・helm.yml の (a)(b) の描画検査の導入）・IADR-0478（描画済み manifest を読む検査器の型）
- 新規 IADR: IADR-0512
- 起票: [#1225](https://github.com/endazon/ai-stock-trading/issues/1225)（起票元 #1204・第 4 回全体監査 C-6）

## 現況の実測（`origin/develop` `facba5c1`）

issue 本文は「57 を確かめる機械は無い」とするが、**`helm.yml` には既に (a)(b) の描画検査（`Assert Finnhub budget and market-monitor cycle fit`。#1030 / PR #1032）が在る**。
issue の受け入れ基準 1 は形式上ここで満たされている。残る欠けは次の 5 点である（実測）。

| # | 欠け | 実害 |
| --- | --- | --- |
| G1 | 陰性対照が無い（受け入れ基準 2） | awk の抽出が壊れて全サービスが既定へ落ちても、50 ≤ 60 で緑のまま（無検査の緑） |
| G2 | 母集合が bash に手書き。描画に Deployment が無くても `val` は既定を返す | サービス名の変更・削除で静かに既定値で数える。新しく Finnhub の鍵を持ったワークロードも数えない |
| G3 | コードの既定値（30・5・60）を bash に複写し、コードとの一致を固定する試験が無い（受け入れ基準 3） | 既定を上げても CI は古い値で数えて緑 |
| G4 | `FinnhubSharedKeyBudgetTests` も情報収集の既定 30 を定数で複写しており、コードと突き合わせていない（Shared → Services の参照は依存方向違反） | 同上 |
| G5 | `values.yaml` / `values-local.yaml` のコメントと chart README の「57」を検査する機械が無い（受け入れ基準 4） | 値を変えても文書が古いまま |

`helm` はこの環境に無いため、CI と同じ v4.2.1 を `get.helm.sh` から作業領域へ取得して描画を実測した（下記）。

## 母集合（規則 9: 記憶で挙げず、誤りの側の文字列で走査した）

走査したもの:

- `git grep -n "Finnhub" -- deploy/helm .github/workflows`（描画される env と既存の検査）
- `grep -rln "Finnhub" backend --include=*.cs`（テストを除く 57 ファイル）から、Finnhub へ要求を送る経路を `FinnhubQuoteClient(` / `MarketDataSourceFactory.Create` / `FinnhubCompanyNewsSource(` で絞った
- `grep -rn "RateLimitPerMinute\|RequestsPerMinute" backend --include=*.cs`（自制レートの設定点）
- 描画（v4.2.1・既定と values-local）の全ワークロード（Deployment 11 本・CronJob・OpenD）の env から `*Finnhub__*` を抽出
- `appsettings.json`（Production で読まれる）に Finnhub のレートの上書きが無いこと（`appsettings.Development.json` の 5 は配備では読まれない。#1192 で配備は Production）
- 各プロセスの限流器の数（DI の寿命）

| プロセス（Deployment） | 鍵の env | 自制レートの env | コードの既定（出所） | 限流器 | 既定描画の値 | values-local の値 |
| --- | --- | --- | --- | --- | --- | --- |
| information-collection-service | `Collection__Source__Finnhub__ApiKey` | `Collection__Source__Finnhub__RateLimitPerMinute` | 30（`InformationCollectionService.Infrastructure.ExternalServices.FinnhubOptions`） | 1 つ（`finnhub` と `finnhub-news` が `FinnhubFamily.Limiter` を共有。IADR-0435） | 未指定→30 | 未指定→30 |
| market-monitor-service | `MarketData__Finnhub__ApiKey` | `MarketData__Finnhub__RequestsPerMinute` | 5（`FinnhubMarketDataOptions`） | 1 つ（`IMarketDataSource` は singleton） | 12 | 12 |
| risk-management-service | 同上 | 同上 | 5 | 1 つ（singleton） | 未指定→5 | 未指定→5 |
| report-service | 同上 | 同上 | 5 | 1 つ（singleton） | 未指定→5 | 未指定→5 |
| trade-decision-service | 既定描画では**無い**／values-local では在る | 同上 | 5 | 1 つ（singleton） | 未指定→5 | 未指定→5 |

- 計 5 プロセス（**issue の「4 サービス」は市況の消費側だけの数**で、情報収集を足すと 5）。合計は 30 ＋ 12 ＋ 5 × 3 ＝ **57**（両プロファイル）。
- レプリカ: 全 Deployment が `replicas: 1`（テンプレートに直書き）。検査器は描画の `spec.replicas` を掛ける（無ければ Kubernetes の既定 1）。
- trade-decision は既定描画で鍵の env を持たないが、**数える**（運用者が鍵を足せば同じ鍵を使い得る。既存の検査・IADR-0434 の予算表と同じ）。
- 除外したもの（理由）:
  - backtest-service: Stooq / moomoo の日足だけ（`StooqHistoricalBarSource.cs` の Finnhub は注記の言及のみ）
  - notification-service: 市場監視の見積りの表示だけ（`FinnhubEstimateView`）。Finnhub へ送らない
  - order-execution / audit / configuration / cost-control: Finnhub の参照なし
  - CronJob（curl の起動だけ）・OpenD: Finnhub の参照なし
  - 1 プロセス内の限流器の重複: 上表のとおり各 1 つ（情報収集の 2 ソースは共有済み）
- **母集合が変わったことを検査器が捕まえる**: 描画のどのワークロードにも `Finnhub__ApiKey` / `Finnhub__RequestsPerMinute` / `Finnhub__RateLimitPerMinute` で終わる env が在り、それが宣言の母集合に無ければ赤にする。宣言の Deployment が描画に無い・2 本以上も赤。

## 設計

### 置き場所

**`scripts/check-finnhub-key-budget.js`（描画済み manifest を標準入力で読む）＋ `helm.yml` が既定・values-local・陰性対照へ当てる。**
既存の `check-decision-volume-parity.js`（IADR-0478）と同じ型で、YAML の読みは `helm-release-drift.js` の `parseManifest` を再利用する。
bash の awk のままにしない理由: 陰性対照・母集合の欠け・重複・読めない値を `scripts.repo.test.js` で helm なしに固定できる（G1・G2）。

### 既定値の複写の単一化

コードの既定値・上限・母集合は **`scripts/finnhub-key-budget.json`** の 1 か所に置く。検査器はここを読み、C# の試験がコードと突き合わせる（G3・G4）。

| JSON の値 | 突き合わせる試験 | コード |
| --- | --- | --- |
| 情報収集の既定 30 | `InformationCollectionService.Tests` の新試験 | `new CollectionSourceOptions().Finnhub.RateLimitPerMinute` |
| 市況の既定 5（4 サービス） | `FinnhubSharedKeyBudgetTests`（拡張） | `new FinnhubMarketDataOptions().RequestsPerMinute` |
| 巡回間隔の既定 60 | `MarketMonitorService.Tests` の新試験 | `new MonitorOptions().PollIntervalSeconds` |
| 市況の消費サービス 4 | `FinnhubSharedKeyBudgetTests`（拡張） | 定数 `MarketDataConsumerServiceCount` と JSON の件数 |

`FinnhubSharedKeyBudgetTests` の情報収集の定数 30 は JSON から読むよう置き換える（複写を 1 つ減らす。JSON はコードと別の試験で固定される）。

### 判定

- (a) 宣言の 5 プロセスの実効値 × レプリカ数の合計 ≤ 60。実効値: env が無ければコードの既定、在れば十進整数として読み `max(1, 値)`（限流器の `Math.Max(1, …)` と同じ）。空・整数でない・平文の value でない（secretKeyRef 等）・同じ名前（大小文字を区別しない）が 2 つ以上は**読めないとして赤**（推測で既定へ戻さない）。
- (b) 市場監視の自制レート × 巡回間隔 ÷ 60（整数除算・既存と同じ）≥ 12（IADR-0434）。巡回間隔も同じ読み（`max(1, 値)`）。
- 文書の主張（G5）: `--claims <file>` で与えたファイルの「`N ≤ 60 回/分`」の N がすべて描画の合計と一致すること。ファイルに 1 件も無ければ赤（空振りを作らない）。
  主張の書式を揃えるため、chart README の 2 か所（「合計 57/分」「＝ **57**」）を「57 ≤ 60 回/分」の形へ書き換える。
  - values.yaml の主張は既定の描画へ、values-local.yaml の主張は values-local の描画へ、README の主張は両方の描画へ当てる。

### helm.yml

- 既存の `Assert Finnhub budget and market-monitor cycle fit` の awk を検査器の呼び出しへ置き換える（(a)(b) とも検査器へ移す）。
- 陰性対照（受け入れ基準 2）: 各プロセスのうち 1 つを、合計が 61 になる値へ上げた values を当てて**赤になること**を確かめる（5 プロセスすべてで 1 回ずつ。情報収集＝34・市場監視＝16・他＝9）。加えて (b) の陰性対照（市場監視の巡回間隔 59 秒→ 11 要求）と、文書の主張の陰性対照（57 と書いた README を合計 58 の描画へ当てる）。
- `paths` に検査器と JSON を足す（変えたら描画検査が走る。起動条件は広がるだけで、必須チェックは変えない）。

## 受け入れ基準

1. Given 既定と values-local の描画 When 検査器を当てる Then 合計 57 ≤ 60・(b) 12 ≥ 12 で緑。
2. Given 5 プロセスのどれか 1 つを合計 61 になるよう上げた values When 検査する Then 赤（helm.yml の陰性対照と `scripts.repo.test.js`）。
3. Given JSON の既定値 When C# の試験 Then コードの既定値と一致する（コードを変えて JSON を直さなければ赤）。
4. Given values.yaml・values-local.yaml・chart README の「N ≤ 60 回/分」 When 検査する Then N が描画の合計と一致する（食い違えば赤）。
5. 否定形: 宣言の Deployment が描画に無い・重複・値が読めない・宣言外のワークロードが Finnhub の鍵を持つ → 赤。

## 試験

| 置き場所 | 内容 |
| --- | --- |
| `scripts/scripts.repo.test.js`（`finnhub-key-budget:` の 4 件） | 正例（57・既定だけで 50）・陰性対照（5 プロセスそれぞれ +4 で 61）・レプリカ・(b)・読めない描画（欠け・重複・空・secretKeyRef・宣言外の鍵）・文書の主張・main の終了コード |
| `backend/Shared/AiStockTrading.Shared.Infrastructure.Tests/MarketData/FinnhubSharedKeyBudgetTests.cs` | JSON の市況の既定・件数・上限をコードと突き合わせ、情報収集の既定を JSON から読む |
| `backend/Services/InformationCollectionService/Tests/Infrastructure/ExternalServices/FinnhubKeyBudgetDefaultsTests.cs` | JSON の情報収集の既定 ＝ `CollectionSourceOptions` |
| `backend/Services/MarketMonitorService/Tests/FinnhubKeyBudgetDefaultsTests.cs` | JSON の巡回間隔の既定 ＝ `MonitorOptions` |
| `helm.yml` | 実 chart の正例 2・陰性対照 7 |

試験 ID: FR-01 はテスト仕様書を持たない（網羅裁定 #211 の必須範囲外）ため T- 番号は振らない。

## 変更するファイル

- `scripts/check-finnhub-key-budget.js`（新）・`scripts/finnhub-key-budget.json`（新）・`scripts/scripts.repo.test.js`・`scripts/README.md`
- `.github/workflows/helm.yml`
- `deploy/helm/ai-stock-trading/README.md`（主張の書式と検査の記述）・`values.yaml` / `values-local.yaml`（コメントに検査器を示す）
- C# の試験 3 ファイル
- `.ai-context/adr/IADR-0512_*.md`（新）・`.ai-context/adr/README.md`

## 残余

- README の主張は両プロファイルで同じ合計を前提にする（現況は両方 57）。食い違えば README を書き分け、`--claims` の当て方を見直す。
- 稼働中の Pod の env は見ない（`helm-release-drift.js` の領域）。`--set` で配備時に与える値も見ない。
- 鍵の値は見ない（別アカウントの鍵でも同じ鍵として数える＝保守側）。
- C# のコード既定を変えたとき helm.yml は起動しない（.NET の CI の C# 試験が JSON との不一致で赤になり、JSON を直すと helm.yml が起動する）。
