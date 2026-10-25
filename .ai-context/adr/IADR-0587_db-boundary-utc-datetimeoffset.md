---
title: IADR-0587 DateTimeOffset は DB の境界（全 DbContext の ConfigureConventions の値の変換）で UTC へ揃える。市場の取引日の範囲は UTC で返す。規約の有無は Architecture.Tests で縛る
type: impl-adr
status: Accepted
related_ids: [FR-10, ADR-0067, NFR, IADR-0539]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0067（決定 1・2。判断由来の決済の未約定時の取消・回数は市場の現地取引日）
related_specs:
  - ../specs/20261024_1473_db-boundary-utc-datetimeoffset.md
---

# IADR-0587: DateTimeOffset を DB の境界で UTC へ揃える

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-24
- 決定者: Claude Code（#1473 が実装に委ねた点 —— どこで正規化するか）

## コンテキスト

1. Npgsql 6 以降は timestamptz へ UTC 以外のオフセットの DateTimeOffset を書けない（`only offset 0 (UTC) is supported`）。
   書き込みだけでなく、照会のパラメータも同じである。
2. 判断由来の決済の未約定取消（IADR-0539）は、市場の現地取引日の範囲（米国東部 −04:00・JST +09:00 のオフセット）を照会へ渡し、
   稼働（2026-10-24）で 30 秒ごとに例外になり、一度も取り消さなかった（#1473）。単体試験はインメモリのストアで SQL を書かず、
   範囲の試験は瞬間だけを比べる等値だったので、どちらも緑だった。
3. 同じ型は既に 3 か所で個別に是正されている（`LedgerScanBounds`・`EfReportStore.GetLatestConfirmedAsOf`・`AuditPeriodRange`）。
   外部入力（Report の as-of・Audit の期間照会）はオフセット付きの時刻を受け付ける。

## 決定

1. **全サービスの DbContext（7 本）の `ConfigureConventions` で `Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>()` を掛ける。**
   変換は `v => v.ToUniversalTime()`（読みはそのまま。timestamptz は UTC で返る）。null 許容の列にも掛かる。EF は比較の相手の列の
   型の写像を照会のパラメータにも使うので、書き込みと `Where` のパラメータが両方 UTC になる。瞬間は変えないので、既存の行・既存の照会の
   結果は変わらない（UTC の値は変換で変わらない）。列の型は変わらず、移行は要らない（統合試験の `MigrateAsync` で未適用の差分が無いことを確かめた）。
2. **`DecisionCloseUnfilledCanceller.TradingDayRange` は名乗りどおり UTC で返す**（`.ToUniversalTime()`）。境界の変換があれば例外にはならないが、
   範囲を DB 以外（プロセス内の記憶・ログ）でも使うので、値の約束を要約と揃える。試験はオフセットも見る（T-10-3067）。
3. **変換の型は各サービスの `Infrastructure/Persistence/UtcDateTimeOffsetConverter.cs` に写しで置き、規約の有無を Architecture.Tests
   （`DbContextUtcDateTimeOffsetConventionTests`）で縛る。** DbContext の一覧は実ツリーのソース（`: DbContext(`）から得て、下限 7 本の母集合検査を持つ。

## 却下した案

- **呼び出し側だけを直す（`TradingDayRange` の 1 か所）**: 4 回目の同型であり、照会を足すたびに書き手が思い出す必要が残る。単体試験では捕まらない。
- **変換を共有物（`AiStockTrading.Shared.Infrastructure`）へ置く**: EF Core の参照を共有物へ足すことになり、EF を使わないサービスへも依存が広がる。
  監査・設定・費用統制は Shared.Infrastructure を参照していないので参照も足すことになる。開場前の配備で影響の範囲を広げない。
- **`DbCommandInterceptor` でパラメータを書き換える**: SQL の組み立て後にパラメータを触るのは EF の型の写像の外であり、読み手に見えにくい。
- **Npgsql の `EnableLegacyTimestampBehavior`**: timestamptz と timestamp の意味が変わる全体のスイッチで、影響が広すぎる。

## 残余リスク

- 生 SQL（`FromSql` 等）へ DateTimeOffset を渡す照会は変換を通らない（2026-10-24 時点で 0 件）。
- 変換を掛けた列のメンバ（`.Date` 等）を LINQ で使う照会は 0 件。足すときは Npgsql の翻訳を確かめる。
- 7 つの写しは同じ内容であることを試験で縛っていない（規約の呼び出しの有無だけを縛る）。
