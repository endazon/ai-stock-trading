---
title: catalogRegistration.test.ts の母集合テストを cwd 非依存にし、基盤の合成 test:coverage でも通るようにする
type: spec
status: done
related_ids: [SC-01, SC-02, SC-03, SC-04, IADR-0340, IADR-0338]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
---

# 仕様書: `catalogRegistration.test.ts` の cwd 依存の除去

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（テストの実行環境の是正。画面の挙動は変えない）
- 非機能要件（NFR）: 無採番（テスト基盤のメタ作業。`.claude/rules/traceability.md` §起点 ID の種別 の場合 2。環流しない）
- ユースケース（UC）: なし
- 画面（SC）: `SC-01` / `SC-02` / `SC-03` / `SC-04`（テストが母集合として読む 4 画面の route factory）
- 関連 ADR: `IADR-0340`（文言カタログの遅延登録。本テストはその不変条件の固定）/ `IADR-0338`（合成前提）/
  `MSP/IADR-0120`（基盤は本リポジトリを変更しない＝是正は本リポジトリ側で行う）

## 目的・背景

#793（IADR-0340）で足した `frontend/src/features/catalogRegistration.test.ts` の「母集合が一致する」テストは、
route factory の実ソースを `resolve(process.cwd(), 'src/features/…')` で読む。単独実行（cwd = `frontend/`）では
通るが、**基盤（microservices-platform）の合成 `test:coverage` は cwd = `src/`（pnpm workspace の root）で本リポジトリの
テストを横断実行する**ため、`src/src/features/…` を開こうとして ENOENT で落ちる（基盤で submodule を `c5cd0de` へ
進めた bump 作業の実測。1 failed / 1623 passed）。基盤からは直せない（`MSP/IADR-0120`）ので本リポジトリで是正する。

## 対象範囲

- 対象: `frontend/src/features/catalogRegistration.test.ts`（母集合テストのパス解決のみ）
- 対象外: テストの検証内容（4 本の完全一致・正規表現・陰性の読み方）、`lib/i18n.ts`、画面、
  `.ai-context/specs/20260912_792_unit-catalog-lazy-registration.md` の本文（追記のみ）

## 設計

- `import.meta.url` は jsdom 環境で `http://localhost/…` になりファイルを指さない（#793 の実測。据え置き）。
- **本ファイル自身の絶対パス**を vitest の `expect.getState().testPath` から取り、`dirname(testPath)` を基点に
  `${dir}/routes/${file}.tsx` を解決する。cwd がどこでも同じ場所を指す。`testPath` が空なら明示的に落とす
  （黙って 0 件へ落ちない）。
- 母集合（規則 1〜10）: 「`process.cwd()` から解決する」記述を `process.cwd`・`cwd` で本リポジトリの
  追跡下ファイル全体（`git grep`）から引いた → 該当は本テストのみ（他の `process.cwd()` 利用は
  `scripts/` の検査器で、リポジトリ root を前提に設計されたもの。対象外）。#793 の作業仕様書・IADR-0340 に
  cwd 前提の記述は無い（除外なし）。

## 受け入れ基準

- [x] 単独: `npm run test`（`frontend/`）で 404 件緑（本テスト 7 件を含む）
- [x] 合成: 基盤の `src/` から `pnpm run test:coverage` を走らせて本テストが緑（ENOENT が消える）
- [x] 変異試験: 解決の基点を `process.cwd()` へ戻すと合成側で再び ENOENT になる（是正前の実測がそれである）
- [x] typecheck / lint / 文書検査 OK

## テスト方針

変更対象がテストそのものである。単独と合成の 2 環境で走らせることが検証である。

## 計画書との差異

- 差異: なし

## 未決事項

- なし
