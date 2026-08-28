---
title: IADR-0255 業務メトリクスは契約プロジェクトの単一レジストリに置き、既存の処理点で計上して、ダッシュボードとの一致を機械検査で守る
type: impl-adr
status: Accepted
related_ids: [NFR-07, NFR-13, FR-01, FR-02, FR-04, FR-05, FR-10, FR-19]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
---

# IADR-0255: 業務メトリクスは契約プロジェクトの単一レジストリに置き、既存の処理点で計上して、ダッシュボードとの一致を機械検査で守る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **NFR-07**（可観測性: 全サービスのメトリクス・ログ・トレースを基盤の可観測性スタックで
  収集。OTel/Prometheus/Loki）、**NFR-13**（LLM API 費用の月次上限）、**FR-01/FR-02**（収集・取引サイクル）、
  **FR-04**（判断）、**FR-05**（発注状態）、**FR-10 / FR-19**（リスク統制・取引ガード）、
  計画 **ADR-0006**（Hetzner・OTel/Prometheus/Loki）
- 対象 Issue: [#287](https://github.com/endazon/ai-stock-trading/issues/287)（業務メトリクスの計上と
  Grafana ダッシュボード整備）。インフラ全般は [#24](https://github.com/endazon/ai-stock-trading/issues/24) の管掌であり重複させない。
- 関連する実装仕様書: [20260828_287_business-metrics-and-dashboards](../specs/20260828_287_business-metrics-and-dashboards.md)
- 関連 IADR: [IADR-0094](IADR-0094_local-infra-observability-gitops.md)（可観測性資産は AST リポの
  opt-in・共有スタックの stand-up は MSP 側へ分離）、[IADR-0011](IADR-0011_foundation-min-port.md)
  （OTel/Serilog の統一計装＝本 ADR が拡張する配線点）、
  [IADR-0163](IADR-0163_allow-list-and-required-dependency-scope.md)（不在が統制の無効を意味する依存は必須にする）

## 背景・課題

可観測性は既に配線されている（`AddAiStockTradingObservability` が OTel トレース・メトリクス・Serilog を
11 サービスすべてへ登録する）が、**計上しているのは既製の技術指標だけ**である。着手前に追跡下の `*.cs`
全体を走査した実測は次のとおりで、**独自計器は 1 つも無かった**。

| 走査 | 生の件数 | 実質 |
| --- | --- | --- |
| `new Meter(` / `CreateCounter` 系 / `IMeterFactory` | 0 | **0** |
| `ActivitySource` | 38 行 / 15 ファイル | **0**（全件が `IOrderActivitySource`＝注文活動のドメインポート） |

結果として、ログ（Loki）とトレース（Tempo）で「**事後に追える**」状態ではあるが、
「**異常に気づける**」状態ではない。取引サイクルが止まっても、統制が空回りしていても、
LLM 費用が上限に迫っても、**メトリクスの側には何も現れない**。

決めるべきことは 4 つである。

1. どの業務指標を計上するか（候補は多く、全部入れるのは保守できない）
2. 計器をどこに置き、どう配線するか（**定義しても DI に登録されなければ 1 系列も出ない**）
3. どこで計上するか（新しい契約イベントを足すのか、既存の処理点で叩くのか）
4. ダッシュボードとコードの乖離をどう止めるか

## 検討した選択肢

### 計上点をどこに置くか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A: 監査サービスへ集約 | 全イベントを購読する監査サービスで数える | 1 か所で済むが、**すべての系列が `service_name=audit-service` になり**サービス別の切り分けができない。監査ハンドラは全数レジストリであり、触ると追随先が増える |
| B: ドメインサービスの内部 | `TradeDecisionService` 等の Application 層で計上 | 判断ロジックの内側に可観測性の関心が入り込む。`TradeDecisionService` は既に 15 個の依存を持ち、16 個目を足す形になる |
| **C: 既存のハンドラ（Infrastructure 層）** | イベントを受けて結果を出す継ぎ目で計上 | **採用。** 発行元サービスに帰属し、ドメインを汚さず、結果（承認/拒否・約定/見送り）が既に手元にある |

### 計器の置き場所

| 案 | 評価 |
| --- | --- |
| PlatformShim（可観測性の配線がある場所） | 配線は近いが、shim は「基盤からの最小移植」であり AST 固有のドメイン語彙を置く場所ではない。Infrastructure 層は shim を参照していない |
| 新規プロジェクト | 依存の追加が広範囲に及ぶ |
| **`Shared.Contracts`（採用）** | **メトリクス名はコードと Grafana ダッシュボードの間の契約である。** 全 17 プロジェクトが既に参照しており、追加の依存が要らない。`System.Diagnostics.Metrics` は net10.0 の共有フレームワークにあり `PackageReference` を要さないため、Domain の外部依存ゼロ（`DomainLayerDependencyTests` 検査 3）を壊さない（実測で確認） |

## 決定

### 決定1: 計上するのは 9 計器とし、選定と非選定の理由を記録する

issue は候補を挙げたうえで「要取捨選択」「最低限、統制と取引サイクルの健全性が見えること」と定める。
**候補 12 件のうち 9 件を採用し 3 件を落とした。**

採用: 収集件数 / 判断回数（buy・sell・見送りの内訳）/ 判断レイテンシ / 発注前審査（承認・拒否）/
拒否理由の内訳 / 発注結果（状態・発注先）/ 発注見送り / LLM 費用（上限対象・対象外）/ 上限消費率。

落とした 3 件と理由:

- **残枠（日次・1 注文）**: 値を得るには `PortfolioSnapshotBuilder.Build()`（DB 参照・ブローカー観測の合成）が
  要り、ObservableGauge のコールバックは**スクレイパ側の都合で走る**ため I/O を伴う構築を毎回叩くことになる。
  残枠の枯渇は拒否理由の内訳（`PerOrderAmountExceeded` / `DailyOrderAmountExceeded`）で観測できる。
- **約定反映の遅延**: 発注時刻→約定反映時刻の相関に使える記録が現状ない（`OrderFillPoller` は
  ディスパッチ時刻を保持しない）。**値を発明しない**（IADR-0159 / IADR-0163 の規律）。
- **LLM 拒否率**: 消費すべき計上が基盤側（MSP/IADR-0110）にあり、MSP の develop 追随は本作業では実行できない。
  **名前だけ作ると、値の来ないパネルが「0 件＝正常」に見える**という最悪の形になる。実環境残件として残す。

### 決定2: 計器名は `BusinessMetricNames` を単一情報源とし、単位は `unit` ではなく名前へ埋める

OTel の Prometheus 変換は `unit` を名前へ接尾する（既存の `http_server_duration_milliseconds_count` が
その形）。`unit` を使うと「コード名 → Prometheus 名」の変換規則が単位表に依存して増え、機械検査が書けない。
**`unit` を与えず、単位は名前へ埋める**（`_ms` / `_jpy` / `_percent`）ことで、変換規則は
「ドットを `_` へ」「Counter は `_total`」「Histogram は `_bucket`/`_count`/`_sum`」の 3 つで閉じる。

### 決定3: 依存は必須にする（省略可能引数にしない）

計上点の 6 クラスは `BusinessMetrics` を**必須の依存**として受ける。省略可能引数（既定 `null`）にすると、
`Program.cs` から配線が消えても**コンパイルが通りテストは全緑のまま計上だけが静かに止まる**
（IADR-0163 決定 2 と同じ規律）。実際、本 PR の作業中に登録漏れが**テストのタイムアウトとして即座に現れた**
（Wolverine の共通再試行 2s/10s/30s を使い切って失敗する）。**壊れ方が目に見える**ほうを選ぶ。

### 決定4: 新しい契約イベントを 1 つも足さない

`Shared.Contracts` へイベントを足すと、`AuditEntryFactory` / `AuditEventHandlers` /
`NotificationFormatter` / `NotificationHandlers` / `event-schemas.baseline.json` / ゴールデン表という
6 つの全数レジストリへの追随が要る。**既存イベントの発行点で計器を叩けば足りる**ため、足さない。

### 決定5: 「計装は有効・既定では外部へ送らない」は collector 側の opt-in で担保し、肯定形と否定形の対で固定する

IADR-0094 の作法を踏襲する。計器は常に in-process の `Meter` へ記録し、外部へ出るかどうかは
otel-collector の exporter 構成が決める（dev 既定は `debug`＝標準出力のみ）。**本 PR は新しい egress を
1 つも増やさない。** 表明は 3 点で固定する。

- 肯定形（配線 1）: `AddAiStockTradingObservability` から `BusinessMetrics` がシングルトンで解決できる
- 肯定形（配線 2）: リーダを付けた `MeterProvider` では業務メトリクスが exporter まで**確かに到達する**
- 否定形（配線 2）: `AddMeter` を含まない構成では**到達しない** ——
  この行は**消えても何も壊れない**（記録は続き、外へ出るものだけが静かに消える）ため、
  否定形が無いと「`AddMeter` を消しても緑」になる

### 決定6: ダッシュボードとコードの乖離は 2 層の機械検査で止める

🔴 **乖離はエラーを出さない。** 系列名がずれたパネルは**空のグラフ**を描き、空のグラフは
「異常が起きていない」と読める。**監視しているつもりで何も見ていない**状態が気付かれずに続く。

| 層 | 検査 | 守るもの |
| --- | --- | --- |
| `scripts/check-observability-assets.js`（CI の `doc-checks`。自己試験 16 件） | ダッシュボード JSON の妥当性・`uid` の一意性・**レジストリ ↔ ダッシュボードの双方向一致**・dev collector が metrics を `debug` にしか出さないこと | 綴り違い・参照切れ・**誰も見ていない計器**・既定での外部送信 |
| `BusinessMetricsTests`（C#） | **レジストリ ↔ 実際に `Meter` が作った計器名**の一致（`MeterListener` で実測） | レジストリだけ直して計器を直さない（またはその逆） |

**2 層は重複ではない。** node 側は計器そのものを見られず（`const` の文字列しか読めない）、C# 側は JSON を
見ない。片方だけでは「レジストリと実装は合っているがダッシュボードが古い」または「ダッシュボードと
レジストリは合っているが計器名が違う」が素通りする。

## 理由

- 計上点をハンドラに置くと、**発行元サービスに帰属した系列**が自然に得られ（`service_name` で切り分けられる）、
  ドメインの純粋性も保てる。監査サービスへ集約する案は 1 か所で済むが、全系列が監査サービスの名前で出るため
  「どのサービスが止まっているか」を見るという本来の目的を果たさない。
- 計器名を契約プロジェクトへ置くのは、**名前が実際に契約だから**である。コードとダッシュボードの 2 者が
  同じ文字列に合意していなければ機能しない。契約が 1 か所にあれば、機械検査は両側をそこへ突き合わせられる。
- **タグの基数を業務量に比例させない。** 銘柄・注文 ID・DecisionId はタグにしない。銘柄単位の追跡は
  ログ（Loki）とトレース（Tempo）が担い、メトリクスは「気づく」ためのものと役割を分ける。

## 結果

- 良い影響: 取引サイクル・統制・発注・費用の健全性が 1 画面で見えるようになり、Grafana ダッシュボードが
  リポジトリ管理される。計器の発火はテストで固定されており、「定義しただけ」ではない。
  ダッシュボードとコードの乖離は CI で止まる。
- 悪い影響 / トレードオフ:
  - **実 Prometheus / Grafana での疎通は本 PR では確認できない**（実バックエンドが無い）。
    本 PR が担保するのは「計器が値を刻む」「名前が一致する」までである。
  - 計上点 6 クラスの依存が 1 つ増え、それらを起こすテストホストは `BusinessMetrics` の登録が要る。
  - LLM 費用のカウンタは**プロセス起動からの累計**であり月次のリセットを表現しない。
    上限判定はゲージ（`ast.llm.cost_limit_ratio_percent`）で行う設計にしてある。
- フォローアップ（**実環境待ちであり、達成したふりをしない**）:
  1. **Prometheus 疎通の確認**（取引サイクル 1 巡回で値が動くこと）。実環境が要る。
  2. **MSP を develop へ追随させ、LLM 拒否率（MSP/IADR-0110 の計上）を消費する。** 別リポジトリの作業。
  3. **scrape target `otel-collector:8888` が down している件の切り分け。** collector は
     `prometheusremotewrite` で送るためランタイム系は到達しており実害は無いと整理済み。実機が無いため文書化に留める。
  4. 閾値（「判断が N 分間 0 件なら異常」等）は実測してから決める。**実測が無いまま閾値を置くと、
     最初のアラートで狼少年になる。**

## 関連

- Supersedes: なし
- Superseded by: なし
