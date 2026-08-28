---
title: 業務メトリクスの計上と Grafana ダッシュボード整備（#287）
type: spec
status: draft
related_ids: [NFR-07, NFR-13, FR-01, FR-02, FR-04, FR-05, FR-10, FR-19]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
---

# 仕様書: 業務メトリクスの計上と Grafana ダッシュボード整備

> 本仕様書は実装着手前に作成した。issue [#287](https://github.com/endazon/ai-stock-trading/issues/287)
> の**コードで完結する部分**を対象とする。

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR）: **NFR-07**（運用・保守 / 可観測性 / 全サービスのメトリクス・ログ・トレースを
  基盤の可観測性スタックで収集。OTel/Prometheus/Loki）が本作業の主たる起点である。
  **NFR-13**（LLM API 費用の月次上限）が費用メトリクスの起点。
- 機能要求（FR）: FR-01/FR-02（収集・取引サイクル）・FR-04（判断）・FR-05（発注状態）・
  FR-10 / FR-19（リスク統制・取引ガード）。
- 関連 ADR: 計画 ADR-0006（Hetzner / OTel・Prometheus・Loki）。
- 計画書リンク: `projects/ai-stock-trading/02_requirements/01_requirements.md`（隣接クローンまたは
  GitHub URL で参照。本リポは planning に依存しない）。

### 🔴 NFR の採番について（`traceability.md`「起点 ID の種別」の判断）

**着手前に計画の ID 列を見て判断した。** 計画 `02_requirements/01_requirements.md` は
2026-08-09 に `NFR-01`〜`NFR-17` の ID 列を持っており、**当たる番号 `NFR-07` が実在する**。
したがって本作業は**無採番 `NFR` を許す 2 場合のいずれにも当たらない**。すべての起点 ID は
`NFR-07`（費用面は `NFR-13`）で書く。

## 目的・背景

経路B の監査で、可観測性が**技術メトリクスのみ**で業務判断に使えないと判明した。
Prometheus に入っているのは dotnet ランタイム系のみ、Grafana の業務ダッシュボードは無い。
ログ（Loki）とトレース（Tempo）には蓄積があるので「**事後に追える**」状態ではあるが、
足りないのは「**異常に気づける**」状態である。

### 実測した現況（着手前・規則 8：走査がそのまま返す数を先に出す）

`git grep` を追跡下の `*.cs` 全体に対して実行した実測値である。

| 走査 | 生の件数 | 除外 | 実質 |
| --- | --- | --- | --- |
| `new Meter(` | 0 | — | **0** |
| `CreateCounter`/`CreateHistogram`/`CreateGauge`/`CreateUpDownCounter`/`CreateObservableGauge` | 0 | — | **0** |
| `IMeterFactory` / `System.Diagnostics.Metrics` | 0 | — | **0** |
| `ActivitySource` | 38 行 / 15 ファイル | 全 38 行が `IOrderActivitySource`（注文活動の**ドメイン**ポート） | **0**（`System.Diagnostics.ActivitySource` は 0） |
| `deploy/observability/dashboards/*.json` | 1 | — | 1（技術指標のみ・パネル 5 枚） |

**結論: 独自計器はリポジトリ全体で 1 つも無い。** 現在 Prometheus へ届いているのは
`AddAspNetCoreInstrumentation` / `AddHttpClientInstrumentation` / `AddRuntimeInstrumentation`
が出す既製の系列だけである（`ObservabilityExtensions.AddAiStockTradingObservability`）。

> 本表は**本仕様書を書く前**に実行した走査の結果である。本仕様書自身は `*.cs` ではないため
> 母集合に入らず、自己参照による増加は発生しない。

## 対象範囲

- **対象**: 業務メトリクスの定義・計上・DI 配線、Grafana ダッシュボード定義（リポジトリ管理）、
  ダッシュボードとコードの乖離を止める機械検査、可観測性仕様書の更新。
- **対象外**（issue の注記どおり #24 の管掌）: Hetzner k3s・Vault・CI ゲート運用などのインフラ全般。
- **対象外（実環境依存・後述「実環境待ちの残件」）**: Prometheus / Grafana の実疎通確認、
  MSP の develop 追随、`otel-collector:8888` の scrape target 切り分け。

## メトリクスの取捨選択（規則 6：引いた結果と除外理由を残す）

issue は候補を列挙したうえで「**要取捨選択**」「最低限、統制と取引サイクルの健全性が見えること」と
定める。**候補 12 件を数え、9 件を採用し 3 件を落とした。**

### 採用（9 件）

| # | 計器名（OTel） | 種別 | タグ | 何が見えるか |
| --- | --- | --- | --- | --- |
| 1 | `ast.information.items_collected` | Counter | — | 収集件数（サイクルの起点が動いているか） |
| 2 | `ast.trade_cycle.decisions` | Counter | `action`(buy/sell/no-trade), `trigger` | 判断回数と Buy/Sell/見送りの内訳 |
| 3 | `ast.trade_cycle.decision_duration_ms` | Histogram | `trigger` | 判断レイテンシ（NFR-01/02 の下地） |
| 4 | `ast.risk.screenings` | Counter | `outcome`(approved/rejected) | 統制が**動いていること**自体 |
| 5 | `ast.risk.rejections` | Counter | `reason` | 見送り理由の内訳（採算・上限超過・kill switch・pause 等） |
| 6 | `ast.order.executions` | Counter | `status`, `provider` | 発注数と Accepted/Rejected/失注の別、発注先の別 |
| 7 | `ast.order.dispatch_forgone` | Counter | `reason` | 発注に**届いてすらいない**見送り（OpenD 断・逆指値不能） |
| 8 | `ast.llm.cost_jpy` | Counter | `category`(Llm/LlmUncapped) | LLM 費用の計上（上限対象と対象外を分ける） |
| 9 | `ast.llm.cost_limit_ratio_percent` | Gauge | — | 月次上限に対する比率（80%/100% のしきい値が見える） |

**#4 と #5 を対にしたのは意図的である。** 拒否理由だけを数えると「違反 0 件」と
「そもそも審査が動いていない」を区別できない（planning の #387 と同型の fail-open）。
`ast.risk.screenings` が承認・拒否の**両方**を数えることで、0 が正当な 0 になる。

### 落とした候補（3 件）と理由

| 落とした候補 | 理由 |
| --- | --- |
| **残枠（日次・1 注文）** | 値を得るには `PortfolioSnapshotBuilder.Build()`（DB 参照・ブローカー観測の合成）が要る。ObservableGauge のコールバックは**メトリクス収集の周期でスクレイパ側の都合で走る**ため、そこから I/O を伴うスナップショット構築を叩くと負荷と副作用がリスクになる。**残枠の枯渇は #5 の `reason` 内訳（`OrderAmountLimitExceeded` / `DailyOrderAmountLimitExceeded`）で観測できる**ため、健全性の把握には足りる。専用の残枠ゲージは供給の設計（キャッシュ・更新契機）を別途決めてから入れる |
| **約定反映の遅延** | 発注時刻→約定反映時刻の相関に使える記録が現状ない（`OrderFillPoller` はポーリング時点の `ExecutedAt` しか持たず、ディスパッチ時刻を保持していない）。**値を発明しない**という既存の規律（IADR-0159 / IADR-0163）に従い、供給元を確保する変更と併せて別 issue で扱う |
| **LLM 拒否率（MSP/IADR-0110 の計上を消費する）** | 消費すべき計上が MSP 側 develop にあり、**MSP の追随は本作業では実行できない**（別リポジトリの取得・マージが要る）。実環境残件として明示する。ここで「拒否率」の名前だけ作ると、**値が来ないダッシュボードのパネルが「0 件＝正常」に見える**という最悪の形になる |

### 単位を計器に付けない（命名の規約）

OTel の Prometheus 変換は `unit` を名前へ**接尾**する（既存の
`http_server_duration_milliseconds_count` がまさにその形）。ダッシュボードとコードの機械照合を
1 本の変換規則で書けるようにするため、**本作業の計器には `unit` を設定せず、単位は名前へ埋める**
（`..._duration_ms` / `..._cost_jpy` / `..._ratio_percent`）。変換規則は
`ドットを _ へ置換` ＋ `Counter なら _total` ＋ `Histogram なら _bucket/_count/_sum` の 3 つだけになる。

## 設計

### 1. 計器の置き場所

`AiStockTrading.Shared.Contracts/Observability/` に置く。

- `BusinessMetricNames`: 計器名の `const` レジストリ。**メトリクス名はコードと Grafana
  ダッシュボードの間の契約**であり、契約プロジェクトが正しい置き場である。
- `BusinessMetrics`: `Meter` と 9 本の計器を保持する `sealed` クラス（DI シングルトン）。

`Shared.Contracts` は**外部パッケージ参照ゼロ**でなければならない（`DomainLayerDependencyTests`
の検査 3 が推移閉包で強制する）。`System.Diagnostics.Metrics.Meter` / `Gauge<T>` は net10.0 の
共有フレームワークに含まれ、`PackageReference` を要さないことを実測で確認した。

### 2. DI 配線（composition root）

`AddAiStockTradingObservability`（PlatformShim。**11 サービス全部が呼ぶ唯一の可観測性配線**）へ
2 行を足す。

- `services.TryAddSingleton<BusinessMetrics>()`
- `.AddMeter(BusinessMetricNames.MeterName)`（OTel のメトリクスパイプラインへ計器源を登録）

**計器を定義しても DI に登録されていなければ 1 つも出ない**ため、ここは単体テストではなく
composition root を起こすテストで固定する（後述）。

### 3. 計上点（既存の処理点で発火させる。新規イベントは 1 つも足さない）

| サービス | 計上点 | 計器 |
| --- | --- | --- |
| InformationCollection | `CollectionPollingService.RunOnceAsync` | 1 |
| TradeDecision | `PriceMovementDetectedHandler` / `InformationCollectedHandler` | 2, 3 |
| RiskManagement | `TradeDecisionMadeHandler` | 4, 5 |
| OrderExecution | `OrderApprovedHandler` | 6, 7 |
| CostControl | `LlmCostIncurredHandler` | 8, 9 |

**`Shared.Contracts` へイベントを 1 つも足さない。** 足せば
`AuditEntryFactory` / `AuditEventHandlers` / `NotificationFormatter` / `NotificationHandlers` /
`event-schemas.baseline.json` / ゴールデン表という 6 つの全数レジストリへの追随が要るが、
**既存イベントの発行点で計器を叩けば足りる**ため不要である。

依存は**必須（省略可能引数にしない）**とする。省略可能にすると `Program.cs` から配線が消えても
コンパイルが通りテストは全緑のまま**計上だけが静かに止まる**（IADR-0163 決定 2 の規律）。

### 4. ダッシュボード

`deploy/observability/dashboards/ai-stock-trading-business.json` を新設する（既存の
`ai-stock-trading-overview.json`＝技術指標は触らない）。issue の要求どおり
**「取引サイクルが回っているか」「統制が効いているか」「費用が上限に近づいていないか」を 1 画面**に
収める。

### 5. 乖離を止める機械検査

**「リポジトリに置いた」だけでは、コードとダッシュボードは黙って乖離する。** 2 層で止める。

| 層 | 検査 | 何を守るか |
| --- | --- | --- |
| `scripts/check-observability-assets.js`（CI の `doc-checks`） | ダッシュボード JSON の妥当性＋**レジストリ ↔ ダッシュボードの双方向一致**＋ dev collector が metrics を `debug` にしか出さないこと | 名前の綴り違い・パネルの参照切れ・**使われていない計器**・既定での外部送信 |
| `BusinessMetricsTests`（C#） | **レジストリ ↔ 実際に `Meter` が作った計器名**の一致（`MeterListener` で実測） | レジストリだけ直して計器を直さない（またはその逆） |

**この 2 層は重複ではない。** node 側はコードの計器そのものを見られず（`const` の文字列しか
読めない）、C# 側は JSON を見ない。片方だけでは「レジストリと実装は合っているがダッシュボードが
古い」または「ダッシュボードとレジストリは合っているが計器名が違う」が素通りする。

## 受け入れ基準

issue 原文の 4 項目を、**本作業で満たせるもの**と**実環境待ち**に分けて確定する。

- [x] （1 の代替）**業務メトリクスが実際に値を刻むことを `MeterListener` で観測して固定する。**
      「メトリクスを定義した」ではなく「取引サイクルの処理点を通すと計器が発火する」ことを、
      各サービスのハンドラテスト（Wolverine の実ホスト）で確認する
- [ ] （1 の残り・**実環境待ち**）Prometheus に系列が出ており、取引サイクル 1 巡回で値が動くこと
- [x] （2）Grafana ダッシュボードがリポジトリ管理され、投入手順が文書化されている
- [x] （3）既定では計装が有効でも**外部へ送らない**（IADR-0094 の opt-in の作法を踏襲）。
      **肯定形と否定形の対で固定する**——(a) opt-in（リーダを付けた MeterProvider）では業務メトリクスが
      確かにパイプラインを通って出ていくこと、(b) 既定の dev collector 構成では metrics の
      exporter が `debug`（標準出力のみ）だけであること
- [ ] （4・**実環境待ち**）MSP を develop へ追随させ、拒否率計上（MSP/IADR-0110）が経路B で見えること

### 実環境待ちの残件（達成したふりをしない）

1. **Prometheus 疎通**: 実 Prometheus / Grafana / k3s が本作業環境に無い。系列が実際に
   Prometheus へ現れることは未確認である。
2. **MSP の develop 追随**: 別リポジトリの取得・マージが要り、本作業の作業ディレクトリ外である。
   LLM 拒否率メトリクスは追随後に別 issue で足す。
3. **`otel-collector:8888` の scrape target down の切り分け**: 実機が無く、issue 自身が
   「文書化に留めてよい」としている。可観測性仕様書へ整理を書く。

## テスト方針

| 受け入れ基準 | テスト |
| --- | --- |
| 計器が実際に値を刻む | `BusinessMetricsTests`（`MeterListener` で 9 本すべての発火・タグを実測） |
| レジストリと計器名が一致する | 同上（`BusinessMetricNames` の `const` をリフレクションで母集合として引き、`Meter` が作った名前と突き合わせる） |
| DI に登録されている（配線の消失を検知） | `BusinessMetricsWiringTests`（`AddAiStockTradingObservability` から `ServiceProvider` を組む composition root テスト） |
| opt-in で確かに出ていく（肯定形） | 同上（`MeterProvider` にリーダを付け `ForceFlush` して、業務メトリクスが export されることを実測） |
| `AddMeter` の行が効いている（否定形） | 同上（`AddMeter` を含まない `MeterProvider` では export されないことを実測。**行が消えても緑になる**のを防ぐ） |
| 処理点で発火する | 各サービスのハンドラテスト（Wolverine 実ホスト）に `MeterListener` の表明を追加 |
| ダッシュボードが妥当・コードと一致 | `scripts/check-observability-assets.js`（`--self-test` 付き）＋ `scripts.repo.test.js` |
| 既定で外部へ送らない | 同スクリプトが `infra/otel/otel-collector-config.yaml` の metrics パイプラインを検査 |

統制系（`ast.risk.rejections`）は**境界値（理由ごとの写像）・プロパティベース（理由列挙の全要素が
1 件ずつ計上される）・否定形（承認時に拒否カウンタが動かない）**の 3 点で書く。

## 計画書との差異

- 差異: **なし**（NFR-07 の「全サービスのメトリクスを収集」を実装側で具体化したのみ）。
- 気付いた点（環流の候補・本 PR では起票しない）: 計画の NFR-07 は「メトリクスを収集する」までを
  定めるが、**どの業務指標を見れば統制の健全性が判定できるか**は計画側に無い。閾値
  （例: 「判断が N 分間 0 件なら異常」）も同様である。運用開始後に実測してから環流するのが妥当と考える。

## 未決事項

- Prometheus 側の系列名は otel-collector の exporter 構成（`add_metric_suffixes` 既定 true）に
  依存する。ダッシュボードのクエリは既定を前提に書き、README にその旨を明記する。
- ダッシュボードの閾値（費用比率 80/100 は計画の統制値と一致）以外の閾値は置かない
  （実測が無いまま閾値を置くと、最初のアラートで狼少年になる）。
