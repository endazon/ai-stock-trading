---
title: IADR-0506 期間開始時点の在庫・期間の約定は、取引日の外包（UTC ±1 日）で市場と約定時刻を SQL へ下ろして読み、取引日の正確な判定は従来の純関数に残す
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-16, IADR-0493, IADR-0115, IADR-0246, IADR-0350, IADR-0012]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06 当日の取引結果・損益の集計 / FR-16 数値はコード集計)
---

# IADR-0506: 台帳の期間の読み取りは取引日の外包で SQL へ下ろし、正確な判定は純関数に残す（#1186）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: claude（[#1186](https://github.com/endazon/ai-stock-trading/issues/1186) の案 1 に沿って起案）

## 起点・関連

- 関連する計画書 ID: FR-06（報告書の損益の集計）・FR-16（数値はコード集計）
- 対象 Issue: [#1186](https://github.com/endazon/ai-stock-trading/issues/1186)（#1181 の後続）
- 関連する実装仕様書: [20261008_1186_ledger-period-read-sql-pushdown](../specs/20261008_1186_ledger-period-read-sql-pushdown.md)（母集合・実測）
- 関連 IADR: [IADR-0493](./IADR-0493_report-opening-inventory-from-ledger-as-of.md)（期間開始時点の在庫。残余リスク「毎回台帳の全行を読む」を本 IADR が扱う）、
  [IADR-0115](./IADR-0115_report-auto-generation-scheduler.md)（期間の約定）、IADR-0246（取引日は市場の現地取引日）、IADR-0350（取り込み行の合流）、IADR-0012（起動時の自動移行）

## コンテキストと課題

期間開始時点の在庫（REST `GET /risk-controls/opening-inventory`・gRPC `GetOpeningInventory`）と期間の約定（`GET /risk-controls/fills`・gRPC `GetFills`）は、
呼ぶたびに `IPortfolioLedgerStore.GetFills()`＝約定 × 承認の無条件の結合と取り込み行の全件を読み、メモリで市場と取引日を絞っていた。
報告書は生成 1 回で在庫を市場ごとに 2 回、約定を 1 回照会する。費用は運用期間（台帳の行数）に比例する。

取引日は**市場の現地取引日**（米国は ET・夏時間あり、東証は JST）であり、`ExecutedAt` は UTC の瞬間である。
SQL で現地の暦日を正確に計算するには、タイムゾーンの規則（夏時間）を SQL 側へ写す必要がある。

## 検討した選択肢

| 案 | 内容 | 判断 |
| --- | --- | --- |
| **A. 外包（UTC ±1 日）で SQL へ下ろし、正確な判定は従来の純関数**（採用） | ストアが市場・約定時刻の範囲を `WHERE` に載せ、純関数（`OpeningInventoryQuery` / `PeriodFillQuery`）が取引日で正確に絞る | タイムゾーンの規則が C# の 1 か所（`TradingDay.Of`）に残る。純関数は無改修で、新旧の一致を試験で固定できる |
| B. 取引日の境界の瞬間を C# で正確に計算して SQL へ渡す | 市場のタイムゾーンで `before` の 0 時を UTC へ換算し、`ExecutedAt <` に渡す | 夏時間の切替日の 0 時の扱い（存在しない・2 回ある時刻）を新たに正しく書く必要がある。境界の判定が 2 か所になる。外包と比べて読む行の差は高々 2 日分 |
| C. SQL でタイムゾーン変換（`AT TIME ZONE`）して取引日で絞る | PostgreSQL の tz データで判定 | 取引日の単一情報源が C# と DB の 2 つになる。InMemory の試験で確かめられない |
| D. 日次の建玉スナップショット（取引日の終わりの射影）を持つ | issue の案 2 | 在庫の照会を履歴の長さから切り離せる唯一の案だが、新テーブル・書き込み経路・再計算（過去の行の訂正）の設計が要る。現状の件数（下の実測）では 10 秒の期限に十分収まり、先送りできる |

## 決定

### 決定 1: 台帳のポートに範囲つきの読み口を足す

`IPortfolioLedgerStore.GetFillsExecutedBetween(Market? market, DateTimeOffset? executedAtOrAfter, DateTimeOffset? executedBefore)`。
既定の実装は `GetFills()` をメモリで絞る（インメモリ実装・試験の偽物はこれで足りる）。EF 実装は上書きし、
`approved_orders.Market`・`trade_fills.ExecutedAt`・`position_drift_adoptions.(Market, AdoptedAtUtc)` の条件を SQL へ下ろす。
結合・射影（`LedgerFill` の列の補完）・「約定の後に取り込み」の連結は `GetFills()` と同じ 1 つの式を通す。**取引日では絞らない。**

### 決定 2: 取引日 → 約定時刻は「±1 日の余裕つきの外包」で写す（`LedgerScanBounds`）

UTC との差は ±14 時間未満なので、現地の暦日は UTC の暦日 ±1 日に入る。

- 取引日 `< before` ⇒ `ExecutedAt < (before + 1 日) 00:00Z`
- 取引日 `>= from` ⇒ `ExecutedAt >= (from − 1 日) 00:00Z`
- 取引日 `<= to` ⇒ `ExecutedAt < (to + 2 日) 00:00Z`

`DateOnly` の端で日付の加減が溢れる側は境界を外す（無制限＝従来の全行読みの側）。境界はオフセット 0 で渡す（Npgsql の timestamptz の要件）。

### 決定 3: 正確な判定は従来の純関数に残し、入口だけを差し替える

`OpeningInventoryQuery.AsOf(IPortfolioLedgerStore, Market, DateOnly)` と `PeriodFillQuery.InTradingDayRange(IPortfolioLedgerStore, DateOnly, DateOnly)` を足し、
REST・gRPC の 4 経路はこちらを呼ぶ。純関数の本体（取引日の判定・安定ソート・畳み込み・取り込み行の除外）は変えない。
外包は上位集合なので、純関数に渡る行は従来の行のうち取引日の条件を満たすものをすべて含み、相対順も変わらない（結果は従来と一致する）。

### 決定 4: `trade_fills.ExecutedAt` にインデックスを張る

マイグレーション `AddTradeFillExecutedAtIndex`（インデックスの作成のみ）。期間の約定（数日の窓）は索引走査になる。
在庫の照会（`< before`＝履歴のほぼ全部）は全走査のままで、市場の条件と期間より後の行の除外だけが効く。
移行はリスク管理サービスの起動時に自動で適用される（`Program.cs` の `MigrateAsync`・IADR-0012）。

## 理由

- タイムゾーンと夏時間の規則を SQL へ持ち込まない（取引日の単一情報源は `TradingDay.Of` のまま）。境界の誤りは「読む行が数日分多い」にしか倒れない。
- 純関数を変えないので、新旧の一致を同じ台帳で突き合わせる試験が書ける（T-06-061）。
- 全履歴の畳み込みが要る他の 11 の呼び出し元（射影・建玉・乖離の取り込み等）は範囲で切れない。それらを軽くするのは案 D であり、本 IADR は形を縛らない。

## 結果

- 良い影響: 期間の約定の照会は窓の行だけを読む（下の実測で 300,000 行の台帳から約 30〜70 ms）。在庫の照会は市場の半分・期間より後の行を読まない。
- 悪い影響・トレードオフ:
  - **在庫の照会は依然として履歴の長さに比例する**（`< before` は履歴のほぼ全部）。期限に近づいたら案 D（日次スナップショット）が要る。
  - 同時刻の約定の並びは従来どおり台帳の読み出し順（PostgreSQL は `ORDER BY` の無い結合の返却順を保証しない）。本 IADR はこの点を良くも悪くもしない（IADR-0493 の残余リスクのまま）。
  - 外包は最大 2 日分の余計な行を読む（純関数が捨てる）。
- 再配備: リスク管理サービスのみ（起動時にインデックスを作成）。報告書サービスは不要（wire 形は不変）。

### 実測（ローカルの使い捨て PostgreSQL 16・4 コア。CI には入れない）

台帳 = 承認 N 件 ＋ 約定 N 件を 2 年に均等に散らし、2 市場・20 銘柄。値は 3 回の呼び出しの範囲（初回は接続・JIT を含む）。

| N | 在庫（米国・従来） | 在庫（米国・本 IADR） | 期間の約定 5 日（従来） | 期間の約定 5 日（本 IADR） | 結果の一致 |
| --- | --- | --- | --- | --- | --- |
| 100,000 | 179〜1,177 ms | 111〜684 ms | 184〜522 ms | 16〜33 ms | 全回一致 |
| 300,000 | 722〜2,749 ms | 379〜671 ms | 690〜1,695 ms | 27〜66 ms | 全回一致 |

期間の約定の SQL は `IX_trade_fills_ExecutedAt` の索引走査（`EXPLAIN ANALYZE` で 300,000 行時 約 27 ms）。在庫の SQL は全走査＋ハッシュ結合（約 184 ms）。
いずれも報告書の照会の期限（10 秒）に十分収まる。

## 関連

- Supersedes: なし（IADR-0493 の残余リスク「毎回台帳の全行を読む」を扱う。IADR-0493 には日付つき追記を置いた）
- Superseded by: なし
