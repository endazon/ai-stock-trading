---
title: IADR-0164 の欠番解消（0165→0164・0166→0165 の繰り下げ・索引整合）
type: spec
status: review
related_ids:
  - IADR-0164
  - IADR-0165
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs: []
---

# 仕様書: IADR-0164 の欠番解消

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（実装リポジトリ内の文書整合の是正。計画書起点を持たない housekeeping）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: 本作業で採番し直す 2 件（**決定内容は一切変更しない**）

  | 旧 | 新 | 主題 | 由来 |
  | --- | --- | --- | --- |
  | IADR-0165 | **IADR-0164** | Stage 1 の最小取引件数の設定化・市場監視パラメータの SC-02 移管 | [#423](https://github.com/endazon/ai-stock-trading/issues/423) / PR [#438](https://github.com/endazon/ai-stock-trading/pull/438) |
  | IADR-0166 | **IADR-0165** | GFV 発生回数の自前計数・決済済み資金の代替値の遮断 | [#425](https://github.com/endazon/ai-stock-trading/issues/425) / PR [#435](https://github.com/endazon/ai-stock-trading/pull/435) |

- 先行作業: [IADR-0060 の欠番解消](20260717_iadr-0060-gap-renumber.md)（同型の作業。手順・取りこぼしの罠を踏襲する）
- 計画書リンク: なし

## 目的・背景

`docs/adr/README.md` の運用ルールは「連番はリポジトリ内で一意・昇順・**欠番なし**」を定める。
現状の `develop`（`372477a`）はこれを満たしていない。

- 実在: `IADR-0000`〜`IADR-0163` が連続 → **`IADR-0164` が欠番** → `IADR-0165`・`IADR-0166`
- 重複: なし
- 索引の行の過不足: なし

**欠番が生じた経緯**: 2026-08-07 の並行実装で、レーン A に `IADR-0163`〜`IADR-0164` を予約したが
**A は `0163` しか使わずに終わった**。その間に PR #436（後に #438 へ差し替え）が `0165` を、
PR #435 が `0166` を確保しており、`0164` が空いたまま両者が先行した。

**なぜ両 PR の中で改番しなかったか**（本作業を別 PR に切り出した理由）:

改番はファイル名・本文・索引・仕様書に加えて**コミット件名の起点 ID**まで追随させる必要がある
（`.claude/rules/traceability.md` §採番衝突時の改番手順）。しかし:

1. **プッシュ済みの中間コミットの件名は force push 禁止（CLAUDE.md）のため書き換えられない。**
   実測でも、PR #435 で `IADR-0166` → `IADR-0165` の改番を試したところ
   `scripts/check-commit-messages.js` が「起点 ID "IADR-0166" が `docs/adr/` に実在しない」で
   **exit 1**（＝`ci` の `commit-messages` ジョブが赤）になった。
2. **`scripts/commit-allowlist.json` による除外も採れない。** スカッシュマージ後は当該 SHA が
   統合ブランチから到達不能になり、`scripts.test.js` の「allowlist の各エントリは git 履歴に実在し
   統合ブランチから到達可能（幻 SHA の検出）」が **develop で赤くなる**
   （`REACH_BASE` は `origin/develop` に解決する）。

**本 PR では同じ問題が起きない。** 本 PR のコミット件名が名乗る起点 ID は**改番後の番号**
（`IADR-0164` / `IADR-0165`）であり、マージ時点で両方とも `docs/adr/` に実在するためである。

**新規の意思決定は行わない（＝新規 IADR を作らない）**。既存 2 件の決定内容・状態・設計は
一切変更せず、ID 表記と参照のみを機械的に置換する。

## 対象範囲

### 対象

1. **ファイル名の変更**（`git mv` で履歴を保つ）

   - `docs/adr/IADR-0165_stage1-trade-count-setting-and-monitor-parameter-relocation.md`
     → `docs/adr/IADR-0164_…`
   - `docs/adr/IADR-0166_gfv-self-counting-and-settled-cash-source-ban.md`
     → `docs/adr/IADR-0165_…`

2. **参照の追随**（実測: `IADR-0165` が 50 ファイル 98 箇所、`IADR-0166` が 40 ファイル 77 箇所）

   - 各 ADR の frontmatter `title`・見出し・本文の自己参照・相互リンク
   - 索引 `docs/adr/README.md` の行
   - 仕様書（`docs/specs/` / `docs/functional/` / `docs/screens/` / `docs/tests/`）の
     `related_ids` と本文・リンク定義（`[IADR-0165]: …`）
   - **コード内コメントの起点 ID**（`backend/**/*.cs`）
   - `docs/blocked-tasks.md`・`feedback/*.md`
   - `scripts/`（`check-banned-settled-cash-sources.js` の理由文言・`scripts/README.md`）
   - `.github/workflows/ci.yml`（`banned-settled-cash-sources` ジョブのコメント）

3. **索引 `docs/adr/README.md` の整合**

   - 行を昇順（…0163 → 0164 → 0165）に並べる
   - **「注（`IADR-0164` の暫定欠番と解消予定）」を解消済みの記述へ書き換える**
     （削除しない。先例が「経緯を残す」形を採っており、同じ事故の再発防止に効く）

### 対象外

- **決定内容・状態（`Accepted`）・設計の変更** —— ID 表記だけを動かす
- **`IADR-0163` 以下の既存の連続部分** —— 不変
- **コミット履歴・PR タイトルの遡及修正** —— force push 禁止。develop に載った
  `feat(FR-19,IADR-0166): …(#435)` / `feat(SC-01,SC-02,FR-20,IADR-0165): …(#438)` の 2 件は
  **旧番号を名乗ったまま残る**（後述「残余リスク」）
- 計画 ADR（`ADR-xxxx`）—— 別の採番空間であり触らない
- **上流 microservices-platform の IADR**（`IADR-0046` / `IADR-0048` 等を無修飾で参照している
  箇所がある。索引の「注（他リポジトリの IADR との区別）」が警告しているとおり、
  **本作業の置換範囲は 0165 / 0166 のみ**なので巻き込まない）

## 設計

### 置換の順序（衝突を避ける）

**`0165` → `0164` を全件行ったあとに、`0166` → `0165` を行う。** この順なら、第 1 段の完了時点で
`IADR-0165` の文字列がツリーから消えているため、第 2 段が第 1 段の結果を二重に書き換えることはない。
逆順（先に `0166`→`0165`）にすると、第 2 段の `0165`→`0164` が第 1 段の結果まで巻き込む。

### 置換から除外するファイル

次の 2 箇所は**旧番号を意図的に含む**ため、機械的置換の対象から外し、人手で書く。

| 箇所 | 理由 |
| --- | --- |
| 本仕様書（`docs/specs/20260807_iadr-0164-gap-renumber.md`） | 「旧 → 新」の対応表そのものが旧番号を含む。置換すると `0164 → 0164` という無意味な表になる |
| `docs/adr/README.md` の「注（`IADR-0164` の暫定欠番と解消予定）」 | 同様に「`0165`→`0164`・`0166`→`0165`」という記述を含む。置換すると経緯が読めなくなる |

### 先例が報告した取りこぼしの罠

[IADR-0060 の欠番解消](20260717_iadr-0060-gap-renumber.md) は **複合表記（`IADR-00xx/00yy` の
後半が素の番号）** を取りこぼしたと記録している。本作業では着手前に実測し、
**該当が 0 件であることを確認した**（`IADR-016[456]\s*[/／]\s*0?[0-9]{3,4}` で走査）。
それでも検証時に素の `0165` / `0166` の残存を再走査する。

## 受け入れ基準

- [ ] `docs/adr/` のファイルが `IADR-0164_…` / `IADR-0165_…` になっている
- [ ] ツリー全体に `IADR-0166` の参照が **0 件**である
- [ ] `IADR-0164` / `IADR-0165` の参照数が、改番前の `IADR-0165`（98）/ `IADR-0166`（77）と一致する
      —— **取りこぼしも過剰置換も無いこと**を数で押さえる
- [ ] 索引 `docs/adr/README.md` が `IADR-0000`〜`IADR-0165` で**一意・昇順・欠番なし**である
- [ ] 索引の「注（`IADR-0164` の暫定欠番）」が**解消済みの記述**になっている
- [ ] 2 件の ADR の**決定内容・状態が変わっていない**（`git diff` が ID 表記の行だけであること）
- [ ] `node scripts/check-doc-links.js` が緑（リンク切れなし）
- [ ] `node scripts/check-commit-messages.js --range origin/develop..HEAD` が緑
      （本 PR の件名が名乗る `IADR-0164` / `IADR-0165` が実在すること）
- [ ] `node scripts/check-banned-settled-cash-sources.js` が緑（#435 の統制が壊れていないこと）
- [ ] `node scripts/check-test-traceability.js` が緑
- [ ] `dotnet build` / `dotnet test` が緑（コメントのみの変更だが、コード内コメントを触るため）
- [ ] frontend の `typecheck` / `lint` / `test` が緑

## テスト方針

**新規テストは書かない。** 本作業は ID 表記の置換であり、振る舞いを変えない。
既存の CI ゲート（`check-doc-links` / `check-commit-messages` / `check-test-traceability` /
`banned-settled-cash-sources` / `build-and-test`）が退行検知を担う。

**検証は「数で押さえる」**（受け入れ基準の 3 番目）。置換前後で参照数が保存されることを確認すれば、
取りこぼし（数が減る）と過剰置換（数が増える）の両方を同時に検出できる。

## 残余リスク

1. **develop の 2 件のコミット件名は旧番号を名乗ったまま残る。**
   `feat(FR-19,IADR-0166): …(#435)` と `feat(SC-01,SC-02,FR-20,IADR-0165): …(#438)` である。
   force push 禁止のため修正できない。**`check-commit-messages.js` は `base..HEAD` しか検査しない**
   ため CI は赤くならないが、**`CHANGELOG.md` には旧番号が載る**。
   是正が必要なら `scripts/changelog-overrides.json`（`action: "remap"`）で生成物のみ直す経路がある
   （`.claude/rules/traceability.md` §PR タイトル（スカッシュ後件名）の検査 の「事後補正」）。
   **本 PR では行わない**（CHANGELOG の生成は別の関心であり、混ぜると本 PR の差分が読めなくなる）。
2. **並行して未マージのブランチが `IADR-0165` / `IADR-0166` を参照している場合、
   マージ時にコンフリクトする。** 着手時点で未マージの PR は無いことを確認する。
3. **同じ事故（予約した番号が使われず欠番になる）は再発し得る。**
   索引の注記を残すことで次の採番者への警告とするが、機械的な検知は無い。
   バックログ監査（[#439](https://github.com/endazon/ai-stock-trading/issues/439)）の
   「採番の健全性」に IADR 連番の欠番・重複の確認を含めた。
