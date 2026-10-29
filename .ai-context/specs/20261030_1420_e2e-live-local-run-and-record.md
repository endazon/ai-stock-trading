---
title: live の E2E を手元で流す口（npm run e2e:live）と、docs/tests/E2E_LIVE.md の実行手順・実行記録の表（#1420 / E-62 の一部。CI のジョブは #1506）
type: spec
status: done
related_ids: [NFR, SC-01, SC-02, SC-03, SC-04, IADR-0565, IADR-0599, IADR-0602]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - MSP#2013 コメント 3 §7
---

# 仕様書: live の E2E の手元の実行と実行記録（#1420）

> 本仕様書は実装着手前に作成した（着手 2026-10-30）。基点は AST `origin/develop` `5abeee47`。

## 起点

- #1420（E-62。親 #1410）。オーケストレーターの判断（2026-10-30）で、self-hosted のランナーの案を採らず、手元の実行（案 M）の範囲だけを入れる。
- CI のジョブの実行場所の判断は、子 issue **#1506**（blocked:decision・decision-needed・再検証期限 2026-11-13）に分けた。本 PR は `Refs #1420` で、#1420 は閉じない。
- 新規 IADR-0602。

## 対象範囲

- 新規 `scripts/e2e-live-run.js`（手元の口）と、`frontend/package.json` の `e2e:live`。
- 新規 `docs/tests/E2E_LIVE.md`（流し方・終了の読み方・未復元一覧の出し方・初回の live 実行で確かめる項目・実行記録の表）。
- 試験: `scripts/scripts.repo.test.js`（引数・終了コードの写し・未復元一覧・記録の 1 行と表の列の一致・使い方の誤り）。
- 対象外: `.github/workflows/e2e-live.yml`（#1506 の判断の後）、表への自動の追記。

## 「初回の live 実行で確かめる項目」に載せたもの

- 門（閉場・in-flight の照会の配備）。
- 突合の未復元一覧が「なし」になること。
- #1505 のレビューで返した 2 点: A-044（保有建玉数上限が 1 の環境では 1 上げる向き）と A-048（履歴の増分 +2 / +1 の前提）。
- A-047（監視銘柄の空き）。

## 母集合（規則 9・10）

- `E2E_LIVE.md` を指す箇所: `git grep -n "E2E_LIVE.md"`（本 PR で足すファイルを除く）→ develop では 0 件。#1505（未マージ）の作業仕様書 20261030_1419 が「初回の結果は E-62 の実行記録に残す」と名前を挙げており、本 PR の表がその受け皿になる。
- スクリプトの一覧（`scripts/README.md` の固有の表）: 新しい口 `e2e-live-run.js` の行を足す。
- live の流し方を述べる既存の箇所: `frontend/playwright.live.config.ts` の冒頭のコメント（流す順）と `docs/tests/README.md` の「フロント Playwright E2E」の節。前者は MSP の setup → 本 config → teardown の順で、本口と矛盾しない。後者の「現状」は #1504 が書き換え中なので触らない（衝突を避ける）。`E2E_LIVE.md` からは README の節へ同じ docs/ 内のリンクで繋いだ。
- trace ブロックの IADR・仕様書は develop に在るものだけを書いた（#1504 の IADR-0600、#1505 の IADR-0601 と仕様書は未マージのため、マージ後に足す）。

## 受け入れ基準（本 PR の範囲）

1. `npm run e2e:live -- --layer trading` は、開場中・まもなく開場なら Playwright を起動せずに「前提未整備」で exit 0。
2. Playwright の exit 2 は「前提未整備」として exit 0、それ以外の失敗は exit 1。trading 層で未復元が残れば exit 1。
3. 終わりに `E2E_LIVE.md` の表と同じ 7 列の 1 行を出す。
4. `E2E_LIVE.md` に、日付・runId・層・結果・未復元一覧・flaky・イメージの作成日時の表がある。

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（E-62 の 7 件を含む。レビュー対応で 2 件足した）。
- 文書の検査（trace-blocks ほか）。
