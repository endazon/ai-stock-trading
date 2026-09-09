---
title: catch (DbUpdateException) 11 箇所の横展開是正（競合判定を「行の実在」へ揃える）
issue: "#714"
related_ids:
  - NFR
  - FR-05
  - FR-06
  - FR-10
  - FR-11
  - FR-17
  - FR-20
  - FR-21
plan_refs:
  - FR-05
  - FR-10
  - FR-17
  - FR-20
  - FR-21
adr_refs:
  - IADR-0317
  - IADR-0319
status: done
created: 2026-09-09
updated: 2026-09-09
---

# 作業仕様書: `catch (DbUpdateException)` 11 箇所の横展開是正（#714）

## 背景

#707 の真因は「同一行の初回シードが競合したとき、一意キー違反を `catch (DbUpdateException)` で
拾う設計が **EF Core InMemory では `ArgumentException`（`An item with the same key has already
been added.`）として投げられる**ため素通りし、エンドポイントのグループ例外フィルタで **400（＝
利用者の要求が悪い、という嘘）** へ写像される」ことだった。`MarketMonitorService` の
`EfMonitoredSymbolStore` は [IADR-0317](../adr/IADR-0317_singleton-seed-race-detected-by-row-presence.md)
で「**例外の型ではなく行の実在で判定し、行が無ければ再送出する**」へ改めた。

IADR-0317 のフォローアップに「同型の `catch (DbUpdateException)` が他サービスに 10 箇所ある」と
記録した。本作業はその掃討である（2026-09-09 の実測では **11 箇所**。`EfBorrowFeeAccrualStore` は
1 箇所だが 2 経路〔計上・未供給〕から共有されている）。

同型が **2 回目**として観測されたので、IADR-0317 決定6 の「2 回目が観測されたら検査へ格上げする」
条件にも触れる。判断は [IADR-0319](../adr/IADR-0319_dbupdateexception-row-presence-sweep.md) 決定7 に書く。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（発注・二重発注の防止）／FR-06・FR-07（報告書）／FR-10・FR-11（リスク統制・
  変更履歴）／FR-17（全体前提条件）／FR-20（段階ゲート）／FR-21（観測到達の記録）。
  本作業は永続化層の競合処理のみを触り、**統制の内容（値域・理由必須・変更履歴・認可）は変えない**。
- 起点 ID: **NFR**（無採番。欠陥是正・信頼性）
- 計画書: `/home/user/project-planning/projects/ai-stock-trading/02_requirements/`（FR 一覧）
- 関連 ADR: ADR-0027（借株料）・ADR-0016（段階ゲート）。いずれも**制約に変更なし**。
- 関連 IADR: IADR-0012 / 0057 / 0070 / 0074 / 0124 / 0181 / 0183 / 0055 / 0059 / 0317

## 対象範囲

- 対象: 下表 11 箇所の `catch (DbUpdateException)` と、その順序固定再現テスト。
- 対象外: グループ例外フィルタ（`ArgumentException → 400`）の構造改修（IADR-0317 案 D。
  判断は IADR-0319 決定6 に記す）。統制の内容。関係のないリファクタ。

## 分類（受け入れ基準 1）

分類の定義は issue のとおり。
**a**＝単一行／対象レコードの初回シード・冪等挿入の競合吸収、
**b**＝本物の失敗の握り潰し、
**c**＝それ以外（冪等性キー・重複メッセージ判定・競合による「負け」の表明）。

| # | ファイル:行（是正前） | 呼び出し側 | 分類 | 根拠・是正前の欠陥 |
| --- | --- | --- | --- | --- |
| 1 | `RiskManagementService/Infrastructure/Persistence/EfRiskSettingsStore.cs:34` `GetCurrent` | `RiskSettingsService` 他（HTTP・ハンドラ全般） | **a＋b** | 単一行 `SingletonKeys.Id` の初回シード競合。InMemory の `ArgumentException` を取りこぼす。**かつ行が無くても未永続の `defaults` を返して黙る**（#707 と完全に同型） |
| 2 | `RiskManagementService/.../EfPositionObservationArrivalStore.cs:42` `Record` | `BrokerPositionsObservedHandler` | **b**（主因は a） | 「その日の行が既に在る」が主因と書きつつ、**接続断など真の失敗も同じ分岐で無言に落とす**（コメントが自ら ⚠️ で認めている） |
| 3 | `RiskManagementService/.../EfPositionDriftStateStore.cs:62` `TrySave` | `PositionDriftTracker.ShouldReport` | **c＋b** | 競合＝「負け」を `false` で表明する設計。ただし**本物の保存失敗も「負け」に化け**、乖離が無言で未報告になる |
| 4 | `RiskManagementService/.../EfStageGateStore.cs:51` `Append` | `StageGateService`（段階遷移の承認） | **c** | `Sequence` 一意違反のみを 409 へ変換する意図は正しいが、**判定が Npgsql の `SqlState: "23505"` に固定**されており、InMemory・他プロバイダでは**必ず false** になる（＝競合が 409 へ変換されず素通りする） |
| 5 | `RiskManagementService/.../EfBorrowFeeAccrualStore.cs:91` `SaveNewRow` | `BorrowFeeAccrualService.Accrue` / `MarkUnavailable` | **a＋b** | 主キー衝突＝同じ建玉・同じ日を先に書いた、が主因。**接続断も `false` に化け、費用が過小計上へ倒れる**（IADR-0183 が残余リスクとして明記していたもの） |
| 6 | `ConfigurationService/.../EfAssumptionsStore.cs:33` `GetCurrent` | `AssumptionsService`（HTTP） | **a＋b** | #1 と同型（単一行・未永続の既定値を返して黙る） |
| 7 | `ConfigurationService/.../EfAssumptionsStore.cs:63` `Save` | `AssumptionsService`（PUT） | **a** | シード競合。直後に読み直して `?? throw InvalidOperationException` があるため黙りはしないが、**本物の失敗が「シードに失敗しました」という別の話へ化ける**（原因が失われる） |
| 8 | `OrderExecutionService/.../EfOrderReservationStore.cs:28` `TryReserve` | `OrderExecutionAppService`（発注前予約） | **c＋b** | 一意違反＝他プロセスが先に予約、が主因。**書き込み失敗一般も `false`** へ落ち、`OrderDispatchReservationConflictException`（＝予約済み）という**嘘の説明**で終わる |
| 9 | `OrderExecutionService/.../EfOrderReservationStore.cs:85` `Release` | `OrderExecutionAppService`（BrokerUnavailable）／`OrderReservationReconciler` | **c** | 削除の競合。**行が消えたか**で判定できるのに例外の型で判定している |
| 10 | `ReportService/.../EfReportStore.cs:51` `UpsertDraft` | `ReportService`（POST/PUT） | **a＋b** | 同一 `PeriodKey` の並行作成。**行が無くても `ReportConcurrencyException(…, 0)` を投げる**＝本物の保存失敗を「競合」に偽装する |
| 11 | `CostControlService/.../EfProcessedMessageStore.cs:32` `TryMarkProcessed` | `LlmCostIncurredHandler` | **c＋b** | 冪等性キー（`MessageId` 行の実在＝処理済み）。**本物の失敗も `false`＝「処理済み」に化け、ハンドラが no-op で return する**＝ 🔴 **費用が計上されないまま消える**（fail-open） |

## 設計（受け入れ基準 2・3）

### 共通の形

```csharp
try { db.SaveChanges(); /* 成功時 */ }
catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
{
    db.ChangeTracker.Clear();               // 追跡を捨ててから読み直す
    var raced = /* 対象レコードを読み直す */;
    if (raced is null) throw;               // 行が生まれていない＝競合ではない本物の失敗
    /* 競合として吸収する（従来の意味を保つ） */
}
```

- 判定は **`raced` の実在**（対象レコードが在るか）で行う。**例外の型は catch の入口を広げるだけ**で、
  判定条件には入れない。
- `ChangeTracker.Clear()` は**同一スコープに他の作業単位を持たないストア**でのみ使う。
  `EfPositionDriftStateStore` は scoped DbContext を他と共有しうるため、**自分の entry だけを
  `Detached` にして `AsNoTracking()` で読み直す**（無関係な追跡状態を巻き込まない）。

### 箇所ごとの是正（意味を変えない）

| # | 読み直す対象 | 実在したら | 実在しなければ |
| --- | --- | --- | --- |
| 1 | `RiskSettings.Find(SingletonKeys.Id)` | その行を返す（従来どおり） | `throw;`（**従来は未永続の既定値**） |
| 2 | `PositionObservationDays.Find(tradingDay)` | 記録の目的は達成＝黙って戻る | `throw;`（**従来は無言**） |
| 3 | `PositionDriftStates`（`AsNoTracking`） | **かつ `Version != state.Version`**（他方が進めた）＝負け＝`false` | `throw;`（`Version` が動いていない場合も同じ＝本物の失敗） |
| 4 | `StageTransitions.Any(Sequence == …)` | `DbUpdateConcurrencyException` へ変換（409。従来の意図） | `throw;`（＝正直な 500。従来の「素通し」と同じ） |
| 5 | 呼び出し側が渡す `rowExists` 述語（計上／未供給で別テーブル） | `false`（既に記録済み＝冪等） | `throw;`（**従来は過小計上へ黙って倒れていた**） |
| 6 | `Assumptions.Find(SingletonKeys.Id)` | その行を返す | `throw;` |
| 7 | `Assumptions.Find(SingletonKeys.Id)` | 続行（従来どおり排他チェックへ） | `throw;`（**従来は `InvalidOperationException` に化けた**） |
| 8 | `DispatchReservations.Any(DecisionId == …)` | `false`（他方が確保済み＝安全側） | `throw;` |
| 9 | `DispatchReservations.Any(DecisionId == …)` | **行がまだ在る**＝解放できていない本物の失敗＝`throw;` | **行が消えている**＝他方が既に解放/削除した＝`false`（自分は解放していない） |
| 10 | `Reports.Find(PeriodKey)` | `ReportConcurrencyException(…, current.Version)` | `throw;` |
| 11 | `ProcessedMessages.Any(MessageId == …)` | `false`（他方が先に処理済み＝二重計上を避ける） | `throw;`（**従来は費用が消えた**） |

> #9 だけ**実在の向きが反転する**（削除の競合であるため）。判定は同じく「対象レコードの実在」であり、
> 「削除したかった行が消えているか」を見る。

### 呼び出し側への影響（意図した挙動変更）

いずれも **「握り潰していた本物の失敗が例外として上がる」** 変更である。倒れる向きは全て安全側：

- #2 / #3: メッセージハンドラが失敗し、再配送される。**記録できていないことが無言でなくなる。**
- #5: 借株料の計上に失敗した日は再配送で再試行される。**過小計上のまま黙らない。**
- #8: 発注は行われない（予約前に落ちる）。二重発注は起きない。
- #11: メッセージが再配送される。**費用の取りこぼしが起きない。**

## 受け入れ基準

- [x] 11 箇所すべての目的を読み、分類（a / b / c）と根拠を表で残した
- [x] 競合吸収は「行（対象レコード）の実在」で判定し、実在しなければ再送出する形へ揃えた
- [x] 分類 b の握り潰しをやめた（やめられない箇所は無かった）
- [x] 各ストアに順序固定の再現テスト（先行がシード → 後発が同じ行を保存）を置いた
- [x] 陽性対照（競合が起きなければ従来どおり）と否定形（行が生まれない保存失敗は送出）を各ストアに置いた
- [x] グループ例外フィルタの見直し要否を判断し、現状維持の理由を IADR-0319 決定6 に残した
- [x] 対象 5 サービスの `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が緑
- [x] Architecture テストが緑

## テスト方針

`EfMonitoredSymbolStoreSeedRaceTests` と**同じ seam**（`DbContext.SavingChanges` イベント）で
交錯を決定的に組み立てる。時間・スレッドに依存させない（不定期に緑になるテストを作らない）。

- **再現**: 後発が `Add` を stage した後・確定する前に、先発（別 `DbContext`）が同じ行を確定させる。
  是正前は赤（InMemory の `ArgumentException` が catch を素通りする）。
- **陽性対照**: 交錯が無ければ従来どおりの結果（`true` / シードした値）になる。
  交錯の有無で結果が変わることを示し、テストが何も検証していない状態を避ける。
- **否定形**: `SaveChangesInterceptor` で必ず失敗させる（行は 1 件も生まれない）。
  例外がそのまま上がることを固定する。
- 交錯を組み立てたことを `Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない")`
  で自己検査する（seam が壊れたら黙って緑にならない）。
- ファイル: 各サービスの `Tests/Infrastructure/Persistence/Ef*SeedRaceTests.cs`。

## 計画書との差異

- 差異: なし。永続化層の競合処理のみを触り、計画が定める統制の内容は 1 つも変えていない。

## 未決事項

- グループ例外フィルタ（`ArgumentException → 400`）の構造改修は本作業でも採らない（IADR-0319 決定6）。
- リレーショナル（Npgsql）実機での再現は本作業では行っていない（テストは InMemory）。
  ただし**判定が例外の型に依存しなくなった**ため、プロバイダ差は原理的に判定へ影響しない。

## 追補（2026-09-09・#719）

develop マージ直後の後段 E2E で `EfPositionDriftStateStore` の初回行同時挿入テスト（REPEATABLE READ の
明示トランザクション内で 23505 を決定的に再現する装置）が赤になった。同じトランザクション内の読み直しは
スナップショットしか見ないため他方の行が「無い」と読め、本物の失敗として再送出していた。
`DbUpdateConcurrencyException` または SQLSTATE 23505 を先に「負け」と判定し、それ以外を行の実在で判定する
順序へ補正した（IADR-0319 追記）。テスト 3 件追加・`RiskManagementService.Tests` 1,617 件緑。
