---
title: 費用統制ゲートのタイムアウト試験を「応答しない上流」で固定する（#901 のフレーク）
type: spec
status: accepted
related_ids: [FR-01, IADR-0031, IADR-0051, IADR-0364, IADR-0367]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01 情報収集)
---

# 仕様書: 費用統制ゲートのタイムアウト試験を「応答しない上流」で固定する（#901）

## 起点

- **[#901](https://github.com/endazon/ai-stock-trading/issues/901)**（test）。`dotnet test backend/backend.slnx`
  （全ソリューション実行）で `InformationCollectionService.Tests` の
  `HttpCostControlGateTests.タイムアウト_応答遅延_は_Normal_停止せず` が稀に落ちる（起票者の実測 6 回中 1 回）。

```
Expected (gate.GetAsync()) to be … CostControlGate { Halted = False, IntervalMultiplier = 1M },
but found … CostControlGate { Halted = False, IntervalMultiplier = 0M }.
```

- 起点 ID: **FR-01**（情報収集）。当該テストが固定しているのは
  [IADR-0031](../adr/IADR-0031_cost-poller-wiring.md)（費用統制の未取得・タイムアウトは Normal＝停止せずへ倒す）。
- 先行事例: [#885](https://github.com/endazon/ai-stock-trading/issues/885) / PR #896 /
  [IADR-0364](../adr/IADR-0364_grpc-deadline-test-excludes-connect-from-budget.md)。
- 起点 ID に `NFR-xx` は採らない（「テストの安定性」に当たる番号が計画に無い。IADR-0364 と同じ扱い）。

## 対象範囲

- 対象: `backend/Services/InformationCollectionService/Tests/Infrastructure/ExternalServices/HttpCostControlGateTests.cs`
  の当該テストと、そこでしか使っていないフェイクハンドラ。
- 対象外: **製品コードは変更しない**。#900（別 PR）、同型の棚卸しと検査器の検討（別途）。
  **`{}` を 200 で受けたときの写像（`IntervalMultiplier = 0`）の是非は本 PR で決めない**（下の「未決事項」）。

## 🔴 機序（起票時は未特定。実測で特定した）

### ① 失敗の値 `0M` の出どころ

テストの `DelayingHandler` は 2 秒後に **`200 OK` ＋ 本文 `{}`** を返す。これを製品の写像へ通すと
`CostStateDto` の両プロパティが既定値になり、**`Halted=False` / `IntervalMultiplier=0`** になる
（計装で実測: `MAPPING 200 + body '{}' -> Halted=False IntervalMultiplier=0`）。
**これは issue が報告した失敗値と一致する** —— つまり **50 ms の打ち切りより先に 2 秒の遅延が完了し、
ハンドラの応答が採用されていた**。

### ② なぜ 50 ms が 2 秒に負けるのか

製品の `HttpCostControlGate` をそのまま呼ぶ計装テストで、①ハンドラ入場 ②要求トークンのキャンセル通知
③`Task.Delay(2s)` の完了 の時刻を記録し、**プールのワーカーを全部塞いだ**（`Thread.Sleep` の作業項目
1024 本 × 2.5 秒。スピンループ 16 本を併走。**塞いでから呼ぶ**）。

```
MAPPING 200 + body '{}' -> Halted=False IntervalMultiplier=0
  0 normal     result=(Halted=False,x1) cancelCb= 2511.5 delayDone=   -1.0
  4 NOT-NORMAL result=(Halted=False,x0) cancelCb=   -1.0 delayDone= 2502.5
  6 NOT-NORMAL result=(Halted=False,x0) cancelCb=   -1.0 delayDone= 2503.7
  7 normal     result=(Halted=False,x1) cancelCb= 2506.3 delayDone=   -1.0
  9 NOT-NORMAL result=(Halted=False,x0) cancelCb=   -1.0 delayDone= 2507.9
SUMMARY not_normal=3/15 blockers=1024 holdMs=2500
```

🔴 **タイマーのコールバックはスレッドプールが配送する。** プールが塞がっている間は
**50 ms の打ち切りも 2 秒の遅延も配送されない**。空いた瞬間には**両方とも期限切れ**で、
**どちらが先に走るかは保証されない** —— 配送が遅れた 6 本のうち **3 本で遅延が先に走り、
結果は `x0`（＝ issue の失敗）**になった。#900 と**同じ機序**である。

### ③ 実テストそのものの再現（是正前）

一時ハーネス（`ZzStarver`。実テストのメソッドを直接呼ぶ）で:

| 条件 | 結果 |
| --- | --- |
| 冷えたプロセス 1 回 ×30（枯渇を先に起こしてから呼ぶ） | **1/30 失敗**（`IntervalMultiplier = 0M`。issue と同一） |
| 冷えたプロセス 1 回 ×60（同上） | **1/60 失敗** |
| 暖まったプロセス内 20 回（呼び出しの 2 ms 後に枯渇波・blockers 4096） | **1/20 失敗** |

**是正前は合計 3/110。** 再現率は低い（issue の 6 回中 1 回は全ソリューション実行という別条件）。

## 設計

**遅延（壁時計）をやめ、「打ち切られるまで決して応答しない上流」にする。**

- `NeverRespondingHandler`: `Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)` を待つだけで、
  **応答が勝つ経路が存在しない**。終わり方は打ち切りだけ。
- 固定したい性質は変えない —— 「上流が上限より遅くても、情報収集は止めず Normal（1×）へ倒す」。
  上流が**無限に遅い**のは「2 秒遅い」の極限であり、命題は弱まらない。
- 加えて、**応答ではなく打ち切りで終わったこと**をハンドラ側の観測（`Cancellation` タスク）で確定させる
  （IADR-0364 決定 2 と同じ「同期点で観測を確定させる」作法）。
- `Guard`（30 秒）は固まらないための安全網であり、合否の基準ではない。

## 受け入れ基準

- [x] 「遅い上流でも `Normal`（`Halted=False` / `1×`）」の assert がそのまま残っている（緩めていない）。
- [x] 合否を決める事象に**壁時計どうしの競争が無い**。
- [x] 応答ではなく打ち切りで終わったことを観測している。
- [x] 是正後、是正前と同じ条件（暖 20 回・冷 60 回）で 0 失敗。
- [x] 変異注入で弱まっていないことを示す。

## テスト方針

テストそのものが対象。製品コードを変えないので、**弱めていないことは変異注入で示す**。

## 計画書との差異

- 差異: なし。

## 未決事項

- 🔴 **`200 OK` ＋ 本文 `{}`（または `intervalMultiplier` 欠落）を `IntervalMultiplier = 0` と写す製品の挙動**は、
  本 PR の射程外だが**記録しておく**。`0×` は「間隔を 0 倍する」であり、安全既定（`1×`）でも停止（`Halted`）でもない。
  費用統制サービスが仕様どおり全項目を返す前提なら現実には起きないが、**欠落時の既定を決めていない**。
  → 別 issue で扱う（本 PR では触らない）。
- **同型の棚卸しと検査器**は #885 / #900 / #901 をまとめて別途。
