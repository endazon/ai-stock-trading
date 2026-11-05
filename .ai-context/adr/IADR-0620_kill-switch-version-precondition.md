---
title: IADR-0620 kill switch の発動・解除は「見た状態」の版（最後に変えた時刻の tick）を前提条件に取り、違えば 409 で何も変えない。段 1 は前提条件なしも受け、段 2 は前提条件なしの解除を拒否する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-14, FR-13, ADR-0009, ADR-0003, IADR-0012]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10・FR-14)
---

# IADR-0620: kill switch の前提条件

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-05
- 決定者: Claude Code（実装）／ オーケストレーター（kill switch の解除・変更に見た状態の前提条件を付け、古い状態を見た操作が上書きしないようにする、の指示）

## 起点・関連

- issue: #1534
- 前提: IADR-0012（設定の楽観排他。Version 列）。
- 実装仕様書: [`.ai-context/specs/20261105_1534_kill-switch-precondition.md`](../specs/20261105_1534_kill-switch-precondition.md)

## コンテキストと課題

kill switch の発動・解除は現在の状態を見ずに上書きする。利用者が画面や Discord で「発動中」を見て解除を決める間に、別の操作（別の端末・撤退の自動発動）が入っても、解除はそのまま通る。

## 検討した選択肢

1. **版を「最後に変えた時刻の tick」で表し、比較と書き込みをストアで原子的に行う（採用）** — 列を足さない（移行が要らない）。書くたびに必ず変わる。
2. 版の列を足す — 移行が要る。単一行で書き込みは稀であり、時刻で足りる。採らない。
3. 前提条件を即座に必須にする — 版を送らない呼び手（Discord ボット・既存の画面）の解除が全部止まる。段で切り替える。

## 決定

1. `KillSwitchState.Version`（tick の文字列・未操作は `"0"`）。`GET /kill-switch` は本文と `ETag` で返す。
2. `KillSwitchRequest.ExpectedVersion`（本文）または `If-Match`。渡されたら `IKillSwitchStore.TrySetState(状態, 版)` で比較と書き込みを原子的に行い、違えば `KillSwitchVersionConflictException`（409）。EF は直列化可能な取引（並行の他方は確定で失敗し、競合として扱う）、インメモリは錠。
3. `RiskControls:KillSwitch:Precondition` = `Report`（既定）/ `Require`。`Require` は前提条件の無い解除を `KillSwitchPreconditionRequiredException`（428）で拒否する。発動は求めない。知らない値は起動で止める。

## 結果

- 段 1 の既定では、前提条件を送らない呼び手の挙動は変わらない（記録が増える）。送る呼び手は古い状態での上書きを防げる。

## 残余・フォローアップ

1. Discord ボット（gRPC）と画面（BFF）が版を送るよう改めること。段 2 の前提（オーナーの判断で上げる）。
2. 時刻の tick が同じになる 2 回の書き込みは版で区別できない（実質起きない。書き込みは利用者の操作の頻度）。
