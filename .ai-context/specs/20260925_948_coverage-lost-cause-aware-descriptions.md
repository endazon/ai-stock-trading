---
title: 保護喪失の対処の説明を原因（Cause）ごとに書き分け、無い巡回・撃ち直しを約束させない（#948）
type: spec
status: accepted
related_ids: [FR-10, FR-11, UC-02, UC-06, IADR-0117, IADR-0210, IADR-0369]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値が未受理・失効した場合は建玉を持たない」)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (業務フロー 02「逆指値が成立しない場合の扱い」)
---

# 仕様書: 保護喪失の対処の説明を原因ごとに書き分ける（#948）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（逆指値なしの建玉を持たない）、FR-11（監査・通知）
- ユースケース（UC）: UC-02（損切り）、UC-06（手仕舞い）
- 画面（SC）: なし
- 関連 ADR: なし（計画 ADR の決定は変えない）
- 関連 IADR: IADR-0369（決定 1・3。PR #916 監査 F1・#941 で通知文面を `Cause` で分けた前例）、IADR-0210 決定 3・4、
  IADR-0117 改定 7・9。本作業の記録は IADR-0369 への日付つき追記（新しい IADR は作らない）
- 計画書リンク: 上記 plan_refs

## 目的・背景

偽りの約束の同型の 3 件目（#857 の F1 → #941 → 本件）。通知の文面（PR #916・#944 で是正済み）ではなく
**説明文**の側が、原因（`Cause`）を見ずに巡回前提の約束を書いている。

1. `docs/functional/FR-10_risk-controls.md` の `Remediation=None` の行: 「常駐ガードは次の巡回で撃ち直す」。
2. `ProtectiveStopCoverageLost.cs` の `ProtectiveStopRemediation.CloseRejected` の xmldoc: 「記録は `Active` のまま残り、次の巡回が改めて評価する」。
3. 同じ表に確認できた拒否（`CloseRejected`）の行が無い。

コードで確かめた事実（挙動は変えない）:

| 原因 | 対処 | 発生箇所 | その後 |
| --- | --- | --- | --- |
| `RejectedAtEntry` | `None` | `OrderExecutionAppService.ResolveUnprotectedEntryAsync`（エントリー取消が失敗し、照会もできないか未約定のまま）／`CloseUnprotectedPositionAsync`（成行手仕舞いが `BrokerUnavailableException`＝確実に未発注） | **保護記録を作らない**（`protectiveStops.Save` は受理側だけ）。巡回も撃ち直しも無い。エントリーの `ExecutionRecord` は保護レグより前に保存されるため、同じ承認の再配送は相 1 で返り、保護喪失を出し直さない（#941 の T-10-751 と同じ根拠）。**通知は 1 回きり** |
| `RejectedAtEntry` | `CloseRejected` | `CloseUnprotectedPositionAsync`（成行が終端 `Rejected`/`Cancelled`/`Expired` で返った） | 同上。記録が無いので「`Active` のまま」も「次の巡回」も無い |
| `LapsedInFlight` | `None` | `ProtectiveStopGuard`（成行が確実に未発注、または発注先に成行の能力が無い） | 記録は `Active` のまま。巡回のたびに再評価し（成行の能力があれば同じ `DecisionId` を再予約して撃ち直す）、失敗が続くあいだ**巡回のたびに** `None` を発行する（間引きなし） |
| `LapsedInFlight` | `CloseRejected` | `ProtectiveStopGuard.RejectedClose` / `HoldRejectedClose` | 記録は `Active` のまま、試行番号を進めて次の巡回で撃ち直す。**3 回で打ち切り**、以後は成行を送らず約 1 時間ごと（と再起動後）に通知 |

あわせて、走査の過程で**利用者に見える文字列の誤り**を 1 件見つけた: `None` の通知の**件名**が既定の腕に落ちて
「リスク統制: 保護逆指値が成立せず建玉を解消」になっている（本文は「解消にも失敗しました」）。件名だけ読むと
**解消済みと読める**。依頼の許可範囲（利用者に見える文字列の誤りは同じ PR で直してよい）で是正する。

## 対象範囲

- 対象:
  - `docs/functional/FR-10_risk-controls.md` の保護喪失の表: `None` の行を原因ごとに書き分け、`CloseRejected` の行を足す
  - `ProtectiveStopCoverageLost.cs`: 冒頭コメントの `None` の説明、`None` と `CloseRejected` の xmldoc を原因ごとに書き分ける
  - `ProtectiveStopGuard.Outcome.CloseFailed` の xmldoc（「発注先に成行の能力が無い」場合にも「撃ち直す」と書いている）
  - `NotificationFormatter` の `None` の件名（上記の誤り）と、その注記。ゴールデンを追随させ、否定形のテストを足す
  - `docs/tests/FR-10_risk-controls-tests.md` にテスト行を足す
  - IADR-0369 への日付つき追記と索引行の追記
- 対象外:
  - `None` の通知**本文**: 約束を述べていない（「直ちに確認してください」）ため偽ではない。原因ごとの後半（巡回の有無）を足すのは
    文面の拡張であり本件の射程（偽りの約束の除去）を超える。残余として報告する
  - 滞留側 `None` が巡回ごとに間引きなしで通知される挙動（挙動の変更は射程外）
  - コードの挙動の変更一切

### 是正の母集合（規則 9〜11。自分で引いた結果と除外理由）

誤りの側の文字列で走査した（issue の本文の挙げた 2 箇所を母集合にしない）。コマンド:

```
git grep -n -E "撃ち直|Active のまま|次の巡回|次回巡回|巡回で|繰り返し|繰り返します|巡回を続け|巡回対象" \
  -- ':!.ai-context' ':!CHANGELOG.md' | grep -v "Tests/" \
  | grep -i -E "保護|逆指値|手仕舞|CoverageLost|Remediation|Cause|CloseRejected|拒否|None"
git grep -n -E "Remediation=None|Remediation\.None|CloseRejected|解消にも失敗|解消も失敗" -- ':!.ai-context' ':!CHANGELOG.md'
git grep -n -E "成立せず|保護喪失|建玉解消" -- docs frontend deploy README.md
```

保護喪失（`ProtectiveStopCoverageLost`）の説明に当たる行だけを判定した（情報収集・費用統制などの「次回巡回」は別機能で除外）。

| 箇所 | 判定 |
| --- | --- |
| `docs/functional/FR-10_risk-controls.md` の `Remediation=None` の行（「常駐ガードは次の巡回で撃ち直す」） | **是正**（issue の 1）。エントリー時の `None` は撃ち直しも再通知も無い。発生条件も「接続確立の失敗」だけでなく「取消も照会もできない」を含む |
| 同表に `CloseRejected` の行が無い | **是正**（issue の 3）。原因ごとに書き分けて足す |
| `ProtectiveStopCoverageLost.cs` の `CloseRejected` の xmldoc（「記録は `Active` のまま残り、次の巡回が改めて評価する」） | **是正**（issue の 2） |
| 同 xmldoc の「混同すると…保護記録が完了して逆指値なしの建玉が巡回対象から外れる」 | **是正**（規則 10 で引き直した自分の近傍）。滞留側に限った帰結である。エントリー時は記録が無い |
| `ProtectiveStopCoverageLost.cs` 冒頭と `None` の xmldoc | **是正**（約束は無いが、原因ごとの「その後」を書き分けないと同じ誤読を招く。同じ enum の隣の値だけ書き分けると非対称になる） |
| `NotificationFormatter.cs` の `None` の件名（既定の腕「建玉を解消」） | **是正**（走査で見つけた利用者に見える誤り。ゴールデン `ProtectiveStopCoverageLost/解消も失敗` を追随） |
| `NotificationFormatter.cs` の `CloseRejected`・`CloseDispatchIndeterminate` の本文 | 除外: PR #916・#944 で `Cause` で分かれている |
| `NotificationFormatter.cs` の `None` の本文 | 除外: 約束を述べていない（上の対象外） |
| `ProtectiveStopGuard.cs` の注記（411・447・455・476・494 行付近・`RejectedClose` のログ文「次の巡回で撃ち直します」） | 除外: すべてガード（`LapsedInFlight`）の内部で、事実どおり |
| `ProtectiveStopGuard.Outcome.CloseFailed` の xmldoc（「または発注先に成行の能力が無い…次の巡回で撃ち直す」） | **是正**: 成行の能力が無ければ撃てない。「次の巡回で改めて評価する（成行を送れる発注先なら撃ち直す）」へ |
| `CloseRejectionTracker.cs` 冒頭（「ガードは次の巡回で撃ち直してよい」）・`ProtectiveStopGuard.MaxConfirmedCloseRejections` | 除外: トラッカーはガード専用で、滞留側の事実どおり |
| `ProtectiveStopGuardService.cs`（発行失敗時の補償の注記） | 除外: ガード専用 |
| `AuditEntryFactory.cs` の保護喪失の要約（None / CloseRejected） | 除外: 約束を述べていない（「要人手対応」だけ） |
| `NotificationHandlers.cs` の注記（「建玉を解消した（Critical）」） | 除外: ハンドラ全体の概括で、次行で None を「解消も失敗」と書き分けている。利用者に見えない |
| `docs/data/audit-events.md`（「複数回残り得る」） | 除外: 可能性の記述で偽ではない（#941 と同じ判定） |
| `docs/operations/broker-execution-paths-runbook.md` の 169・171 行（「保護逆指値ガードは次の巡回で…」） | 除外: 主語が保護逆指値ガードで、滞留側に限った事実 |
| `docs/tests/FR-10_risk-controls-tests.md` の T-10-634〜638・684・685・750〜757 | 除外: ガードの試験、またはエントリー側の否定形の記述で事実どおり |
| S1（`SoftwareStopExecuted` の `CloseRejected`・`AuditEntryFactory.cs:541`・`NotificationFormatter.cs:249`・FR-10 の S1 の表） | 除外: 別イベント（原因の概念を持たない）。#950 で「待ち時間を置いて撃ち直しを続ける」へ改まっており事実どおり |
| `.ai-context/specs/` の過去の仕様書・IADR-0369 本文 | 除外（凍結記録）。IADR-0369 へは日付つき追記で本件を記録する |

規則 10（自分の新しい記述が誤りにならないか）: 新たに書く「エントリー時は 1 回きり」は #941 の T-10-751 がコードで固定した根拠
（保護記録を作らない・再配送は相 1 で返る）に依る。`None` の経路でも相 1 の前提（エントリーの記録が保護レグより前に保存される）は
同じである。「巡回のたびに `None` を発行する」は `ProtectiveStopGuard` の `events.Add(... None ...)` が間引きを持たないことで確かめた。

規則 11（窓）: 本件は時間差を扱う是正ではない（説明文の書き分け）ため適用外。

## 設計

- FR-10 の表の `None` の行: 場面欄に 2 つの発生条件（取消も照会もできない／成行が確実に未発注。滞留側では発注先に成行の能力が無い場合も）を書き、
  動作欄を「エントリー直後: 保護記録が無く巡回の対象に入らないため、撃ち直しも再通知も無い（通知は 1 回きり）」と
  「滞留中に失効した側: 記録は `Active` のまま、巡回のたびに撃ち直し、失敗が続くあいだ巡回のたびに通知する」に分ける。
- `CloseRejected` の行を足す: 建玉が残っていること・手仕舞いレグを運ばない（台帳は押さえない）こと・原因ごとのその後
  （エントリー直後は 1 回きり／滞留側は 3 回で打ち切り、記録は閉じず約 1 時間ごとに通知）。
- `None` の件名: 「リスク統制: 保護逆指値が成立せず、建玉の解消にも失敗」。

## 受け入れ基準

- [x] FR-10 の表で、`None` の行がエントリー直後に撃ち直し・再通知を約束しない（原因ごとに書き分け）
- [x] FR-10 の表に `CloseRejected` の行があり、原因ごとに書き分けている
- [x] `CloseRejected` / `None` の xmldoc が原因ごとに書き分けられ、エントリー時に「`Active` のまま」「次の巡回」を約束しない
- [x] `None` の通知の件名が「建玉を解消」と読ませない（両原因。否定形のテスト T-10-854）。ゴールデンが追随する
- [x] コードの挙動は変わらない（変えたのは件名の文字列と説明文だけ）

## テスト方針

| ID | 対象 |
| --- | --- |
| T-10-854 | `Remediation=None` の通知（`RejectedAtEntry` / `LapsedInFlight`）の件名が「建玉を解消」を含まず「解消にも失敗」を含む（否定形）。他の対処の件名は変わらない（`PositionClosed` は「建玉を解消」のまま＝対の表明） |

テスト ID は依頼で割り当てられた T-10-854〜T-10-859 から 1 つだけ採る（`git grep "T-10-85[4-9]"` を origin/* 全ブランチで実行し、
#842 の仕様書が範囲の予約として書いた 1 行以外に使用が無いことを確認）。

## 計画書との差異

- 差異: なし（説明文と件名を実装の事実に合わせる是正）。

## 未決事項

- なし。
