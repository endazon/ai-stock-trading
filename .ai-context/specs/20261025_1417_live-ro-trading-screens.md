---
title: AST の live-ro で 4 画面の到達・paper のバナー・REAL モーダルの無効送信を流し、発注先の変更と OpenD 認証の送信を abort で塞ぐ（#1417 / E-43）
type: spec
status: in-progress
related_ids: [NFR-06, FR-09, FR-10, FR-12, FR-17, FR-20, SC-01, SC-02, SC-03, SC-04, ADR-0038, IADR-0588, IADR-0589]
author: claude (Claude Code)
created: 2026-10-25
updated: 2026-10-25
---

# 仕様書: AST の live-ro（4 画面・バナー・REAL モーダル）と書き込みの保険（#1417 / E-43）

> 本仕様書は AST `origin/develop` `28c6963d` を起点に、実装の前に作成した。判断は [IADR-0589](../adr/IADR-0589_live-ro-write-guard-and-shell-paths.md) に残す。
> 先行は E-33（#1416・[IADR-0588](../adr/IADR-0588_live-config-reads-msp-owner-state.md)）。🔴 稼働中の PoC・本番の OpenD・証券口座には触れない（AST に live 層の CI は無い）。

## 起点（トレーサビリティ）

- issue: #1417（E-43）。網羅表（#1410）の A-002・A-003・A-004・A-007・A-025・A-042・A-043・A-057・A-058・A-066。

## 受け入れ基準（#1417）

- [ ] `PUT broker-provider` と `POST /opend-auth/*` が `page.route` で abort され、呼ばれた記録が 0 件 ——
  `e2e/live/support/guard.ts` が各 test の前に `/bff/` への GET 以外をすべて記録して abort し、`trading.ro.spec.ts` の afterEach が
  危険な 3 種（`PUT …/broker-provider`・`POST /bff/opend-auth/code`・`…/resend`）の記録が 0 件、書き込みの記録が 0 件であることを見る。
  保険そのものの働き（画面の中から送った PUT / POST が abort され記録に残る、GET は通す）は L1 の `e2e/live-opt-in.spec.ts` で固定する。

## 判断（IADR-0589）

1. **書き込みの保険は `/bff/` への GET・HEAD・OPTIONS 以外の全部**（危険な 3 種だけに絞らない）。live-ro は読み取りの層なので、送ろうとしたこと自体を失敗にする。
2. **AST の画面のパスは `/settings`・`/settings/risk`・`/controls`・`/opend-auth`**（MSP の共通シェルの直下）。網羅表・設計（MSP#2013 コメント 3）・
   E-33 の `owner-reach.ro.spec.ts` の `/trading/*` は誤り（MSP の `router.test.ts` 190 行が `/settings`・`/settings/risk`・`/controls` を固定している）。
   本 PR で `owner-reach.ro.spec.ts` も直す。ナビのグループ名は「株式自動売買」、パンくずの根は「ホーム」（網羅表 A-004 の「基盤ポータル」は古い）。
3. A-043 は **チェックだけ**を入れ、「REAL」の文字は入れない（送信の条件を揃えない）。前提外（現在の発注先が REAL・損切りの実行機構が S0 以外）は理由つきで skip。
4. A-007 は、BFF の統制状態の発注先が paper なら 3 画面でバナーが 1、それ以外なら 0。A-058 は paper のときだけ「取得できていません」を見て、
   paper 以外は発注先を annotation に残して理由つきで skip する。

## 検証

- `e2e/live-opt-in.spec.ts`（L1）8 件、`E2E_LIVE=1 … --list` で live-ro に 11 件（trading.ro 10・owner-reach 1）、`npm run lint`・`e2e:typecheck`。
- live の実走は PoC（MSP の setup を `E2E_KEEP_SESSIONS=trading-owner` で流してから。手順は 20261025_1416 の「流す順」）。
