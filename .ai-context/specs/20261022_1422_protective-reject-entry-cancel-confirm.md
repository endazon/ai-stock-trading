---
title: 保護逆指値が未受理のときのエントリー取消を、照会で終端を確かめてから「取消済み」と扱う（取消の窓の約定を保護なしで残さない）
type: spec
status: done
related_ids: [FR-10, UC-02, ADR-0016, ADR-0040, IADR-0210, IADR-0428, IADR-0113, IADR-0569]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
---

# 仕様書: 保護逆指値が未受理のときのエントリー取消の確認（#1422）

> 本仕様書は実装着手前に作成した（着手 2026-10-22。AST `origin/develop` `66a6e0ee`）。

## 起点（トレーサビリティ）

- 起点: #1422（PoC セッションの AST 機能面の全体監査・2026-10-21。S0 / S3 の経路。稼働 PoC は S1 のため現状は通らない）。
- 計画: FR-10（逆指値なしの建玉を持たない）、業務フロー 02「逆指値が成立しない場合の扱い」の表
  （**逆指値が未受理 → エントリー自体を取り消す。既に約定していれば即座に成行で手仕舞う**）、ADR-0016 決定 2(b)、ADR-0040 決定 1（S0 / S3）。
- 前提の IADR: IADR-0210（未受理時の建玉解消）、IADR-0428（保護レグの 3 相・AwaitingEntry）、IADR-0113（約定追跡）。
- 本件の判断: IADR-0569（新規）。
- 計画との矛盾: なし（計画は「取り消す」としか書かず、取消要求と約定の競合〔`Cancelling_*` を経て約定し得る〕の扱いを定めていない。
  実装は計画の表の 2 行〔取り消す／約定していれば成行で手仕舞う〕を、取消の結果を確かめたうえで適用するだけで、規律を足しも減らしもしない）。

## 問題（再確認・develop `66a6e0ee`）

- `OrderExecutionAppService.ResolveUnprotectedEntryAsync` は、未約定（取消前のスナップショットで `FilledQuantity == 0`）のエントリーへ
  `CancelOrderAsync` を送り、**例外が出なければ照会せず** `EntryCancelled`（数量＝承認数量）を返す。
- その直前に `CompleteAwaitingProtection` で承認時の保護の文脈（AwaitingEntry）を Completed にしている。
- 取消要求の後に約定すると (a) 通知は「取り消した（建玉は生じていない）」と事実の逆、(b) 保護記録が無く巡回されない建玉が残る。
  約定は約定追跡（`OrderFillPoller`）が台帳へ届けるが、保護も手仕舞いも誰も行わない。

## 母集合（保護逆指値の発注・受理確認・エントリーの経路を全件走査）

走査: `grep -rn "CancelOrderAsync(\|PlaceStopOrderAsync(\|PlaceAlternativeStopOrderAsync(" backend/Services/OrderExecutionService --include=*.cs`（試験・アダプタ除く）、
および `ResolveUnprotectedEntryAsync` / `PlaceProtectiveStopAsync` / `CompleteAwaitingProtection` / `ProtectiveStopState.AwaitingEntry` の参照。

| # | 箇所 | 何をするか | 本件の対象か |
|---|---|---|---|
| 1 | `OrderExecutionAppService.ExecuteCoreAsync` → `PlaceProtectiveStopAsync`（S0 / S3 の同時発注） | 逆指値の受理・据え置き・未受理を分岐し、未受理なら `ResolveUnprotectedEntryAsync` | **対象**（未受理→取消の分岐） |
| 2 | `OrderExecutionAppService.ProtectAsync`（突合で確定したエントリー）→ `PlaceProtectiveStopAsync` | 1 と同じ分岐を通す | **対象**（同じ関数を通るので同時に直る） |
| 3 | `ResolveUnprotectedEntryAsync` の取消成功の枝（`filled == 0` → 取消 → 即 `EntryCancelled`） | 照会せず「取消済み」 | **是正の本体** |
| 4 | `ResolveUnprotectedEntryAsync` の取消例外の枝（照会して `filled == 0` なら None、>0 なら成行） | 例外時だけ照会 | 対象外（挙動は変えない。残余は IADR-0569 §残余リスク） |
| 5 | `ResolveUnprotectedEntryAsync` の「取消前のスナップショットで既に約定」の枝（成行手仕舞い） | 部分約定の残りを取り消さない | 対象外（別件。IADR-0569 §残余リスク） |
| 6 | `OrderAmendmentService`（`CancelOrderAsync` → 照会で終端を確かめる） | 確認する経路（対比） | 対象外（既に正しい） |
| 7 | `SoftwareStopExecutor.ResolveEntryAsync` / 判断の手仕舞いの取消（取消 → 照会・非終端は据え置き） | 確認する経路（対比） | 対象外（既に正しい） |
| 8 | `ProtectiveStopGuard`（建玉消滅時の**逆指値**の取消、失効時の再発注） | エントリーは取り消さない | 対象外 |
| 9 | `OrderFillPoller.ApplyAsync`（約定追跡の終端化） | エントリーの記録を終端化し `OrderExecuted` を出す | **フック先**（確認待ちのエントリーの後始末をここで行う） |
| 10 | `AwaitingEntry` を読む箇所（`ProtectAsync`・`CloseUnplacedEntryProtection`・`StopLossMethodCoexistenceGate`・`FindIncomplete`） | 新しい状態の扱いを確認 | 確認のみ（下の「新しい状態の波及」） |

## 受け入れ基準（#1422 の推奨を写像）

1. **AC1**: 保護逆指値が未受理でエントリーが未約定なら、取消を送った**後に照会し**、終端かつ約定 0 を確かめたときだけ `EntryCancelled` を返し、承認時の保護の文脈を閉じる。
2. **AC2**: 取消を送った後の照会で終端かつ約定 > 0（取消の窓で約定した）なら、**約定数量を成行で手仕舞う**（`PositionClosed` 等。業務フロー 02 の表の 2 行目）。
3. **AC3**: 取消を送った後の照会が**非終端または不明**なら、「取消済み」と主張しない。保護の文脈を閉じずに「取消の確認待ち」（新状態 `AwaitingEntryCancel`）として残し、
   保護喪失を新しい対処 `EntryCancelUnconfirmed`（Critical・建玉解消をまだ確定していない）で通知する。
4. **AC4**: 約定追跡がその後エントリーの終端化を観測したら、確認待ちの記録を閉じ、約定 0 なら `EntryCancelled`、約定 > 0 なら約定数量を成行で手仕舞った結果の保護喪失を発行する（**1 回だけ**。楽観並行で二重に手仕舞わない）。
5. **AC5**: 確認待ちでないエントリー（平常の受理・据え置き・記録なし）の終端化では何もしない（否定形）。
6. **AC6**: 通知・監査の文面が `EntryCancelUnconfirmed` を「取り消した」「解消した」と読ませない（件名・本文・要約に専用の腕）。

## 設計（IADR-0569）

- 取消後の照会は `TryGetOrderAsync`（例外は null＝不明）。終端の判定は `OrderStatusLifecycle.IsTerminal`。約定数は取消前と照会の大きい方。
- 確認待ちは保護記録の状態 `AwaitingEntryCancel = 3`（整数列・末尾追加・Migration 不要）。`FindActive` に載らない（ガードは見ない）。
  `FindIncomplete`（≠Completed）には載る＝併存の関門は「生きているかもしれないエントリー」として数える（安全側）。
- 後始末の口 `IUnconfirmedEntryCancelResolution`（実体は `OrderExecutionAppService`）。約定追跡は終端化した**新規建て**の記録についてだけ呼び、
  返った保護喪失を結果へ載せ、Worker が `OrderExecuted` の**後**に発行する（台帳は約定を先に知る）。
- 失敗で巡回を止めない（Critical を残す）。記録の状態の遷移は `TrySave`（版の一致）で 1 回だけ。

### 新しい状態の波及（確認）

- `ProtectAsync`: `row.State != AwaitingEntry` → `AlreadyHandled`（張り直さない）。妥当（取消済みのエントリーに保護レグを張らない）。
- `CloseUnplacedEntryProtection`: 既定の腕 `false` → `LeftOpen`。発注の記録がある行なのでそもそも到達しない。
- `StopLossMethodCoexistenceGate` / `FindIncomplete`: 未完了として数える（解決までは同じ銘柄・方向の別手法の新規建てを見送る＝安全側）。
- `SoftwareStopFloorRetrofitter`: S1 の Active 行だけを見る（対象外）。

## 試験（xUnit v3 + AwesomeAssertions）

- `OrderExecutionServiceProtectiveStopTests`: AC1（照会で Cancelled/0）、AC2（照会で終端・約定 > 0 → 成行）、AC3（非終端・不明 → 確認待ち・成行なし・Completed にしない）。
- 確認待ちの後始末（`ResolveUnconfirmedEntryCancelAsync`）: AC4（約定 0 → EntryCancelled・約定 > 0 → 成行・2 回目は null）、AC5（AwaitingEntry 等では null）。
- `OrderFillPollerTests`: 終端化で口を呼び結果へ載せる・非終端／決済では呼ばない・口の例外で巡回を止めない。
- 通知・監査: `EntryCancelUnconfirmed` の件名・本文・要約。
- 既存の「取消成功 → EntryCancelled」を前提にした偽ブローカーは、取消後の照会で Cancelled を返すように直す（確認を前提にするのが本件の規律）。
- 変異確認: 是正（取消後の照会）を外すと AC2・AC3 の試験が赤になること。

## 検証

- `dotnet build backend/backend.slnx`、`dotnet test`（OrderExecutionService / NotificationService / AuditService）、`dotnet format --verify-no-changes`、文書系検査。

［2026-10-22 追記 / PR #1436 の AI レビュー］後始末の成行手仕舞いが例外で落ちたら `None`（Critical）の保護喪失を返す（IADR-0569 決定 5）。
試験 T-10-2983（保護喪失が出る・巡回は止まらない・二重に手仕舞わない）。変異 EC4（例外を投げ返す）で赤を確認。

［2026-10-22 追記 / PR #1436 の AI 再レビュー］Completed の後の成行手仕舞いは取消トークンに依存させず、`OperationCanceledException` も握って `None` を返す（T-10-2984・変異 EC5／EC6）。
確認待ちの残留は併存の関門（別の手法・同じ銘柄・同じ方向）を塞ぎ続けることをコードで確認した。解除は人の手順として Runbook に書いた（自動解除は範囲外）。

