---
title: 作業仕様書 — 秘密の検出の規則に API キーの形を 1 つ足す（#1540）
type: spec
status: done
related_ids: [NFR]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#1540"
---

# 作業仕様書 — 秘密の検出の規則に API キーの形を 1 つ足す（#1540）

> 本仕様書は実装着手前に作成した（着手 2026-11-05。develop `18524551b`）。

## 目的

`.claude/hooks/guard-secrets.js` のブロックする形の一覧に、API キーの形（`sk-ant-<種別><2 桁>-` で始まる値）を 1 つ足す。既存の `sk-[A-Za-z0-9]{20,}` は `sk-` の直後に `-` を含む形に掛からない。

## 母集合

- 秘密の検出の規則を持つのは `.claude/hooks/guard-secrets.js` の `BLOCK` / `WARN` だけである（`rg -n "BLOCK = \[" .claude scripts`）。

## 受け入れ基準と試験（`scripts/scripts.repo.test.js`）

1. `api03` / `admin01` / `oat01` の種別の値を書き込むと exit 2（ブロック）になり、理由に規則の名前が出る。
2. 接頭辞への言及・短い値・語の途中は exit 0（誤検知しない）。例示・文書のパスは従来どおり警告に緩める。
3. 見張り値は実行時に連結で組み、試験のファイル自体が検出に掛からない。

先に試験だけを当てて 1 が赤（exit 0）になることを確かめてから、規則を足した。

## 対象外

- 他の検出の規則の見直し。
