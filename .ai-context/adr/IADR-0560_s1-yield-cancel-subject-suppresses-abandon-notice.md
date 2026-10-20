---
title: IADR-0560 S1 が損切りの前に取り消した判断の手仕舞いは「手仕舞いが約定せず終了」にしない —— 取消の主体を発注の記録の印で運び、残った建玉は推定と書く
type: impl-adr
status: Accepted
related_ids: [FR-09, FR-10, UC-06, ADR-0040, ADR-0050, IADR-0344, IADR-0357, IADR-0466, IADR-0539]
author: endazon (with Claude Code)
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md
---

# IADR-0560: S1 が取り消した判断の手仕舞いを「手仕舞いが約定せず終了」にしない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-20
- 決定者: Claude Code（実装）

## 起点・関連

- 関連する計画書 ID: FR-10（S1）、FR-09（通知）、UC-06。ADR-0040 決定1（S1）、ADR-0050 決定1（S1 の前の取消）
- 関連する実装仕様書: [`.ai-context/specs/20261020_1395_s1-yield-cancel-suppresses-abandon-notice.md`](../specs/20261020_1395_s1-yield-cancel-suppresses-abandon-notice.md)
- 前提: [IADR-0466](IADR-0466_s1-close-cancels-in-flight-decision-close.md)（S1 が判断の手仕舞いを取り消す・記録を書かない）、
  [IADR-0357](IADR-0357_owner-close-market-order-cancel-path-and-expiry-notice.md)（「手仕舞いが約定せず終了」）、
  [IADR-0539](IADR-0539_decision-close-unfilled-cancel-only-and-disabled-as-expired.md)（系の未約定取消・取消の印の先例）、
  [IADR-0344](IADR-0344_s1-software-stop-loss.md)（S1）
- 関連 issue: #1395（本件）

## コンテキストと課題

PoC（2026-10-20、AST rev 50）。SMCI の判断由来の決済（指値）が未約定のまま損切りラインに届き、S1 は設計どおり指値を取り消して
成行で全量（7165 株）を決済した。ところが取消の直後に「手仕舞いが約定せず終了 … 建玉はこの数量ぶん残っています … 手仕舞い直すか、
建玉を確認してください」が出た。建玉は残っていない。通知は手仕舞い直し（二重の売り）を促し得る。

経路: S1 は取消を `broker.CancelOrderAsync` で直に送り、記録も OrderCancelled も書かない（IADR-0466。書き手は約定追跡 1 つ）。
約定追跡が取消を観測して `OrderExecuted(Cancelled)` を出し、リスク管理の `OrderExecutedLedgerHandler` が終端を初めて記録して
`PositionCloseAbandoned` を返す。**どの段も、取り消したのが S1 自身かを知らない。**

## 決定

### 決定 1 — 取消の主体は「発注の記録の印」で運ぶ（取消の理由・別のイベントでは運ばない）

S1 は**取消を送る前に**、発注の記録へ `SoftwareStopCancelRequestedAt`（列の追加だけ・既存行 null）を書く
（`IExecutedOrderStore.MarkSoftwareStopCancelRequested`。#1339 の `MarkUnfilledCancelRequested` と同じ形の 1 列だけの書き込み）。
約定追跡は終端が `Cancelled` で印があるときだけ `OrderExecuted.CancelledBySoftwareStop = true`（末尾の省略可能な欄・既定 false）を載せる。
印は**保存先から読み直す**（`FindSoftwareStopCancelRequestedAt`。EF は追跡しない問い合わせ）——巡回は記録を読んでから 1 件ずつ照会するので、
頭で読んだ写しは S1 の印より古いことがある。

| 候補 | 運び手 | 採らない理由 |
| --- | --- | --- |
| A. 取消の理由（OrderCancelled.Reason） | S1 が OrderCancelled（理由＝S1）を出す | 約定追跡の OrderExecuted(Cancelled) と**別のキュー**で順序が保証されず、終端の記録は先着が確定する（単調）。約定追跡が先に着くと従来どおり誤って出る。S1 の取消が FR-19 の注文アクティビティの母集団に入る副作用もある |
| B. 別のイベント（SoftwareStopExecuted に取り消した注文を載せる） | S1 の ClosePlaced | A と同じ順序の問題（S1 の結果は成行の受理の後に出る） |
| **C. 注文の属性（発注の記録の印）＋ OrderExecuted の欄** | 約定追跡 | **採る**。印は取消より前に保存されるので、約定追跡がいつ観測しても主体が分かる（順序に依存しない）。終端を運ぶ唯一の経路（約定追跡）に載る |

C は列 1 本・契約の欄 1 つを足すが、A・B では決定的にならない。発注の挙動は変えない（印の書き込みが失敗しても取消は送る＝印なし＝従来どおり通知が出る側）。

### 決定 2 — S1 が取り消したときは「何も出さない」

`OrderExecutedLedgerHandler` は `CancelledBySoftwareStop` かつ `Cancelled` の終端では `PositionCloseAbandoned` を返さない
（終端の記録＝在庫の解放は従来どおり。ログ〔Information〕は残す）。置き換えの文面（「損切り（S1）の成行決済に置き換えたため取り消しました」）は出さない——
S1 の決済は別に「ソフトウェア逆指値」（ClosePlaced）と「取引実行」（約定）の通知が出ており、3 本目は情報を足さず、取消の時点では S1 の成行の
受理も約定も未確定である。`OrderCancelledLedgerHandler`（OrderCancelled の経路＝利用者の取消・系の未約定取消）は変えない（S1 はこの経路を使わない）。
`PositionCloseAbandonment.Describe` も変えない（2 経路の共通の判定であり、主体は OrderExecuted の経路にしか来ない）。

### 決定 3 — 「建玉はこの数量ぶん残っています」は推定と書く（照会は足さない）

残った建玉は**終了の時点の記録（承認数量 − 約定）からの推定**であり、建玉の照会では確かめていない。照会を足すと通知の経路に
ブローカーへの依存と失敗の分岐が増えるため、範囲を最小にして文言だけを替える:
「記録上はこの数量の建玉が残っている見込みです（終了の時点の記録による推定で、建玉の照会では確かめていません。ソフトウェア逆指値などの別の決済で
既に減っていることがあります…）」「建玉を確認してから、手仕舞い直すか判断してください」。重大度（Warning）は変えない。

## 結果

- S1 が取り消した判断の手仕舞いは「手仕舞いが約定せず終了」にも監査の「手仕舞いが流れた」にも載らない（S1 の取消は発注執行のログと S1 の ClosePlaced に残る）。
- S1 以外の取消（利用者・系の未約定取消）・失効は従来どおり出る。文面は推定と分かる言い方になる。
- 発注の挙動（取消・成行・据え置き）は変わらない。
- 移行: `executed_orders.SoftwareStopCancelRequestedAt`（timestamptz NULL）の追加だけ。

## 残余リスク

- 印を書いた後に S1 の取消が効かず、別の原因（利用者の取消）で `Cancelled` になった場合も抑止される。S1 は到達済みで取消・決済を続けるため
  建玉は S1 が売る（ADR-0040 決定1・IADR-0344 決定4）。
- 失効（Expired）は印があっても抑止しない（主体は S1 ではない）。その間に S1 が売っていれば決定 3 の文言（推定）で読む。
- 印の書き込み・読み直しに失敗すると従来どおり通知が出る（安全側＝黙らない側）。
