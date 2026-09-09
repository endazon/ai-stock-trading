---
title: IADR-0319 永続化層の競合吸収は全 11 箇所で「行の実在」判定へ揃え、握り潰しをやめる
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-05
  - FR-06
  - FR-10
  - FR-11
  - FR-17
  - FR-20
  - FR-21
author: 実装担当
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - FR-05
  - FR-10
  - FR-17
  - FR-20
  - FR-21
---

# IADR-0319: 永続化層の競合吸収は全 11 箇所で「行の実在」判定へ揃え、握り潰しをやめる

- 状態: Accepted
- 日付: 2026-09-09
- 決定者: 実装担当

## 起点・関連

- 起点 issue: [#714](https://github.com/endazon/ai-stock-trading/issues/714)（[#707](https://github.com/endazon/ai-stock-trading/issues/707) / [IADR-0317](./IADR-0317_singleton-seed-race-detected-by-row-presence.md) の横展開）
- 関連する計画書 ID: FR-05（発注・二重発注の防止）／FR-06・FR-07（報告書）／FR-10・FR-11（リスク統制・変更履歴）／
  FR-17（全体前提条件）／FR-20（段階ゲート）／FR-21（観測到達の記録）。計画 ADR-0016・ADR-0027 の制約に変更なし。
- 関連する実装 ADR: [IADR-0317](./IADR-0317_singleton-seed-race-detected-by-row-presence.md)（本 IADR の親）・
  [IADR-0012](./IADR-0012_risk-settings-persistence.md)・[IADR-0034](./IADR-0034_cost-concurrency-lock.md)・
  [IADR-0055](./IADR-0055_llm-cost-metering-event.md)・[IADR-0057](./IADR-0057_order-dispatch-idempotency.md)・
  [IADR-0070](./IADR-0070_stage-gate-persistence-and-approval.md)・
  [IADR-0074](./IADR-0074_reservation-reconciliation.md)・
  [IADR-0124](./IADR-0124_position-drift-state-durable.md)・
  [IADR-0181](./IADR-0181_buy-in-inference-query-and-observation-arrival.md)・
  [IADR-0183](./IADR-0183_borrow-fee-accrual-recording.md)
- 関連する実装仕様書: [20260909_714_dbupdateexception-row-presence-sweep](../specs/20260909_714_dbupdateexception-row-presence-sweep.md)

## コンテキストと課題

IADR-0317 は `MarketMonitorService.EfMonitoredSymbolStore` について「**競合の判定は例外の型ではなく
行の実在で行い、行が無ければ再送出する**」と決め、同型の `catch (DbUpdateException)` が他サービスに
10 箇所残ることを**射程外**として記録した。#714 はその掃討である。

2026-09-09 に `rg "catch \(DbUpdateException" backend/Services --glob '!**/Tests/**'` で再走査した結果は
**11 箇所**であった（IADR-0317 の「10 箇所」は `MarketMonitorService` を除いた数え方の差ではなく、
`EfBorrowFeeAccrualStore` の 1 箇所が 2 経路〔計上・未供給〕から共有されている点を数え落としていた）。

11 箇所の目的を読むと、欠陥は 3 つの形に分かれていた。

1. **プロバイダ差の取りこぼし**（全 11 箇所）。一意キー違反の例外型は relational では
   `DbUpdateException`、EF Core InMemory では `ArgumentException` である。型で判定すると
   取りこぼした側だけが素通りし、#707 のように「400 という嘘の説明」へ化ける。
2. **本物の失敗の握り潰し**（8 箇所）。行が生まれていない＝競合ではない保存失敗のときにも、
   未永続の既定値・`false`・別の例外を返して黙る。
3. **判定条件のプロバイダ固定**（1 箇所）。`EfStageGateStore` の `IsSequenceUniqueViolation` は
   `Npgsql.PostgresException { SqlState: "23505" }` を見ており、**InMemory では必ず false**
   になって 409 変換経路がまったく動いていなかった（既存テストの注記が「InMemory では再現できない」
   と述べていたのは、この実装制約を仕様と取り違えたものである）。

とくに 3 つ目と、`EfProcessedMessageStore` の握り潰しは実害が重い。後者は
「**本物の書き込み失敗が `false`＝重複とみなされ、`LlmCostIncurredHandler` が no-op で return する**」
＝ **LLM 費用が 1 円も計上されないままメッセージが消える（fail-open）** 形であった。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | 各 catch に `ArgumentException` を足すだけ | 症状（プロバイダ差）は消えるが**握り潰しは残る**。IADR-0317 が案 A として既に棄却済み |
| B | **全箇所で「対象レコードの実在」判定へ揃え、実在しなければ `throw;`**（採用） | 競合の定義をそのまま書く。プロバイダ差と握り潰しを 1 つの形で同時に閉じる |
| C | 共通ヘルパ（拡張メソッド・基底クラス）へ抽出する | 読み直す対象・実在の向き・競合時の戻り値が 11 箇所すべて異なるため、ヘルパは述語 2 本と戻り値を引数に取る**抽象化のための抽象化**になる。CLAUDE.md の「過剰な抽象化をしない」に反する |
| D | 静的検査器を新設して同型の再発を止める | IADR-0317 決定6 の条件（同型事故 2 回）には触れるが、**本作業で母集合が 0 になる**。判断は決定7 に記す |

## 決定

1. **11 箇所すべてで、競合の判定を「対象レコードが実在するか」へ移す。** catch の入口は
   `catch (Exception ex) when (ex is DbUpdateException or ArgumentException)` に統一し、
   **例外の型は判定条件に入れない**（入口を広げるだけ）。IADR-0317 決定1 の横展開である。
2. 🔴 **実在しなければ `throw;` で再送出する。** 分類 b（握り潰し）8 箇所はすべて再送出へ改めた。
   **やめられない箇所は 1 つも無かった**（#714 受け入れ基準 3 が求めた「やめられない理由」は不要）。
   倒れる向きはいずれも安全側である —— 発注は行われず（`TryReserve`）、メッセージは再配送され
   （`Record` / `TryMarkProcessed` / `SaveNewRow`）、報告は行われない（`TrySave`）。
3. **`EfStageGateStore` の `IsSequenceUniqueViolation` を廃止する。** 意図（Sequence 一意制約違反
   だけを 409 とみなし、接続断など他の失敗は 409 に丸めず素通しする）は**そのまま保つ** ——
   判定条件だけを「その `Sequence` の行が実在するか」へ置き換える。これにより 409 変換経路が
   プロバイダに依らず動き、**順序固定テストで初めて検証できるようになった**（既存テストの
   「InMemory では再現できない」注記は本 IADR で更新した）。
4. 🔴 **削除経路（`EfOrderReservationStore.Release`）では実在の向きが反転する。**
   「消したかった行が消えている＝競合（他方が先に解放/削除した）」「まだ残っている＝本物の失敗」
   である。判定の原理は同じ（対象レコードの実在）で、真偽の割り当てだけが逆になる。
5. **`EfPositionDriftStateStore` では `ChangeTracker.Clear()` を使わない。** 同ストアの
   `DbContext` は scoped で他の作業単位と共有されうるため、自分の entry だけを `Detached` にし、
   読み直しは `AsNoTracking()` で行う。競合の判定は「行が実在し、かつ版が自分の読んだ版から
   動いている」ことであり、**版が動いていなければ本物の失敗として再送出する**。
6. **グループ例外フィルタ（`ArgumentException → 400`）は現状維持とする**（IADR-0317 案 D の再掲）。
   `ArgumentException` を検証エラーとして投げる箇所がサービス横断にあり、専用の検証例外型へ
   絞る改修は #714 の射程を大きく超える。**ただし本作業で、インフラ層の `ArgumentException` が
   フィルタまで届く経路そのものが 11 箇所すべてで塞がった** —— 残るのはアプリケーション層の
   `ArgumentException` だけであり、そちらは「要求が悪い」という説明が実際に正しい。
   したがって #707 で観測された「嘘の 400」は構造的に再発しない。
7. **静的検査器は足さない。** IADR-0317 決定6 は「2 回目が観測されたら格上げする」としており、
   本件は形式上 2 回目に当たる。しかし**本作業で母集合が 0 になった**（`catch (DbUpdateException)`
   は `backend/Services` の非テストコードから消えた）ため、検知すべき対象が存在しない。
   🔴 **格上げの条件を次のように置き直す: 本作業の後に `catch (DbUpdateException)`（または
   例外の型で競合を判定する新規の catch）が 1 箇所でも再び現れたら、静的検知を新設する。**
   母集合が 0 の今なら「1 件でも出たら赤」という単純な検査で足り、閾値の議論が要らない。
8. **`EfBorrowFeeAccrualStore` を `internal` から `public` へ変える。** 順序固定テストが本クラスを
   直接組み立てるためである（テストは別アセンブリ）。兄弟の EF ストア・`InMemoryBorrowFeeAccrualStore`
   はいずれも `public` であり、**揃える方向の変更**である。`InternalsVisibleTo` は採らない
   （1 クラスのために全 internal をテストへ開くのは射程が広すぎる）。
9. **統制は 1 つも緩めない。** 値域・理由必須・変更履歴・認可・冪等の意味づけ（最初の計上を正とする・
   二重発注しない・二重計上しない）はいずれも変更していない。是正は永続化層の競合処理だけである。

## 理由

案 B は**競合の定義そのもの**を書いている。「他方が先に対象レコードを作った（削除した）」以外に
一意キー制約が挿入を弾く理由は無く、プロバイダが何を投げるかは実装詳細である。**判定条件へ実装詳細を
持ち込むと、実装詳細の変化がそのまま統制の穴になる。**

決定2 は「握り潰しの範囲を狭めることは統制を強める側の変更である」という IADR-0317 決定2 の
踏襲である。とくに `EfProcessedMessageStore` は、握り潰しが**費用統制の fail-open** を作っていた ——
「安全側へ倒している」という説明が付いていた箇所ほど、実際には倒れる先が安全でないことがある。

案 C を採らなかったのは、11 箇所の差異が本質的だからである。読み直す対象（単一行・複合キー・
述語）、実在の向き（挿入か削除か）、競合時の戻り値（値・`false`・別例外への変換）がすべて違い、
共通化しても引数で場合分けを表現し直すだけになる。**11 箇所が同じ「形」を持つことは、コメントと
テストで担保する方が読み手に伝わる。**

## 結果

- 良い影響:
  - `catch (DbUpdateException)` は `backend/Services` の非テストコードから**消えた**（実測 11 → 0）。
  - **本番（Npgsql）でも InMemory でも同じ判定が働く。** とくに `EfStageGateStore` の 409 変換は
    **これまで relational でしか動いていなかった**（テストで一度も通っていなかった）。
  - 費用統制の fail-open（`EfProcessedMessageStore`）と、借株料の無言の過小計上
    （`EfBorrowFeeAccrualStore`。IADR-0183 が残余リスクとして明記していたもの）が閉じた。
  - 各ストアに**順序固定の再現テスト＋陽性対照＋否定形**が付いた（新規 33 件）。
- 悪い影響・トレードオフ:
  - **握り潰していた失敗が例外として上がるため、メッセージハンドラの失敗（再配送・`_error` 送り）が
    増えうる。** これは意図した変更である（従来は「記録できていない」ことが誰にも見えなかった）。
    ただし `BrokerPositionsObservedHandler` の再配送では推定・乖離検知も再走するため、
    **DB 障害が長引くと同じ観測の処理が繰り返される**。これは DB 障害時の一般的な挙動と同じであり、
    本作業で新たに導入したものではない（同ハンドラは他の DB 操作でも同様に落ちる）。
  - `catch` が `ArgumentException` を含むため、**万一その経路で別の `ArgumentException` が出ても
    一度は捕まる**。ただし対象レコードが無ければ `throw;` で素通りするので握り潰しにはならない
    （IADR-0317 と同じトレードオフ）。
  - `EfBorrowFeeAccrualStore.SaveNewRow` が述語（`Func<bool>`）を受け取るようになり、呼び出し側が
    「自分が書こうとした行」を明示する責務を負った。共有メソッドから対象テーブルを決め打ちできない
    ためであり、**引数 1 本で済む最小の形**を採った。
- フォローアップ:
  - グループ例外フィルタの構造改修（専用の検証例外型）は引き続き未着手（決定6）。
  - リレーショナル（Npgsql）実機での再現は行っていない。**判定が例外の型に依存しなくなったため
    プロバイダ差は原理的に判定へ影響しない**が、実コンテナ E2E での確認は残件である。
- 実測（変異試験。是正前の実装へ戻して確認した）:
  - `EfRiskSettingsStore` を戻すと `EfStoreSeedRaceTests` が **2 失敗 / 14 合格**、戻して 16 合格。
  - `EfStageGateStore` を戻すと **1 失敗 / 15 合格**、戻して 16 合格。
  - `EfProcessedMessageStore` を戻すと `EfProcessedMessageStoreSeedRaceTests` が **2 失敗 / 1 合格**、
    戻して 3 合格。
- 実測（全体）: `RiskManagementService.Tests` 1,614 件・`ConfigurationService.Tests` 23 件・
  `OrderExecutionService.Tests` 323 件・`ReportService.Tests` 843 件・`CostControlService.Tests` 126 件・
  `AiStockTrading.Architecture.Tests` 121 件（1 skip）がすべて緑。`dotnet build` 警告 0、
  `dotnet format --verify-no-changes` 差分 0。
- 残余リスク:
  - 再現テストは `DbContext.SavingChanges`（`EfProcessedMessageStore` のみ options 上の
    `SaveChangesInterceptor`）を seam に使う。EF Core がこれらを廃止すると交錯を組み立てられなく
    なるが、そのときは**黙って緑になるのではなくコンパイルが落ちる**。
  - `EfPositionDriftStateStore` の競合判定は「版が動いていること」を条件に含むため、
    **別レプリカが自分とまったく同じ版を書いた**場合は本物の失敗として再送出する。
    版は保存のたびに +1 されるため実際には起こらないが、判定の穴として記録しておく。

## 追記（2026-09-09・#719）—— 読み直しは明示トランザクションの中では証拠にならない

develop へマージ直後の後段 E2E（`Integration E2E`・run 34314851154）で
`PositionDriftStateConcurrencyE2ETests.初回行の同時挿入は実DBでも片方だけが勝つ` が赤になった（#719）。
同テストは呼び出し側が **REPEATABLE READ の明示トランザクション**を張り、先に 1 度読んでスナップショットを
固定してから別接続に行を作らせ、その後の INSERT を確実に 23505 にする——初回行の同時挿入を実 DB で
決定的に再現する装置である。

決定 5 の `EfPositionDriftStateStore` は失敗後に `AsNoTracking()` で読み直していたが、**同じ接続・同じ
トランザクションの読み直しはスナップショットしか見ない**ため他方のコミット済み行が「無い」と読め、
「行が無い＝本物の失敗」として再送出した。Postgres では失敗した文でトランザクション自体が abort する
ことも重なる。InMemory はトランザクションが no-op で行を隠さないため、決定 5 の順序固定テストでは
この形を観測できなかった（残余リスク「Npgsql 実機での再現は未実施」が的中した）。

### 是正（決定 5 の補正）

同ストアの競合判定を次の順にする。

1. **EF／DB が既に確定させた事実を先に見る**: `DbUpdateConcurrencyException`（並行トークン不一致）、
   または内側の `DbException.SqlState == "23505"`（SQL 標準の unique_violation。プロバイダ固有の
   例外型ではなく .NET の抽象で読むので、決定 1「型を列挙しない」は保つ）→ **負け（false）**。
   固定キーの単一行 INSERT が制約で失敗する理由は他方が先に作ったこと以外に無い。
2. どちらでもないとき（InMemory の `ArgumentException`・SQLSTATE を持たない失敗）は従来どおり
   **行の実在と版**で判定し、行が無ければ再送出する。

順序固定テストに 3 件を足した（23505 → 負け／`DbUpdateConcurrencyException` → 負け／08006
〔connection_failure〕→ 送出）。`RiskManagementService.Tests` 1,617 件緑。

### 射程

- 是正は `EfPositionDriftStateStore` のみ。同 E2E の他 12 件（注文予約・報告書・費用統制の経路を含む）は
  緑であり、他ストアの読み直しは呼び出し側が明示トランザクションを張らない経路で使われている。
  **他ストアが明示トランザクションの中から呼ばれる設計変更をするときは、本追記と同じ補正が要る。**
- 決定 7（検査器の格上げ条件）は変えない。

## 関連

- Supersedes: なし（IADR-0317 のフォローアップ「同型 10 箇所は射程外」を消化する）
- Superseded by: なし
