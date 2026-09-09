---
title: IADR-0317 単一行の初回シード競合は例外の型ではなく「行の実在」で判定し、競合でなければ握り潰さない
type: impl-adr
status: Accepted
related_ids:
  - FR-03
  - FR-13
  - NFR
author: 実装担当
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - FR-03
  - FR-13
---

# IADR-0317: 単一行の初回シード競合は例外の型ではなく「行の実在」で判定し、競合でなければ握り潰さない

- 状態: Accepted
- 日付: 2026-09-09
- 決定者: 実装担当

## 起点・関連

- 関連する計画書 ID: FR-03（価格変動検知）・FR-13（設定変更は利用者のみ）
- 関連する実装 ADR: [IADR-0012](./IADR-0012_risk-settings-persistence.md)（単一行 JSON＋Version 楽観排他）・
  [IADR-0282](./IADR-0282_watchlist-config-seed.md)（構成による初回シード）・
  [IADR-0155](./IADR-0155_sc01-collection-parameters-supply.md)（収集パラメータの部分更新）
- 関連する実装仕様書: [20260909_707_market-monitor-settings-flaky](../specs/20260909_707_market-monitor-settings-flaky.md)
- 起点 issue: [#707](https://github.com/endazon/ai-stock-trading/issues/707)（基盤側の受け皿は MSP#858）

## コンテキストと課題

`MarketMonitorService.Tests` の設定更新テスト 2 件が**同型に「200 期待で 400」**で不定期に落ち、
基盤リポジトリの無関係な PR まで赤くしていた。同一コミットの再実行では緑になる。

真因は**バリデーションではない**。`Program.cs` が登録する `MonitorPollingService` は
`PeriodicTimer` を待つ前に 1 回目の巡回を走らせるため、`WebApplicationFactory` がホストを起こした
直後に巡回が走る（`WeekdayMarketSchedule.IsOpen` は平日 true なので**平日に走った回だけ**）。
その巡回も HTTP 要求も `IMonitoredSymbolStore.GetSettings()` を通り、**どちらも「設定の単一行が
無い」を観測してから `SaveChanges` する**。後から確定した側は一意キー違反で失敗する。

`EfMonitoredSymbolStore.GetSettings()` はこの競合を意図した `catch (DbUpdateException)` を
既に持っていた。**しかし EF Core の InMemory プロバイダは一意キー違反を
`System.ArgumentException`（`An item with the same key has already been added. Key: 1`）として
投げる**（実測）。型が違うため catch は素通りし、例外は
`MonitorSettingsEndpoints` のグループ例外フィルタ（`catch (ArgumentException) → Results.BadRequest`）
まで届き、**利用者の正しい要求が 400 になった**。

つまり **1 つの取りこぼしが「検証エラー」という嘘の説明に化ける**という、原因から最も遠い形で
壊れていた。加えて既存の catch は、**行が生まれていない（＝競合ではない本物の保存失敗）ときも
未永続の既定値を返して黙る**という第 2 の欠陥を持っていた。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | `catch` の型に `ArgumentException` を足す | 症状は消えるが**列挙を続ける形**が残る。プロバイダを替える・EF が例外型を変えるたびに同じ事故が再発し、しかも**次も「400 という嘘」として現れる**ため気づきにくい |
| B | **失敗したら行を読み直し、行があれば競合・無ければ再送出**（採用） | 例外の型に依存しない。競合の定義（他方が先に行を作った）を**そのまま**表現する。第 2 の欠陥（握り潰し）も同時に閉じる |
| C | テストで `MonitorPollingService` を止める | 赤は消えるが**本番（Npgsql）の競合は残る**。競合源をテストから外すとこの欠陥は二度と観測されない |
| D | グループ例外フィルタを専用の検証例外型へ絞る | 構造的には正しいが、`ArgumentException` を検証エラーとして投げる箇所がサービス横断にあり、#707 の射程を大きく超える |

## 決定

1. **競合の判定は例外の型ではなく「行が実在するか」で行う。** `SaveChanges` が
   `DbUpdateException` または `ArgumentException` で失敗したら `ChangeTracker` を捨てて読み直し、
   **行があれば他方が先にシードしたものとしてその値を返す**。
2. 🔴 **行が無ければ再送出する（`throw;`）。** 未永続の既定値を返すと「保存できていないのに既定値で
   監視が動く」状態を静かに作る。**競合の吸収と障害の隠蔽を混ぜない。**
3. **統制は 1 つも緩めない。** 値域（`MonitorSettingsBounds`）・理由必須（FR-11）・変更履歴・
   認可（OwnerOnly）はいずれも変更しない。是正は永続化層の競合処理だけである。
4. **`MonitorPollingService` はテストでも止めない**（案 C を採らない）。巡回は本番と同じ競合源であり、
   止めれば同型の欠陥が観測されなくなる。
5. **診断を恒久化する。** 設定更新系のステータス断定は `HttpStatusAssertions.ShouldHaveStatusAsync`
   （応答本文つき）を通す。#707 の一次の障害は「本文が出ないので 400 の理由が読めない」ことだった。
6. **検査器は足さない。** CLAUDE.md「検査器・規約の追加は同型事故 2 回から」に照らし、
   永続化層の初回シード競合は 1 回目である。**2 回目が観測されたら**、「単一行ストアの
   `catch (DbUpdateException)` が行の実在を確かめずに既定値を返す」形を静的に検知する検査へ格上げする
   （この条件を明記することが暫定の恒久化を防ぐ担保。IADR-0309 決定5 と同じ規律）。

## 理由

案 B は**競合の定義そのもの**を書いている。「他方が先に行を作った」以外に、`Add` が
一意キーで失敗する理由は無い。プロバイダが何を投げるかは実装詳細であり、**判定条件に持ち込むと
実装詳細の変化が統制の穴になる**。読み直しは失敗経路でだけ走るのでホットパスの費用も増えない。

決定 2 は「握り潰しの範囲を狭めることは統制を強める側の変更である」という判断による。
従来は本物の保存失敗でも既定値が返り、呼び出し側からは正常と見分けが付かなかった。

## 結果

- 良い影響:
  - 平日に走る CI で不定期に赤くなる原因が消える（基盤リポジトリの無関係な PR を巻き込まなくなる）。
  - **本番（Npgsql）でも同じ競合が正しく吸収される**（従来も `DbUpdateException` は拾えていたが、
    行が無いときに黙る欠陥は残っていた）。
  - 400 の応答本文が断定失敗メッセージへ必ず出るため、次に同種の混入が起きたときは即座に読める。
- 悪い影響・トレードオフ:
  - `catch` が `ArgumentException` を含むため、**万一シード経路で別の `ArgumentException` が
    出ても一度は捕まる**。ただし行が無ければ `throw;` で素通りするので、握り潰しにはならない。
  - 競合時に返るのは**他方が書いた値**であり、自分がシードしようとした構成シードではない
    （冪等性を優先。両者とも同じ構成から作るため実際の差は出ない）。
- フォローアップ:
  - 同型の `catch (DbUpdateException)` が他サービスに 10 箇所ある（`RiskManagementService` 4・
    `ConfigurationService` 2・`OrderExecutionService` 2・`ReportService` 1・`CostControlService` 1）。
    **#707 の射程外**とし、掃討の要否は別途判断する。
- 実測（変異試験）: 是正前の実装へ戻すと、**永続化層の順序固定テストは 2 失敗 / 1 合格**、
  **エンドポイント面（`~EndpointsTests` 36 件）を並列 6 / 8 / 10 / 12 本走らせると 36 本中 2 本が赤**
  になった（不定期＝報告どおり）。是正後は同じ手順で 36 本すべて緑、`MarketMonitorService.Tests`
  全体は 130 → 135 件緑（連続 6 回＋並列 4 本でも全緑）。捕まえた赤は診断ヘルパにより
  `because 応答本文: {"error":"An item with the same key has already been added. Key: 1"}` を伴い、
  **バリデーションではないことが一目で読めた**。
- 残余リスク:
  - グループ例外フィルタが `ArgumentException` を一律 400 へ写像する構造は残る（案 D）。
    インフラ層の `ArgumentException` は「要求が悪い」という**誤った説明**として利用者へ返り続ける。
    専用の検証例外型へ絞るのが構造的な解だが、サービス横断の改修になるため本件では採らない。
  - 再現テストは `DbContext.SavingChanges` を seam に使う。EF Core がこのイベントを廃止すると
    交錯を組み立てられなくなる（そのときは検知できずに緑になるのではなく、**コンパイルが落ちる**）。

## 関連

- Supersedes: なし
- Superseded by: なし
