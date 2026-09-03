---
title: east-west gRPC 移行順序の計画側裁定（MSP/ADR-0075）への文書追随（#584）
type: spec
status: done
issue: "#584"
related_ids:
  - FR-17
  - MSP:ADR-0029
  - MSP:ADR-0075
  - IADR-0284
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 作業仕様書: east-west gRPC 移行順序の計画側裁定（MSP/ADR-0075）への文書追随（#584）

> 本作業はコードを変更しない（文書追随のみ）。実装は裁定が定める先行条件
> （`MSP/ADR-0029` フォローアップの履行・期限 2026-11-30）が満たされてから着手する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-17（設定管理＝全体前提条件の照会元。#584 名指しの対象を含む射程）
- 関連 ADR: `MSP/ADR-0075`（east-west gRPC への移行は基盤先行とする。2026-09-03 確定）、
  `MSP/ADR-0029`（同期通信の使い分け基準。本裁定が部分改定した実施方針の元 ADR）
- 計画書リンク: `https://github.com/endazon/project-planning/blob/main/projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md`

## 目的・背景

`.ai-context/specs/20260903_584_grpc-scope-decision.md`（判断フェーズ）が環流した 3 点の裁定依頼
（planning#520）に対し、計画側が `MSP/ADR-0075` で応答した。決定 1〜4 は `IADR-0284` の Proposed
決定と同じ向きであり、新たな決定事項は「順序」と「期限」の 2 点である。本作業はこの裁定を
`IADR-0284`・`docs/blocked-tasks.md`・#584 へ反映する文書追随であり、実装判断そのものは追加しない。

## 対象範囲

- 対象: `IADR-0284` の状態遷移（Proposed → Accepted）と追記、`.ai-context/adr/README.md` 索引行、
  `docs/blocked-tasks.md` B-4 の #584 行、#584 のラベル張り替え（`blocked:decision` → `blocked:env`）と
  コメント、planning#520 への実装側受理コメント
- 対象外: コードの変更・段 0 以降の着手（先行条件が 2026-11-30 まで未確定のため）

## 反映内容の要約

- 移行順序は**基盤先行**（MSP が proto 配置・versioning・h2c・s2s トークンの写し方の現物を作り、AST は追随する）
- 先行条件（`MSP/ADR-0029` フォローアップの履行）に**期限 2026-11-30** を設定。未履行なら基盤先行を見直す
- 一括移行の義務は緩めない。例外 ADR は起こさない（`IADR-0284` 決定 1・3 と同じ向き。据え置き）
- AST→MSP 4 本は MSP の proto 公開後に移行。REST 継続は例外ではなく過渡状態の継続
- #584 の「REST 継続で閉じてよい」は不採用（`IADR-0284` 決定 1 と同じ結論）
- 「基盤先行」は MSP 自身の east-west 移行を含む

## 受け入れ基準

- [x] `IADR-0284` が Accepted へ遷移し、裁定内容が日付付き追記として残っている（本文プロズは書き換えない）
- [x] `.ai-context/adr/README.md` の `IADR-0284` 索引行が更新されている
- [x] `docs/blocked-tasks.md` B-4 の #584 行のみが更新され、他行は変更していない
- [x] #584 のラベルが `blocked:decision` → `blocked:env` へ張り替えられ、裁定・待ち先・期限・再測定手順のコメントがある
- [x] planning#520 に実装側受理のコメントがある
- [x] コードを変更していない
- [x] `check-trace-blocks` / `check-cross-repo-refs` / `check-doc-links` / `check-adr-index-sync` が緑

## テスト方針

コード変更なし。文書検査器（上記）を PR で通す。

## 計画書との差異

なし（本作業は計画側の裁定への追随であり、差異は生じない）。
