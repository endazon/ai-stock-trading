---
title: 保護記録に楽観並行の版番号を持たせ、古い写しで並行に進んだ状態（完了・再武装・試行番号）を上書きしない
type: spec
status: accepted
related_ids: [FR-10, FR-12, UC-02, ADR-0040, IADR-0344, IADR-0389, IADR-0370, IADR-0057, IADR-0396]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 損切り)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
---

# 仕様書: 保護記録の古い写しからの上書きを止める（#833 項目 3）

## 起点

- [#833](https://github.com/endazon/ai-stock-trading/issues/833) の **項目 3 のみ**。項目 2（待ち時間）は PR #950 で扱い、本 PR はその上に積む
  （同じ `protective_stop_orders` の列・同じ `SoftwareStopExecutor` を触るため。migration の並びも #950 の後になる）。
- 2026-09-25 に `origin/develop`（`3d9b91c2` → `b2d26a67`）で引き直した（`git rev-parse --is-shallow-repository` → `false`）。

## 🔴 実測（コードで確認）

| 箇所 | 現在の挙動 |
| --- | --- |
| `EfProtectiveStopOrderStore.Save` | `Find` → **全列を上書き** → `SaveChanges()`。期待する版の照合が無い |
| `ProtectiveStopOrderRow` / `OrderExecutionDbContext` | `Version` / `RowVersion` / `IsConcurrencyToken` の出現は 0 件（`grep -rniE 'RowVersion\|IsConcurrencyToken\|ConcurrencyCheck'`） |
| `SoftwareStopExecutor.TryCloseCoreAsync` | 呼び出し側の写し（ガードは巡回の先頭・ハンドラは到達の受信時点）を、エントリーの照会・建玉照会・成行の送信（いずれも await）を跨いで持ち、`Settle` で全列を書き戻す |
| `ProtectiveStopDriftAdopter.ApplyAsync` | 群を読んだ後、建玉照会・逆指値の取消（await）を跨いで `ReduceBooks` が古い写しを全列で書き戻す |
| `ProtectiveStopNetting.Replace` / `ConfirmEntryFills` / `DetectUnattributedPositions` | 呼び出し側から渡された群の写しで全列を書き戻す（決済経路からは await 前に読んだ群が渡る） |
| 対照 | `ReportService/Features/Reports/IReportStore.cs` は版の照合を既に持つ |

### 起きること（実害の筋道）

1. **ClosePlaced の二重発行**: ハンドラが試行 1 の成行を送り、発注記録を保存した直後（行の確定の前）に、
   ガードの巡回が「記録済みの試行 1」を見つけて確定する（`Settle`・ClosePlaced）。続いてハンドラも自分の写しで確定し、
   **帳簿の減算と ClosePlaced が 2 回**になる。
2. **完了 → Active の巻き戻し**: 乖離の取り込みが建玉照会を待つあいだに決済が確定して行が完了（試行 1）する。
   取り込みは古い写し（Active・試行 0）で書き戻し、**完了も試行番号も巻き戻る**。次の巡回は試行 1 を
   「送信後に行の更新だけが失われた窓」と読んで記録の結果で確定し直し、**帳簿を二度減らして ClosePlaced を二度出す**。

## 射程

1. `protective_stop_orders` に `Version`（integer・NOT NULL・既定 0）を足し、EF の**並行トークン**にする（migration 1 本・列の追加だけ）。
   ドメインの `ProtectiveStopOrder.Version` は「**この写しを読んだ時点の版**」。
2. `IProtectiveStopOrderStore.TrySave(stop)`: 保存先の版が写しの版と一致するときだけ書いて版を 1 進め true。
   一致しない・行が無いなら**何も書かず** false。EF は `UPDATE … WHERE Version = @読んだ版`（0 行なら衝突）で原子的、
   インメモリは区間ロックで原子的。false の後の `Find` は保存先の最新を返す（EF は追跡を外す）。
3. `ProtectiveStopStoreUpdates.Update(id, mutate)`: **最新を読み直し**、変更を「最新の行に対する関数」として当て、
   `TrySave` できるまで最大 5 回やり直す。変更の条件（この試行はまだ確定されていない等）は最新の行で判定する。
   続けて衝突したら `ProtectiveStopConcurrencyException`（黙って諦めない）。
4. `Save` は**無条件の上書きのまま**残す（版は保存先の値から 1 進める）。EF で追跡が古いときは読み直して当て直す。
5. 書き手の切り替え:
   - `SoftwareStopExecutor`: 到達の記録・約定数量の確定・完了・`Settle`（**最新の試行番号が既にこの試行に達していたら確定も通知もしない**）・
     据え置き継続の通知を `Update` へ。割り当ての後に行が Active でなくなっていたら撃たない。
     衝突し続けたら エラーログ（LogError）を出して据え置く（到達ハンドラは他の候補を続ける）。
   - `SoftwareStopReArmer`: 戻す量を最新の行から計算して `Update`（記録は終端化済みなので、諦めると再武装は二度と起きない）。
   - `ProtectiveStopNetting`: `Replace` / `ConfirmEntryFills` / 帰属不明の印を `TrySave` へ。書けなければ群の行を最新へ差し替え、**その変化の通知を出さない**。
     ただし決済経路の観測は、書けなくても**その呼び出しの数量の上限には効かせる**（保存しない。観測前の主張で建玉より多く売らない）。
   - `ProtectiveStopDriftAdopter.ReduceBooks`: `TrySave`。衝突したら**減らさない**（Warning）。取消の事実のイベントは残す。
   - `OrderExecutionAppService`（建玉が生じなかった S1 の完了）: `TrySave`。衝突したら完了にせずガードに委ねる。

### 射程外（やらないこと）

- **`ProtectiveStopGuard` の直接の保存（S0 の再発注・手仕舞い・完了、S1 の未到達の完了）は無条件の `Save` のまま。**
  ブローカーへの操作（逆指値の再発注・取消）の**後**に書く経路であり、保存を落とすと実在する注文と記録が食い違う
  （次の巡回が同じ試行 ID で逆指値を出し直し得る）。楽観並行に切り替えるなら「読み直して当て直す」形が要る。
  着手時は**同ファイルを並行 PR #945 が編集中**だった（#945 はマージ済み）。切り替えは逆指値と記録の食い違いの検証を伴うので
  本 PR では触らない（IADR-0396 の残る制約・後続の作業）。
- `docs/` の機能仕様書の変更（保存の方式は利用者に見える挙動ではない。見える挙動＝ClosePlaced が二重に出ない は既存の記述どおりになる）。

## 🔴 母集合（走査したファイルと除外理由）

走査: `grep -rn 'stops\.Save\|protectiveStops\.Save\|IProtectiveStopOrderStore' backend --include=*.cs`（`obj` を除く）。

- **採る**: `SoftwareStopExecutor.cs`（6 箇所）・`SoftwareStopReArmer.cs`（1）・`ProtectiveStopNetting.cs`（4）・
  `ProtectiveStopDriftAdopter.cs`（1）・`OrderExecutionAppService.cs`（完了 1。新規作成の 1 箇所は「行が無ければ」の挿入なので `Save` のまま）・
  `IProtectiveStopOrderStore.cs`・`EfProtectiveStopOrderStore.cs`・`InMemoryProtectiveStopOrderStore.cs`・`ProtectiveStopOrderRow.cs`・
  `OrderExecutionDbContext.cs`・`ProtectiveStopOrder.cs`・migration（生成）。
- **除外**: `ProtectiveStopGuard.cs`（3 箇所。上の射程外）。`Hosted/ProtectiveStopGuardService.cs`（読むだけ）。
  試験の包み型（`ProtectiveStopGuardServiceTests.cs` の 3 つ・`SoftwareStopReArmerTests.cs` の 1 つ）は `TrySave` の既定実装
  （読んで比べてから書く・非原子）で動く——着手時は **PR #945 が `ProtectiveStopGuardServiceTests.cs` を編集中**だったため触らない。

## 受け入れ基準（テスト ID は T-10-795..T-10-799）

| ID | 基準 |
| --- | --- |
| T-10-795 | EF（別コンテキスト）とインメモリの両方で、古い写しの `TrySave` は false で何も書かず、読み直した写しは書ける。失敗後のコンテキストは汚れない。無条件の `Save` は追跡が古くても通り版を進める |
| T-10-796 | ハンドラとガードが同じ試行を確定しに行っても、成行は 1 本・ClosePlaced は 1 回・残保護数量の減算は 1 回 |
| T-10-797 | 乖離の取り込みの古い写しで、並行に完了した行を Active・試行 0 へ巻き戻さない。次の巡回で同じ試行を二度確定しない |
| T-10-798 | 巡回の割り当て・帰属不明の通知が古い群の写しで書くとき、完了した行を巻き戻さず、書けなかった変化を通知しない |
| T-10-798b | 決済経路は建玉照会を待つあいだに並行に完了した行を撃たない。観測の保存が衝突しても、その回の数量は建玉に見合う量に縮める |
| T-10-799 | 衝突し続けた行は書かずに エラーログ（LogError）を出して据え置き、同じ到達の他の行（AAPL 715 株 / 713 株）の決済とイベントを失わない。記録済みの試行は再送しない。再武装は衝突しても当て直す |

## 稼働中の表への影響（`protective_stop_orders`・Active 2 行）

migration は `Version integer NOT NULL DEFAULT 0` の**追加だけ**。既存 2 行は版 0 で読まれ、最初の保存で 1 になる。
行の書き換え・削除・インデックスの作り直しは無い。Down は列の削除。**アプリを先に古い版へ戻す場合**は、列が残っていても
旧コードは列を読まない（既定 0 のまま）ので動く。
