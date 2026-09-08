---
title: MarketMonitorService の設定更新テストが不定期に 400 で落ちる欠陥の是正
issue: "#707"
related_ids:
  - FR-03
  - FR-13
  - NFR
plan_refs:
  - FR-03
  - FR-13
adr_refs:
  - IADR-0012
  - IADR-0282
  - IADR-0317
status: done
created: 2026-09-09
updated: 2026-09-09
---

# 作業仕様書: MarketMonitorService の設定更新テストが不定期に 400 で落ちる欠陥の是正（#707）

## 背景

基盤リポジトリ（MSP#858 の受け皿）で、`MarketMonitorService.Tests` の 2 件が**同型に 200 期待で
400** を受け取り不定期に赤くなる。同一コミットの再実行では緑になる。

- `Tests/Features/MarketMonitor/MonitorCollectionSettingsEndpointsTests.cs` の `利用者はクールダウンを変更できる`
- `Tests/Features/MarketMonitor/MonitorSettingsEndpointsTests.cs` の `利用者は監視設定を更新でき永続化される`

400 の応答本文がテスト出力に現れないため、どのバリデーションが弾いたのかが読めない状態だった。

## 真因（実測で確定）

**バリデーションは 1 件も弾いていない。** 400 は**インフラ層の例外がエンドポイントの例外フィルタで
400 へ写像された**ものである。

1. `Program.cs` は `MonitorPollingService`（`BackgroundService`）を登録している。**巡回ループは
   `PeriodicTimer` を待つ前に 1 回目を走らせる**ため、`WebApplicationFactory.CreateClient()` で
   ホストが起きた直後に 1 巡回が走る。`WeekdayMarketSchedule.IsOpen` は**平日なら true** なので、
   平日に走るテストだけがこの巡回を伴う。
2. その巡回は `MarketMonitorAppService.EvaluateRoundAsync` → `IMonitoredSymbolStore.GetSettings()` を
   呼ぶ。**設定は単一行**（`SingletonKeys.Id`）であり、行が無ければ**その場でシードして `SaveChanges`**
   する。一方、テストの HTTP 要求も `GetSettings()` を通るため、**同じ「未設定の単一行」を
   2 つの `DbContext` が同時に初回シードしうる**。
3. 後から `SaveChanges` した側は一意キー違反で失敗する。**EF Core の InMemory プロバイダは
   これを `System.ArgumentException`（`An item with the same key has already been added. Key: 1`）
   として投げる**（実測）。
4. `MonitorSettingsEndpoints` のグループ例外フィルタは `ArgumentException` を**検証エラーとみなして
   400** に写像する。よって**巡回が先に確定し、HTTP 要求が後に確定した回だけ** 200 期待のテストが
   400 で落ちる。

`EfMonitoredSymbolStore.GetSettings()` には既にこの競合を意図した `catch (DbUpdateException)` が
あるが、**InMemory プロバイダの例外型は `DbUpdateException` ではない**ため素通りしていた。

### なぜ「この 2 件」なのか

同じ競合はクラス内のどの書き込みでも起こり得るが、**巡回とぶつかるのはホスト起動直後の
最初の 1 要求だけ**であり、かつ**混入した 400 が失敗として顔を出すのは 200 を期待するテストだけ**
である（`400 を期待するテスト`は偽の 400 でも緑になる）。設定更新系で 200 を期待するテストは
少数であり、xUnit のクラス内実行順序が保証されないため、**そのどれが最初に走るか**で顔ぶれが揺れる。

### 棄却した仮説（実測で潰した）

| 仮説 | 潰し方 | 結果 |
| --- | --- | --- |
| 同一クラス内のテスト間の状態汚染（変更なし更新の拒否・ETag/バージョン不一致・理由必須・同値の再設定拒否） | `MonitorSettingsService` / `MonitorSettingsBounds` / `EfMonitoredSymbolStore.Save` の全分岐を読む | **そのような拒否条件は 1 つも無い**。検証は値域（`0 < ratio <= 0.50` / `0 <= cooldown <= 24h`）と理由の非空白のみで、いずれも前段のテストが残す状態に依存しない |
| クラス間の DB 汚染 | `MonitorWorkerWebApplicationFactory` の DB 名はファクトリ単位の Guid | 汚染しない（issue の前提どおり） |
| 楽観排他（`Version`）の競合 | 同時 2 要求を 20 回実行するプローブ | **409** が出る（400 ではない）。既に `DbUpdateConcurrencyException → 409` へ写像済みで、本件の症状と一致しない |
| 単発の書き込みが起動直後に落ちる | 新しいファクトリ＋即 PUT を 40 回実行するプローブ | 40/40 緑。**窓は「双方が行なしを観測した後、巡回が先に確定する」交錯に限られる**ため、素の反復では踏めない |

## 決めたこと

詳細と根拠は [IADR-0317](../adr/IADR-0317_singleton-seed-race-detected-by-row-presence.md)。要点のみ。

1. **単一行の初回シード競合は、例外の型ではなく「行が実在するか」で判定する。** `SaveChanges` が
   失敗したら `ChangeTracker` を捨てて読み直し、**行があれば競合（他方が先にシードした）として
   その値を返す。行が無ければ本物の障害なので再送出する**（`throw;`）。プロバイダごとに違う
   例外型（relational: `DbUpdateException` / InMemory: `ArgumentException`）を列挙し続けない。
2. **統制は 1 つも緩めない。** 値域検証・理由必須・変更履歴・認可はいずれも変更しない。
   是正は永続化層の競合処理だけである。
3. **`MonitorPollingService` をテストで止めない。** 止めれば赤は消えるが、**本番（Npgsql）でも
   同じ競合が起きる**（そちらは `DbUpdateException` なので `GetSettings` の既存 catch が拾うが、
   **行が無くても未永続の既定値を返して黙る**という別の欠陥だった）。競合源をテストから外すと
   この欠陥が二度と観測されない。
4. **診断を先に確保する。** 設定更新系のステータス断定は**必ず応答本文を添える**共通ヘルパを通す。
   本件は「本文が出ないので原因が読めない」ことが一次の障害だった。
5. **検査器は足さない。** CLAUDE.md「検査器・規約の追加は同型事故 2 回から」に照らし、
   本件（永続化層の初回シード競合）は 1 回目である。

## 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `backend/Services/MarketMonitorService/Infrastructure/Persistence/EfMonitoredSymbolStore.cs` | 初回シードの競合判定を「行の実在」に変更し、競合でなければ再送出する |
| `backend/Services/MarketMonitorService/Tests/HttpStatusAssertions.cs` | 新規。断定失敗時に応答本文を出す共通ヘルパ |
| `backend/Services/MarketMonitorService/Tests/Features/MarketMonitor/MonitorCollectionSettingsEndpointsTests.cs` | 全ステータス断定をヘルパ経由へ。汚染からの陽性対照を追加 |
| `backend/Services/MarketMonitorService/Tests/Features/MarketMonitor/MonitorSettingsEndpointsTests.cs` | 同上 |
| `backend/Services/MarketMonitorService/Tests/Infrastructure/Persistence/EfMonitoredSymbolStoreSeedRaceTests.cs` | 新規。順序を固定した再現テストと、握り潰さないことの否定形 |
| `.ai-context/adr/IADR-0317_singleton-seed-race-detected-by-row-presence.md` | 新規 |
| `.ai-context/adr/README.md` | 索引へ 1 行追記 |

## テスト方針

**順序を固定して赤くする**（推測で終わらせない）。`DbContext.SavingChanges` を seam に使い、
「後発が `Add` を stage した後・`SaveChanges` が確定する前に、先発が同じ行をシードして確定する」
交錯を**決定的に**組み立てる。これは巡回と HTTP 要求の交錯そのものであり、本番コード
（`EfMonitoredSymbolStore.GetSettings`）を素のまま通す。

3 点セット:

- **再現（是正前は赤）**: 上記の交錯で例外を投げず、**先発が書いた値**を読み直して返す。
- **陽性対照（汚染から始めて緑）**: 巡回が既にシード済み・利用者が既に別の値へ変更済み、
  という**汚れた状態から始めても**設定更新が 200 になる（＝実行順序に依存しない）。
- **否定形**: **競合ではない保存失敗は握り潰さない。** 行が生まれていないのに `SaveChanges` が
  失敗した場合は例外がそのまま上がる（`throw;`）。

## 実測（変異試験）

**是正前の実装（`EfMonitoredSymbolStore.cs` を HEAD へ戻した状態）で赤くなることを実測した。**

| 面 | 手順 | 是正前 | 是正後 |
| --- | --- | --- | --- |
| 永続化層（順序固定・決定的） | `EfMonitoredSymbolStoreSeedRaceTests` 3 件 | **2 失敗 / 1 合格** | 3 合格 |
| エンドポイント（本来の不定期な赤の再現） | `--filter FullyQualifiedName~EndpointsTests`（36 件）を**並列 6 / 8 / 10 / 12 本**同時実行し CPU を競合させる | **36 本中 2 本が赤**（不定期＝報告どおり） | **36 本すべて緑** |

エンドポイント面で捕まえた赤は、**新しい診断ヘルパのおかげで原因が本文に出た**：

```
Expected response.StatusCode to be HttpStatusCode.OK {value: 200}
because 応答本文: {"error":"An item with the same key has already been added. Key: 1"},
but found HttpStatusCode.BadRequest {value: 400}.
```

これが #707 が報告した「200 期待で 400」の正体である。**素の断定のままでは、この本文は 1 文字も
出なかった。** 2 本とも赤くなったのは `MonitorSettingsEndpointsTests.全置換で変えた変動閾値と
クールダウンは履歴に残る` であり、**issue が挙げた 2 件とは別の顔ぶれ**だった。これは
**「200 を期待する設定更新テストのうち、たまたまホスト起動直後に走ったものが落ちる」**という
真因の説明と一致する（クラス内の実行順序は保証されないため、顔ぶれは環境ごとに揺れる）。

## 受け入れ基準

- [x] 400 の応答本文が断定失敗メッセージに出る（設定更新系すべて）
- [x] 順序を固定した再現テストが**是正前の実装で赤くなる**ことを変異試験で実測する
- [x] 汚染した状態から始めても設定更新が 200 になる（陽性対照）
- [x] 競合でない保存失敗を握り潰さない（否定形）
- [x] `dotnet build` 警告 0 / `dotnet format` 差分 0
- [x] `dotnet test` を 5 回以上連続で全緑

## 計画書との差異

- 差異: なし。FR-03 / FR-13 の統制（値域・理由必須・履歴・認可）には手を触れていない。

## 未決事項・残余リスク

1. **同型の `catch (DbUpdateException)` が他サービスに 10 箇所ある**（`RiskManagementService` 4・
   `ConfigurationService` 2・`OrderExecutionService` 2・`ReportService` 1・`CostControlService` 1）。
   いずれも「行が無くても未永続の値を返して黙る」または「InMemory の `ArgumentException` を
   拾えない」同じ形を持ちうる。**本 issue の射程外**とし、掃討は別 issue の判断に委ねる
   （同型事故 2 回目が観測されたら検査器へ格上げする）。
2. **グループ例外フィルタが `ArgumentException` を一律 400 へ写像する**構造は残る。インフラ層の
   `ArgumentException` は「要求が悪い」という誤った説明になる。専用の検証例外型へ絞るのが構造的な
   解だが、サービス横断の改修になるため本 issue では採らない（IADR-0317 残余リスク）。
