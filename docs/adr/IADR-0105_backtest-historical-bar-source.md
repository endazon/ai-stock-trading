---
title: IADR-0105 バックテストの実過去データ源は非同期ポート（IHistoricalBarSource）で取得しスナップショットへ固定する（Stooq・既定 no-op）
type: impl-adr
status: Accepted
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

# IADR-0105: バックテストの実過去データ源は非同期ポートで取得しスナップショットへ固定する（Stooq・既定 no-op）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-26
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-15（バックテスト＝Stage 0 の前提・Must）、FR-20（段階ゲート）、
  [ADR-0004](../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md)（情報源＝案A+。**検証・学習用に J-Quants Free ＋ Stooq**）、
  [ADR-0008](../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md)（段階ゲートとバックテスト）
- 対象 Issue: [#208](https://github.com/endazon/ai-stock-trading/issues/208)
- 関連する実装仕様書: [20260726_backtest-historical-bar-source](../specs/20260726_backtest-historical-bar-source.md)
- 関連 IADR: [IADR-0043](IADR-0043_backtest-foundation.md)（バックテスト基盤・`IBarDataSource` と PIT ユニバース）、
  [IADR-0044](IADR-0044_overfitting-correction.md)（過剰適合統制・データカットオフ）、
  [IADR-0045](IADR-0045_stage0-gate.md)（Stage 0 判定の合成・`MinTrials` 暫定値）、
  [IADR-0064](IADR-0064_official-source-connectors.md)（外部データ源アダプタとレート制御）、
  [IADR-0068](IADR-0068_live-quote-feed-finnhub-extraction.md)（provider 選択・構成不備は警告して no-op）、
  [IADR-0089](IADR-0089_backtest-verdict-supply.md)（verdict 供給・実 publish ホストは #82）

## 背景・課題

実環境構築前監査（2026-07-18）が「バックテストのバー供給が決定的な in-memory 実装しか無く、**実データが供給されない
限り Stage 0→1 昇格ゲートは実効化しない**」を High として指摘した（#208）。

`develop` の実コードでも `IBarDataSource` の実装は `InMemoryBarDataSource` だけで、ポート自身が
「実データ源コネクタ（J-Quants Free / Stooq 等）は本ポートのアダプタとして後続 Issue で差し込む」と記していた。
一方でポートの形は `IReadOnlyList<PriceBar> GetBars(DateOnly from, DateOnly to)` ＝**同期・銘柄引数なし**であり、
外部 API アダプタをそのまま実装できる形ではない。

## 決定

### 1. 取得（非同期・I/O）と評価（同期・純粋）を別ポートに分ける

`IBarDataSource`（評価側）は**シグネチャを変えない**。実データ取得は新ポート `IHistoricalBarSource` に置く。

```
Task<HistoricalBarLoad> LoadBarsAsync(symbols, from, to, ct)   // 取得（非同期・失敗し得る）
IReadOnlyList<PriceBar> GetBars(from, to)                      // 評価（同期・純粋・既存のまま）
```

取得はシミュレーションの**外側で 1 回だけ**行い、結果を `MaterializedBarDataSource`（スナップショット）へ固定する。

- **決定性が壊れない**: ウォークフォワードの分割ごと・試行ごとに再取得して結果が揺れることが構造的に起きない。
  同じ入力から同じ verdict が出る性質は、段階ゲートの根拠として譲れない。
- **純関数のシミュレータへ外部依存が侵入しない**: `BacktestSimulator` / `BacktestRunner` は無改修。
- **レート制御が意味を持つ**: 外部への要求回数が「銘柄数 × 1」に固定される。

`IBarDataSource` を非同期化する案（`GetBarsAsync`）は、評価側の純粋性を壊し既存呼び出しを全面改修する割に、
「取得は 1 回で足りる」という事実を表現できないため採らなかった。

### 2. 安全既定は no-op（provider 明示指定でのみ外部へ接続する）

`Backtest:BarData:Provider` は既定・空・`none`・**未知の値**のすべてで `NoOpHistoricalBarSource` を返す
（`MarketDataSourceFactory`・IADR-0068 と同形）。ベース URL が絶対 http/https でない場合も no-op へ倒す。

Stooq は登録不要で API キーを持たないため、**opt-in の唯一の閂は provider の明示指定**である。
構成不備で起動は落とさない（バーが取れなければ Stage 0 が不合格になり昇格が止まる＝安全側に縮退する）。
ただし「有効化したつもりで効いていない」に気づけるよう必ず警告する。

> **改訂（2026-08-06・[IADR-0156](./IADR-0156_us-ohlc-history-source-absence.md) / 計画 ADR-0023 / [#382](https://github.com/endazon/ai-stock-trading/issues/382)）**:
> 本決定の**構造**（provider 明示指定でのみ外部へ接続する・構成不備は起動を落とさず警告して no-op へ倒す）は
> 有効なまま残る。改まったのは**前提**である。本 ADR 起案時（2026-07-26）の既定 `none` は「**差し替え漏れ**
> （設定すれば Stooq が使える）」を意味していたが、計画 ADR-0023 決定1（2026-08-04）が Stooq を取得不能として扱い
> **ボット検知チャレンジの回避実装を禁じた**ため、現在の既定 `none` は「**差し替え先の不在**（設定できる先が無い）」を
> 意味する。代替源として moomoo が実測されたが**採用には計画側の改定裁定と実装の両方が要り、いずれも未了**である。
> したがって **Stage 0 の合格判定は現時点で一度も発火し得ない**。現行の読み方は IADR-0156 を正とする。
> 本文は書き換えていない（履歴として残す）。

### 3. 欠測は無音破棄せず銘柄と理由で残す

`HistoricalBarLoad(Bars, Gaps)` として、非成功応答・データなし・解析不能・銘柄記法へ写像不能を
`HistoricalBarGap(Symbol, Market, Reason)` に残す。`BacktestRun.UnfilledOrderCount`（#99 レビュー指摘）と同じ方針。

- Stooq は個人運営で SLA が無く予告なく停止し得る（計画書 02_datasource-candidates.md）。
  1 銘柄の失敗で全体を止めない一方、**銘柄が丸ごと欠けた検証を「合格材料」として黙って通さない**。
- **解析は部分採用しない**: 1 行でも壊れていればその銘柄を丸ごと欠測とする。壊れた行だけを捨てると偽の価格
  ギャップができ、約定不能や過小な DD として Stage 0 判定へ静かに混入する。
  破損の判定は OHLC の整合まで見る（価格が 0 以下・高値 < 安値・**始値/終値が [安値, 高値] の外**・出来高負値）。
  約定は翌営業日始値・時価評価は終値で行うため、始値/終値がレンジ外の行はそのまま指標を壊す。
  境界一致（寄り天・寄り底）は正常データとして受け入れる。
- 通信例外は握りつぶさず送出する（`FinnhubQuoteClient` と同じ方針）。取得が失敗すればバックテストは完走せず、
  verdict も出ない＝昇格は起きない。

### 4. 取得対象は PIT ユニバースから導出する（生存者バイアス排除を取得段階から）

`SecurityUniverse.MembersBetween(from, to)` を追加し、**期間内に一度でも構成銘柄だった銘柄**（期間中に上場廃止
された銘柄を含む）を取得対象にする。現存銘柄だけを取りに行くと、日付単位のフィルタ（`MembersAsOf`）が正しくても
入力データの時点で生存者バイアスが混入する。日付単位の構成判定は従来どおり `BacktestRunner` が権威。

### 5. 合成面として `BacktestService.Worker` を新設し、アダプタと基盤依存をそこに閉じる

外部 I/O を伴うアダプタ（`StooqHistoricalBarSource` / `StooqDailyCsvParser` / `StooqSymbolMapper` /
`NoOpHistoricalBarSource` / `HistoricalBarSourceFactory` / `BarDataOptions`）は
`BacktestService.Worker/Composable/Adapters/` に置く（他サービスの慣行と同じ）。`AiStockTrading.Shared.Infrastructure`
への参照（`IRateLimiter` / `TokenBucket` / `DelayingRateLimiter`・IADR-0064 の再利用）も Worker が持つ。

これにより **`BacktestService.Application` は同期・純粋なポートと `IBarDataSource` 実装に閉じ**、
Domain 以外への依存を持たない（レイヤリングの逸脱なし・レート制御の実装も 2 つに割らない）。

**本ホストの責務は実過去データ源の合成に限る。** Stage 0 判定そのものは純ドメインが持ち、ホストは
定時実行も verdict の実 publish も行わない。理由は 2 つある。

- **本番戦略（`IBacktestStrategy` 実装）がまだ存在しない**（リポジトリ内の実装はテストダブルのみ）。
  実行する対象が無い以上、定時トリガを置いても回すものが無い。
- `BacktestEvaluated` の実 publish と実コンテナ E2E は #82（IADR-0089 で整理済）。

なお [IADR-0089](IADR-0089_backtest-verdict-supply.md) / [IADR-0103](IADR-0103_observed-drawdown-supply.md) と当時の作業仕様書は
「`BacktestService` は Domain＋Application のライブラリのみでホストを持たない」を前提に書かれている。その前提は本 ADR で
更新される（履歴文書は書き換えず、現在の権威は本 ADR）。ただし**実 publish が #82 である点は変わらない**。

したがって現時点のホストは「構成から過去データ源を解決し、実効構成を自己申告する」薄い合成面である。
公開する HTTP 面は `/health/*` と `GET /internal/introspection` のみで、いずれも無認可（メッシュ内部限定）。
DB もメッセージバスも持たない。定時トリガ・publish を足す場所はこのホストであり、別 issue で載せる。

### 5.1 実効構成の自己申告で「効いていない有効化」を検知可能にする

`GET /internal/introspection` が選択中の過去データ源（`historical-bar-data` ポート）を申告する（#22 受け入れ基準③）。
申告値と実際の選択がずれると検知そのものが嘘になるため、選択規則は `HistoricalBarSourceFactory.ResolveProvider`
を単一情報源とし、`Create` と自己申告が同じ答えを返すことを構造で保証する（ベース URL 不正時は双方 `none`）。

### 6. `InMemoryBarDataSource` はテスト・検証用に限定する

本番経路（実データのスナップショット供給）は `MaterializedBarDataSource` が担う。
`InMemoryBarDataSource` の用途をコメントで明示的にテスト・検証へ限定した（#208 受け入れ基準①）。

### 7. 閾値較正（`MinTrials` ほか）は本 ADR では決めない

`Stage0GateCriteria.Default.MinTrials = 1` は IADR-0045 が明記した暫定値であり、較正には**実データでの実測**が要る。
本 PR は「実データで実行できる状態」までを担い、実測に基づく閾値決定と根拠の記録は #208 に残す。
実測していない値を「較正済み」として ADR に書くことは、統制の根拠を偽装することになるため行わない。

## 検討した代替案

- **`IBarDataSource` を非同期化して HTTP アダプタを直接実装する**: 評価側の純粋性・決定性を壊す。
  取得のたびに外部へ出る形は、ウォークフォワードや試行の反復で結果が揺れ得る。
- **J-Quants Free アダプタを同時に実装する**: 2 段認証（refreshtoken→idtoken）とページングの契約を
  実アカウント無しに検証できず、スタブだけで書いた推測コードが残る。ADR-0004 では Stooq と併記の候補であり、
  Stooq 単独で日米両市場をカバーできるため、実アカウントを用意する回に回す（#208 に残置）。
- **欠測を件数だけ持つ／黙って落とす**: 「取れなかった銘柄」が分からないと、欠測だらけの検証で Stage 0 を
  合格させ得る。監査可能性（FR-11）の観点でも銘柄と理由を残す方が良い。
- **壊れた行だけを捨てて残りを採用する**: 偽の価格ギャップを作り、Stage 0 判定へ静かに混入する。
- **Stooq を既定で有効化する**: 「既定では外部へ接続しない」という本リポの安全既定（IADR-0066/0068）から外れる。
  外部サイトへの意図しないアクセスも避ける。

## 影響・トレードオフ

- **良い点**: `IBarDataSource` の実データ実装が初めて用意され、Stage 0 判定を実過去データで実行できるようになる。
  既定構成では外部へ 1 リクエストも出ないため、既定の実行時挙動は不変。実データ未供給時の昇格拒否（fail-safe）は
  回帰テストで固定した。
- **トレードオフ**: Stooq は日足 EOD のみで SLA が無い。分足・Tick を要する執行分析には使えない
  （計画上は有料プランの領域）。欠測時の代替源（J-Quants）は未実装のため、当面フォールバックは「欠測として記録」に留まる。
- **トレードオフ**: 実行する仕事（定時バックテスト）を持たないホストが 1 つ増える（決定 5）。合成面が実在することで
  「構成で有効化したつもりが no-op のまま」を配線テストと自己申告で検知できる一方、compose / helm に
  当面ほぼ何もしないサービスが並ぶ。本番戦略の実装と定時トリガが載るまではこの状態が続く。
- **残る前提**: `HttpClient` の調整（タイムアウト・User-Agent・リトライ方針）は合成面（将来の go-live ホスト・#82）の
  責務として外に出してある。アダプタは注入された `HttpClient` をそのまま使う（`FinnhubQuoteClient` と同じ形）。
  実 Stooq に対する挙動（応答ヘッダ・実効レート上限・User-Agent 要否）の確認は実データ取得の実施回（#208）で行う。
- `Shared.Contracts` は不変（新規イベント無し）。DB スキーマ変更なし（BacktestService は永続化を持たない）。
- 実弾 triple-latch（IADR-0060）・SIMULATE 固定には一切触れない。
