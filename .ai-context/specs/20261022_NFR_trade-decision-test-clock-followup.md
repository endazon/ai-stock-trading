---
title: 取引判断の試験の時計の固定を、提供側の呼び出しを数える残りの試験へ広げる（#1431 の是正漏れ）
type: spec
status: done
related_ids: [NFR, FR-10, FR-19, IADR-0561]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
---

# 仕様書: 取引判断の試験の時計の固定を、残りの試験へ広げる

> 本仕様書は実装着手前に作成した（着手 2026-10-22。AST `origin/develop` `29a4d9dd`＝#1431 のマージ後）。

## 起点（トレーサビリティ）

- 起点: #1431（`20261022_NFR_trade-decision-test-clock.md`）の是正漏れ。MSP#2080 の作業で
  `Stage4GrpcWiringTests.T_10_1697_Grpc_を宣言すれば_3_つのポートが_gRPC_実装になり実際に提供側を呼ぶ` が
  2026-10-22T06:21〜06:23Z に 3 回とも赤（`PolicyCalls` の差分が 1 ではなく 2）。試験の決定性＝ `NFR`。
- 関係する統制: 閉場前の手仕舞いの常駐（FR-10, FR-19, IADR-0561 決定 3）。
- IADR: 作らない（試験側だけの是正。本番の構成・判断ロジックは変えない）。計画との矛盾: なし。

## 漏れた理由（#1431 の母集合の引き方の穴）

- 常駐の照会は**窓の段ごとに増える**。閉場の 60 分前（M の上限）以降はサイジング文脈を照会し、**効いている M（既定 15 分）の窓に入ると
  さらに方針（`IDailyPolicyProvider`）を照会する**（`PreCloseFlattener.FlattenAsync`）。#1431 は実時刻 05:42Z・05:49Z（60 分の窓の中・
  15 分の窓の外）でしか全件を走らせておらず、方針の照会を数える `Stage4GrpcWiringTests` は窓の段の違いで見えなかった。
- 常駐の初回の巡回は起動直後に試験と並行して走るため、差分で数える試験でも混ざるかは競合で決まる（同じ時刻でも赤・緑が揺れる）。
  1 回の実走で母集合を確定できない。

## 母集合（規則 9・10）

### 引き方

1. 静的: `TradeDecisionService.Tests` で `WebApplicationFactory<Program>` を使う 38 ファイルを列挙し、偽の提供側（`HttpMessageHandler`・
   gRPC の偽ホスト・`ConfigurePrimaryHttpMessageHandler`）を**本番の組み立てに差し込んで**、その呼び出しの**数・経路**を表明するものを抜き出した
   （`grep -rl "WebApplicationFactory<"` → 偽の輸送の有無 → 表明の目視）。
2. 経路: 常駐の照会先は `PreCloseFlattener` の依存（サイジング文脈・方針・保有銘柄・保有建玉・新規建ての可否・処理中の決済・換算レート・現在値）に限る。
   LLM ゲートウェイは依存に無い。
3. 動的（プローブ）: 一時的に `Program.cs` の `IClock` を環境変数で固定時刻へ差し替え（`PreCloseFlatten__IntervalSeconds=10` で巡回も密にする）、
   全 2128 件を窓の段ごとに 2 回ずつ走らせた（変更は戻した）。結果は下の「検証」。

### 当てた（`UseFixedClock(TestHostClock.OutsideMarketHours)` を `Factory` の `ConfigureTestServices` へ足す）

| クラス | 数える提供側 | 理由 |
| --- | --- | --- |
| `Stage4GrpcWiringTests` | 日報（方針）・市場監視の gRPC 偽ホストの rpc ごとの回数 | 常駐が M の窓で方針を照会する（実測で赤） |
| `OrderExecutionGrpcWiringTests` | 発注執行の gRPC 偽ホストの処理中の決済・日足の回数 | 処理中の決済は常駐の依存。今の偽の構成（サイジング文脈・保有の提供側なし＝既定で無効）では常駐はそこまで進まないが、提供側の呼び出しを数える同型の組み立てであり、構成が増えた時点で混ざる |

### 既に当ててある（#1431）

`RiskManagementGrpcWiringTests` / `EntryBlockersProviderTests` / `RiskManagementReadContractTests`。

### 除外（理由）

| クラス | 理由 |
| --- | --- |
| `LlmGatewayAuthWiringTests` / `LlmPurposeWiringTests` | 数えるのは LLM ゲートウェイの呼び出し。常駐は LLM を呼ばない（`PreCloseFlattener` の依存に無い）。時計を固定すると判断の経路（場中の判定）を変え得るため触らない |
| `InFlightCloseProviderTests` / `HttpHeldPositionProviderTests` | 経路・回数の表明は**直接組み立てた**提供側の試験だけで、`Factory` 側は型の選択しか見ない |
| `LlmCompletionClientSelectionTests` / `Stage0RecordingStateWiringTests` | 偽の輸送は置くが、呼び出しの数・経路を表明しない |
| 残り 27 ファイル | 偽の提供側を差し込まない（型・構成の選択・起動の失敗だけを見る） |

### 同じ形の常駐を持つ他のサービス

`: BackgroundService` を全サービスで grep し、`IClock`／`TimeProvider` の場中・暦で照会を出し分ける常駐を調べた。

| サービス | 常駐 | 判断 |
| --- | --- | --- |
| MarketMonitorService | `MonitorPollingService`（場中だけ巡回） | 呼び出しを数える組み立ての試験（`RiskManagementGrpcTests`・`PositionRowToleranceCompositionTests`・`CycleOverrunToleranceCompositionTests`）は**常駐を登録から外す**（後 2 者は時計も固定）。他の `Factory` 試験は監視銘柄の書き込み・読み出しの表明で、巡回の照会先を数えない。是正不要 |
| RiskManagementService | `QuoteRefreshService`（場中だけ現在値を引く） | `MarketData:EnableMarkToMarket=true` のときだけ登録。これを立てる `Factory` 試験は `CompositionWiringGuardTests`（配線の抜けの検査）だけで、呼び出しを数えない。是正不要 |
| RiskManagementService | 維持率・出金・観測ドローダウンの評価 | 営業日の暦で動き、構成で有効にしたときだけ登録する。`Factory` 試験で提供側の呼び出しを数えるのは `ShortSellContextWiringTests`（発注執行の空売り可否の照会）だけで、これらの常駐は空売り可否を照会しない（`Hosted/` に参照なし）。是正不要 |
| OrderExecutionService | 予約の照合・保持・約定の巡回等 | 時刻は保持期限・滞留の閾値に使うだけで、場中の窓で照会を出し分けない。是正不要 |

## 是正

- 上の 2 クラスの `Factory` に `builder.ConfigureTestServices(services => services.UseFixedClock(TestHostClock.OutsideMarketHours))` を足す。
- `Stage4GrpcWiringTests` の冒頭コメント（「常駐の巡回が起動直後に呼び得るため差分で数える」）に時計の固定を併記する。差分で数える形は残す。
- 試験の削除・skip はしない。本番の構成は変えない。

## 受け入れ基準

- [x] プローブ（`Program.cs` の時計を窓の中へ固定）で、是正前は `Stage4GrpcWiringTests` が赤（JP の M 窓 06:20Z・06:28Z で 4/4、
      US の M 窓 19:50Z で 1/2）。60 分の窓だけ（05:40Z・19:10Z）では緑。プローブ自体が起こす 2 件（`CompositionWiringGuardTests` が
      `ProbeClock` を、`PreCloseFlattenWiringTests` が巡回間隔 10 秒を咎める）以外に赤は無い。
- [x] 是正後、同じプローブ（06:20Z・06:28Z・19:50Z を各 3 回）で、プローブ自体が起こす 2 件以外は全件緑。
- [x] 是正後、`TestHostClock.OutsideMarketHours` を一時的に 2026-10-22T06:20Z（窓の中）へ書き換えると `Stage4GrpcWiringTests` が赤になる
      （固定した時計が常駐へ届いている。#1431 の 3 クラスの 4 件とあわせて 5 件が 3/3 で赤。`OrderExecutionGrpcWiringTests` は今の偽の構成では
      常駐が処理中の決済まで進まないため緑のまま＝上の当てた理由のとおり）。書き換えは戻した。
- [x] `dotnet test` の全件が緑。`dotnet format --verify-no-changes` が通る。
