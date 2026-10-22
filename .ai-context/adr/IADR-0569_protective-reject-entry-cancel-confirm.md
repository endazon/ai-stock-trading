---
title: IADR-0569 保護逆指値が未受理のときのエントリー取消は、送った後に照会して終端を確かめてから「取消済み」と扱う。確かめられなければ保護の文脈を「取消の確認待ち」で残し、約定追跡の終端化で取消の確定か約定数量の成行手仕舞いを行う
type: impl-adr
status: Accepted
related_ids: [FR-10, UC-02, ADR-0016, ADR-0040, IADR-0113, IADR-0117, IADR-0210, IADR-0369, IADR-0389, IADR-0396, IADR-0428, IADR-0481]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md（逆指値が成立しない場合の扱い）
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md（FR-10）
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md（決定 1）
related_specs:
  - ../specs/20261022_1422_protective-reject-entry-cancel-confirm.md
---

# IADR-0569: 保護逆指値が未受理のときのエントリー取消の確認

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-22
- 決定者: Claude Code（#1422 の推奨に沿う実装の形。計画の規律は変えない）

## 起点・関連

- 起点: #1422（PoC セッションの AST 機能面の全体監査。S0 / S3 の経路）。
- 計画: 業務フロー 02「逆指値が成立しない場合の扱い」の表（**逆指値が未受理 → エントリー自体を取り消す。既に約定していれば即座に成行で手仕舞う**）、FR-10（逆指値なしの建玉を持たない）、ADR-0040 決定 1。
- 前提（覆さない）: IADR-0210（未受理時の建玉解消）、IADR-0428（保護レグの 3 相・AwaitingEntry）、IADR-0117 改定 7（成行手仕舞いの 3 相）、IADR-0369（確認できた拒否）、
  IADR-0113（約定追跡）、IADR-0389（約定追跡の終端化での後始末＝S1 の再武装と同じ置き場所）、IADR-0396（保護記録の楽観並行）、IADR-0481（未完了の記録で併存を見送る）。

## コンテキスト

`ResolveUnprotectedEntryAsync` は未約定のエントリーへ取消を送り、例外が出なければ照会せず `EntryCancelled`（建玉は生じていない）を返していた。
直前に保護の文脈（AwaitingEntry）を閉じているため、取消要求の後に約定すると（moomoo は `Cancelling_*` を経て約定し得る）、
通知は事実の逆になり、保護記録の無い・巡回されない建玉が残る。同じリポの `OrderAmendmentService`・`SoftwareStopExecutor` は取消の後に照会して終端を確かめている。

## 検討した選択肢

1. 取消の後に照会し、終端なら確定・非終端なら人手対応（Remediation=None）で終える — 簡単だが、取消の窓で約定した建玉の後始末を人に任せ、通知は 1 回きり。
2. 取消の後に照会し、非終端なら短く待って照会し直す — 発注の経路（メッセージの処理）を待たせ、待っても終端する保証は無い。
3. **取消の後に照会し、終端なら確定、非終端・不明なら保護の文脈を「取消の確認待ち」で残し、約定追跡の終端化で後始末する**（#1422 の推奨）。

3 を採る。後始末の行き先は計画の表の 2 行目（約定していれば成行で手仕舞う）に揃え、逆指値の送り直し（`ProtectAsync`）は採らない
——未受理だった逆指値を同じレグ番号で送り直すことになり、計画の表にも無い。

## 決定

1. **取消は送った後に照会して終端を確かめる。** 終端・約定 0 → `EntryCancelled`。終端・約定 > 0（取消前のスナップショットと照会の大きい方、承認数量まで）→
   約定数量を成行で手仕舞う（`CloseUnprotectedPositionAsync`。3 相・拒否・不明の分岐は従来どおり）。保護の文脈を閉じるのは行き先が確定した枝だけ。
2. **確かめられない（非終端・照会不能）なら保護記録を新しい状態 `AwaitingEntryCancel = 3` へ移す**（AwaitingEntry の行を楽観並行で更新。行が無ければ作る）。
   整数列の末尾追加で Migration 不要。`FindActive` に載らない（ガードは見ない）。`FindOpenFor`（≠Completed）には載る＝解決までは同じ銘柄・方向の別手法の新規建てを見送る（安全側）。
   成行も逆指値の送り直しもしない（非終端のまま約定数で手仕舞うと残りの約定がまた無保護で残る）。
3. **保護喪失の対処に `EntryCancelUnconfirmed` を足す**（共有契約の列挙の末尾。序数を動かさない）。決済意図・CloseDecisionId は運ばない（台帳は押さえない）。
   通知（Critical）・監査の要約は専用の腕で「取消を確認中／取消を送ったが未確認」と書き、「取り消しました」「建玉は生じていません」「解消」と読ませない。
4. **後始末の口 `IUnconfirmedEntryCancelResolution`（実体は発注執行）を約定追跡へ渡す。** 約定追跡は新規建ての記録を終端化した**後**に呼ぶ。
   口は行が `AwaitingEntryCancel` で記録が終端のときだけ、行を `TrySave` で Completed へ移せたら進む（同じ終端化を 2 回処理しても成行は 1 本）。
   約定 0 → `EntryCancelled`、約定 > 0 → 約定数量（承認数量まで）の成行手仕舞いの結果。Worker は `OrderExecuted` の**後**に発行する（台帳は約定を先に知る）。
   口の例外で巡回を止めず Critical を残す。Program.cs は moomoo 構成の約定追跡へ発注執行を渡す（渡し忘れは合成の試験が赤にする）。
5. **［2026-10-22 追記 / PR #1436 の AI レビュー］後始末の成行手仕舞いが例外で落ちたら、`None`（解消にも失敗・Critical）の保護喪失を返す**（案 A）。
   行は Completed へ移した後であり、終端化の観測は 1 回きりなので、例外を投げ返すと保護喪失が 1 件も出ず、約定した建玉が無音で残る。
   `None` は既存の通知・監査の経路に乗る（台帳は押さえない＝決済意図を運ばない）。
   行を `AwaitingEntryCancel` へ戻す案 B は採らない: (a) 再び呼ばれる契機が無い（エントリーの記録は終端化済みで約定追跡の対象から外れる）ので、戻しても再試行されず、
   黙って確認待ちに残るだけになる。(b) 例外の位置によっては成行が送られた後（予約の後・記録の前）であり得て、再試行は二重決済の危険がある
   （成行の 3 相の「届いたか不明」は `CloseUnprotectedPositionAsync` 自身が `CloseDispatchIndeterminate` で返す。ここへ来る例外は予約・記録の障害）。
   ［2026-10-22 追記 / PR #1436 の AI 再レビュー］**Completed へ移した後の区間は取消トークンに依存させない**（`CancellationToken.None` で成行手仕舞いへ進む。
   突合が確定後の保護レグに取消トークンを渡さないのと同じ）。ホストの停止で `OperationCanceledException` が抜けると、再び呼ばれる契機が無く無音になるため。
   取消トークン以外の `OperationCanceledException`（通信のタイムアウト等）も握って `None` を返す。成行は従来どおり 1 回だけ送り、再試行はしないので、
   二重発注の危険は増えない（予約は届いたか不明なら Reserved のまま残り、同じ CloseDecisionId では送り直さない）。

## 試験と変異

`UnconfirmedEntryCancelTests`（受け入れ基準 AC1〜AC5）、`UnconfirmedEntryCancelCompositionTests`（Program.cs）、`NotificationFormatterTests`・`AuditEntryFactoryTests`（AC6）。
既存の偽ブローカー（取消成功で照会は null）は、取消の後の照会で Cancelled を返すように直した（確認を前提にするのが本件の規律）。
変異: 取消の後の照会を外す・Program.cs の口を落とす・後始末の状態の確認を外す・後始末の成行の例外を握らない・Completed の後の成行へ取消トークンを渡す・`OperationCanceledException` を握らない——いずれも赤（テスト仕様書 FR-10 の EC1〜EC6）。

## 結果

- 取消の窓で約定した建玉は、取消の確認の時点か、約定追跡の終端化（既定 30 秒周期）で成行手仕舞いへ進む。通知は事実の順に 2 回（未確認 → 結果）。
- 稼働 PoC の損切り方式は S1 のため、本経路は S0 / S3 を選ぶまで通らない。

## 残る制約

- 取消が例外で失敗した枝・送った時点で既に一部約定していた枝は従来どおり（残りを取り消さない／非終端のまま約定数で手仕舞う）。別件として扱う。
- 確認待ちのまま約定追跡が打ち切られた（追跡上限 24 時間を過ぎて非終端）ときは後始末が起きず、行は確認待ちのまま残る（当日限りの注文は通常その前に終わる）。
- 内蔵 paper は約定追跡を配線しない。即時に約定するため確認待ちは生じない。
- 取消の後の照会（非終端）から確認待ちの保存までのごく短い窓で、約定追跡がエントリーの終端化を先に観測すると、後始末は起きない（行は確認待ちのまま残る。
  確認待ちの通知 `EntryCancelUnconfirmed` は出ているので無音ではないが、結果の通知が来ない＝人が確かめる。保存できなかった枝と同じ扱い）。
- 後始末の成行手仕舞いが例外で落ちたとき（決定 5）は自動の再試行をしない。`None` の Critical 通知で人が手仕舞う。
- **確認待ち（`AwaitingEntryCancel`）が残ると、同じ銘柄・同じ方向の別の手法の新規建てを併存の関門（`StopLossMethodCoexistenceGate`。`State != Completed`・
  `Mechanism` が違う行を数える）が見送り続ける**（コードで確認。同じ手法の新規建て・決済は止めない）。自動では閉じない（エントリーが生きていれば約定し得るので、
  見送る側に倒す）。解除は人の手順（運用の Runbook「エントリーの取消が未確認のまま残ったとき」：証券会社で注文と建玉を確かめてから、版を合わせて Completed にする）。
  既存の `AwaitingEntry` の残留と同じ扱いであり、打ち切り時の自動解除は本 IADR の範囲に入れない。
