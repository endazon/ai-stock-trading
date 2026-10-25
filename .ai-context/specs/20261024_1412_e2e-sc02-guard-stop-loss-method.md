---
title: SC-02 の未検証の PUT（取引ガード・損切りの実行機構）を E2E で埋め、defaultBff() に guard / stop-loss-method / opend-auth を足す
type: spec
status: done
related_ids: [SC-02, SC-04, FR-10, FR-19, FR-20, UC-06, ADR-0040, NFR, IADR-0086, IADR-0422, IADR-0087, IADR-0146]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
---

# 仕様書: SC-02 の未検証の PUT を E2E で埋める（#1412 / E-20）

> 本仕様書は実装着手前に作成した（AST `origin/develop` `3f9153ff`＝#1463 のマージ後）。

## 起点（トレーサビリティ）

- 起点: #1412（親 #1410 の網羅表 A-037〜A-040。設計の正本は MSP#2013 の「実装の分割案」E-20 行と基準書）。
- 計画: SC-02（`05_screens/01_screens.md` §SC-02 主要素・入力の表・警告モーダル 6）、FR-10・FR-19・FR-20、UC-06、
  ADR-0040 決定 1（実弾では S0 以外を選べない・逆方向も止める）。planning `origin/main` で読んだ。
- 実装の判断: IADR-0086（取引ガードの変更・危険な緩和の明示確認）、IADR-0422（損切りの実行機構の選択 UI）、
  IADR-0087（E2E の BFF モック）、IADR-0146（モック本文は契約フィクスチャを土台にする）。新しい IADR は起こさない
  （試験の追加だけで、実装の判断を伴わない）。計画との矛盾: なし（網羅表の A-040 の文言だけが古い。下記）。

## 範囲

L1（CI スタブ層。harness ＋ `installBff`）だけ。触るのは `frontend/e2e/fixtures.ts` と `frontend/e2e/sc02-risk-settings.spec.ts`。

- `defaultBff()` に足す: `PUT /risk-controls/settings/guard`、`PUT /risk-controls/settings/stop-loss-method`
  （いずれも 200・変更後の設定）、`GET /opend-auth/state`（SMS 待ち）、`POST /opend-auth/code`・`POST /opend-auth/resend`（204）。
  SC-04 の 3 本は E-21（#1413）が使う。波 1 で fixtures.ts を触るのは本 issue だけにする（後続は rebase）。
- `opend-auth/state` には契約フィクスチャが無い（バックエンドの生成対象は risk-controls / monitor だけ）。
  型 `OpendAuthStateView` で縛った literal を置き、生成は後続に残す（報告に書く）。

## 受け入れ基準（試験への写像）

| ID | 基準 | 試験（`sc02-risk-settings.spec.ts`） |
| --- | --- | --- |
| A-038 | 取引ガード: 商品種別 3 値・市場 2 値を表示し、`PUT settings/guard` の 200（本文の形）/400/409。危険な緩和は確認まで送らない | 「SC-02 取引ガードの変更」describe の 4 本 |
| A-039 | 損切りの実行機構: SIMULATE で 4 値を選べ、`PUT settings/stop-loss-method` の 200（`{method, reason}`）/400/409。理由空・未変更は送らない | 「SC-02 損切りの実行機構」describe の 4 本 |
| A-037 | REAL の間は S1〜S3 を選べず理由を出す。S0 以外のまま REAL を選ぶと確認モーダルへ進めず、PUT broker-provider を送らない | 同 describe の 2 本 |
| A-040 | 運用段階の参照表示に入力要素が無い | 「運用段階と発注先（参照）」の 1 本 |

- 409・400 はいずれも PUT を 1 回だけ送り、自動再試行しない（横断規約 A-009）。
- **A-040 の「ガードは参照のみ」は計画の改定（planning#822・2026-10-22）で古くなった**。ガードは SC-02 で変更する
  （A-038）。本 issue は Stage の参照だけを固定し、網羅表の文言の訂正は親 #1410 へ報告する。

## 是正の母集合（規則 9）

- `defaultBff()` に PUT を足すと、これまで 404 に落ちていた要求が 200 になる。既存 spec で guard / stop-loss-method /
  opend-auth の 404 に依存する試験: `grep -rn "settings/guard\|stop-loss-method\|opend-auth" frontend/e2e` で 0 件（依存なし）。

## 検証（手元）

- `npm run lint` / `npm run typecheck` / `npm run e2e:typecheck` / `npm run format:check`（あれば）/ `npm test`
- `npx playwright test`（CI の `frontend-e2e` と同じ構成。Chromium は `/opt/pw-browsers`）
- 変異: `isStopLossMethodPermittedOn` を常に真にすると A-037 の 2 本が赤、GuardForm の危険確認を外すと A-038 の 1 本が赤。
