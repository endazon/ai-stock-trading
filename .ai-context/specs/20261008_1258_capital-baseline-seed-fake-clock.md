---
title: 試験の基準資金の仕込みを偽の時計から数え、固定日の結合試験が実時刻の経過で赤になるのを止める
type: spec
status: accepted
related_ids: [FR-10, IADR-0354]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs: []
---

# 仕様書: 試験の基準資金の仕込みを偽の時計から数える（#1258）

## 起点となる計画書（トレーサビリティ）

- 起点: FR-10（試験の基盤。本番コードは変えない）
- 関連: IADR-0354（基準資金の鮮度・当日の行を見ない）・#905（さかのぼり日数を 2 とした経緯）・#1176（T-10-2320）
- 起票: #1258（AST#1257 の CI で `backend-test (1)` が赤。develop b12ea688 でも 3 回とも再現）

## 目的・背景

`RiskWorkerWebApplicationFactory.CreateHost` は基準資金の行を**実時刻** `UtcNow − 2 日` の ET 暦日で仕込む。
`DecisionExitReentryWiringTests`（T-10-2320）は `FakeClock` を 2026-10-06 17:39 UTC に固定して審査する。
`EfCapitalBaselineStore.GetCurrent` は偽の時計の当日（ET 10-06）より前の行しか使わないため、実時刻が
ET 10-08 に入った時点（2026-10-08 04:00 UTC）で仕込みの行（ET 10-06）が当日扱いで外れ、
新規建てが `CapitalBaselineUnavailable` で止まり、期待する理由が 1 つ多くなって赤になる。

## 母集合（規則 9・10）

- `git grep -n "new RiskWorkerWebApplicationFactory" backend` × `FakeClock(` を持つファイル:
  `DecisionExitReentryWiringTests.cs`・`StopOutReentryWiringTests.cs` の 2 本（固定日の偽の時計 × 実時刻の仕込み）。
  後者は固定日 2026-09-23 で、現時点は審査の期待が基準資金に依らず緑だが、同じ食い違いを持つため揃える。
- 偽の時計を使わない試験は実時刻どうしで整合しており、既定の挙動を変えない。

## 変更

- `RiskWorkerWebApplicationFactory` に `CapitalBaselineSeedNow`（既定 `null` ＝実時刻）を足し、仕込みの起点に使う。
- 上の 2 本は偽の時計と同じ時刻を渡す。

## 受け入れ基準と検証

1. 固定日の結合試験は実時刻に依らず緑（`DecisionExitReentryWiringTests`・`StopOutReentryWiringTests` 3 件緑。是正前は T-10-2320 が赤）。
2. 偽の時計を使わない試験の挙動は不変（RiskManagementService.Tests 2275 件緑）。
3. `dotnet format --verify-no-changes` 差分なし。
