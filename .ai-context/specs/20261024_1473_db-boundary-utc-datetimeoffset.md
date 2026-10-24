---
title: 判断由来の決済の未約定取消の照会が UTC 以外のオフセットの DateTimeOffset を timestamptz へ渡して毎巡回失敗する件を、DB の境界で UTC へ揃えて直す（#1473）
type: spec
status: done
related_ids: [FR-10, ADR-0067, NFR, IADR-0539, IADR-0587]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
---

# 仕様書: DB の境界で DateTimeOffset を UTC へ揃える（#1473）

> 本仕様書は AST `origin/develop` `5fc14b56` を起点に、実装の前に作成した。判断は [IADR-0587](../adr/IADR-0587_db-boundary-utc-datetimeoffset.md) に残す。
> IADR-0586 は一時停止中の #1472 が使っているため、0587 を採った（欠番は許す）。

## 起点（トレーサビリティ）

- 起点: #1473（稼働 PoC・2026-10-24 の一括配備 AST `3f9153ff`。#1424 で `DecisionCloseUnfilledCancel__Enabled=true`）。
- 計画: ADR-0067 決定 1・2（判断由来の決済の指値の未約定時の取消・1 建玉 1 日の回数の上限は市場の現地取引日）、FR-10。
- 前提（覆さない）: IADR-0539（#1339。取消の部品・印を先に書く・回数の上限）。

## 症状と原因

- `DecisionCloseUnfilledCanceller.RunOnceAsync` が `TradingDayRange(market, now).Start`（市場の現地取引日の始まり）を
  `FindUnfilledDecisionCloses` の `placedOnOrAfter` へ渡す。`TradingDayRange` は要約で「UTC の範囲」と名乗りながら、
  `new DateTimeOffset(localDate, zone.GetUtcOffset(localDate))`＝**市場のオフセット（米国東部 −04:00・JST +09:00）**のまま返していた。
- Npgsql 6 以降は timestamptz へ UTC 以外のオフセットを書けない（`only offset 0 (UTC) is supported`）。照会は 30 秒ごとに例外になり、
  取消は一度も行われなかった。照会が通っても、次の `CountUnfilledCancelRequests(dayStart, dayEnd)` が同じ形で落ちる。
- 単体試験はインメモリのストアで、SQL のパラメータを書かない。`TradingDayRange` の試験（T-10-2672）は DateTimeOffset の等値
  （瞬間だけを比べる）なので、オフセットが −04:00 でも緑だった。

## 受け入れ基準

1. `FindUnfilledDecisionCloses` に −04:00・+09:00 の上限・下限を渡しても、実 PostgreSQL で例外にならず対象を返す（T-10-3068）。
2. `MarkUnfilledCancelRequested`（−04:00 の時刻）と `CountUnfilledCancelRequests`（−04:00 の範囲。null 許容列）が実 PostgreSQL で通る（T-10-3069）。
3. UTC 以外のオフセットの記録を保存でき、同じ瞬間（オフセット 0）で読み戻る（T-10-3070）。
4. `TradingDayRange` はオフセット 0 で返す（T-10-3067）。
5. `backend/Services` 配下の全 DbContext が同じ規約を持ち、規約を持たない DbContext が増えたら試験が落ちる（T-10-3071）。
6. 既存の移行に未適用のモデルの差分を生まない（統合試験の `MigrateAsync` が通る）。

## 方針（どこで直すか）

**DB の境界（DbContext の `ConfigureConventions`）で、全 DateTimeOffset 列に `v => v.ToUniversalTime()` の変換を掛ける**（IADR-0587 決定 1）。
EF は比較の相手の列の型の写像（変換を含む）を照会のパラメータにも使うので、書き込みと `Where` のパラメータの両方が UTC になる。
加えて `TradingDayRange` を名乗りどおり UTC で返す（決定 2）。

- 呼び出し側の個別修正（`TradingDayRange` だけ）を採らない理由: 同じ型の個別の是正が既に 3 か所ある
  （`RiskManagementService` の `LedgerScanBounds`〔試験に「Npgsql の timestamptz の引数はオフセット 0 を要する」〕・
  `ReportService` の `EfReportStore.GetLatestConfirmedAsOf`〔`at.ToUniversalTime()`〕・`AuditPeriodRange`〔`ToUniversalTime()`〕）。
  今回が 4 回目で、同型の事故 2 回の条件を満たす。照会を足すたびに書き手が思い出す方式は、今回のように単体試験で捕まらない。
- 変換の型は各サービスの `Infrastructure/Persistence/UtcDateTimeOffsetConverter.cs` に置く（7 つの同じ写し）。共有物
  （`AiStockTrading.Shared.Infrastructure`）へ置くには EF Core の参照を足す必要があり、監査・設定・費用統制はそもそも
  Shared.Infrastructure を参照していない。開場前の配備で影響の範囲を広げないため写しにし、規約の有無は Architecture.Tests で縛る（決定 3）。

## 母集合（規則 9。誤りの側の型で全コードを引いた）

引いた式: `new DateTimeOffset(…, zone.GetUtcOffset(…))`・`new DateTimeOffset(…, JstOffset|JapanOffset|Jst)`・`.ToOffset(`・
`TimeZoneInfo.ConvertTime(`・`.ToLocalTime()`（`backend/Services`・`backend/Shared` の本番コード）と、外部入力の
`DateTimeOffset.TryParse` / `ParseExact`。DB 側は `: DbContext(` の 7 本と、その store の DateTimeOffset 引数・`FromSql` / `ExecuteSql` / `ExecuteUpdate`。

| 引いた場所 | DB へ渡るか | 判定 |
| --- | --- | --- |
| `DecisionCloseUnfilledCanceller.TradingDayRange` → `FindUnfilledDecisionCloses`・`CountUnfilledCancelRequests` | 渡る（−04:00・+09:00） | **是正**（本件。UTC で返す＋境界の変換） |
| `OrderFillPollingService` の `_cappedUntil`（`TradingDayRange(...).End`） | 渡らない（プロセス内の比較） | UTC になるだけ。比較は瞬間なので挙動は変わらない |
| ReportService `AsOfEndpoint`（`+09:00`・`-04:00` を受け付ける）→ `EfReportStore.GetLatestConfirmedAsOf` | 渡る | 既に個別に `ToUniversalTime()`。境界の変換でも覆う |
| AuditService の REST / gRPC `GetByTypesInPeriod`（`from`・`to` を任意のオフセットで受ける）→ `EfAuditEventStore` | 渡る（呼び出し元がオフセット付きで送れば） | **是正**（境界の変換。本 PR より前はオフセット付きの照会で例外になり得た。本ユニットの呼び出し元 `AuditPeriodRange` は UTC で送るので稼働の実害は観測していない） |
| RiskManagementService `LedgerScanBounds` | 渡る | 既に UTC（先例）。対象外 |
| 共有カーネル `MarketHours.NextOpen`・`SessionCloseOn`（市場のオフセットで返すと名乗る） | 渡らない（ログ・プロセス内の比較：OES の `SoftwareStopExecutor`・`ProtectiveStopGuard`・`IMarketSessionGate`、RM の `QuoteSessionFreshness`・`PreCloseEntryCutoffSettings`、MM の生存の要約、Report の `ReportNoResponsePolicy`、TradeDecision） | **除外**: 戻り値の約束（市場のオフセット）を読むログがある。DB へ渡っても境界の変換が揃える |
| ReportService `ReportSchedule`・`ReportSessionWindow`・`TradeHistoryViewBuilder` ほか（JST の表示・暦日） | 表示・暦日の導出。境界まで全経路は追っていない | 境界の変換で覆う（渡っても例外にならない） |
| RiskManagementService `EasternTradingDate`・`TradingDay` | 渡らない（`DateOnly` と分を返す） | 対象外 |
| InformationCollectionService（BOJ・EDINET・FINRA の公表時刻を JST・東部で作る） | DbContext を持たない。事実の `CollectedAt` は時計の UTC | 対象外 |
| TradeDecisionService（`AsOfBaseInputPorts`・`AsOfReferenceFeeds`・`PreCloseFlattener`） | DbContext を持たない | 対象外 |
| `Shared.Infrastructure` の為替（`BojFxRateSource` の JST の `AsOf`） | DB へ書く store は無い（キャッシュはプロセス内） | 対象外 |
| IADR-0584 の `MonthKey`・`AuditEntryFactory` の `llm-cost:{yyyy-MM}`・`CostControlAppService` の `ToOffset(+9)` | 文字列のキー | 対象外（timestamptz ではない） |
| `KLineQuotaProbeCommand`（JST の表示） | ログの文字列 | 対象外 |
| `ExecuteSql` / `ExecuteSqlInterpolated`（勧告ロック）・`ExecuteUpdate`（`StopLossPrice` だけ） | DateTimeOffset を渡さない | 対象外（生 SQL は変換を通らないが、該当なし） |
| Wolverine の durable storage（`WolverineFx.Postgresql`） | EF を通らない。封筒の時刻は Wolverine が UTC で書く | 対象外 |
| `scripts/nightly-ledger-summary.sh` ほか psql の SQL | Npgsql のパラメータではない | 対象外 |
| 凍結記録（`.ai-context/specs/`・既存 IADR） | — | 書き換えない |

## 試験の写像

- T-10-3067（単体・OES）、T-10-3068〜3070（統合・実 PostgreSQL・`ExecutedOrderStoreNonUtcOffsetE2ETests`）、T-10-3071（Architecture.Tests）。
  表は `docs/tests/FR-10_risk-controls-tests.md` の #1339 の節の下に置く。
- 実測（手元・PostgreSQL 16 を `E2E_POSTGRES_CONNECTION` で注入）: 修正前の develop で T-10-3068〜3070 の 4 件が
  `Cannot write DateTimeOffset with Offset=-04:00:00 …`（+09:00 も同じ）で赤、修正後に緑。Architecture.Tests は 1 つの DbContext の規約を
  消すと赤。既存の実 PostgreSQL の統合試験（`ApprovedOrderTerminal`・`ClosePositionLock`・`LedgerStopLineWidening`・`EntryScreening`・
  `SettingsChangeHistory`）は緑。`PositionDriftStateConcurrencyE2ETests` の 2 件は外部注入の DB では修正前の develop でも赤
  （単一行を試験間で共有するため。Testcontainers の CI では各クラスが新しいコンテナを使う）＝本件と無関係。

## 残余

- 生 SQL（`FromSql` 等）へ DateTimeOffset を渡す照会を足すと変換を通らない（現在は 0 件）。
- 変換を掛けた列で DateTimeOffset のメンバ（`.Date` 等）を LINQ で使う照会は現在 0 件。足すときは Npgsql の翻訳を確かめる。
- 監査・設定・費用統制・市場監視・報告書・リスク管理の各サービスの単体試験は手元のディスクの都合で CI に任せた（ビルドは通した）。
