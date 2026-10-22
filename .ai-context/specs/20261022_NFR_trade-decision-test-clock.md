---
title: 取引判断の試験の時計を固定する（閉場前の手仕舞いの常駐が実時刻の窓で照会し、提供側の呼び出しを数える試験が赤になる）
type: spec
status: done
related_ids: [NFR, FR-10, FR-19, IADR-0561]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
---

# 仕様書: 取引判断の試験の時計を固定する

> 本仕様書は実装着手前に作成した（着手 2026-10-22。AST `origin/develop` `a55e0f38`）。

## 起点（トレーサビリティ）

- 起点: MSP#2070 の CI ジョブ `submodule-backend-build (ai-stock-trading)`（2026-10-22T05:38Z）で、MSP が pin する AST `9a39760f` の
  `TradeDecisionService.Tests` が 4 件失敗（合計 2128 件）。同じ pin で 04:36Z は成功。試験の決定性＝ `NFR`。
- 関係する統制: 閉場前の手仕舞い（FR-10, FR-19, IADR-0561 決定 3。常駐の導入は #1399 `4a8c3a65`＝pin `9a39760f` の祖先）。
- IADR: 作らない（試験側だけの是正で、本番の構成・取引の判断ロジックは変えない）。
- 計画との矛盾: なし。

## 失敗した試験（手元で 2026-10-22T05:42Z に再現）

1. `RiskManagementReadContractTests.保有建玉は送り手の本物の型を直列化した応答から本番の配線のアダプタで読める`
   — `handler.Paths` に `/risk-controls/sizing-context` が混ざる。
2. `EntryBlockersProviderTests.T_10_1790_新規建ての可否は送り手の本物の型を直列化した応答から本番の配線のアダプタで読める` — 同上。
3. `RiskManagementGrpcWiringTests.T_10_1057_Grpc_を宣言すれば両ポートが_gRPC_実装になり実際に提供側を呼ぶ` — 呼び出し 2 回の想定に 3 回。
4. `RiskManagementGrpcWiringTests.T_10_1057_前提条件とリスク管理の_gRPC_は別々の宛先に並存し互いの照会が混ざらない` — 同上。

## 原因

- 本番の Program.cs は `PreCloseFlattenService`（閉場前の手仕舞いの常駐）を**常に登録**し、起動直後から `IClock` の時刻で巡回する。
  閉場の 60 分前（M の上限）以降の場中では、有効・無効を判定するためにサイジング文脈を照会する（`PreCloseFlattener.FlattenAsync`）。
- 本体は `IClock` を注入しており実時刻を直に読んでいない。**試験のホスト（`WebApplicationFactory<Program>`）が本番の `SystemClock` のまま**
  であるため、実時刻が窓（日本株 JST 14:30〜15:30＝05:30〜06:30Z、米国株 ET 15:00〜16:00＝夏時間 19:00〜20:00Z）に入ると、
  常駐の照会がリスク管理の偽の提供側へ 1 回混ざり、呼び出しの経路・回数を表明する 4 件が赤になる。05:38Z は日本株の窓の中、04:36Z は外。

## 是正

- 試験に `TestHostClock`（`Tests/TestHostClock.cs`）を足し、`UseFixedClock` で `IClock` を固定時刻へ差し替える。
  固定値 `OutsideMarketHours` は 2026-10-24（土）12:00Z（JST 21:00・ET 08:00。両市場とも休場）。
- 上の 3 クラスの `Factory`（提供側の呼び出しを数える試験の組み立て）の `ConfigureTestServices` で `UseFixedClock(TestHostClock.OutsideMarketHours)` を呼ぶ。
- 常駐の登録は変えない（`PreCloseFlattenWiringTests` が本番構成の登録を固定したまま）。試験の削除・skip はしない。

## 母集合（規則 9）

- 窓で混ざる照会は常駐の 1 回（サイジング文脈）だけである（既定は無効のため、それ以降の方針・保有の照会には進まない）。
  実時刻が日本株の窓の中（05:42Z・05:49Z）で全 2128 件を走らせ、赤は上の 4 件だけであることを確かめた。
- 米国株の窓（19:30Z に固定）でも同じ 4 件だけが赤になる（下の再現）。他の時刻依存の試験は本件の範囲外。

## 受け入れ基準

- [x] `IClock` を 2026-10-22T05:38Z に固定すると 4 件が赤（再現）。04:36Z では緑。19:30Z（米国株の窓）では同じ 4 件が赤。
- [x] 固定値を休場の時刻にすると、実時刻が窓の中でも 4 件を含む 37 件（3 クラス）が緑。
- [x] 実時刻が窓の中で `dotnet test` の全 2128 件が緑。
- [x] `dotnet format --verify-no-changes` が通る。
