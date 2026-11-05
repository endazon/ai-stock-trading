---
title: kill switch の発動・解除に「見た状態」の版の前提条件を付け、古い状態を見た操作が上書きしないようにする（段で切り替える）
type: spec
status: accepted
related_ids: [FR-10, FR-14, FR-13, ADR-0009, ADR-0003, IADR-0620, IADR-0012]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 kill switch・FR-14 Discord からの kill switch 操作)
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md
---

# 仕様書: kill switch の前提条件（#1534）

## 起点

- #1534。kill switch の発動・解除の要求は理由だけを持ち、操作した人が見た状態を前提にしない。後から別の操作が入っても、古い状態を見た操作がそのまま上書きする。

## 実測（develop `26d0a18a7`）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | 要求は `KillSwitchRequest(string Reason)` だけ | `KillSwitchRequest.cs` |
| 2 | `KillSwitchService.Engage/Disengage` は現在の状態を見ずに `store.SetState` で上書きする | `KillSwitchService.cs` |
| 3 | 永続（`EfKillSwitchStore`）は単一行。版の列は無い（`ChangedAt` は書くたびに変わる） | `EfKillSwitchStore.cs`・`KillSwitchRow` |
| 4 | 呼び手: REST（利用者・BFF 経由）、gRPC（Discord ボット・`RiskControlsOwnerWrite`）、内部（撤退の自動発動 `StageGateService`） | `git grep "EngageKillSwitch\|DisengageKillSwitch\|killSwitch.Engage"` |

## 決定（IADR-0620）

1. 版 `KillSwitchState.Version` = 最後に変えた時刻の tick（未操作は `"0"`）。照会 `GET /kill-switch` は本文の `version` と `ETag` で返す。列を足さない（移行が要らない）。
2. 要求は `expectedVersion`（本文）か `If-Match`（ヘッダー）で前提条件を渡せる。渡されたら、ストアが比較と書き込みを原子的に行う（`TrySetState`。EF は直列化可能な取引・インメモリは錠）。違えば 409、状態は変えない。
3. 段（構成 `RiskControls:KillSwitch:Precondition`）: `Report`（既定）は前提条件の無い要求も従前どおり受け、記録する。`Require` は前提条件の無い**解除**を 428 で拒否する。発動は安全側の操作のため段 2 でも求めない（撤退の自動発動も同じ口）。
4. gRPC の口（Discord ボット）は版のフィールドを持たないため、段 1 では前提条件なしで従前どおり。段 2 へ上げる前に、ボットと画面が版を送るよう改める必要がある（残余）。

## 母集合（規則 9）

- 書く口: `KillSwitchService.Engage/Disengage` の呼び手 3 つ（REST 2 本・gRPC 2 本は同じ `Handle` を通る・撤退の自動発動）。
- ストアの実装: `EfKillSwitchStore`・`InMemoryKillSwitchStore`（試験の偽物は既定の実装〔比較して書く〕を継ぐ）。
- 例外の写し: `RiskControlEndpoints.MapException`（REST と gRPC で共用）。

## 受け入れ基準

- [x] 古い版を前提にした解除・発動は 409 になり、状態は変わらない
- [x] 正しい版なら成功する
- [x] 段 1 では前提条件の無い要求も通る
- [x] 段 2 では前提条件の無い解除は拒否される（発動は通る）
- [x] 同じ版での並行の書き込みは 1 つだけ通る

## 範囲外（残余）

- Discord ボット（gRPC）・画面（BFF）が版を送るようにする変更。段 2 の前提。
- 一時停止（pause / resume）にも同じ形が当てはまる。必要なら同じ部品で広げる。
