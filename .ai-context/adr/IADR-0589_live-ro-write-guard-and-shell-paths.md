---
title: IADR-0589 AST の live-ro は /bff/ への GET 以外を全部 abort して記録し、送ろうとしたこと自体を失敗にする。AST の画面のパスは共通シェル直下（/trading の接頭辞は無い）
type: impl-adr
status: Accepted
related_ids: [NFR-06, FR-09, FR-20, SC-02, SC-04, IADR-0588]
author: claude (Claude Code)
created: 2026-10-25
updated: 2026-10-25
related_specs:
  - ../specs/20261025_1417_live-ro-trading-screens.md
---

# IADR-0589: AST の live-ro の書き込みの保険と画面のパス

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-25
- 決定者: Claude Code（#1417）

## コンテキスト

1. AST の live-ro は常設の PoC（本番の OpenD・証券口座につながる）で流す。`PUT /bff/risk-controls/settings/broker-provider` は発注先（実弾を含む）を切り替え、
   `POST /bff/opend-auth/code|resend` はオーナーの端末へ SMS を飛ばし OpenD の状態を変える。live-ro の操作（REAL モーダルを開く等）が転んでも、これらを外へ出してはならない。
2. 網羅表と設計（MSP#2013 コメント 3）は AST の画面を `/trading/*` と書いているが、MSP の SPA は AST のルートを共通シェルの直下に載せる（`createAiStockTradingRoutes(shell)`。
   `/settings`・`/settings/risk`・`/controls` は MSP の `router.test.ts` 189〜191 行、`/opend-auth` は `sc04OpendAuthRoute.tsx` 30 行）。E-33 の `owner-reach.ro.spec.ts` はこの誤りを写していた。

## 決定

1. `e2e/live/support/guard.ts` の `installWriteGuard` が、`/bff/` への **GET・HEAD・OPTIONS 以外の全部**を記録して `page.route` で abort する。live-ro の spec は
   各 test の前に張り、後で「危険な 3 種の記録が 0 件」「書き込みの記録が 0 件」を見る。abort は保険であり、**送ろうとしたこと自体を失敗にする**。
   URL を解析できない要求は、読み取り（GET・HEAD・OPTIONS）以外なら abort の対象にする（**fail-closed**。通さない）。
2. 危険な 3 種（発注先の変更・OpenD 認証の送信と再送）は `DANGEROUS` に名前で持ち、失敗の文言で書き込み一般と分ける。
3. AST の画面のパスは `/settings`・`/settings/risk`・`/controls`・`/opend-auth`。live の spec はこれを使い、`owner-reach.ro.spec.ts` の `/trading/settings` を直す。
4. 保険そのものの働きは L1（`e2e/live-opt-in.spec.ts`）が固定する（画面の中から送った PUT・POST が abort され記録に残る。判定の純関数の表）。
5. **A-002（閲覧者〔e2e-user〕で 4 画面が NotFound・ナビに「株式自動売買」が無い）は MSP の live-ro（`e2e/live/shell.ro.spec.ts`）が持つ**。AST の live は
   取引オーナーの state だけを読む。理由: MSP の opt-out（MSP#2232）が残せるのは取引オーナーだけであり、e2e-user も残すと cookie を手元に置く面が広がる。
   MSP の shell.ro は e2e-user の storageState で既に流れている（同じ実行の中で失効まで閉じる）。
6. **A-043 の skip**: 現在の発注先が REAL のとき（前提外）と、損切りの実行機構が S0 以外のとき（確認モーダルの手前の関門で止まる設計。L1 の
   `sc02-risk-settings.spec.ts` が固定）だけ、理由つきで skip する。どちらも**現在の設定を GET で読んだ値による条件つき**で、既定の構成（SIMULATE・S0）では流れる。
7. **A-058 の skip**: 発注先が paper 以外のときだけ、発注先を annotation に残して理由つきで skip する（paper 以外は REAL 照会の有無で数値の出方が変わり、
   読み取りだけでは期待値を決められない）。**発注先による条件つき**で、paper の構成では流れる。

## 検討した選択肢

| 選択肢 | 判断 |
| --- | --- |
| A. 危険な 3 種だけを abort | 採らない（他の書き込み〔上限・監視銘柄等〕も live-ro では送らない。網羅を名前の列挙に頼ると漏れる） |
| **B. GET 以外の全部を abort し、送ろうとしたこと自体を失敗にする** | **採る** |

## 影響

- 共通シェルや画面が読み取りのために POST を使う形（照会の POST）を足すと、live-ro が赤になる。そのときは照会の口を明示して許す（本 IADR を改める）。
- 🔴 **guard は `page` の経路（画面が送る要求）だけを塞ぐ。`page.request`（APIRequestContext）は `page.route` を経由しない。** そのため live の spec では
  `page.request` / `request` の書き込み系（`post`・`put`・`patch`・`delete`・`fetch`）を使わない（読み取りの `get` だけ）。この規約は L1 の
  `e2e/live-opt-in.spec.ts` が、`e2e/live/*.spec.ts` の本文に `request.(post|put|patch|delete|fetch)(` が 0 件であることで機械的に固定する。
- 網羅表（#1410）の `/trading/*` と A-004 の「基盤ポータル」は誤り／古い。PR で訂正を明記する。

## 残余リスク

- live の実走は CI で確かめられない（AST に live 層の CI が無い）。最初の実走は常設の PoC で行う。
