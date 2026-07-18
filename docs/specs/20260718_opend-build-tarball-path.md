---
title: opend-build.sh の tarball 既定パスをリポジトリ相対に是正する
type: spec
status: draft
related_ids: []
author: endazon
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - "GitHub issue: endazon/ai-stock-trading#150"
  - "セキュリティ監査環流: endazon/project-planning#27"
---

# 仕様書: opend-build.sh の tarball 既定パスのポータビリティ是正（#150）

> 3リポジトリ横断セキュリティ監査（個人情報・環境情報の混入調査）で検出した軽微事項（severity: low）の是正。
> 設計上の決定は伴わない純粋な chore のため IADR は起こさない。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（保守・ハードニング）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: [IADR-0053](../adr/IADR-0053_moomoo-opend-dockerization.md)（OpenD Docker 化。tarball パスに関する制約記載なし）
- 計画書リンク: endazon/ai-stock-trading#150 / endazon/project-planning#27

## 目的・背景

`scripts/opend-build.sh` の tarball 既定探索パスに、特定の開発者 PC のディレクトリ構成
`/c/10_SourceCode/references/` がハードコードされていた。秘密情報ではないが、開発者ローカル環境の
構成がリポジトリに露出し、他環境ではフォールバックが必ず失敗してポータビリティを損なう。

## 対象範囲

- 対象: `scripts/opend-build.sh` の既定探索パス、`deploy/opend/README.md` の該当説明。
- 対象外: `OPEND_TARBALL_PATH` env・第1引数による明示指定の挙動（従来どおり優先・不変）。

## 設計

- 既定探索先を絶対パス `/c/10_SourceCode/references/` から、リポジトリルート基準の相対
  `"$ROOT"/../references/`（`$ROOT="$(cd "$(dirname "$0")/.." && pwd)"`）へ置換する。
- これによりリポジトリ隣接の `references/` を参照し、開発者固有の絶対パスに依存しない。
- 明示指定（`SRC="${1:-${OPEND_TARBALL_PATH:-}}"`）の優先順位は変更しない（後方互換）。

## 受け入れ基準

- [x] 開発者ローカル絶対パスのハードコードを除去した。
- [x] 環境変数（未設定時はリポジトリ相対の妥当な既定）で解決する形にした。
- [x] `deploy/opend/README.md` の該当記述も更新した。
- [x] `bash -n` 構文チェック通過・リポ内に当該絶対パスの残存なし。

## テスト方針

- ビルドスクリプト（シェル）のため自動テストは追加しない。`bash -n` による構文検証と、
  リポジトリ全体の `git grep '/c/10_SourceCode/references'` で残存ゼロを確認する。

## 計画書との差異

- 差異: なし。

## 未決事項

- なし。
