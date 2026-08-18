---
title: IADR-0206 キット pin 179a69a の追随 — check-cross-repo-refs.js の置換点をファイル内で埋め A → B 5 へ移す
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0200, IADR-0203, IADR-0205]
author: claude (Claude Code)
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - ../../planning/tools/impl-handoff-kit/repo-template/scripts/check-cross-repo-refs.js
  - ../../planning/tools/impl-handoff-kit/repo-template/scripts/scripts.test.js
---

# IADR-0206: キット pin 179a69a の追随 — check-cross-repo-refs.js の置換点をファイル内で埋め A → B 5 へ移す

- 状態: Accepted
- 日付: 2026-08-18
- 決定者: claude（起票 #530。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: NFR（無採番。キット追随のメタ作業）
- 関連する実装仕様書: [`docs/specs/20260818_530_pin-179a69a-catchup.md`](../specs/20260818_530_pin-179a69a-catchup.md)

## コンテキストと課題

`check-cross-repo-refs.js` は IADR-0203 で分類 A（キットとバイト一致）へ移し、置換点（`CROSS_REPOS` / `SELF_NAMES` / `EXCLUDE_PATHSPECS` 等）は **env 注入**（`scripts.repo.test.js` が与える。IADR-0200 決定 5）でバイト一致を温存していた。

pin `179a69a` のキット版 `scripts.test.js`（分類 A・差し替え必須）は、**実データの本走を env なしの素実行**で行う。素実行ではキット既定の除外（`:!planning` のみ）が使われ、本リポの point-in-time 記録（`docs/specs/` / `feedback/`。裁定 2026-08-15 で後付けの表記是正をしない）が走査に入り、**是正できない違反 100 件超で恒久的に赤くなる**。env 注入方式はこの構造では成立しない。

## 検討した選択肢

1. **置換点をファイル内で埋め、A → B 5 へ移す**（採用）
2. env 注入を維持し、キット版 `scripts.test.js` の素実行テストを迂回する — `scripts.test.js` は分類 A であり編集できない。**却下**
3. `docs/specs/` / `feedback/` の違反を是正して素実行を緑にする — point-in-time 記録の凍結（裁定 2026-08-15）に反する。**却下**

## 決定

1. `check-cross-repo-refs.js` の置換点をファイル内で埋める: `CROSS_REPOS`＝`project-planning:planning` / `microservices-platform:MSP`、`SELF_NAMES`＝`AST` / `ai-stock-trading`、`EXCLUDE_PATHSPECS`＝`:!planning` / `:!docs/specs` / `:!feedback`、`KNOWN_OWNERS`＝`endazon`（型 4〔owner 誤り〕の検査が新たに実効する）。
2. 分類を **A → B 5**（キットが選択・追記を委ねている欄＝宣言された【置換点】を埋めた）へ移す。**IADR-0203 の A 化判断を本決定で上書きする**（当時は置換点を埋めない選択が可能だったが、キット側のテスト構造が変わった）。
3. env（`CROSS_REPO_*`）による上書きは引き続き有効（キット実装のまま）。`scripts.repo.test.js` の env 経由テストも従来どおり効く。
4. 走査範囲の拡大（追跡下の全ファイル）で新たに検出された live ファイルの違反 41 件（ワークフロー・helm・バックエンドのコード内コメント・frontend e2e）は是正した。`docs/specs/` / `feedback/` は凍結のまま除外する。

## 理由

キット自身が「配布時に同スクリプト冒頭の置換点を必ず書き換える」を設計として明記しており、ファイル内充填が配布物の想定形である。環流（planning#379 等）でキット版が本リポ版と機能同等以上になった現在、バイト一致に固執して env 注入を維持する利得（キット是正の自動検出）は、B 5 でも `check-kit-sync.js` の被覆検査と pin 追随 issue の運用で担保できる。

## 残余リスク

- B 5 のため、キット側が同ファイルを改修した場合の追随は機械（バイト一致）でなく pin 追随作業の diff 読みに依る（B 分類全般と同じ）。
- `KNOWN_OWNERS` の充填により型 4 が実効し、既存記録の owner 誤りが今後の編集で検出され得る（検出時に個別判断）。
