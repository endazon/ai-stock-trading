---
title: バックテストの実過去データ源アダプタ（Stooq）と no-op 既定のフォールバック
type: spec
status: In progress
related_ids: [FR-15, FR-20, ADR-0004, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-26
updated: 2026-07-26
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/02_datasource-candidates.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: バックテストの実過去データ源アダプタ（Stooq）

> Issue [#208](https://github.com/endazon/ai-stock-trading/issues/208)。バックテストのバー供給が決定的な
> in-memory 実装しか無く、**実データが供給されない限り Stage 0→1 昇格ゲートが実効化しない**という
> 実環境構築前監査の指摘（High）に対し、**go-live に依存しない in-repo 部分**を閉じる。
>
> **実弾には一切触れない。** 実弾 triple-latch（`Broker__Provider=paper` / `Broker:Moomoo:TrdEnv=simulate` /
> 起動時 real 拒否・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）は不変。本作業で追加する
> 実データ源は **provider 既定 `none`＝外部に一切接続しない**であり、既定構成の実行時挙動は変わらない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-15（バックテスト＝Stage 0 の前提・Must）、FR-20（段階ゲート）
- ADR:
  - [ADR-0004](../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md)
    （情報源は案A+。**「検証・学習用: J-Quants Free ＋ Stooq」**を明記）
  - [ADR-0008](../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md)（段階ゲートとバックテスト）
- 計画書（技術検討）:
  - [02_datasource-candidates.md](../../planning/projects/ai-stock-trading/06_technical/02_datasource-candidates.md)
    §Stooq（無料・登録不要・日足EOD・日本株/米国株・CSV・**個人系サイトでSLAなし**・バックテスト向き）
  - [06_daytrading-review.md](../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md) §3.2（生存者バイアス・ルックアヘッド・コスト過小評価の統制）
- 関連 IADR:
  - [IADR-0043](../adr/IADR-0043_backtest-foundation.md)（バックテスト基盤・`IBarDataSource` と PIT ユニバース）
  - [IADR-0044](../adr/IADR-0044_overfitting-correction.md)（過剰適合統制・データカットオフ）
  - [IADR-0045](../adr/IADR-0045_stage0-gate.md)（Stage 0 判定の合成・**`MinTrials=1` は較正前の暫定**）
  - [IADR-0064](../adr/IADR-0064_official-source-connectors.md)（外部データ源アダプタとレート制御の型）
  - [IADR-0068](../adr/IADR-0068_live-quote-feed-finnhub-extraction.md)（provider 選択・**構成不備は警告して no-op へ倒す**）
  - [IADR-0089](../adr/IADR-0089_backtest-verdict-supply.md)（verdict の供給経路・実 publish ホストは #82）
  - 本作業で新規 [IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md)
- 対象 Issue: [#208](https://github.com/endazon/ai-stock-trading/issues/208)（`Refs #208`）

## 現状（develop で確定した事実）

`develop`（`ac415e8`）の実コードを走査して確定した。

| 項目 | 実態 |
| --- | --- |
| サービス構成 | `BacktestService` は `Domain` / `Application` の**純ライブラリ 2 本のみ**（Worker・API・publish 無し） |
| バー供給 | `Adapters/InMemoryBarDataSource.cs` のみ。「実データ源は後続 Issue」とコメント済 |
| ポート | `IBarDataSource.GetBars(from, to)` ＝**同期・銘柄引数なし・認証概念なし**。実 HTTP 源をそのまま実装できる形ではない |
| ゲート | 7 条件は実装・テスト済（`Stage0GateEvaluator`）。`Stage0GateCriteria.Default.MinTrials = 1` は較正前の暫定 |
| verdict 供給 | `BacktestEvaluatedFactory` が「バス発行の実駆動は go-live ホスト（#82 系）」と明記済 |

### 空データ時の挙動（実測で確定した fail-safe の所在）

バー 0 本のとき、エクイティ曲線が空になり `DoubledCostTotalReturn = 0` / `WalkForwardOutOfSampleReturn = 0` と
なるため、`CostRobustness` と `WalkForward` が落ちて**昇格は拒否される**（受け入れ基準③は現状でも成立）。

ただし `DataCutoffPolicy.IsAllAfterCutoff([]) == true`（空は真空的に真）であり、**データカットオフ条件だけは
空データを検出しない**。この非自明な依存関係を回帰テストで固定する（下表 #10）。

## 目的

1. `IBarDataSource` の実データ実装を用意し、Stage 0 判定を**実過去データで実行できる状態**にする（受け入れ基準①）。
2. 外部接続は **provider 明示指定でのみ有効化**し、既定・構成不備・未知 provider は警告して no-op（外部を叩かない）。
3. 実データ未供給時に**従来どおり昇格拒否**であることを回帰テストで固定する（受け入れ基準③）。
4. 決定性（同一入力→同一結果）を壊さない。取得はシミュレーションの外側で 1 回だけ行う。

## スコープ

### 対象

1. **新ポート `IHistoricalBarSource`（`Ports/IHistoricalBarSource.cs`）**
   - `Task<HistoricalBarLoad> LoadBarsAsync(symbols, from, to, ct)`。既存の同期 `IBarDataSource` は**変更しない**。
   - 戻り値 `HistoricalBarLoad(Bars, Gaps)`: 取得できなかった銘柄を `Gaps` に理由つきで残す。
     無音破棄しない設計は `BacktestRun.UnfilledOrderCount`（#99 レビュー指摘）と同じ方針。
2. **`MaterializedBarDataSource`（`Adapters/`）**
   - 取得済みスナップショットを `IBarDataSource` として供給する本番経路。
   - 正規化（`(Symbol, Market, Date)` の重複排除＝後勝ち／日付・銘柄順の安定ソート）を担う。
     `IBarDataSource` の契約コメントが「正規化はアダプタ側の責務」と定めているため、ここを単一情報源にする。
   - `LoadAsync(source, universe, from, to, ct)` 静的ファクトリで「取得 → 正規化 → スナップショット」を 1 経路にする。
3. **`SecurityUniverse.MembersBetween(from, to)`（Domain）**
   - 期間内に一度でも構成銘柄だった銘柄（＝当時上場・後に上場廃止を含む）を返す。
     取得対象を PIT ユニバースから導出し、生存者バイアスの排除をデータ取得段階から一貫させる。
4. **`StooqHistoricalBarSource`（`Adapters/`）**＋純関数の分離
   - `StooqSymbolMapper`: `Market.Japan` → `<code>.jp` / `Market.UnitedStates` → `<symbol>.us`（小文字化）。
   - `StooqDailyCsvParser`: 日足 CSV（`Date,Open,High,Low,Close,Volume`）の解析（`InvariantCulture`）。
   - 銘柄ごとに `GET {BaseUrl}/q/d/l/?s=<mapped>&d1=<yyyyMMdd>&d2=<yyyyMMdd>&i=d` を**逐次**取得し、
     `IRateLimiter`（`TokenBucket` + `DelayingRateLimiter`）で送信前に自制する。
5. **`NoOpHistoricalBarSource`（`Adapters/`）** — 常に空を返し、初回 1 回だけ警告（`NoOpMarketDataSource` と同型）。
6. **`HistoricalBarSourceFactory` + `BarDataOptions`（`Adapters/`）**
   - `Backtest:BarData:Provider` 既定 `none`。空・`none`・未知 provider は**警告して no-op**（起動は落とさない）。
7. **テスト**（受け入れ基準へ写像。後述）。外部送信はゼロ（`HttpMessageHandler` スタブ）。

### 対象外（実データ依存・go-live 依存。#208 に残置）

| 項目 | 残す先 | 理由 |
| --- | --- | --- |
| `Stage0GateCriteria` の閾値較正（`MinTrials` ほか）と較正根拠の IADR 記録（受け入れ基準②の後半） | **#208 に残置** | 較正は**実データでの実測**そのもの。本 PR は「実データで実行できる状態」までを担い、実測値に基づく閾値決定は実データ取得の実施回で行う |
| Stage 1 昇格の前提条件（実データ合格）の運用仕様書追記 | **#208 に残置** | 較正結果（閾値と根拠）が確定してから書くべき記述であり、先に書くと数値のない空文になる |
| J-Quants Free アダプタ | **#208 に残置** | 2 段認証（refreshtoken→idtoken）＋ページングの契約を実アカウント無しに検証できない。ADR-0004 では Stooq と併記の候補であり、Stooq 単独で日米両市場をカバーできる |
| BacktestService の go-live ホスト（`BacktestEvaluated` の実 publish・実コンテナ E2E） | #82 | IADR-0089 で既に #82 へ整理済 |
| 分足・Tick データ | 対象外 | 計画上は有料プラン（J-Quants Premium）の領域 |

### 変更しないもの

- `IBarDataSource` の既存シグネチャ、`BacktestRunner`、`BacktestSimulator`、Stage 0 判定ロジック。
- `Shared.Contracts`（新規イベント無し）。DB スキーマ（BacktestService は永続化を持たない）。
- 実弾・SIMULATE 関連の設定とコード。

## 設計

### 取得（async）と評価（sync・純粋）を分ける

`IBarDataSource` は同期・純粋のまま残し、実 HTTP 取得は別ポート `IHistoricalBarSource` に置く。
取得はシミュレーションの**外側で 1 回**だけ行い、結果をスナップショット（`MaterializedBarDataSource`）に固定する。

- 決定性が壊れない: ウォークフォワードの分割ごと・試行ごとに再取得して結果が揺れることが構造的に起きない。
- 外部依存がシミュレータへ侵入しない: `BacktestSimulator` は純関数のまま。
- レート制御が意味を持つ: 取得回数が「銘柄数 × 1」に固定される。

### 欠測を無音にしない

Stooq は個人運営で SLA が無く、予告なく停止し得る（計画書 §フォールバックと欠測検知）。
非成功応答・`No data`・解析不能はいずれも**その銘柄を欠測として `Gaps` に記録**し、他銘柄の取得は続ける。

- 部分データによる沈黙のバイアスを避けるため、欠測は件数ではなく**銘柄と理由**で残す。
- 解析不能な行が 1 行でもあれば、**その銘柄を丸ごと欠測**として扱う（部分行の採用は偽の価格ギャップを作り、
  約定不能や誤った DD を生むため）。
- 通信例外は握りつぶさず送出する（`FinnhubQuoteClient` と同じ方針）。取得が失敗すればバックテストは
  完走せず、verdict も出ない＝昇格は起きない（fail-safe）。

### 安全既定（既定構成はバイト等価）

1. `Backtest:BarData:Provider` 既定 `none` → `NoOpHistoricalBarSource`＝**外部へ 1 リクエストも出さない**。
2. Stooq は登録不要で API キーが無いため、opt-in の唯一の閂は provider の明示指定である。
   未知 provider・不正 URL も no-op へ倒し、必ず警告を出す（「有効化したつもりで効いていない」の検知）。
3. バーが 0 本なら Stage 0 は `CostRobustness`／`WalkForward` で落ち、昇格は拒否される（受け入れ基準③）。

### 配置（合成面 `BacktestService.Worker` の新設）

外部 I/O を伴うアダプタ（Stooq 一式・no-op・ファクトリ・オプション）は新設の
`BacktestService.Worker/Composable/Adapters/` に置き、レート制御の基盤依存
（`IRateLimiter`／`TokenBucket`／`DelayingRateLimiter`・IADR-0064 の再利用）も Worker が持つ。
これにより `BacktestService.Application` は同期・純粋なポートと `IBarDataSource` 実装に閉じ、Domain 以外へ依存しない。

ホストの責務は**過去データ源の合成と実効構成の自己申告**に限る（定時実行・verdict の実 publish は行わない。
本番戦略 `IBacktestStrategy` の実装がまだ無く、publish は #82）。公開する HTTP 面は `/health/*` と
`GET /internal/introspection` のみ・無認可（メッシュ内部限定）、DB もメッセージバスも持たない。
根拠は [IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md) 決定 5 に記録する。

**運用面の登録**: `backend.slnx`／`docker-compose.yml`／helm `values.yaml`（`services.backtest`）／
`scripts/k8s-local-images.sh`／`scripts/validate-runtime-scaffold.js`（Worker 一覧）に新サービスを追加する。
有効化点は `Backtest__BarData__Provider`（空既定＝外部接続なし）のみ。

## 受け入れ基準 → テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 日足 CSV を `PriceBar` へ解析する（`InvariantCulture`・日付昇順） | `StooqDailyCsvParserTests.日足CSVをバーへ解析する` |
| 2 | ヘッダ不正・`No data`・空本文は解析失敗として扱う | `StooqDailyCsvParserTests.データなし応答は解析失敗として扱う` ほか |
| 3 | 不正な行（日付/数値/列数/価格 0 以下/OHLC 整合崩れ/出来高負値）が 1 行でもあれば銘柄丸ごと解析失敗（部分採用しない）。寄り天・寄り底の境界は正常として受け入れる | `StooqDailyCsvParserTests.不正な行があれば部分採用せず解析失敗とする` / `始値終値が高値安値の境界と一致する行は受け入れる` |
| 4 | 市場ごとの銘柄記法へ写像する（`.jp` / `.us`・小文字） | `StooqSymbolMapperTests.市場ごとの銘柄記法へ写像する` |
| 5 | 複数銘柄を取得し期間で絞ったバーを返す（スタブ HTTP・外部送信なし） | `StooqHistoricalBarSourceTests.複数銘柄の日足を取得する` |
| 6 | 非成功応答・データなしは欠測として記録し他銘柄の取得を続ける | `StooqHistoricalBarSourceTests.非成功応答は欠測として記録し他銘柄を続行する` |
| 7 | 送信前にレート制御を通す（取得回数＝銘柄数） | `StooqHistoricalBarSourceTests.送信前にレート制御を通す` |
| 8 | provider 既定・空・未知は no-op（外部へ接続しない・警告する） | `HistoricalBarSourceFactoryTests.*`（3 ケース） |
| 9 | `MaterializedBarDataSource` が重複を排除し期間で絞る | `MaterializedBarDataSourceTests.*` |
| 10 | **実データ未供給（バー 0 本）では Stage 0 不合格＝昇格拒否**（fail-safe 維持） | `Stage0GateServiceTests.実データ未供給ならStage0は不合格で昇格しない_failsafe` |
| 11 | 期間内に構成銘柄だった銘柄を取得対象に含める（上場廃止銘柄を落とさない） | `SecurityUniverseTests.期間内に構成銘柄だった銘柄を返す` |
| 12 | PIT ユニバースから取得 → スナップショット化の通し（外部送信なし） | `MaterializedBarDataSourceTests.ユニバースから取得してスナップショットを作る` |
| 13 | ホストの配線（既定 no-op／`stooq` 指定で実データ源／未知 provider でも起動／singleton） | `BacktestWorkerWiringTests.*`（4 ケース） |
| 14 | 実効構成の自己申告が選択中の過去データ源を示す（不正 URL では `none`） | `BacktestWorkerWiringTests.実効構成の自己申告に選択中の過去データ源を載せる` / `ベースURLが不正なら自己申告もno_opを示す` |
| 15 | ヘルスチェックが起動直後に ready（DB・バスを持たない） | `BacktestWorkerWiringTests.ヘルスチェックは起動直後にreadyを返す_DBもバスも持たない` |

## 完了条件

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が緑。
- `dotnet format` 適用済み・警告ゼロ。
- 既定構成での実行時挙動が不変（provider 既定 `none`・外部接続ゼロ・実弾 OFF／SIMULATE 不変）。
- テストが外部ネットワークへ一切送信しない（`HttpMessageHandler` スタブのみ）。
- `docs/DEFINITION_OF_DONE.md` を満たす。
- [IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md) に決定を記録する。

## 残課題（本 PR 外・#208 に残置）

- `Stage0GateCriteria` の実データ較正（`MinTrials` ほか）と較正根拠の IADR 記録。
- Stage 1 昇格の前提条件（実データでのバックテスト合格）の運用仕様書への明記。
- J-Quants Free アダプタ（実アカウントでの契約確認が要る）。
- `BacktestEvaluated` の実 publish ホストと実コンテナ E2E → #82。
