---
title: CI の frontend-e2e に上限の時間・レポートの常時の保存・flaky の集計と警告・develop の 3 回連続の起票を足す（#1421 / E-72）
type: spec
status: done
related_ids: [NFR, IADR-0208, IADR-0603, MSP:IADR-0623]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - MSP#2013（E-72）
---

# 仕様書: frontend-e2e のレポート・上限・flaky（#1421 / E-72）

> 本仕様書は実装着手前に作成した（着手 2026-10-30）。基点は AST `origin/develop` `1a38717c`。

## 起点

- #1421（E-72。親 #1410）。先行 E-20〜E-23（#1412〜#1415）は close 済み。
- 検査器 `scripts/check-e2e-flaky.js` は基盤の MSP#2037（E-73。MSP develop `4f1dd770`）と同一の実装を写した（バイト一致。kit の同期の義務は無い）。`scripts/lib/ci-annotate.js` は基盤と同一であることを diff で確かめた。
- 新規 IADR-0603。

## 対象範囲

- `.github/workflows/ci.yml`:
  - frontend-e2e: `timeout-minutes: 30`、E2E の JSON レポーター、flaky の集計と要約の成果物、レポートの常時の保存。
  - 新規ジョブ `e2e-flaky-streak`（develop の push だけ）と `report-failure`（`ci-failure-issue.yml`、marker `e2e-flaky`）。
- 新規 `scripts/check-e2e-flaky.js`。
- 試験: `scripts/scripts.repo.test.js`（自己試験の起動・警告と 0 件の赤・ci.yml の結線）。
- 文書: `scripts/README.md`、IADR-0603、索引。

## 母集合（規則 9・10）

- 必須チェックの表（`docs/ai-workflow.md` の 236 行目）: 新しいジョブは develop の push だけで走り、必須にしない → 表は変えない。frontend-e2e の名前は変えていない（必須のまま）。
- `report-failure` の呼び出し側（integration・codeql・security・ci-latency-watch）と同じ書き方（`always() && github.event_name != 'pull_request' && contains(needs.*.result, 'failure')`、permissions は contents: read と issues: write）に揃えた。
- 文書で flaky・`playwright-report` に触れている箇所: `git grep -i -E 'flaky|frontend-e2e' -- docs` → 必須チェックの表と blocked-tasks の 1 行（実走経路の説明）だけで、どちらも変わらない。

## 受け入れ基準

1. 失敗時以外もレポートが残る。
2. flaky 1 件で `::warning::`。
3. 同一の test が develop で 3 回連続して flaky なら、`ci-failure-issue.yml` 経由で起票する。

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（E-72 の 3 件を含む）。`node scripts/check-e2e-flaky.js --self-test`（17 件）。`node scripts/check-workflow-job-refs.js`。
- 手元で、L1 の 1 ファイルを `npm run e2e -- --reporter=list,html,json`（`PLAYWRIGHT_JSON_OUTPUT_FILE` つき）で流し、JSON を検査器で集計した（flaky 0 件 / 6 件、html レポートも生成）。
- develop の 3 回連続の判定と起票は、develop の実際の実行でしか通らない（自己試験が判定の関数を固定している）。
