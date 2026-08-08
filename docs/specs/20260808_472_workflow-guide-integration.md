---
title: 計画リポの実装作業運用ガイドを CLAUDE.md / AGENTS.md へ組み込み、計画 pin を前進させる
type: spec
status: approved
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-08
updated: 2026-08-08
---

# 仕様書: 実装作業運用ガイドの組み込みと計画 pin の前進

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#472](https://github.com/endazon/ai-stock-trading/issues/472)
- 起点 ID: **NFR**（運用保守）
- 計画リポの正本: `planning/docs/ai-implementation-workflow-guide.md`（project-planning PR #294 で 2026-08-08 に確定・fixed）
- 先例: [#459](https://github.com/endazon/ai-stock-trading/issues/459)（計画 pin の前進 / [作業仕様書 20260808_459](20260808_459_planning-pin-advance.md)）

## 背景

計画リポで実装作業の運用標準（フェーズ分割・並列実装・監査・裁定の流し方・メタ作業の統制）が確定した。実装セッションは自リポの CLAUDE.md / AGENTS.md しか読まないため、組み込まない限りガイドは効かない。

## やること

1. planning submodule の pin を `d9c2014` → **`356e8c7`**（ガイドを新設した project-planning main）へ進める
2. CLAUDE.md に「実装作業の進め方（計画リポの運用ガイド）」節を **15 行以内**で追加する（正本への参照＋拘束点の要約）
3. AGENTS.md に同内容の **3〜5 行**要約を追加する

## pin の前進

| | 値 |
| --- | --- |
| 変更前 | `d9c2014` |
| 変更後 | **`356e8c7`** |

差分は 1 コミット（project-planning #294）のみであり、`projects/ai-stock-trading/` 配下（計画書本体）に変更は無い。追加は `docs/ai-implementation-workflow-guide.md`（新規・fixed）と `draft/cross-project/` の分析記録である。したがって **`PlanRiskDefaults` / `PlanSourceDigests` / `KnownPlanDeviations` の再照合は不要**（出典文書が 1 バイトも動いていない。#459 検査2 と同じ論法）。

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | pin が `356e8c7` 以降になっている | `git submodule status` |
| 2 | CLAUDE.md の新節が 15 行以内で、参照と拘束点の要約を含む | 目視 |
| 3 | AGENTS.md の要約が 3〜5 行である | 目視 |
| 4 | 毎セッション必読の総量が 50KB 予算を超えない | CLAUDE.md + AGENTS.md のサイズ確認 |
| 5 | doc-links / commit-messages の CI が緑 | CI 結果 |

## やらないこと

- ガイド本文の転記（正本は計画リポ側。要約と参照のみ組み込む）
- 計画書本体（`projects/`）への追随作業（本 pin 前進に計画書の変更は含まれない）
