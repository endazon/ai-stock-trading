---
title: 件名が空でスカッシュされた cf784543（#1491）を CHANGELOG の生成時だけ補正する
type: spec
status: done
related_ids: [NFR, IADR-0595]
author: claude (Claude Code)
created: 2026-10-29
updated: 2026-10-29
---

# 仕様書: 空の件名のコミットの CHANGELOG 補正（#1482 / PR #1491）

> 本仕様書は作業着手前に作成した（2026-10-29。AST `origin/develop` `cf784543`）。

## 起点

- PR #1491 を squash でマージしたとき、GitHub API の 503 でタイトルを取れず、develop のコミット `cf784543e9012855a1207ed4a92062f4514b01b5` の件名が ` (#1491)`（本文 `Closes #1482`）になった（オーケストレーターの操作）。履歴は書き換えない（force push 禁止）。

## 確かめたこと

1. `scripts/gen-changelog.js` は種別の無い件名を捨てない。正規表現に当たらない件名は `type: 'other'`・要約＝件名として読み、その後で `applyOverride` を掛ける。よって remap が効く（コードの修正は不要）。
   - 補正の前: 「その他」の節に `-  (#1491) (cf784543e)`。補正の後: 「不具合修正」の節に `- **NFR**: RabbitMQ.Client を 7.2.2 へ上げ、… (#1491) (cf784543e)`（手元で生成して確認）。
2. develop への push で走る検査: `commit-messages` は PR のときだけ走り、push では skipped。`scripts-tests`・`static-checks`・`changelog` 等は成功（cf784543 の check-runs で確認）。PR の検査範囲（`origin/develop..HEAD`）にこのコミットは入らないので、後続の PR も赤にならない。

## 変更

- `scripts/changelog-overrides.json` に cf784543 の remap（type=fix・scope=NFR・desc は PR #1491 のタイトル）を足す。reason に経緯を書く。

## 検証

- `node scripts/gen-changelog.js` の出力で補正を確認。`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-commit-messages`。
