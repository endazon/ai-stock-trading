---
title: 期間開始時点の在庫・期間の約定の読み取りで、市場と約定時刻の絞り込みを SQL へ下ろす
type: spec
status: accepted
related_ids: [FR-06, FR-16, IADR-0493, IADR-0115, IADR-0246, IADR-0350, IADR-0506]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06 当日の取引結果・損益の集計 / FR-16 数値はコード集計)
---

# 仕様書: 期間開始時点の在庫・期間の約定の読み取りで、市場と約定時刻の絞り込みを SQL へ下ろす（#1186）

## 起点となる計画書（トレーサビリティ）

- 起点: FR-06（報告書の損益の集計）・FR-16（数値はコード集計）
- 関連 IADR: IADR-0493（期間開始時点の在庫。残余リスク「毎回台帳の全行を読む」を本作業が扱う）、IADR-0115 決定5（期間の約定）、
  IADR-0246（取引日は市場の現地取引日）、IADR-0350 決定 2・4（取り込み行の合流と `/fills` からの除外）
- 新設: IADR-0506（本作業の判断）
- 起票: #1186（#1181 の後続）

## 目的・背景

`GET /risk-controls/opening-inventory`・gRPC `GetOpeningInventory` は、呼ぶたびに `EfPortfolioLedgerStore.GetFills()`
（約定 × 承認の無条件の結合＋取り込み行の全件）を読み、メモリで市場と取引日を絞る。報告書の生成 1 回で市場ごとに 2 回呼ぶ。
同じ読み方の `GET /risk-controls/fills`・gRPC `GetFills` も期間が数日なのに全履歴を運ぶ。

## 母集合（規則 9）: 台帳の全行読み（`GetFills()`）の本番の呼び出し元

`git grep -n "GetFills()" origin/develop -- backend/Services/RiskManagementService ':!backend/Services/RiskManagementService/Tests'` で引いた 16 行から、定義 3 行（`IReadOnlyList<LedgerFill> GetFills()` で除外）と経路の登録 1 行（`RiskControlEndpoints` の `MapGetFills()`）を除いた 15 箇所（着手時点の develop）を仕分けた。

| 呼び出し元 | 入力に要る範囲 | 本作業 |
| --- | --- | --- |
| `GetOpeningInventory/Endpoint.cs`（REST） | 1 市場・取引日 `< before` | **SQL へ下ろす** |
| `RiskControlsReadGrpcService.GetOpeningInventory`（gRPC） | 同上 | **SQL へ下ろす** |
| `GetFills/Endpoint.cs`（REST `/fills`） | 全市場・取引日 `[from, to]` | **SQL へ下ろす** |
| `RiskControlsReadGrpcService.GetFills`（gRPC） | 同上 | **SQL へ下ろす** |
| `QuoteRefreshService`・`BrokerPositionsObservedHandler`・`OpenPositionsService`・`ShortSellingStatusService`・`PositionCloseService`・`MaintenanceMarginReductionService`・`WorkingEntryOrdersService` | 現在の建玉＝**全履歴の畳み込み** | 対象外（時刻で切れない。日次スナップショットが要る） |
| `LedgerPortfolioStateProvider`（射影・ピーク資金） | 全履歴 | 対象外（同上） |
| `PositionDriftAdoptionService`・`ShortSellContextSupplier`・`BuyInInferenceService` | 全履歴（建玉・覆う買付の探索） | 対象外（同上） |

全履歴の畳み込みが要る 11 箇所は、絞り込みでは軽くならない（issue の案 2「日次の建玉スナップショット」が要る）。
本 issue の受け入れは在庫の照会の 2 経路であり、同じ形（取引日で切る）の `/fills` 2 経路だけを併せて下ろす。

## 設計（判断の比較は IADR-0506）

1. **ストアのポートに範囲つきの読み口を足す**: `IPortfolioLedgerStore.GetFillsExecutedBetween(Market? market, DateTimeOffset? executedAtOrAfter, DateTimeOffset? executedBefore)`。
   既定の実装は `GetFills()` をメモリで絞る（インメモリ実装・試験の偽物はそのまま通る）。EF 実装は上書きし、
   `trade_fills.ExecutedAt`・`approved_orders.Market`・`position_drift_adoptions.(Market, AdoptedAtUtc)` の条件を SQL の `WHERE` に載せる。
   射影（`select new LedgerFill(...)`）は `GetFills()` と同じ 1 か所の式を使う（列の補完規則を 2 か所に持たない）。
2. **取引日 → UTC の範囲は「±1 日の余裕つきの外包」にする**（`LedgerScanBounds`）。SQL にタイムゾーンを持ち込まない。
   - 取引日 `< before` ⇒ `ExecutedAt < (before + 1 日) 00:00 UTC`
   - 取引日 `>= from` ⇒ `ExecutedAt >= (from − 1 日) 00:00 UTC`
   - 取引日 `<= to` ⇒ `ExecutedAt < (to + 2 日) 00:00 UTC`
   - 根拠: どのタイムゾーンでも UTC との差は ±14 時間未満であり、現地の暦日は UTC の暦日 ±1 日に入る。夏時間はこの幅の内側の揺れにすぎない。
   - `DateOnly` の端（`0001-01-01`・`9999-12-31`）で日付の加減が溢れる場合は、その側の境界を外す（無制限＝従来どおり）。
3. **正確な絞り込みは従来の純関数がそのまま行う**（`OpeningInventoryQuery.AsOf` / `PeriodFillQuery.InTradingDayRange`）。
   ストアを受ける多重定義を足し、REST・gRPC の 4 経路はそちらを呼ぶ。純関数の本体（取引日の判定・並び・畳み込み）は 1 バイトも変えない。
4. **インデックス**: `trade_fills.ExecutedAt` に張る（マイグレーション `AddTradeFillExecutedAtIndex`）。`/fills` の窓（数日）は選択的に効く。
   在庫の照会（`< before`＝履歴のほぼ全部）では計画器が全走査を選び得るが、市場の条件と期間より後の行の除外は効く。
   **マイグレーションはサービスの起動時に自動で適用される**（`Program.cs` の `MigrateAsync`。ホスト稼働時・relational のときだけ）。

### 同時刻の並び（同値の扱い）

純関数は `OrderBy(ExecutedAt)`（安定ソート）で、同時刻の行は**台帳の読み出し順**に従う（IADR-0493 残余リスク）。
範囲つきの読み口は従来と同じ結合・同じ「約定の後に取り込み」の連結であり、`WHERE` で行を間引くだけである。
EF の InMemory では残った行の相対順は従来と同じ（試験で固定）。PostgreSQL は `ORDER BY` の無い結合の返却順を保証しない
（従来の全行読みも同じ）——本作業はこの点を良くも悪くもしない。

## 受け入れ基準と試験の写像

| 受け入れ | 試験 |
| --- | --- |
| 外包は取引日の判定の上位集合である（境界・夏時間の切替日・両市場・`DateOnly` の端） | T-06-059（`LedgerScanBoundsTests`） |
| 範囲つきの読み口が市場・時刻で絞る（EF・インメモリの既定実装の両方。取り込み行も） | T-06-060（`LedgerPeriodReadEquivalenceTests`。オフセット付きの境界も瞬間として比べる） |
| 在庫の照会・期間の約定が全行を畳む従来と一致する（取引日の端・夏時間・複数市場・同時刻・空の台帳） | T-06-061（`LedgerPeriodReadEquivalenceTests`。EF InMemory の台帳で新旧を突き合わせる） |
| REST・gRPC の 4 経路が範囲つきの読み口を通る | 既存の `OpeningInventoryEndpointTests`・gRPC の読み取り試験が回帰で通ること（結線） |
| 台帳 N 万行で 1 回の呼び出しが 10 秒の期限に十分収まる | 下の「実測」（ローカルの使い捨て PostgreSQL 16。CI には入れない） |

Testcontainers の PostgreSQL 試験は本リポジトリに無い（永続化の試験は EF InMemory）。SQL への翻訳は実測の手順で
実 PostgreSQL に対して確かめた。

## 実測

ローカルの使い捨て PostgreSQL 16（4 コア）に本ブランチのマイグレーションを当て、承認 N 件＋約定 N 件を 2 年に均等に散らした台帳
（2 市場・20 銘柄）で、従来（`GetFills()` → 純関数）と本作業（ストアを受ける多重定義）を同じ `DbContext` で 3 回ずつ呼んだ。
計測用の試験は一時ファイルで実行し、コミットしていない（CI に PostgreSQL は無い）。

| N | 在庫（米国・従来） | 在庫（米国・本作業） | 期間の約定 5 日（従来） | 期間の約定 5 日（本作業） | 結果の一致 |
| --- | --- | --- | --- | --- | --- |
| 100,000 | 179〜1,177 ms | 111〜684 ms | 184〜522 ms | 16〜33 ms | 全回一致 |
| 300,000 | 722〜2,749 ms | 379〜671 ms | 690〜1,695 ms | 27〜66 ms | 全回一致 |

- `EXPLAIN ANALYZE`（300,000 行）: 期間の約定は `IX_trade_fills_ExecutedAt` の索引走査＋ハッシュ結合で約 27 ms。在庫は全走査＋ハッシュ結合で約 184 ms。
- EF の翻訳: `WHERE t."ExecutedAt" >= @lo AND t."ExecutedAt" < @hi` と `approved_orders` 側の `WHERE a."Market" = @m` に下りる（`ToQueryString` で確認）。
  結果の一致が実 PostgreSQL 上でも成り立つ（取り込み行の条件・オフセット 0 の引数を含め、翻訳が通る）ことも同じ実測で確かめた。
- 結論: いずれも 10 秒の期限に十分収まる。在庫の照会は履歴の長さに比例したまま（決定の残余。IADR-0506）。

## 対象外

- 全履歴の畳み込みが要る 11 箇所（上表）。日次の建玉スナップショットは別 issue の範囲（本 PR では起票しない。必要になった時点で起票する）。
- 同時刻の並びを決定的にする `ORDER BY` の追加（従来から未定義の並びを、本作業で変えない）。
- 報告書サービス側（呼び出し回数・期限）は変えない。

## 再配備

- リスク管理サービス（risk-management）のみ。起動時に `AddTradeFillExecutedAtIndex` が自動適用される（インデックスの作成のみ・列やデータは変えない）。
- 報告書サービスの再配備は不要（wire 形は不変）。
