---
title: live の取引影響層（trading）の閉場の門・in-flight の門・スナップショット・突合・復元の部品（#1418 / E-60）
type: spec
status: done
related_ids: [NFR, SC-01, SC-02, IADR-0565, IADR-0588, IADR-0599, MSP:IADR-0569]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - MSP#2013 コメント 3 §5.2・§5.3
---

# 仕様書: live の取引影響層の門と復元の部品（#1418 / E-60）

> 本仕様書は実装着手前に作成した（着手 2026-10-30）。基点は AST `origin/develop` `45a46525`。

## 起点

- #1418（E-60。親 #1410。設計の正本は MSP#2013 コメント 3 §5.2〜5.3）。先行の E-33（#1416）は develop にある。
- IADR-0565 決定 2・3 の実体。新規 IADR-0599。
- 子 issue #1502（in-flight を BFF から読む経路）。オーケストレーターの判断（2026-10-30）で案 A を採り、E-60 の後・E-61（#1419）の前に処理する。

## 対象範囲

- 新規 `scripts/e2e-market-closed.js`（市場の閉場の門。CLI と関数）。
- 新規 `scripts/e2e-trading-restore.js`（スナップショット・突合・復元の本体と単独実行の口）と `scripts/e2e-trading-restore.test.js`（本体の試験）。
- 新規 `frontend/e2e/live/support/snapshot.ts`（trading の globalSetup。閉場の門・in-flight の門・スナップショット）と `restore.ts`（trading の globalTeardown。突合と復元）。
- `frontend/playwright.live.config.ts`（trading のときだけ globalSetup / globalTeardown を配列にする）。
- 試験: `scripts/scripts.repo.test.js`（市場の門の 5 ケースほか）、新規 `frontend/e2e/live-trading-gate.spec.ts`（L1。門と突合の判定）。
- 文書: `scripts/README.md`、`docs/tests/README.md` の「現状」、IADR-0599、IADR の索引。
- 対象外: in-flight の情報源（#1502）、trading 層の spec（E-61 以降）、禁止行の grep 検査、`e2e-live.yml`（E-62）。

## 判断の要点

- in-flight の情報源が BFF に無い（`RiskStatusView` に欄が無く、近い口は BFF が中継していない）。門は常に不成立（exit 2）にし、そのことを門のスクリプトに書く（spec の skip にはしない）。
- 復元は単純で取引を新たに動かさない項目だけ。発注先・損切り機構は送らない。それ以外の差分は赤にして画面で戻す（IADR-0599 決定 3）。

## 母集合（規則 9・10）

- 閉場判定・復元を「未整備」と述べる箇所: `git grep -n -E '閉場|e2e-market-closed|e2e-trading-restore|スナップショット'` を docs/tests で引いた → `docs/tests/README.md` §6 の「現状」の行（直す。E-33 で入った live 用 config と `e2e/live/` も「未整備」と書かれていた）。
- `.ai-context/specs/20261021_1411_…` の「後続の issue が持つ」は凍結記録のため除外。
- 市場の規則の写し先: C# の `MarketSessions.cs` / `MarketHolidays.cs` は変えない（読むだけ）。
- 新たに誤りになる自分の記述（規則 10）: IADR-0565 §結果の「閉場判定・突合は規約だけで、機械では守られていない」は、本変更で一部が機械に変わる。凍結記録のため本文は直さず、IADR-0599 が実体であることを索引で示す。

## 受け入れ基準

1. 開場中・次の開場まで 60 分未満・in-flight が判断できない（いまは常に）のどれかで、trading の門は exit 2 で、spec を 1 本も流さない。
2. 突合の後も差分か復元の失敗が残れば、Playwright の実行は失敗（exit 1）になる。単独実行は exit 1。取引オーナーの失効は、その場合も走る。
3. 市場の判定は `MarketSessions.cs` と同じ 5 ケース（前場・後場・閉場・半日・休場）を単体試験で固定する。
4. 発注先・損切り機構の差分は、復元を送らずに赤にする。

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（市場の門 7 件と、復元の本体の試験の起動 1 件を含む）。
- `node scripts/e2e-trading-restore.test.js`（10 件。レビューで監視銘柄の 1 件目の失敗の試験を足した）。
- `npx playwright test e2e/live-trading-gate.spec.ts e2e/live-opt-in.spec.ts`（L1）、`npm run typecheck`・`e2e:typecheck`・eslint。
- 規則の突合（実装時に 1 回）: 2022-01-01〜2035-12-31 の米国の休場日・半日取引日を、C# の `MarketHolidays`（使い捨てのコンソールで共有カーネルを参照）と本スクリプトで列挙し、170 行が一致した。
- 門の結線の手動の確かめ: 偽の BFF（`/bff/auth/me` が 200）を手元に立て、`E2E_LIVE=1 E2E_LAYER=trading` で live の config を流した。readiness を通った後、trading の門が「UnitedStates は開場中」と「in-flight を BFF から読めない（#1502）」で exit 2 になり、取引オーナーの state が消えたことを確かめた。
