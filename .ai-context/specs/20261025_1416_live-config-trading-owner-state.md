---
title: AST の live 用 Playwright config と、MSP の setup が作る取引オーナーの storageState の共有（#1416 / E-33）
type: spec
status: in-progress
related_ids: [NFR, SC-01, ADR-0038, IADR-0588]
author: claude (Claude Code)
created: 2026-10-25
updated: 2026-10-25
---

# 仕様書: AST の live 用 config と取引オーナーの storageState の共有（#1416 / E-33）

> 本仕様書は AST `origin/develop` `2778ee72` を起点に、実装の前に作成した。判断は [IADR-0588](../adr/IADR-0588_live-config-reads-msp-owner-state.md) に残す。
> 親は #1410、設計の正本は MSP#2013（コメント 3 §2.3）。🔴 稼働中の PoC・本番の OpenD・証券口座には触れない（AST には live 層の CI が無い）。

## 起点（トレーサビリティ）

- issue: #1416（E-33）。先行: MSP の E-32（MSP#2026）。後続: E-43（#1417）。
- 計画: SC-01（設定画面）・ADR-0038（連結配備の認証は MSP の realm `platform` に一本化）。

## 受け入れ基準（#1416）と、この PR での確かめ方

- [ ] MSP の setup が作った取引オーナーの state で `/trading/settings` に到達 —— `e2e/live/owner-reach.ro.spec.ts`。
  **live の実走は AST の CI に無い**ので、オーケストレーターの指示どおり次を L1（`e2e/live-opt-in.spec.ts`）で固定する:
  - 既定（`E2E_LIVE` 未設定）は 0 件で exit 0
  - `E2E_LIVE=1` の `--list` で live-ro の spec が読み込める
  - 取引オーナーの state を `E2E_AUTH_DIR` から **MSP と同じ名前**（`<E2E_AUTH_DIR>/trading-owner.json`）で読む。門（state が無い／失効／別の役割）の判定
- 🔴 **#1416 の本文の名前の訂正**: 本文は `e2e-trading-owner.json` と書くが、MSP の setup が書くのは `stateFile(authDir, role)` ＝ `<authDir>/trading-owner.json` である。AST は MSP の実際の名前に合わせる。

## 流す順（常設の PoC。PoC セッションが行う）

1. **MSP** の live の setup を、取引オーナーのセッションだけを残す opt-out つきで流す:
   `E2E_LIVE=1 E2E_KEEP_SESSIONS=trading-owner pnpm --filter @platform/frontend exec playwright test --config playwright.live.config.ts --project live-setup`
   （MSP の teardown は他の 5 役割を必ず失効させ、取引オーナーの state だけを 0600 で残す。opt-out は MSP 側の別 issue・別 PR で入る。**入るまでは本手順は流せない**）
2. **AST** の live を、MSP の state の置き場を渡して流す（`E2E_AUTH_DIR` は必須）:
   `E2E_LIVE=1 E2E_AUTH_DIR=<MSP>/src/platform/frontend/playwright/.auth npx playwright test --config playwright.live.config.ts`
3. AST の globalTeardown が、成否によらず取引オーナーのセッションを BFF のログアウトで失効させ、state のファイルを消す。失効できなければ役割名だけを出して exit 1。

## 判断（IADR-0588）

1. AST は自分ではログインしない（資格情報を読まない）。MSP の setup が書いた state を `E2E_AUTH_DIR` から読む。live のときは `E2E_AUTH_DIR` を必須にする（単独のチェックアウトから MSP の置き場は推測できない）。
2. 門（globalSetup）は、取引オーナーの state が在り、その cookie で `/bff/auth/me` が 200 かつ `trading-owner` を持つことを見る。満たさなければ exit 2。
3. AST の teardown が取引オーナーを失効させる（MSP の opt-out で残したセッションの後始末を引き受ける）。
4. L1 の config は `e2e/live/` を `testIgnore` する（ハーネスとモックで live の spec を流さない）。
5. 層の project 名・接尾辞は MSP と同じ（`live-ro`＝`*.ro.spec.ts`、`trading`＝`*.trading.spec.ts`）。AST の層は `ro` / `trading` だけ。

## 検証

- `npm run e2e:typecheck`・`npm run lint`、L1 の `e2e/live-opt-in.spec.ts`（5 件）、L1 の `--list` に `e2e/live/` が載らない。
