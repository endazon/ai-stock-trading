---
title: MSP の E2E 基準を AST へ写す（参照のみ）——テスト戦略 §6 の Playwright E2E 行の更新と、live の取引ガードレールの IADR（#1411・E-01）
type: spec
status: done
related_ids: [NFR, SC-01, SC-02, SC-03, SC-04, FR-20, IADR-0087, IADR-0565, MSP:IADR-0569]
author: claude (Claude Code)
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - MSP:docs/tests/E2E_STANDARDS.md（MSP#2041 でマージ。develop `147e66fb`）
  - MSP:IADR-0569（層・タグ・project 名・E2E_LIVE / E2E_LAYER）
  - MSP#2013 コメント 3（稼働中の PoC での流し方。§5 が AST の取引影響層と閉場判定）
---

# 仕様書: MSP の E2E 基準を AST へ写す（#1411）

> 本仕様書は実装着手前に作成した（着手 2026-10-21。AST `origin/develop` `40de5a61`）。

## 起点（トレーサビリティ）

- issue: #1411（`Closes`。E-01・波 0）／親 #1410（AST の網羅表と禁止行）／設計の正本 MSP#2013、先行 E-00 = MSP#2014（PR MSP#2041 でマージ済み）
- 計画書 ID: NFR（無採番。テストの統制）。網羅表の対象画面 SC-01〜SC-04、禁止行 A-049 は FR-20（発注先）
- 正本として参照する MSP の決定: MSP:IADR-0569（層 L1/L2/L3/L3-T/L3-L・タグ・project 名・`E2E_LIVE` / `E2E_LAYER`）
- 新規 IADR: **IADR-0565**（E2E の live 取引ガードレール）。番号は push 直前に develop と開いている PR の最大（0564）を確かめて ＋1

## 触るファイル（宣言）

- `docs/tests/README.md`（§6 の行・trace ブロック・変更履歴）
- `.ai-context/adr/IADR-0565_e2e-live-trading-guardrails.md`（新規）
- `.ai-context/adr/README.md`（索引行の追加）
- 本仕様書

`backend/`・`deploy/`・`scripts/`・`frontend/` は触らない（並行エージェントの領域）。

## 方式

1. **本文を複写しない。** MSP の基準書（`docs/tests/E2E_STANDARDS.md`）を正本として名前で指し、AST の README には
   AST 固有の点だけを書く。`docs/` の規約（`docs/README.md` 運用ルール 4）は他リポジトリへの可視リンクを禁じるため、
   可視本文はリポジトリ名とパスのインラインコードに留め、MSP の IADR・issue は trace ブロック（`iadrs: MSP:IADR-0569`・
   `issues: MSP#2013, MSP#2014`）へ入れる。AST の IADR（0565）も trace ブロックの `iadrs` へ。
2. AST 固有として README に書く点:
   - テスト ID `T-<FR>-<連番>` を E2E のテスト直前コメントにも付ける（§1 の採番規約・採番空間を共有）。
   - L1 は harness（`frontend/e2e/harness/`。合成点と同じことを最小構成で行う test-only の vite ＋ AuthHarness）＋
     `installBff`（`frontend/e2e/fixtures.ts`。契約フィクスチャを土台にした `page.route` スタブ）。MSP の L1 のような
     ビルド済み SPA の preview ではない。
   - L3-L は持たない（画面操作で LLM を呼ぶものが無い）。
   - 禁止行 A-049（live の `PUT broker-provider`）／A-067（live の `POST /opend-auth/{code,resend}`）と、L3-T の
     閉場後限定・スナップショット・復元（決定は IADR-0565）。
3. IADR-0565 の決定は依頼の 4 点そのもの（送らない操作・globalSetup の閉場判定と exit 2・閉場後の書き込みと
   スナップショット／復元／差分の赤・層は MSP:IADR-0569 に従い L3-L を持たない）。配線（live 用 config・
   `e2e-market-closed.js`・restore スクリプト・禁止行の grep 検査）は後続の issue が持ち、本 PR は文書だけ。

## 母集合（規則 9・10）

- 「フロント Playwright E2E」を書いている docs を走査した: `grep -rn "Playwright" docs/` → `docs/tests/README.md` §6 の 1 行のみが
  未整備としての記述（他は機能仕様書などの言及ではなかった。実行は下の検証に記す）。更新対象は同行だけ。
- 本変更で新たに誤りになり得る自分の記述: README §6 は「未整備（担当 issue で追加する）」の表であり、行を
  「基準は確定・配線は後続」へ書き換えるので、表の見出しの趣旨（未整備）とは「配線が未整備」として整合する。
- IADR 索引の同期（`check-adr-index` 系）: 索引へ 0565 の行を 1 本足す。

## 受け入れ基準

- [x] IADR-0565 に「live では `PUT broker-provider`（どの値へも）と `POST /opend-auth/{code,resend}` を送らない」が決定として書かれている
- [x] IADR-0565 に「閉場判定を globalSetup に置き、開場中・in-flight ありなら exit 2」が決定として書かれている
- [x] IADR-0565 に「取引に影響する書き込みは閉場後だけ。スナップショット・復元・差分が残れば赤」が決定として書かれている
- [x] IADR-0565 に「層の定義は MSP:IADR-0569 に従う。AST は L3-L を持たない」が決定として書かれている
- [x] `docs/tests/README.md` §6 の行が MSP の基準書を正本として指し、AST 固有（`T-<FR>-<連番>`・harness・A-049/A-067）を書いている。可視本文に IADR・修飾付き issue が無い
- [x] 文書系の検査（trace-blocks・knowledge-graph・doc-links・IADR 索引・cross-repo-refs・plan-id-qualification）と `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑

## 範囲外

- live 用 Playwright config・`e2e/live/`・閉場判定スクリプト・復元スクリプト・禁止行の grep 検査の実装（後続の E-20 以降）
- 閲覧専用ロールの要否（計画への起票 P-02。#1410 で扱う）
