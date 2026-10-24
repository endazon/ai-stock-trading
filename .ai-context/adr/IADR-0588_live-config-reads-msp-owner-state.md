---
title: IADR-0588 AST の live 層は自分でログインせず、MSP の setup が残した取引オーナーの storageState を E2E_AUTH_DIR から読み、自分の teardown で失効させる
type: impl-adr
status: Accepted
related_ids: [NFR, SC-01, ADR-0038]
author: claude (Claude Code)
created: 2026-10-25
updated: 2026-10-25
related_specs:
  - ../specs/20261025_1416_live-config-trading-owner-state.md
---

# IADR-0588: AST の live 層は MSP の取引オーナーの state を読む

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-25
- 決定者: Claude Code（#1416。設計の正本 MSP#2013 コメント 3 §2.3）

## コンテキスト

1. AST の画面は MSP の SPA の `/trading/*` にだけ在り、認証は MSP の realm `platform` に一本化されている（ADR-0038）。試験アカウント `e2e-trading-owner` は MSP の realm が持ち、ログイン（パスワード → TOTP）は MSP の live の setup が行う。
2. MSP の live の teardown は、実行の終わりに 6 役割すべてのセッションを失効させ state を消す（MSP/IADR-0624）。そのままでは AST の別の実行は state を読めない。
3. AST には live 層の CI（使い捨てスタック）が無い。

## 決定

1. **AST は自分ではログインしない**（資格情報を読まない）。MSP の setup が書いた storageState を `E2E_AUTH_DIR` から、MSP と同じ名前 `<E2E_AUTH_DIR>/<role>.json`（取引オーナーは `trading-owner.json`）で読む。live のとき `E2E_AUTH_DIR` は必須。
2. MSP 側に、取引オーナーだけを残す opt-out（`E2E_KEEP_SESSIONS=trading-owner`。既定は全部失効。値域外は例外。残すファイルは 0600）を別 issue・別 PR で足す。**それが MSP に入るまで、AST の live は流せない**（門が「state が無い」で exit 2 になる）。
3. AST の globalSetup（門）は、取引オーナーの state が在り、その cookie で `/bff/auth/me` が 200 かつ `trading-owner` を持つことを確かめ、満たさなければ exit 2。
4. AST の globalTeardown は、成否によらず取引オーナーのセッションを BFF のログアウトで失効させ（同じ cookie で `/bff/auth/me` が 401 になることを見る）、state を消す。確かめられなければ役割名だけを出して exit 1。
5. opt-in（`E2E_LIVE` は `1` か未設定・空）・層（`E2E_LAYER` は `ro` / `trading`）・project 名と接尾辞は MSP と同じにする（MSP/IADR-0569 決定 3・4）。値域外は読み込みで例外。
6. L1 の config は `e2e/live/` を `testIgnore` する。live の config の opt-in・読み込み・state の読み先・門の判定は L1 の `e2e/live-opt-in.spec.ts` が固定する（AST の CI で走る）。

## 検討した選択肢

| 選択肢 | 判断 |
| --- | --- |
| A. MSP の state を読み、MSP に取引オーナーだけの opt-out を足し、AST の teardown が失効させる | **採る**（設計 §2.3 の共有のまま。ログインの実装を 2 か所に持たない） |
| B. AST が自分で e2e-trading-owner にログインする（MSP の setup の写し） | 採らない（ログイン・`fillSecret`・TOTP の部品が 2 か所に分かれ、片方だけ古くなる。資格情報を AST にも渡すことになる） |

## 影響

- 常設の PoC での流す順は作業仕様書の「流す順」節（MSP の setup を opt-out つき → AST の live → AST の teardown）。
- #1416 の本文の `e2e-trading-owner.json` は誤りで、MSP の実際の名前は `trading-owner.json`。

## 残余リスク

- AST の live の実走は CI で確かめられない（AST に live 層の CI が無い）。最初の実走は常設の PoC で、MSP の opt-out が入った後に行う。
- AST の teardown が走らない異常終了では、取引オーナーのセッションが寿命まで残る（MSP の teardown と同じ性質）。
- **teardown は失効できなかったときも state のファイルを消す**（MSP の teardown と同じ形）。失効できなかったセッションは、BFF のセッションの寿命で自然に切れるまで生きている。再試行の手がかりより、ディスクに cookie を残さないことを取る。
- 門（globalSetup）が exit 2 で抜けると Playwright は globalTeardown を呼ばない。そのため門が不成立のときも、取引オーナーの state が在れば、teardown と同じ失効と削除の関数を先に呼んでから exit 2 にする（`readiness.ts` の `gate`。L1 で「state が在って門で落ちたときに 1 回呼ばれる」を固定）。
