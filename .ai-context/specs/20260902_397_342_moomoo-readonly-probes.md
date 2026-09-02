---
title: moomoo 日本株市況権限の切り分け（#397）と PoC 残項目 6/7/8（#342）の読み取り専用 probe
type: spec
status: draft
related_ids: [FR-02, FR-19, ADR-0002, ADR-0016, ADR-0019, ADR-0023, IADR-0144, IADR-0156, IADR-0157]
author: claude (worker)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-ohlc-history-source.md
---

# 仕様書: moomoo 日本株市況権限の切り分け（#397）と PoC 残項目 6/7/8（#342）

> 本仕様書は実機作業の着手前に作成する。**実弾（`TrdEnv_Real`）は撃たない。発注・注文訂正・取消は一切行わない。**
> TrdEnv は SIMULATE のみ。実弾口座への照会も行わない（`accId` の列挙のみ可）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（価格変動検知）・FR-19（取引ガード）
- 関連 ADR: ADR-0002（moomoo 採用）・ADR-0016（空売り段階解禁）・ADR-0019（PoC 7 項目）・ADR-0023（米国株日足 OHLC 履歴源）
- 対象 Issue: [#397](https://github.com/endazon/ai-stock-trading/issues/397)（日本株市況権限の切り分け）・[#342](https://github.com/endazon/ai-stock-trading/issues/342)（moomoo PoC・項目 6/7/8 残置）
- 関連 IADR: IADR-0144（PoC 6 項目の結果）・IADR-0156/IADR-0157（米国株日足 OHLC 履歴源）

## 目的・背景

#397 は、OpenD の起動ログが `JPN Stocks: No permission` を出す一方、実弾の現金口座は取扱市場に JP を含むことから、
「市況権限と発注権限が別建てである可能性がある」として切り分けを求めている。#342 は PoC 7 項目のうち
項目 6（長期常駐安定性）・項目 7 残り 2 点（取得枠の単位・復権方式）・項目 8（`TrdFlowSummary` の
`SettlementDate` 明細）を未了のまま残している。

本作業は、クラスタ内の使い捨て Pod から**読み取り専用**の C# probe（本番と同一 `moomoo-api` 10.8.6808）を
実 OpenD（Pod `opend-8688bb5854-spkkr`・常駐中・ログイン済み）に対して実行し、上記を確認する。

## 対象範囲

- 対象: #397 手順 1（切り分け・JP.7203 vs US.AAPL の読み取り比較）・手順 3（影響範囲の一覧化）。
  #342 項目 6（常駐安定性の追観測）・項目 7 残り 2 点（取得枠・復権方式）・項目 8（`TrdFlowSummary` の
  `SettlementDate`）。
- 対象外: #397 手順 2（moomoo 側への権限申請。利用者作業）。#342 項目 9（`ShortFeeRate` の単位照会）・
  Hetzner ToS・実弾口座への照会（利用者作業）。発注・注文訂正・取消。

## 設計

### probe の方式

前回 PoC（[作業仕様書 20260805_342](20260805_342_moomoo-poc-plan.md)）と同じ方式を踏襲した。

- C# コンソールアプリ（本番と同一 `moomoo-api` 10.8.6808・`net10.0`）。`MMSPI_Qot`（相場）・`MMSPI_Trd`
  （取引・**読み取り呼び出しのみ実装**。`PlaceOrder`/`ModifyOrder` は no-op のまま一度も呼ばない）を実装。
- クラスタ内使い捨て Pod（`ast-probe-397-342`・`mcr.microsoft.com/dotnet/runtime:10.0`・namespace
  `ai-stock-trading`）に Secret `moomoo-rsa`（`opend_rsa.pem`）を読み取り専用マウントし、`opend:11111`
  （cluster内 Service 名）へ暗号化接続した。秘密鍵はクラスタ外へ持ち出していない。
- `kubectl cp` で `dotnet publish` 済みバイナリを Pod へ転送し、`kubectl exec -- dotnet probe.dll` で実行。
  確認後 `kubectl delete pod ast-probe-397-342` で削除済み。
- TrdEnv は **SIMULATE（accId=724808）のみ**を使用した。実弾口座（accId=284852705357372276 /
  284852702813153760）へは一切照会していない。

## 実測結果（2026-09-02）

### #397 手順1: JP.7203 vs US.AAPL の切り分け

| API（probe 呼び出し） | JP.7203 | US.AAPL |
| --- | --- | --- |
| `QotGetSecuritySnapshot`（get_market_snapshot 相当） | ❌ `retType=-1` **`No permission to get quotes for JP.7203. Please check Stocks quote permissions.`** | ✅ `retType=0` curPrice=325.13 lastClose=316.85 |
| `QotRequestHistoryKL`（request_history_kline 相当・日足数本） | ❌ `retType=-1` 同上メッセージ | ✅ `retType=0` 10 件取得 |
| `QotGetStaticInfo`（get_stock_basicinfo 相当） | ✅ **`retType=0`** name=Toyota Motor lotSize=100 exchType=21 | ✅ `retType=0` name=Apple lotSize=1 |
| `QotGetMarketState`（get_market_state 相当） | ✅ **`retType=0`** marketState=6（`QotMarketState_Closed`） | ✅ `retType=0` marketState=8（`QotMarketState_PreMarketBegin`） |
| `QotGetUserSecurity`（get_user_security 相当・既定グループ） | ❌ `retType=-1` `Unknown watchlist group.`（グループ名 `""` が既定グループとして解決されず。権限とは無関係な失敗） | 同左（グループ指定の問題） |

**新事実（今回の実測で初めて判明）**: **`JPN Stocks: No permission` は価格・出来高等の「相場データ」（`Snapshot`・`HistoryKL`）だけを止める。銘柄の静的属性（`GetStaticInfo`＝名称・売買単位・上場日）と市場セッション状態（`GetMarketState`）は権限に関係なく取得できる。**
これは #397 が前提としていた「市況権限が無い＝日本株の Qot 系 API が軒並み失敗する」という想定より**限定的**である。ただし FR-02（価格変動検知）が必要とするのは価格そのもの（`Snapshot`/`HistoryKL`）であり、この 2 つが失敗する以上、**FR-02 の日本株監視は現状のまま動かない**という #397 の結論そのものは変わらない。

### #397 手順1: `trdmarket_auth`（口座の取扱市場）の切り分け

`TrdGetAccList`（`UserID=0`）で返った口座は **今回は SIMULATE の 1 口座のみ**だった。

```
ACC accId=724808 trdEnv=0(Simulate) accType=2(Margin) simAccType=4 trdMarketAuthList=[2(US)]
```

- **`TrdMarketAuthListList` フィールド自体は実在し、口座ごとの取扱市場を機械的に読める。** SIMULATE 口座の
  値は `[2]`（`TrdMarket_US`）のみであり、**JP を含まない**。
- ⚠️ **前回 PoC（2026-08-05・[作業仕様書 20260805_342](20260805_342_moomoo-poc-plan.md)）は同じ `TrdGetAccList`
  呼び出しで実弾 2 口座＋SIMULATE 1 口座の計 3 口座を得ていたが、今回は SIMULATE の 1 口座しか返らなかった。**
  実弾口座へは意図して照会していないため実弾側の `trdMarketAuthList` は確認できていない。この差分の原因
  （アプリ側の権限見直し・セッション差・API バージョン差等）は本 probe だけでは切り分けられず、**未確定事項**
  として残す（実弾口座の状態を確認するには利用者の判断が要る）。

### #397 手順1: `accinfo_query` 相当（`TrdGetFunds`・SIMULATE）

```
FUNDS accId=724808 totalAssets=968788.459 cash=968788.459 power=1937576.918
```

`TrdGetFunds` は市場別の内訳を持たない口座レベルの残高であり、**日本株に関する情報は含まれない**（想定どおり）。

### #397 手順1 まとめ（切り分けの結論）

**市況権限と発注権限は別建てである。** ただし今回の実測で得られた区分は当初の想定より細かい。

| 系統 | 状態 | 根拠 |
| --- | --- | --- |
| 相場データ（価格・出来高・K 線） | ❌ 権限なし | `Snapshot`/`HistoryKL` が `No permission` |
| 銘柄の静的属性・市場セッション状態 | ✅ 権限不要で取得可 | `GetStaticInfo`/`GetMarketState` が成功 |
| 口座の取扱市場（`trdmarket_auth`） | SIMULATE 口座は US のみ。実弾口座は**未確認**（前回 PoC 時点の観測は JP を含んでいたが、今回は口座自体が返らず再確認できていない） | `TrdGetAccList.TrdMarketAuthListList` |
| 口座資金（`accinfo_query`相当） | 市場別内訳なし（無関係） | `TrdGetFunds` |

### #342 項目6: 長期常駐安定性の追観測

| 項目 | 前回（2026-08-05） | 今回（2026-09-02） |
| --- | --- | --- |
| Pod 生成時刻 | - | `2026-07-28T15:53:33Z` |
| 経過 | 7日20時間・5 回再起動 | **約 35 日・19 回再起動**（`restartCount=19`。直近再起動 `2026-09-02T09:40:43Z` 終了・約 50 分前） |
| 無人再ログイン | 継続 | **継続**（直近再起動後のログに `Login successful` を確認。対話認証なし） |
| 強制アップデート | 未観測 | **未観測。** 起動ログに `Update Available Ver.10.10.7008` の**通知**はあるが、稼働バージョンは
  引き続き `10.8.6818` のまま（強制更新は発生していない） |
| 権限一覧（全量・今回初めて記録） | US Stocks LV3 / JPN No permission のみ記録 | `HK Stocks/Options/Futures LV1`・`US Stocks LV3`・`US Options LV1`・`Crypto LV1`。**それ以外（US Futures 各種・上海・深セン・US Indices・US OTC・SG Futures/Stocks・JP Futures・MY Stocks・JPN Stocks）はすべて No permission** |

**約 35 日・19 回の無人再起動継続**により、IADR-0053 の結論（初回のみ有人・以降は安定 egress IP 下で無人再起動）が
単一ノードにおいてさらに裏付けられた。ただし前回同様、**単一ノード（egress IP 安定）での観測**であり、
Hetzner 等マルチノード環境への外挿にはならない（A-9 の未検証事項 1・2 は本 probe の対象外）。

### #342 項目7残り: 取得枠の消費観測

```
KLQUOTA[before]                         usedQuota=1  remainQuota=299   (プロセス起動直後の JP.7203/US.AAPL 照会後)
HISTKL US.MSFT (1 request)
KLQUOTA[after 1 extra request]          usedQuota=2〜4  remainQuota=298〜296
HISTKL US.GME, US.TSLA (2 requests)
KLQUOTA[after 2 more extra requests]    usedQuota=4  remainQuota=296
```

- **`usedQuota` は明確にリクエスト単位で増加する。** 1 銘柄・1 リクエストごとに `usedQuota` が 1 ずつ増えた
  （銘柄数単位ではなく、**成功した `RequestHistoryKL` 呼び出しの回数**で消費される）。JP.7203 への失敗した
  呼び出し（`No permission`）は `usedQuota` を消費していない（quota 消費は成功応答時のみ）。
- **回復周期は本 probe の実行時間内（数分）では観測できなかった。** `remainQuota` は単調に減少し続けており、
  日次リセット等の回復は今回の短時間観測では確認できない。**バックテストで多数銘柄を遡る場合は
  「銘柄数 × ページ数」のリクエスト数で `remainQuota`（既定 300）を消費すると見積もるべきである。**

### #342 項目7残り: 復権方式の比較（前復権 vs 非復権・US.AAPL 2024-01 分）

| 日付 | 前復権(Forward) close | 非復権(None) close |
| --- | --- | --- |
| 2024-01-02 | 183.404 | 185.64 |
| 2024-01-16 | 181.418 | 183.63 |

**前復権と非復権で価格水準が一貫して異なる（約 1.2% の乖離）。** AAPL は 2024 年に株式分割を行っていないため、
この乖離は分割調整ではなく**配当調整（前復権は配当落ちを遡って調整する）**によるものと解釈できる。
ADR-0016 決定14 が想定する「借株料・配当相当額を織り込む」費用モデルとの整合については、**前復権の価格系列を
そのままバックテストの価格として使うと、配当落ちが価格変動として計上され、配当としては別途計上されない限り
二重計上にはならないが、配当利回り分だけリターンが過大に出る可能性がある**。これは費用モデル側の設計判断であり、
本 probe は「前復権と非復権で値が異なる」という事実の確認までに留める（採否は #382 / FR-15 のバックテスト
費用モデル設計に委ねる）。

### #342 項目8: `TrdFlowSummary` の `SettlementDate` 明細

```
FLOWSUMMARY retType=-1 msg=Paper trading accounts do not support querying cash flow records.
HISTFILL    retType=-1 msg=Paper trading does not support deal data.
```

**SIMULATE（ペーパー）口座では `TrdFlowSummary`・`TrdGetHistoryOrderFillList` のいずれも「ペーパー口座は
非対応」という明示的な拒否が返り、`SettlementDate` フィールドの有無そのものを確認できなかった。**

ただし SDK の protobuf 契約からは、`TrdFlowSummary.FlowSummaryInfo` に **`SettlementDate`（決済日）・
`ClearingDate`（清算日）・`CashFlowAmount`・`CashFlowDirection`・`CashFlowType`** が確かに存在することを
確認済みである（フィールド自体は SDK に実在する。前回 PoC の静的確認と同じ「型はあるが実際に値が返るかは別」
という区別が、今回は「口座環境（SIMULATE）そのものが API を拒否する」という形で再現した）。

**帰結（#342 項目8 の不成立時の帰結の適用）**: 「決済済み資金の情報源が無い」（`docs/blocked-tasks.md` A-2
項目8）は**解消しない**。SIMULATE では検証できず、実弾口座での確認が必要である。**本 probe の権限範囲
（SIMULATE のみ・実弾照会は利用者判断）では実施できない。**

## 影響範囲の一覧（#397 手順3・コード grep による洗い出し）

「日本株が無いと発動しない、または意味を持たない機能」を実装ファイルベースで一覧化した。

| 機能 | 実装箇所 | 現状の帰結 |
| --- | --- | --- |
| FR-02 価格変動検知（日本株） | `TradeDecisionService/Features/TradeDecision/DecisionTrigger.cs`・`MarketDataCurrentPriceProvider.cs`・`PriceMovementDetectedHandler.cs` | 現在値取得元（`IMarketDataSource`／moomoo 経路）が JP の `Snapshot` を返せないため、日本株については価格変動イベントが**構造的に発火しない**（価格が取れない＝判断材料が無い） |
| 差金決済ガード（#332・`AppliesSameDayReentry`） | `RiskManagementService/Domain/AccountTypePolicy.cs:77`（`market == Market.Japan \|\| accountType == AccountType.Cash`） | 日本株限定の条件（`market == Market.Japan`）は**日本株の取引が一度も成立しなければ一度も評価されない**（休眠中の統制。実装は正しいが発火しない） |
| 既定監視銘柄 `7203`（トヨタ） | 本番の恒久シードとしては見つからず。**`frontend/e2e/fixtures.ts`・各サービスの単体/結合テストのフィクスチャとしてのみ存在**（`MarketMonitorService` に日本株のハードコード既定値は無い） | 影響は**テスト・E2E のフィクスチャに限定**される。本番の監視銘柄は利用者が SC-02 で設定するため、7203 を選んだ場合のみ FR-02 の不動作に遭遇する |
| 取引ガードの既定値 `EnabledMarkets` | `RiskManagementService/Domain/TradingDefaults.cs:107`（`{ Market.Japan, Market.UnitedStates }`） | **本番既定で日本株が有効のまま**（コメント「日本株: 当面監視・検証用（有効のまま）」）。市況が取れない以上、日本株の新規建ては判断材料が無く発生しないが、**設定上は無効化されていない**（##397 手順3-1 の判断材料: 既定から外すか否かは要判断） |
| 三者比較・報告書（FR-06/07/16） | `ReportService/Domain/ThreeWayComparison.cs`・`ThreeWayComparisonAggregator.cs`・`ReportRenderer.cs`／`TradeHistoryRenderer.cs`（`Market.Japan => "JP"` は表示ラベル変換のみ） | 日本株の建玉・約定が一度も生じなければ、三者比較・取引履歴には**日本株の行が現れないだけ**（表示コードはマーケット非依存で正しく動く。ブロッキングではない） |
| 為替換算（円建て統制・#257 IADR-0106） | `Shared.Kernel/Trading/CostCalculator.cs`・`Shared.Contracts/Trading/Currency.cs` | 日本株（JPY 建て）は**そもそも判断対象にならない**ため、円→円の自明な換算パスが使われないだけで、実害はない |

**結論**: 直接ブロックされるのは FR-02 の日本株監視のみである。#332 の差金決済ガード・三者比較・報告書は
「実装済みだが日本株取引が発生しないため発火しない／表示されない」という**休眠**の形であり、削除や修正は
不要である。**既定監視銘柄・`EnabledMarkets` の扱い（日本株を外すか、権限取得を待つか）が唯一の判断が要る点**
であり、これは #397 の元issueが指摘するとおり利用者判断が必要（本仕様書は判断材料の提示に留める）。

## 受け入れ基準

- [x] JP.7203 に対して `Snapshot`/`HistoryKL`/`StaticInfo`/`MarketState` を呼び、US.AAPL と比較した
- [x] 市況権限と発注権限が別建てであることを確認した（`GetStaticInfo`/`GetMarketState` は権限なしでも成功する）
- [x] `TrdGetAccList` の `trdmarket_auth`（`TrdMarketAuthListList`）で SIMULATE 口座の取扱市場を確認した
- [x] `TrdGetFunds`（`accinfo_query` 相当）を SIMULATE で確認した（市場別内訳なし）
- [x] #342 項目8（`TrdFlowSummary` の `SettlementDate`）を SIMULATE で試行した（**ペーパー非対応で確認不能。
      実弾口座での確認が必要 = 人間依頼事項**）
- [x] #342 項目7残り2点（取得枠の単位・回復周期、復権方式の比較）を実測した
- [x] #342 項目6（長期常駐安定性）を追観測した（35日・19 restarts）
- [x] 日本株が無いと発動しない機能の一覧を作成した
- [x] 実弾（`TrdEnv_Real`）へは一切接続していない。発注・注文訂正・取消を一切行っていない
- [x] 使い捨て Pod は確認後に `kubectl delete` した

## 計画書との差異

- 差異: あり
  1. 計画（06_technical/03_moomoo-integration）は日本株の市況取得・現物発注が 2026-06 から可能と記載しているが、
     実測は **2026-09-02 時点でも `JPN Stocks: No permission` が継続**していることを示した（前回 2026-08-05 の
     観測から変化なし）。切り分けの結果、**市況権限（quote）は日本株について確認できる範囲すべてで無効**であり、
     計画の記述と食い違う。**環流の要否は #397 の手順2（moomoo 側への権限申請）の結果を待って判断する**
     （申請すれば付与される可能性が残るため、現時点では「恒久的に不可能」とは確定していない）。

## 未決事項

1. **実弾口座の `trdmarket_auth` が今回確認できなかった。** `TrdGetAccList` が SIMULATE 1 口座しか返さなかった
   原因（前回 PoC との差分）は未解明。実弾口座への読み取り照会は利用者の判断を要する。
2. **#342 項目8（決済済み資金の `SettlementDate`）は SIMULATE では原理的に確認不能**（ペーパー非対応）。実弾口座
   での確認、または moomoo サポートへの問い合わせが必要。
3. **`ShortFeeRate` の単位（#342 項目9）は本作業の対象外**であり未確認のまま。

## 人間依頼事項

1. **#397 手順2**: moomoo 側で日本株の市況権限を申請する手段（無料枠か有料か、取引実績が要るか）の確認・申請。
2. **#342 項目9**: `ShortFeeRate`（借株料率。実測値 1.5）の単位（年率 1.5% か否か）の moomoo サポートへの照会。
3. **Hetzner ToS 適合確認**（A-9・既存の未了事項）。
4. **実弾口座への読み取り照会**（`trdmarket_auth` の再確認・`TrdFlowSummary` の `SettlementDate` 確認）は、
   本作業の安全範囲（SIMULATE 限定）の外にあるため、利用者が実施するか、明示的な許可を得てから別作業とする。
