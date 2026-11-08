---
title: 再確定の KB の後処理の時間予算の試験を、壁時計の大小から予算の取り消しへの同期へ移す（Integration の揺らぎ）
type: spec
status: accepted
related_ids: [NFR, FR-07, FR-08, ADR-0068, IADR-0556, IADR-0549, IADR-0364]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0068_daily-policy-reconfirm-by-adding-version.md (決定6: 確定版の写しの置き換えとドラフトの写しの削除の順序)
---

# 作業仕様書 — 再確定の時間予算の試験の揺らぎ（NFR）

> 着手 2026-11-08（develop `b78b0b196`）。

## 起点

- Integration（`integration.yml` の `integration-e2e`）の run 42431353521 / job 128580737711 で、
  `ReportService.Tests.ReportConfirmKnowledgeBudgetTests.T_10_2726_再確定で置き換えが予算内に終わらなければドラフトの写しを消さない`
  が 1 回落ちた（7 秒）。走った head は #1611 の e458a0b8 で、同 PR の差分は helm と文書だけ（ReportService に触れていない）。
- 失敗の形（ログの原文の要約）: 139 行の `catalog.Docs.Should().Contain(draft, …)` が落ちた。catalog に残っていたのは
  **確定版の写し 1 件だけで、その本文は `# 日報\n\n版3の本文`**（＝置き換えが完了している）。ドラフトの写しは消えていた。
- 起点 ID: 計画の非機能要件表に「試験の安定性」に当たる番号は無い（メタ作業。`traceability.md` の無採番 NFR の場合 2。
  #900 / IADR-0364 と同じ扱い）。試験が固定している性質は FR-07 / FR-08・ADR-0068 決定6・IADR-0556 決定 1。

## 原因（どの経路・どの時計・どの競合か）

- 経路: `ConfirmReportEndpoint.RunKnowledgeAftercareAsync` → `ReplaceConfirmedCopyAsync` →
  `ReportKnowledgeReingestService.RunAsync`（`refreshExisting: true`・1 期間キー）。予算は
  `new CancellationTokenSource(budget)`（0.2 秒・実時間のタイマー）の取り消しとして渡る。
- 試験の偽物 `FakeKnowledgeCatalog` は**取り消しを一切見ない**（一覧・本文の差し込み・削除はすべて同期で `Task.FromResult`）。
  旧い試験は一覧の前に `Task.Delay(1 秒)`（取り消しを受けない実時間の遅延）を挟み、「1 秒 ＞ 0.2 秒だから、一覧が返った時点で
  予算は切れている」に頼っていた。
- 入れ直しが予算を見るのは、一覧の後の `foreach` の先頭の `cancellationToken.IsCancellationRequested` だけである
  （偽物が同期なので、ここを抜けると置き換え → 残ったドラフトの写しの削除まで止まらずに進む）。
- 競合: **予算のタイマーの呼び戻し（取り消しの印を立てる）と、1 秒の遅延の完了（一覧の継続）は、どちらもタイマーの呼び戻しとして
  スレッドプールで配送される。** プールが飢えると（遅い runner・並列の試験）、空いた時点で**両方が期限切れ**になっており、
  **どちらが先に走るかは保証されない**。遅延の継続が先に走ると、入れ直しは「取り消されていない」と読んで本文を差し替え、
  ドラフトの写しを消す（＝ログの形そのもの）。#900（IADR-0364）と同型の「合否を決める 2 つの事象がどちらも壁時計」。
- **実装の順序の誤りではない**: ドラフトの写しを消すのは**置き換えが成功した後だけ**であり（`RemoveLeftoverDraftsAsync` は
  Created / BodyRefreshed / BodyAttached / AlreadyPresent のときだけ）、ログでも確定版の写しは版 3 の本文に置き換わっていた。
  ADR-0068 決定6 の順序（確定版の写しを作れなければドラフトの写しを消さない）は破れていない。予算は「打ち切りの上限」であり、
  取り消しの印が立つ前に終わった仕事を巻き戻す約束ではない（IADR-0556 決定 1）。よって**実装は変えない**。IADR は書かない
  （実装の判断を伴わない）。

## 再現の実測（着手前）

### 第 1 巡: CPU を絞る・負荷をかけるだけ

- 1 CPU に絞りスピンループ 6 本を併走して当該クラスを 5 回: 5/5 緑。
- `DOTNET_ThreadPool_ForceMaxWorkerThreads=3` で 6 回: 6/6 緑。
- 全件（2093 件）を 3 本並走（2 CPU）で 12 回: 当該試験は 12/12 緑（別件 `CompositionWiringGuardTests` は、写した bin を
  リポジトリの外から走らせたために落ちる。本件と無関係）。

→ CPU を焼くだけでは再現しない（#900 の第 1 巡と同じ）。

### 第 2 巡: 一覧に入った時点でスレッドプールを塞ぐ（一時的な診断試験・コミットしない）

一時ハーネス `ZzBudgetRaceProbeTests`（試験と同じ構成・同じ再確定。一覧に入った瞬間に `Thread.Sleep(1500)` の作業項目
1024 本を積む＝#900 の第 2 巡と同じ波）で、同一プロセス内で反復した。

- 第 1 回（20 回・スピンループ 8 本を併走〔途中で止まった〕）: **19 緑 / 1 赤**。赤はドラフトの写しが消え、本文が置き換わった
  （ログの形と同じ）。
- 第 2 回（50 回・スピンループなし）: 50/50 緑。プールを塞ぐと**両方のタイマーの配送が遅れる**だけで、多くは順序が入れ替わらない。

→ 再現はするが稀。

### 第 3 巡: 一覧に入った直後にプロセス全体を 1.5 秒止める（一時的な診断試験・コミットしない）

一時ハーネス `ZzStopProbeTests`（同じ構成・同じ再確定。一覧に入った 50 ms 後に外から `kill -STOP <pid>`、1.5 秒後に `kill -CONT`。
遅い runner で VM が止まる形＝CI の 7 秒に合う）。止まっている間に予算（0.2 秒）と遅延（1 秒）の**両方が期限切れ**になり、
再開時の配送の順序が試験の合否を決める。

| 形 | 回数 | 緑 | 赤 |
| --- | --- | --- | --- |
| 直す前（`Task.Delay(1 秒)`・取り消しを見ない） | 10 | 2 | **8** |
| 直す前（同上・再走） | 50 | 18 | **32** |
| 直した後（予算の取り消しまで待つ） | 50 | **50** | 0 |

赤はすべて「ドラフトの写しが消えた／本文が差し替わった」（`catalog.Docs.Contains(draft) && PutCalls == 0` が偽）。

## 直し方

- `FakeKnowledgeCatalog.BeforeList` を `Func<Task>` から `Func<CancellationToken, Task>` にし、一覧に渡された取り消しを渡す
  （もう 1 つの利用者 `ReportKnowledgeReingestWiringTests.実行中の二本目は409で何もしない` は `_ =>` に変えるだけ）。
- 当該試験の一覧は**予算の取り消しが届くまで返らない**（`Task.Delay(30 秒, 予算の取り消し)` を待ち、取り消しで抜けたら一覧を返す）。
  取り消しの印は呼び戻しの中で継続より**先に**立つので、一覧が返った時点で `IsCancellationRequested` は必ず真であり、
  実時間の速さに依らず「予算の後に一覧が返った」という同じ命題を作る。経路（一覧は返る → `foreach` の先頭で取り消しを見て
  NotAttempted → 置き換えも削除もしない）は旧い試験と同じ。
- 30 秒は**退行の検知用の上限**であり合否の基準ではない（予算が一覧へ届かなければ 30 秒後に一覧を返し、置き換え → 削除へ進んで
  期待が落ちる。IADR-0364 決定 2 の Guard と同じ役割）。
- 期待は緩めない。足したもの: 一覧まで進んだ（`ListCalls == 1`）・一覧は予算の取り消しを受けてから返った・本文を差し替えていない
  （`PutCalls == 0`）。既存の「ドラフトの写しを消さない」「確定版の写しは版 2 の本文のまま」「削除 0 回」はそのまま。

## 母集合（規則 9・6）

- 誤りの側の形で引いた: `BeforeList` の利用者（`git grep -n BeforeList backend`）は当該試験と `ReportKnowledgeReingestWiringTests`
  の 2 箇所と定義 2 行。後者は解放の同期（`TaskCompletionSource`）で待っており壁時計に依らないので、型の追随だけ。
- 同じクラスの他の試験: T-10-2726（初回）と T-10-2728 は `HangingWriter`（`Task.Delay(Infinite, 取り消し)`）で予算の取り消しを
  待つ形で、すでに同期している。T-10-2727 は純関数。追随不要。
- 同型の全テストの棚卸し（「合否を決める 2 つの事象がどちらも壁時計か」）は射程外（#900 の未決事項と同じ）。

## 受け入れ基準

- [x] 「置き換えが予算内に終わらなければドラフトの写しを消さない」の期待を保つ（緩めない・skip しない）。
- [x] 合否を決める事象に壁時計どうしの競争が無い（一覧の返りが予算の取り消しに同期する）。
- [x] 第 2 巡の条件で、直す前の形が落ち、直した後の形が緑であることを示す。
- [x] 負荷をかけた状態で多数回（50 回）緑。
- [x] 変異注入で弱まっていないことを示す。
- [x] `dotnet build backend/backend.slnx`・ReportService.Tests 全件・`dotnet format --verify-no-changes` が通る。

## 計画書との差異

- 差異: なし（試験の観測方法だけを変える。製品コードは変えない）。

## 検証

- 第 3 巡の表のとおり、プロセスを止める条件で**直す前 32/50 赤 → 直した後 0/50 赤**。
- 負荷の下での多数回（2 CPU に絞り、全件 2093 件の並走 2 本を負荷にして、`FullyQualifiedName~Knowledge` の 105 件を
  1 プロセスで回す）: 直す前 50/50 緑、**直した後 50/50 緑**（当該試験の赤 0）。自然な負荷だけでは直す前も落ちない（第 1 巡と同じ）。
- 変異注入（直した後の試験が弱まっていないこと）:
  - M1: 確定の後処理が入れ直しへ予算を渡さない（`CancellationToken.None`）→ **赤**（30 秒の上限で一覧が返り、置き換えへ進む）。
  - M2: 入れ直しの `foreach` の先頭の取り消しの確認を外す → **赤**（1 秒）。
  - いずれも元に戻した。
- `dotnet build backend/backend.slnx`: 警告 0・エラー 0。`ReportService.Tests` 全件: 2093/2093 緑。
  `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし。
- 一時ハーネス（`ZzBudgetRaceProbeTests` / `ZzStopProbeTests`）はコミットしていない。
