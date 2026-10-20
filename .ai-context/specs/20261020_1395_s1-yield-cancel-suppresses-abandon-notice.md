---
title: S1 が損切りの前に自分で取り消した判断の手仕舞いには「手仕舞いが約定せず終了」を出さない（#1395）
type: spec
status: done
related_ids: [FR-09, FR-10, UC-06, ADR-0040, ADR-0050, IADR-0344, IADR-0357, IADR-0466, IADR-0539, IADR-0560]
author: claude (Claude Code)
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-09・FR-10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md
---

# 仕様書: S1 が自分で取り消した判断の手仕舞いを「手仕舞いが約定せず終了」にしない（#1395）

> 本仕様書は実装着手前に作成した（着手 2026-10-20）。基点は AST `origin/develop` `b5c2d568`。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（損切り・ソフトウェア逆指値 S1）、**FR-09**（通知）。UC-06（手仕舞い）
- 関連 ADR: ADR-0040 決定1（S1）、ADR-0050 決定1（S1 の前の取消）
- 関連 IADR: IADR-0466（S1 が判断の手仕舞いを取り消す）、IADR-0357（「手仕舞いが約定せず終了」）、IADR-0539（#1339。系による未約定の取消）。新設 **IADR-0560**
- 起票: #1395（`Closes #1395`）

## 目的・背景

PoC（2026-10-20 02:30 JST、AST rev 50）。SMCI の判断由来の決済（指値 32.56）が未約定のまま損切りライン 32.25 に届き、
S1 は設計どおり指値を取り消して成行で全量（7165 @ 32.215）を決済した。ところが取消の直後に
「手仕舞いが約定せず終了 … 建玉はこの数量ぶん残っています … 手仕舞い直すか、建玉を確認してください」が出た。
建玉は残っていない。利用者に手仕舞い直し（二重の売り）を促し得る。

## 原因（経路）

1. S1（`SoftwareStopExecutor.YieldDecisionClosesAsync`）は判断の手仕舞いを `broker.CancelOrderAsync` で直に取り消す。
   **記録は書かず、OrderCancelled も出さない**（終端の記録は約定追跡に任せる＝書き手を 1 つに保つ。IADR-0466）。
2. 約定追跡（`OrderFillPoller.ApplyAsync`）が取消を観測して `OrderExecuted(Status=Cancelled)` を出す。
3. リスク管理の `OrderExecutedLedgerHandler` が終端を初めて記録し、`PositionCloseAbandonment.Describe` が
   Close・未約定残ありなので `PositionCloseAbandoned` を返す。通知が上の文面を出す。
4. どの段も**取り消したのが S1 自身か**を知らない。

## 方針

1. **主体の運び方**: 発注執行の記録（`ExecutionRecord`）に「S1 が損切りの前に取消を求めた時刻」の列
   （`SoftwareStopCancelRequestedAt`。列の追加だけ・既存行 null）を足し、**S1 は取消を送る前に**書く
   （`IExecutedOrderStore.MarkSoftwareStopCancelRequested`。#1339 の `MarkUnfilledCancelRequested` と同じ形）。
   約定追跡は終端が `Cancelled` で印があるとき `OrderExecuted.CancelledBySoftwareStop = true`（末尾の省略可能な欄・既定 false）を載せる。
   取消の理由（OrderCancelled.Reason）・別のイベントで運ぶ案は、約定追跡の OrderExecuted と順序が保証されず（別キュー）、
   先に着いた側が終端を確定するため取り違える（IADR-0560 に比較を残す）。
2. **判定**: `OrderExecutedLedgerHandler` は `CancelledBySoftwareStop` のとき `PositionCloseAbandoned` を返さない（終端の記録＝在庫の解放は従来どおり）。
   置き換えの文面も出さない（**出さない**を選ぶ。S1 の決済の約定通知〔取引実行〕と「ソフトウェア逆指値」の通知が別に出るため）。ログ（Information）は残す。
3. **文面**: 「建玉はこの数量ぶん残っています」は取消・失効の時点の記録からの推定であり建玉照会で確かめていないので、
   推定と分かる文言へ替える（照会は足さない＝範囲を最小にする）。
4. **発注の挙動は変えない**: 取消・成行・据え置きの判断は不変。印の書き込みに失敗しても取消は送る（印が無い＝従来どおり通知が出る側）。

## 母集合（規則 9）

走査語: `手仕舞いが約定せず終了`・`PositionCloseAbandoned`・`判断の手仕舞いを取り消しました`・`建玉はこの数量ぶん残っています`（`grep -rln` で全ファイル。`.ai-context/specs/` は凍結のため除く）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `NotificationFormatter.From(PositionCloseAbandoned)` | 文面を推定の言い方へ（方針 3） |
| 2 | `NotificationTemplateGoldenTests`（PositionCloseAbandoned） | 黄金を追随 |
| 3 | `PositionCloseAbandonment.Describe` | 変えない（取消経路 2 本の共通判定。S1 の印は OrderExecuted にしか来ない） |
| 4 | `OrderExecutedLedgerHandler` | 印で抑止（方針 2） |
| 5 | `OrderCancelledLedgerHandler` | 変えない（S1 は OrderCancelled を出さない。出すのは利用者の取消・系の未約定取消＝従来どおり通知） |
| 6 | `SoftwareStopExecutor.YieldDecisionClosesAsync` | 取消の前に印を書く |
| 7 | `OrderFillPoller.ApplyAsync` | 印を OrderExecuted へ載せる |
| 8 | `PositionCloseAbandoned` 契約・`EventMessageTypeNameTests`・`event-schemas.baseline.json`（PositionCloseAbandoned） | 変えない |
| 9 | `event-schemas.baseline.json`（OrderExecuted） | 欄の追加を基準へ追随 |
| 10 | 監査（`AuditEntryFactory`・`AuditEventHandlers`・`AuditCycleCompletenessTests`） | 変えない（抑止した取消は PositionCloseAbandoned が無いので監査にも載らない。S1 の取消はログと S1 の ClosePlaced の監査で残る） |
| 11 | `OrderCancellationConfirmationTests` | 変えない（OrderCancelled 経路） |
| 12 | `docs/operations/operations.md` §「手仕舞いが約定せず終了」の行 | 「出どころを問わない」に S1 の例外を書き足す |
| 13 | `docs/functional/FR-10_risk-controls.md` | 表示の規則のみ。文面・判定に触れないので変えない |
| 13a | `docs/tests/FR-10_risk-controls-tests.md` | 必須のテスト仕様書（FR-10）。本件の節（T-10-2912〜T-10-2918・変異 SY1〜SY6・残余）を足し、trace ブロックへ本件の ID を足す |
| 14 | `.ai-context/adr/IADR-0357`・`IADR-0539`・README | 凍結記録は書き換えない。README へ IADR-0560 の行を足す |

除外: `.ai-context/specs/`（凍結）。

## 受け入れ基準と試験

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | S1 が取り消した判断の手仕舞いは「手仕舞いが約定せず終了」にならない | リスク管理: `CancelledBySoftwareStop=true` の OrderExecuted(Cancelled) で PositionCloseAbandoned が 0 件・終端は記録される |
| 2 | S1 以外の取消（利用者・系の未約定取消・失効）では従来どおり出る | リスク管理: 印なしの Cancelled/Expired・OrderCancelled 経路で 1 件（既存試験＋追加） |
| 3 | S1 は取消を送る前に印を書く・発注の挙動は変えない | 発注執行: S1 の取消後、記録に印・送った成行は従来と同じ |
| 4 | 約定追跡は印つきの取消だけに欄を立てる | 発注執行: 印あり Cancelled → true、印なし → false、印ありでも Filled → false |
| 5 | 文面は推定と分かる | 通知の黄金 |
| 7 | S1 の取消が例外で失敗し注文が生きていれば印を戻し、後の利用者の取消は通知される側（PR レビュー 🟡 で追加） | 発注執行: T-10-2918（印が null・後の取消の OrderExecuted は false。例外でも取消が効いていれば true） |
| 6 | 変異: 主体の判定を外すと赤 | ハンドラの判定を外して試験 1 が赤、約定追跡の欄を外して試験 4 が赤（手元で確認） |

## 残余

- 取消が例外で失敗したら印を戻す（受け入れ基準 7）。取消が受け付けられたが板でまだ効いていない間の利用者の取消は抑止される。S1 は到達済みで取消・決済を続けるため建玉は S1 が売る（IADR-0560 残余）。
- 失効（Expired）は印があっても抑止しない（主体は S1 ではない）。
