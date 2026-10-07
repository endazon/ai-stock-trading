---
title: テストが参照する FR/UC/SC の実在検査を、撤去済み submodule ではなく宣言レンジで行う
type: spec
status: accepted
related_ids: [NFR, ADR-0029, IADR-0504]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0029_impl-docs-restructure.md (決定 2: planning 非依存。レンジは本リポの追跡ファイルに宣言する)
---

# 仕様書: テストが参照する FR/UC/SC の実在検査を、撤去済み submodule ではなく宣言レンジで行う（#1235）

## 起点となる計画書（トレーサビリティ）

- 起点: 無採番 NFR（メタ作業。トレーサビリティ検査の実効性）
- 関連 ADR: 計画 ADR-0029 決定 2（planning submodule の撤去）／IADR-0504（宣言不読は fail-loud）
- 起票: #1235（#1234〔#1233〕の独立監査の 🟡 を範囲外として切り出したもの）

## 目的・背景

`scripts/check-test-traceability.js` の本走の検査 3（テストが参照する FR/UC/SC が計画書に実在すること）は、
`planIds(REPO_ROOT)` が planning submodule を走査して実在集合を作る。submodule は ADR-0029 決定 2 で撤去済みのため
`planIds` は常に `null` を返し、検査 3 は CI・ローカルとも**恒久的に notice だけを出して skip** していた。
テストに `FR-99` と書いても、どの検査にも掛からない。

同じファイルには `readPlanIds()`（`.claude/rules/traceability.repo.md` の宣言レンジから実在集合を作る拡張点）が
既にあり、`check-commit-messages.js` はそちらを使っている。本走だけが旧経路のまま残っていた。

## 着手前の実測（既存テストの参照 ID）

長期間未検査だったため、修正前に実ツリーの参照を宣言レンジへ突き合わせた。

```
node -e '…tt.collectReferences(tt.testFiles(root), root) を new Set(tt.readPlanIds()) と突合…'
files 953 refs 31 declared 32
（MISSING 行なし）
```

- **宣言レンジに無い ID を参照しているテストは 0 件。** 誤記の是正も baseline も不要。
  baseline の仕組み（T2 の `test-id-duplicate-baseline.json`）は本検査には持ち込まない（違反が 0 件のため、
  抜け道だけを作ることになる）。

## 母集合（規則 9・10。着手前に引いた）

`git grep -n -E "require-planning|実在検査.{0,20}skip|未 populate|doc-links-planning" -- . ':!.ai-context/specs' ':!.ai-context/adr' ':!CHANGELOG.md'`

| 箇所 | 記述 | 扱い |
| --- | --- | --- |
| `scripts/check-test-traceability.js` ヘッダ（検査 3・使い方） | 「未 populate では skip」「`--require-planning` は恒久的に exit 1」 | 書き換える |
| `.github/workflows/ci.yml` `Check test traceability` 直前のコメント | 「恒久的に skip へ倒れる」 | 書き換える（起動条件・必須チェックは不変。`run:` 行は変えない） |
| `scripts/README.md` コラム（#712） | 「恒久的に skip」「恒久的に exit 1」 | 日付つき変更注記を追記（経緯の段落は残す） |
| `scripts/README.md` `test-traceability` 行 | 「`--require-planning` は恒久的に exit 1 で使えない」 | 書き換える |
| `docs/tests/README.md` §CI が強制する 3 | 「PR CI では skip し、夜間の `doc-links-planning` が担う」（同ジョブは既に存在しない） | 書き換える。trace ブロックへ本仕様書と #1235 を足す |
| `scripts/README.md` 133 行「`--require-planning` と併用可」 | 事実として正しい（受理する） | 変更なし |
| `scripts/README.md` 155 行（fail-closed フラグの規約） | 一般規約 | 変更なし |
| `scripts/check-doc-links.js` / `ci.yml` 188 行 | 別検査器の撤去済み分岐の記録 | 対象外 |
| `scripts/scripts.test.js` 592 / 996 行・`docs/traceability-appendix.md` 104 行 | 計画 ADR 実在性（別検査器）の skip | 対象外（#1235 の射程外） |

- この変更で新たに誤りになる自分の記述: 既存テスト名「planning 未 populate なら planIds は null（実在検査を skip する合図）」
  を「宣言レンジへ切り替える合図」へ改名した。模擬ツリー（`TEST_TRACE_ROOT`）で本走を起こす既存の T1 テスト 5 件は、
  宣言ファイルが無いと新たに fail-loud で落ちるため、共通ヘルパ `mkTraceabilityFixture` に宣言を書かせた。

## 対象範囲

- 対象:
  - `scripts/check-test-traceability.js` `main()` 検査 3: `planIds()` が `null` なら `new Set(readPlanIds(<root>/RULES_FILE))`
    を実在集合に使う。宣言が読めなければ理由を出して exit 1。OK 行と違反行に実在集合の出典を出す
  - `scripts/scripts.repo.test.js`: 陽性対照・正の確認・否定形（下記）
  - 上表の文書 4 箇所
- 対象外:
  - `planIds()` と `--require-planning` の撤去（#1235 の選択肢 2。別 PR）
  - IADR の新設（IADR-0504 の fail-loud 契約を既存の拡張点へ配線するだけで、新しい判断を持たない）

## 設計

1. **実在集合の出典**: `planIds(root)` が非 `null`（submodule が populate された環境。現実には存在しない）なら旧経路、
   `null` なら宣言レンジ。宣言は `REPO_ROOT` 配下の `.claude/rules/traceability.repo.md` を読む
   （`TEST_TRACE_ROOT` の模擬ツリーでは模擬ツリー側の宣言を読む＝試験で範囲を制御できる）。
2. **fail-loud**: `readPlanIds()` の例外（ファイル不在・節不在・書式崩れ・同種トークン重複）は捕まえて理由を出し exit 1。
   skip へは倒さない。
3. **`--require-planning`**: 「実在検査の skip を許さない」フラグであり、検査 3 が常に走る今は**満たされた状態**。
   受理だけを残し、付けても付けなくても同じ結果にする（未知引数として落とすと既存の呼び出しが壊れ、
   旧来どおり exit 1 にすると「宣言が読めるのに落ちる」誤った赤になる）。CI は従来どおりフラグ無し。

## 受け入れ基準

1. 宣言レンジに無い ID（`FR-99` / `UC-08` / `SC-05`）を参照する模擬ツリーで exit 1、各 ID の違反行が出る
2. 模擬ツリーの宣言（`FR-01..20`。実ファイルと違う値）の上端の 1 つ上（`FR-21`）で exit 1（模擬ツリーの宣言が読まれている）
3. 宣言レンジ内の ID だけなら exit 0、OK 行に実在集合の出典と件数が出る
4. 宣言ファイルが無い／書式が崩れていれば exit 1（skip しない）
5. `--require-planning` は宣言が読めれば exit 0
6. 変異確認: 検査 3 の fallback を「何も落とさない」へ戻す、または develop の版へ戻すと 1〜5 の試験が落ちる
7. 実ツリーで `node scripts/check-test-traceability.js` が緑（既存参照の違反 0 件）

## 検証結果

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 総数 507 / 成功 507 / 失敗 0
- 変異確認: fallback を参照集合そのものへ差し替え → 5 件失敗／`check-test-traceability.js` を develop の版へ戻す → 6 件失敗
- `node scripts/check-test-traceability.js`: OK（テスト 953 ファイル・起点 ID 31 種。実在集合: 宣言レンジ・32 件）
