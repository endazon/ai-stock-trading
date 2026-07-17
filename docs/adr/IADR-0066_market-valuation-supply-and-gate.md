---
title: IADR-0066 現在値は moomoo 非依存の既定 no-op ポートで供給し、時価評価は既定オフのゲートで切り替える
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-05, FR-16, FR-03, ADR-0002, ADR-0007, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-17
updated: 2026-07-17
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# IADR-0066: 現在値は moomoo 非依存の既定 no-op ポートで供給し、時価評価は既定オフのゲートで切り替える

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-17
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制）、FR-05（発注）、FR-16（報告書の数値定義）、FR-03（市場監視）、
  ADR-0002（証券会社=moomoo）、ADR-0007（取引ガード・信用）、ADR-0008（段階ゲート）
- 対象 Issue: [#81](https://github.com/endazon/ai-stock-trading/issues/81)（残スコープ＝供給アダプタ＋結線）
- 関連する実装仕様書: [20260717_market-valuation-wiring](../specs/20260717_market-valuation-wiring.md)
- 関連 IADR: [IADR-0036](IADR-0036_unrealized-pnl-valuation.md)（純関数コア。本 IADR はその**供給側**を決める）、
  [IADR-0008](IADR-0008_daily-loss-limit-basis.md)、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)、
  [IADR-0025](IADR-0025_pnl-aggregation.md)。安全既定の踏襲元: [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（既定 no-op の整備を先行させ切替を人手に残す）

## 背景・課題

`PortfolioValuation.UnrealizedPnl` / `DrawdownRatio`（IADR-0036）は純関数として完成しているが、
呼び出し側が常に `currentPrices=null` / `equityHighWaterMark=null` を渡しており、含み損益・DD は 0 のままである。
供給経路（誰がどこから現在値を取るか）が未決だったためで、これは以下の 3 点を同時に決める必要がある。

1. **現在値をどこから取るか**: 素朴には「証券会社（moomoo）から」だが、実装済みの取引ポート `IMoomooTradeClient` は
   `PlaceOrder` / `QueryOrder` / `CancelOrder` のみで **quote（市況）API を持たない**。moomoo から現在値を得るには
   OpenD の**市況サブスクリプション**という別系統（実接続・実基盤依存）が要る。
2. **取得できないときどうするか**: 現在値は常に取れるとは限らない（市況断・レート制限・休場）。
3. **有効化した瞬間に何が変わるか**: 含み・DD が初めて非 0 になり、**取引可否の判定が変わる**。

## 決定

### 1. 現在値ソースは `IMarketDataSource` 一本にし、既定実装は no-op とする

現在値の供給は `Shared.Contracts/Ports` の `IMarketDataSource`（`GetLatestQuoteAsync` → `Quote?`）を唯一のポートとし、
**moomoo（取引ポート）には依存させない**。既定実装は常に取得不可（null）を返す `NoOpMarketDataSource`
（`Shared.Infrastructure`）とし、リスク管理・報告書・市場監視の全てがこれを既定で注入する。

実 OpenD 市況サブスクリプションは**本決定の範囲外**とし、後続 issue＋**手動 opt-in の live 検証**へ分離する。
実装候補は 2 つあり、後続でどちらを採るかを決める。

- **OpenD 市況サブスクリプション**: moomoo 本線。実 OpenD 接続・実基盤依存（IADR-0060 のゲート下）。
- **Finnhub `/quote`**: 既に `FinnhubInformationSource`（#9・IADR-0064）が同 API を叩いており、無料枠・API キー既定なしで
  opt-in。ただし米国株のみ・`InformationCollection.Worker` 内の `internal` 実装のため、共有化の切り出しが要る。

### 2. 未取得時のフォールバックは「保持期限付きの前回値 → 超過は 0」とする

`QuoteCache`（共有）が `(銘柄, 市場)` ごとに直近の取得値と**取得時刻**を保持し、保持期限
（`MarketData:MaxQuoteStalenessSeconds`・既定 300s）の判定も担う唯一の実装とする。取得できなければ期限以内の
前回値を返し、前回値が無い／期限超過なら `null`＝当該建玉の含みは 0（`PortfolioValuation` の既存フォールバック）。
経過時間は `Quote.AsOf` ではなく**取得時刻（受信）**を基準とする（取得成功パスが `AsOf` を評価せずソースの値を
そのまま信じる規約に揃える。`AsOf` の妥当性は当該ソースの責務）。

ただし前回値フォールバックは**発注を伴わない時価評価**（リスク管理の判定入力・報告書の提示）にのみ適用し、
**市場監視の巡回には適用しない**。市場監視は損切りライン到達で `StopLossTriggered`＝実際の決済発注を引き起こすため、
市況断のあいだ古い価格で判定すると既に回復した価格に対して誤発火しうる。取得不可はスキップ（＝発注しない）が
安全側であり、現行挙動でもある。評価では前回値が 0 より実態に近く、発注では前回値が新たな誤動作を生む——
この非対称は意図的である。

### 2-a. リスク管理では補充（非同期・背景）と読み出し（同期・判定時）を分離する

`IPortfolioStateProvider.GetCurrent()` は**同期**で、発注判断の同期経路（`OrderScreeningService.Screen` →
`PortfolioSnapshotBuilder.Build` → `GetCurrent`）から呼ばれる。そこで非同期の市況取得はできず、また発注判断の
レイテンシにネットワーク往復を持ち込むべきでもない。よって `QuoteRefreshService`（`BackgroundService`）が
保有建玉ぶんの現在値を定期的に `QuoteCache` へ補充し、判定側（`CachedCurrentPriceSource`・同期ポート
`ICurrentPriceSource`）は手元の値を読むだけにする。報告書の生成経路は非同期のため分離は不要で、
`LastKnownQuoteSource` デコレータ経由で直接引く。

### 3. エクイティピークは台帳から再計算する（永続化しない）

`DrawdownRatio` の入力となるピークは、純関数 `PortfolioValuation.EquityHighWaterMark(fills, initialCapital, currentEquity)` が
`初期資金 + 累積実現損益` の走査最大と現在エクイティの最大として台帳から求める。

### 4. 時価評価は `MarketData:EnableMarkToMarket`（既定 false）で切り替える

既定 false では現在値ソースを**注入しない**（＝`currentPrices=null` / `equityHighWaterMark=null`）＝**現行と同一挙動**
（含み 0・DD 0）。補充（`QuoteRefreshService`）も登録しないため、巡回による台帳アクセスも起きない。
このゲートは「全環境の既定」である base `appsettings.json` に置いてはならないため、`validate-runtime-scaffold.js` の
`FORBIDDEN_BASE_KEYS` に `MarketData` を加えて機械的に守る（IADR-0048 決定 1/2 に準拠）。

**報告書にはゲートを置かない**。報告書は発注判断を行わず評価損益を提示するだけで、有効化しても取引可否は変わらない。
既定の no-op ソースが取得不可を返すことがそのまま安全既定であり、実市況ソースへの差し替えがそのまま有効化になる。

## 根拠

- **moomoo 非依存にする理由**: 現在値を取引ポートに縛ると、市況が取引クライアントの実接続（OpenD 実稼働・IADR-0060 で
  まだ人手ゲートの下にある）に人質を取られる。現在値は取引の実弾解禁とは独立に必要（報告書の評価損益は
  ペーパートレードでも要る）であり、ポートを分ければ Finnhub 等の非 moomoo ソースも差し替えで入る。
- **既定 no-op（整備先行）の理由**: 実市況は実接続・実基盤依存で CI では検証できない。IADR-0060 と同様に、
  **配線を先に通して既定は no-op**とすれば、CI 緑を保ったまま実装の受け皿が完成し、実接続は手動 opt-in の
  live 検証として切り分けられる。
- **前回値に TTL を付ける理由**: 期限なしの前回値は、市況断のあいだ古い価格に基づく含み・DD を無期限に信じ込ませる。
  これは安全側ではない（実際には下落しているのに DD が出ない）。古すぎる値は「取得不可」に落として 0 へ倒す方が保守的。
- **ピークを永続化しない理由**: 台帳から再計算できるものを持つと、再起動・復旧時に台帳と乖離した第二の真実になる。
  実現ベースのピークは含みだけで生じたピークを捉えず DD を**過小**に見積もるが、ゲート既定オフと併せて現行挙動を
  変えないため、実観測ピークの永続化が要るかは live 検証後に判断すれば足りる（過剰な作り込みを避ける）。
- **ゲートを既定オフにする理由**: 有効化は `DrawdownRatio` を初めて非 0 にし、最大DD の**取引ゲート**（IADR-0008）の
  入力を変える。実市況の live 検証を経ずに取引可否の挙動が変わるのは受け入れられない。切替は人手の判断に残す。

## 影響・トレードオフ

- 既定では**何も変わらない**（含み 0・DD 0）。#81 の受け入れ条件のうち「実市況からの供給」は本決定では満たされず、
  PR は `Refs #81` に留め、残作業（実市況実装＋live 検証＋ゲート ON）を後続 issue へ送る。
- 実現ベースのピークは DD を過小に出しうる（上記のとおり暫定として許容）。
- 実市況が入ると、市場監視（巡回）とリスク管理（補充）が**それぞれ独立に**同じ銘柄の現在値を引く（＋報告書はドラフト
  生成時に引く）。ソース単位のレート制限（IADR-0064）に対して要求が重複するため、実市況の実装時には共有の
  スナップショット（下記「却下した代替案」の 2 つめ）を再評価する余地がある。既定 no-op の現時点では実要求は 0 のため
  先に作らない。
- 報告書側の現在値辞書は**銘柄コードのみ**がキーのため（`PnlAggregator`・IADR-0025）、同一銘柄コードが複数市場に
  現れると市場を判別できない。**曖昧な銘柄はキーを落とす**（＝評価損益 0）とし、誤った市場の価格で評価しない。
  辞書を `(Symbol, Market)` へ変える改修は報告書側の公開 API に波及するため本決定では行わない。

## 却下した代替案

- **moomoo（`IMoomooTradeClient`）を現在値ソースにする**: 却下。当該ポートに quote API が無い。OpenD 市況を足すと
  現在値が OpenD 実稼働ゲート（IADR-0060）に従属し、ペーパートレード時にも評価損益が出せなくなる。
- **市場監視に現在値スナップショット表＋ `GET /market-data/quotes` を設けてリスク管理・報告書が同期照会する**: 却下。
  IADR-0030 の同期照会に倣う形だが、既定 no-op のソースではスナップショットは**恒久的に空**であり、
  新規テーブル・マイグレーション・s2s 認可面を「今は必ず空のデータ」のために増やすことになる。実市況が入って
  なお共有キャッシュが要ると分かった時点で導入すれば足りる。
- **ゲート無しで常時 ON**: 却下。最大DD ゲートの入力が live 検証前に変わる。
- **前回値を期限なしで使う**: 却下。市況断のあいだ DD を過小に出し続ける（上記「根拠」）。
- **`IPortfolioStateProvider.GetCurrent()` を非同期化して判定時に現在値を引く**: 却下。発注経路
  （`OrderScreeningService`・`PortfolioSnapshotBuilder`・コンシューマ・各テスト）へ広く波及して並行作業との衝突面が
  大きい割に、補充と読み出しの分離（決定 2-a）で同じ結果が得られる。加えて**発注判断ごとに市況取得の往復が入る**ため
  レイテンシ面ではむしろ劣り、市況の遅延が発注の遅延に直結する。
- **ピークを DB に永続化して実観測ピークを追う**: 見送り（却下ではない）。台帳と乖離する第二の真実を作る割に、
  ゲート既定オフの現時点では観測差が出ない。live 検証後に必要性を再評価する。

## 残課題（後続）

- 実市況実装（OpenD 市況 or Finnhub 共有化）＋手動 opt-in の live 検証＋ゲート ON → [#158](https://github.com/endazon/ai-stock-trading/issues/158)（#81 から分離）。
- **市場別の取引日境界**: 単一取引日境界は `PortfolioProjection.TradingDayOffset`（JST=+9・DST なし）を唯一の定義とし
  変更しない。米国市場の取引日が JST 境界と一致しない点は IADR-0018 からの既知の残課題として引き続き後続とする。
- 市場監視 `HttpPositionStore` の損切り価格 3% 近似（IADR-0030）は本件と別系統のため対象外のまま。
