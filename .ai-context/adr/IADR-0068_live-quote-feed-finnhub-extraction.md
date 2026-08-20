---
title: IADR-0068 実市況は Finnhub の HTTP 層を共有物へ抽出して供給し、構成で opt-in・既定は no-op のままとする
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-01, FR-03, FR-16, ADR-0004, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-17
updated: 2026-07-17
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0068: 実市況は Finnhub の HTTP 層を共有物へ抽出して供給し、構成で opt-in・既定は no-op のままとする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-17
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制）、FR-01（情報収集）、FR-03（市場監視）、FR-16（報告書の数値定義）、
  ADR-0004（情報源＝案A+）、ADR-0008（段階ゲート）
- 対象 Issue: [#158](https://github.com/endazon/ai-stock-trading/issues/158)（実市況実装）／親 [#81](https://github.com/endazon/ai-stock-trading/issues/81)（受け入れ条件 1）
- 関連する実装仕様書: [20260717_live-market-data-feed](../specs/20260717_live-market-data-feed.md)
- 関連 IADR: [IADR-0066](IADR-0066_market-valuation-supply-and-gate.md)（**本 IADR の前提**。供給経路・前回値
  フォールバック・ゲートを決めた。本 IADR はその決定 1 が後続へ送った「実市況の実装」を埋める）、
  [IADR-0064](IADR-0064_official-source-connectors.md)（Finnhub コネクタ＝抽出元・ソース単位レート制限）、
  [IADR-0022](IADR-0022_information-collection-safe-sourcing.md)（情報源選択の fail-safe の形）、
  [IADR-0049](IADR-0049_integration-e2e-foundation.md)（CI と実基盤依存テストの切り分け）、
  [IADR-0048](IADR-0048_runtime-scaffold.md)（base appsettings の挙動中立）、
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（OpenD 実稼働のゲート＝候補 2 が従属する先）

## 背景・課題

IADR-0066 で `IMarketDataSource`（現在値の唯一のポート）・既定 no-op・前回値フォールバック・
`MarketData:EnableMarkToMarket` ゲートまでは通したが、**実装が `NoOpMarketDataSource` しか無い**ため、
含み損益・DD は構成を何ひとつ変えても 0 のままである（#81 受け入れ条件 1 が未充足）。

IADR-0066 決定 1 は実市況の候補を 2 つ挙げ、どちらを採るかを後続（#158）へ送った。加えて、Finnhub を採る場合は
「`InformationCollection.Worker` 内の `internal` 実装のため共有化の切り出しが要る」という前提条件も残している。
つまり本決定は「どちらを採るか」と「どう切り出すか」の 2 点を決める必要がある。

## 決定

### 1. 実市況ソースは Finnhub `/quote` を採る（OpenD 市況は採らない）

IADR-0066 決定 1 の 2 候補のうち **Finnhub `/quote`** を実装する。理由は本 IADR「根拠」のとおりで、要点は
「実接続を伴う新規実装がほぼゼロ（同じ API を叩くコネクタが #9 で実証済み）」かつ「OpenD 実稼働ゲート
（IADR-0060）に従属しない」こと。OpenD 市況サブスクリプションは**却下ではなく見送り**とし、
moomoo 実弾解禁（IADR-0060）の後に、日本株の現在値が要ると判明した時点で再評価する。

### 2. 抽出の境界は「HTTP 呼び出し＋応答解析」に置き、`Quote` への写像には置かない

`FinnhubQuoteClient`（`Shared.Infrastructure`）が `/quote` を 1 銘柄ぶん呼び、`FinnhubQuoteSnapshot`
（`Current`/`High`/`Low`/`PreviousClose`/`AsOf`）へ解析するところまでを担う。その上に**薄い写像を 2 つ**置く。

- `FinnhubMarketDataSource : IMarketDataSource`（共有）: スナップショット → `Quote`（`Price = Current`）
- `FinnhubInformationSource : IInformationSource`（情報収集・現状の場所のまま）: スナップショット →
  `RawInformationItem`（`current`/`high`/`low`/`prevClose` を含む**現行と同一の文字列**）

**新しい契約は増やさない**（`IMarketDataSource` は既存ポート）。`FinnhubQuoteSnapshot` は Finnhub 応答の
写しであって共有の抽象ではなく、当該アダプタの内部表現として同じ場所に置く。

### 3. レート制限一式を `Shared.Infrastructure` へ移し、時刻源は `TimeProvider` に揃える

`TokenBucket`（`InformationCollection.Domain`）・`IRateLimiter`（同 `Application/Ports`）・
`DelayingRateLimiter`（同 `Worker`）を `Shared.Infrastructure/Composable/RateLimiting/` へ移動する
（ロジックは不変・`DelayingRateLimiter` は `internal` → `public`）。共有クライアントも要求前の自制（IADR-0064）を
要するが、`Shared.*` からサービスを参照するのは依存方向の違反であり、移動以外に共有する手が無い。

移動に伴い `DelayingRateLimiter` の時刻源を `IClock`（情報収集のポート）から `TimeProvider` へ替える。
`Shared.Infrastructure` の既存物（`PaperBrokerAdapter`・`LastKnownQuoteSource`・`QuoteRefreshService`）は
`TimeProvider` を使っており、サービス固有のポートを共有物へ持ち込まないため。**情報収集の `IClock` は残す**
（`EdinetInformationSource` の日付計算等で使う）。レート制限だけが `TimeProvider` になる。

### 4. レート枠はサービスごとの予算を構成で配る（協調はしない）

実フィードが入ると同じ Finnhub の枠を **4 プロセスが独立に**消費する（情報収集の巡回・リスク管理の補充・
市場監視の巡回・報告書のドラフト生成時）。プロセスをまたぐトークンバケットの協調は行わず、
`MarketData:Finnhub:RequestsPerMinute`（既定 **10**）で各サービスへ予算を配り、**合計が無料枠を超えない**ように
運用で守る。既定の内訳は 情報収集 30（既存・IADR-0064）＋ 市況 10 × 3 サービス = **60 回/分 = Finnhub Free の上限**。

これで足りない（＝銘柄数が増えて補充が枠に収まらない）と live 検証で分かった場合は、IADR-0066 が
「却下した代替案」に挙げた**共有スナップショット**（市場監視が現在値を持ち、リスク管理・報告書が同期照会する）を
再評価する。今それを作らない理由は IADR-0066 と同じで、実測なしに共有機構・s2s 認可面を増やすことになるため。

### 5. 米国以外の市場は要求を出さずに取得不可（null）とする

Finnhub 無料枠の `/quote` は米国株のみ。`Market.UnitedStates` 以外は**要求を出さず**に null を返す
（レート枠を無駄に消費しない・404/空応答を待たない）。日本株の現在値は引き続き取得不可＝含み 0 に倒れる。
市場ごとに 1 回だけ警告する（巡回のたびのログ氾濫を避けつつ、差し替え漏れは気づけるようにする）。

### 6. 選択は構成で行い、既定・不備はすべて no-op へ倒す

`MarketDataSourceFactory`（共有）が `MarketData:Provider` から実装を選ぶ。形は `InformationSourceFactory`
（IADR-0022/0064）に揃える。

| 構成 | 結果 |
| --- | --- |
| 未設定・空・`none`（**既定**） | `NoOpMarketDataSource`（実接続しない＝現行と同一） |
| `finnhub` ＋ `MarketData:Finnhub:ApiKey` あり | `FinnhubMarketDataSource` |
| `finnhub` ＋ キー無し | 警告ログ → `NoOpMarketDataSource`（**起動は失敗させない**） |
| 未知の provider | 警告ログ → `NoOpMarketDataSource` |

**`MarketData:EnableMarkToMarket` の既定（false）は本決定でも変えない**。実フィードが入っても、リスク管理の
含み・DD は人手で有効化するまで 0 のままである（IADR-0066 決定 4）。報告書はゲートを持たないため、実フィードの
opt-in がそのまま評価損益の有効化になる（発注判断を伴わないため・IADR-0066 決定 4 と同じ理由）。

## 根拠

- **Finnhub を先に採る理由**: 同じ `/quote` を叩くコネクタが #9 で実装・実 API で確認済みであり、実市況の
  実装は「既存の HTTP 層の置き場所を変えて `Quote` へ写す」だけになる。対して OpenD 市況は新規プロトコル実装
  ＋実 OpenD 接続で、しかも IADR-0060 のゲート下にある。IADR-0066 が現在値を moomoo から切り離した目的
  （ペーパートレードでも評価損益を出す）に照らすと、**先に入れるべきは moomoo に依存しない方**である。
- **抽出の境界を HTTP 層に置く理由**: 情報収集側を `IMarketDataSource` 経由に付け替えると、`Quote` が `Price` しか
  持たないため `high`/`low`/`prevClose` が落ち、FR-01 の収集内容が劣化する。`Quote` を拡張して 4 値を持たせる案は、
  現在値だけが要る全消費者（リスク管理・市場監視・報告書）に Finnhub 固有の形を押し付ける。共有するのは
  「Finnhub をどう呼ぶか」であって「何を取り出すか」ではない。
- **レート制限を移す理由**: 同じ外部ソースを 2 系統から叩く以上、自制の実装が 2 つあるのは危険（片方だけ直す事故が
  起きる）。移動先が `Shared.Infrastructure` になるのは、共有物からサービスを参照できない依存方向の制約による。
- **プロセス間で協調しない理由**: 協調には共有ストア（Redis 等）か調停サービスが要る。現時点の要求は
  「無料枠 60 回/分に収める」だけで、静的な予算配分で満たせる。実測前に分散レート制限を作るのは過剰。
- **キー無しを起動失敗にしない理由**: `InformationSourceFactory` と同じく、構成不備で**落ちる**より **no-op へ倒れる**方が
  安全側（現在値が取れなければ含みは 0＝保守的な評価に倒れる。IADR-0066 決定 2）。ただし「有効化したつもりで
  効いていない」に気づけるよう必ず警告を出す。

## 影響・トレードオフ

- **既定では何も変わらない**（`Provider` 未設定＝no-op・実接続なし・含み 0・DD 0）。#158 の live 検証と
  `EnableMarkToMarket` の切替は本決定の範囲外で、人手に残る。
- **#81 の受け入れ条件 1 が充足する**（実市場データ源からの供給が構成で可能になる）。ただし実際に値が流れることの
  確認は手動 opt-in の live 検証（CI 対象外・IADR-0049）に依存する。
- **日本株の現在値は依然として取得できない**（Finnhub 無料枠は US のみ）。日本株建玉の含みは 0 のままで、
  DD は引き続き過小に出る。日本株の市況は OpenD 市況（見送り・上記決定 1）か有料ソース（ADR-0005 の方針に従う）待ち。
- **予算 60 回/分は上限ちょうど**で余裕が無い。銘柄数が増える・巡回間隔を詰めると枠を割る。live 検証の確認項目
  （429 が出ないこと）に含め、割るなら共有スナップショットを再評価する（決定 4）。
- `InformationCollection.Worker` が `Shared.Infrastructure` を参照するようになる（従来は `Shared.Contracts` のみ）。
  Worker＝合成ルートからの参照であり、他サービス（リスク管理・報告書・市場監視）の Worker と同じ形になる。
- レート制限のテストが `InformationCollection.{Domain,Worker}.Tests` から `Shared.Infrastructure.Tests` へ移る
  （**内容は不変**）。移設によりテストが消えていないことは、移動前後のテスト名の一致で確認できる。

## 却下した代替案

- **OpenD 市況サブスクリプションを先に実装する**: 見送り（却下ではない）。新規プロトコル実装＋実 OpenD 接続で、
  IADR-0060 のゲート下にある。ペーパートレード時に評価損益が出せないという、IADR-0066 が避けた従属をそのまま招く。
- **`FinnhubInformationSource` を `IMarketDataSource` の上に載せ替える（写像を 1 つにする）**: 却下。`Quote` が
  `Price` しか持たないため `high`/`low`/`prevClose` が落ち、FR-01 の収集内容が劣化する（上記「根拠」）。
- **`Quote` に `High`/`Low`/`PreviousClose` を足す**: 却下。全消費者が現在値しか要らないのに Finnhub 固有の形を
  共有契約へ持ち込む。`Quote` は既に発注・監視・報告書が参照する共有契約であり、変更の波及が抽出の利得に見合わない。
- **レート制限を移さず `Shared.Infrastructure` にもう 1 つ実装する**: 却下。同じ外部ソースへの自制が 2 実装に割れる。
- **レート制限をプロセス間で協調させる（共有ストア）**: 却下（現時点）。静的な予算配分で無料枠を満たせる。
- **`MarketData:Finnhub:Symbols` を構成で列挙する**（情報収集の `Collection:Source:Finnhub:Symbols` と同じ形）: 却下。
  現在値が要る銘柄は保有建玉・監視銘柄から**すでに決まっている**（`QuoteRefreshService` は台帳の建玉、市場監視は
  監視対象を渡す）。列挙すると台帳と乖離した第二の真実になり、建玉があるのに列挙漏れで含みが 0 になる事故を招く。
- **情報収集の Finnhub 構成（`Collection:Source:Finnhub:ApiKey`）を市況でも読む**: 却下。情報収集の有効化と
  市況の有効化は別の判断（後者は最大DD ゲートの入力に効き、レート予算も別枠）。片方を有効化したらもう片方も
  黙って有効になるのは、opt-in の粒度として粗い。同じ Finnhub アカウントの鍵を両方へ設定するのは運用上は自由。
- **キー無しで起動失敗させる**: 却下（上記「根拠」）。

## 残課題（後続）

- **手動 opt-in の live 検証**（実 Finnhub API・CI 対象外）と、その後の `MarketData:EnableMarkToMarket=true` への
  切替 → #158 に残る（本 PR では行わない）。
- **実観測ピークの永続化要否**（IADR-0066 決定 3 のトレードオフ＝実現ベースのピークは DD を過小に出しうる）→
  live 検証後の判断事項として #158 に残る。
- **日本株の現在値**（Finnhub 無料枠は US のみ）→ OpenD 市況 or 有料ソース（ADR-0005）で後続。
- 市場別の取引日境界（JST 固定・IADR-0018）、`HttpPositionStore` の損切り価格 3% 近似（IADR-0030）は本件と別系統。
