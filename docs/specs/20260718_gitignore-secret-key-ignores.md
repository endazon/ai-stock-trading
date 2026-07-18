---
title: .gitignore に秘密鍵ファイルの汎用除外を追加する（多層防御）
type: spec
status: review
related_ids: [P3]
author: endazon
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - "GitHub issue: endazon/ai-stock-trading#151"
  - "セキュリティ監査環流: endazon/project-planning#27"
---

# 仕様書: .gitignore への秘密鍵ファイル汎用除外の追加（#151）

> 3リポジトリ横断セキュリティ監査で検出した予防的ハードニング（severity: low）。
> 設計上の決定は伴わない純粋な chore のため IADR は起こさない。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（保守・ハードニング）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: [IADR-0053](../adr/IADR-0053_moomoo-opend-dockerization.md)（moomoo OpenD 用 RSA 秘密鍵運用。status: Proposed の**参考リンク**であり確定制約ではない）
- 計画書リンク: endazon/ai-stock-trading#151 / endazon/project-planning#27

## 目的・背景

`.gitignore` は `*.pfx` / `*.publishsettings` を除外する一方、`*.pem` / `*.key` / `id_rsa*` 等の
秘密鍵ファイルを汎用的に除外していなかった。本リポジトリは moomoo OpenD 用 RSA 秘密鍵
（`opend_rsa.pem` 等）を作業ディレクトリに置く運用があり、`.claude/hooks/guard-secrets.js` や
CI gitleaks を経由しない手動 `git add` に対する保険がなかった。多層防御として汎用除外を追加する。

## 対象範囲

- 対象: `.gitignore`（「秘密情報・ローカル設定」節への追記）。
- 対象外: 既存の除外パターン、hooks・gitleaks の内容ベース検知（別レイヤ・不変）。

## 設計

- 追加パターン: `*.pem` / `*.key` / `*.p12` / `id_rsa*` / `id_ed25519*`。
- `.example` 系テンプレート（例: `rsa-secret.example.yaml`）は対象拡張子に一致せず影響なし。
- 公開鍵（`*.pub`）・証明書（`*.crt` / `*.cer`）は秘密情報でないため除外対象外。
- `id_rsa*` / `id_ed25519*` は前方一致のため公開鍵 `id_rsa.pub` / `id_ed25519.pub` にも一致してしまう。
  コメントの意図（公開鍵は除外しない）と glob 挙動を一致させるため、直後に `!*.pub` を置いて
  公開鍵の追跡を明示的に許可する。
- 追加パターンに一致する追跡済みファイルは存在しないため、`!` による既存ファイルの個別復帰は不要
  （`!*.pub` は将来の公開鍵コミットに対する予防的許可）。

## 受け入れ基準

- [x] `*.pem` / `*.key` / `id_rsa*` 等の秘密鍵パターンを追加した。
- [x] `.example` 系テンプレートを誤除外しないことを確認した。
- [x] 既に追跡されている該当ファイルの有無を確認した（`git ls-files | git check-ignore --stdin` で 0 件）。

## テスト方針

- `.gitignore` 変更のため自動テストは追加しない。`git check-ignore -v` で
  実鍵名が除外され `.example` テンプレートが除外されないことを確認する。

## 計画書との差異

- 差異: なし。

## 未決事項

- なし。
