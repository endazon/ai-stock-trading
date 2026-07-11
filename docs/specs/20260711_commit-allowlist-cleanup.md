---
title: commit-allowlist.json の幻 SHA エントリ整理
type: spec
status: review
related_ids: [P3]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs: []
---

# 仕様書: commit-allowlist.json の幻 SHA エントリ整理

> Issue #47 の対応。`scripts/commit-allowlist.json` の `allow` 配列に、develop への rebase/squash-merge で
> **現行 git 履歴から消えた短縮/完全 SHA**（`d1652dcf` 等 9 件）が残っていた問題を是正し、現行 develop に
> 実在する非準拠コミットの完全 SHA へ整理する。Issue #32/#44（PR #44）のレビュー指摘（🟢）由来。

## 起点・課題

- 起点 ID: P3（リポジトリ整備。特定 FR/UC に紐づかないハウスキーピング）
- 対象 Issue: #47（関連: #32, #44）
- 課題: `check-commit-messages.js` は PR の `base..HEAD` のみ検査するため実害は無いが、除外リストとしての
  正確性・可読性が損なわれ、読者（人間・AI）が根拠 SHA を辿れない。`reason` にも「rebase で SHA が更新された」
  との記載が残存していた。

## 調査結果

現行 develop 全 53 コミットを `check-commit-messages.js` で走査した結果:

- 旧 9 エントリはいずれも現行 git 履歴に不在。`git cat-file -t` で 5 件（`d1652dcf`/`394fa1fd`/`079490d1`/
  `153810a4`/`d4835097`）はオブジェクト不在、4 件（`fdd7ae14`/`3319bbc7`/`a87d3cfb`/`ddb4609d`）は
  オブジェクトは残存するが develop から到達不可（rebase 前の旧履歴上）。
- 現行 develop で規約違反として検出される非準拠コミットは **2 件のみ**:
  - `d1cfeb5ff1d6fcefc44afde8231fdc2644fbb6fe` — `feat: フロントエンドのCIワークフローを追加し...`（起点 ID 無し）
  - `739bf023b83bb748e35ce635978cb63038212f79` — `fix: 修正されたソリューションファイル名を使用して脆弱性スキャンを更新`（起点 ID 無し）
  - root `Initial commit`（`fc103e4`）も非準拠だが、常に検査範囲の起点として除外され PR レンジに載らないため対象外。
- 両コミットは規約チェック（`ci.yml` の `check-commit-messages` 手順）が有効化された初期化コミット #1
  （2026-07-08）より後（2026-07-09）に、`(#NN)` を持たない = develop へ直接 push されたもの。よって
  旧エントリの `category=A`（規約導入前）ではなく **`category=B`（規約導入後・書き換え不可）** が正しい。

## 対象範囲

- `scripts/commit-allowlist.json`: 幻 SHA 9 件を削除し、上記 2 件の完全 SHA へ整理。category を A→B へ是正し、
  各 `reason` に「develop 直接 push・force push 禁止で書き換え不可・除外の人間レビューは本 PR で承認」を明記。
  `_note` に「追跡可能性のため現行 develop 実在の完全 SHA を明記」旨を追記。
- `scripts/scripts.test.js`: `findAllowlisted` 系テストの合成 SHA を実在値へ更新。加えて、tautology（ファイルが
  書いた文字列を含むことの確認）を解消し、**各エントリが実在・develop から到達可能・かつ規約違反件名である**ことを
  git で best-effort 検証する回帰テストを追加（フル履歴では幻 SHA を検出して失敗、浅いクローンではスキップ）。
  到達可能性の基準は `origin/develop → develop → HEAD` の順で解決する。
- `.github/workflows/ci.yml`: 上記回帰テストを CI で実際に走らせるため、`commit-messages` ジョブ
  （既に `fetch-depth: 0`）に `node scripts/scripts.test.js` の実行ステップを追加（幻 SHA 再発を CI で検出可能にする）。

## 受け入れ基準

- [x] `commit-allowlist.json` の全エントリが現行 develop に実在する完全 SHA（`git cat-file -t` で commit・到達可能）
- [x] `node scripts/scripts.test.js` が全緑（28 tests）。幻 SHA を注入するとフル履歴で失敗することを確認
- [x] 上記テストが CI（`ci.yml` の `commit-messages` ジョブ・`fetch-depth: 0`）で実行される
- [x] `node scripts/check-commit-messages.js --range <root>..develop` が 2 件を allowlist で除外し違反 0
- [x] `commit-allowlist.json` が有効な JSON
- [x] scripts/ ・ docs/ に旧幻 SHA（`d1652dcf` 等・旧 SHA `afe2a66`/`10e8c8a`/`a736cbb`）の残存参照が無い

## 計画書との差異

- 差異なし（計画書に紐づかないリポ整備）。
