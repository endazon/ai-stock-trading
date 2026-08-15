---
title: キット追随（planning#342 の裁定反映）と、traceability.md の分類矛盾の解消 — 除外を 1 件外す
type: spec
status: approved
related_ids: [NFR, IADR-0189, IADR-0191, IADR-0200, IADR-0202]
author: endazon (with Claude Code)
created: 2026-08-15
updated: 2026-08-15
---

# 仕様書: キット追随と、分類矛盾の解消（#517）

> 本仕様書は実装着手前に作成する。

## 起点

- 起点 issue: [#517](https://github.com/endazon/ai-stock-trading/issues/517)
- 起点 ID: **NFR**（無採番。工程の統制であり計画側の非機能要件表に当たる番号が無い／環流しない。`.claude/rules/traceability.md` の 2 番）
- 計画側の反映: [planning#342](https://github.com/endazon/project-planning/pull/342)（キットへの環流 6 件の裁定・反映）
- 実測時点: `develop` = `9c0eb7c` / 計画 `ce9abd2`（本リポ pin は `2bd984c`）

## 課題

### 課題1: 分類 A のドリフト 2 件

`node scripts/check-kit-sync.js` の生出力（**加工しない**。規則 7）:

```
[check-kit-sync] 追随の違反 2 件を検出しました:
    [drift] scripts/check-feedback-status-sync.js が分類 A なのにキットとバイト一致でない。…
    [drift] scripts/setup.sh が分類 A なのにキットとバイト一致でない。…
```

| ファイル | 差分 | キット側の変更内容 |
| --- | --- | --- |
| `scripts/check-feedback-status-sync.js` | 71 行 | `--require-planning`（**参照できないとき skip ではなく fail**）の新設と、**未知の引数を黙って無視せず落とす**（planning#343）。自己テストも同時に増えている |
| `scripts/setup.sh` | 3 行 | `PIPESTATUS` を使ってよい理由の注記（**シバンが bash を要求している**ため。従前は「実行シェルを選べないため使わない」と書いていた） |

いずれも**本リポが遅れている側**である（`check-kit-sync.js` の分類 B に記録した「本リポが進んでいる」型ではない）。

### 課題2: 🔴 `.claude/rules/traceability.md` の分類が、ファイル自身の要求と矛盾している

| 出典 | 何を言っているか | 含意する分類 |
| --- | --- | --- |
| `scripts/kit-sync-classification.json` の `classes.C` | 「本リポの中身そのもの（雛形から書き起こした実体、または**置換点を持つ配布物**）。**同期しない**」 | **C** |
| `.claude/rules/traceability.md` の冒頭（**当のファイル自身**） | 「本ファイルはキットの配布物である。直接編集しないこと」「**直接編集するとバイト一致が崩れ**、キットを同期するたびに手動マージが要る」 | **A** |

**置換点は無い。** 実測（`grep -n '置換点' .claude/rules/traceability.md` → 2 行）で当たったのは
**`check-cross-repo-refs.js` / `check-plan-id-qualification.js` の置換点を説明している本文**であり、
**このファイル自身が埋める欄を持っているわけではない**。固有規約は companion
（`traceability.repo.md`）へ分離済みである（[IADR-0200](../adr/IADR-0200_cross-repo-ref-notation.md)）。

> 🔴 **分類 C は「同期しない」であるため、`check-kit-sync.js` は何も言わない。**
> 結果として**本リポの写しだけが古いまま固定され**、それを検出する機械が無かった。
> **「緑だが検査されていない」の再発である**（[IADR-0191](../adr/IADR-0191_kit-sync-classification.md) が
> 分類表を作った動機そのものと同型）。

**取りこぼしていたものを実測した（2 件）。**

1. **`planning issue #202`（空白区切り）が本リポの写しにだけ残っていた。** キット側は planning#349 の
   環流を受けて **`planning#202` へ是正済み**である。
   この 1 件が、[IADR-0200](../adr/IADR-0200_cross-repo-ref-notation.md) 決定3 で**検査の除外を 1 件抱える**理由だった。
2. **母集合の取り方「規則 8」が丸ごと欠けていた**（planning#350 の環流。「走査対象に自分の記録が
   入るときは、記録を書く行為が母集合を動かす」）。

### 課題3: 除外を外せる条件が満たされた

IADR-0200 の残余リスクは明記していた —— 「**キット配布物の除外は暫定である。planning#349 が
是正されたら除外を外すこと（外し忘れると、キットが直った後も検査が 1 件甘いままになる）**」。
**是正は入った。** 外し忘れないのが本 PR の役目である。

## 決定と方針

| # | 決定 |
| --- | --- |
| 1 | 計画 pin を `2bd984c` → `ce9abd2` へ進める |
| 2 | 分類 A のドリフト 2 件を**キット原文で上書き**する（固有デルタの主張はしない） |
| 3 | 🔴 **`.claude/rules/traceability.md` を分類 C → A へ移す**（矛盾の解消）。同時にキット版へ同期する |
| 4 | **除外 `:!.claude/rules/traceability.md` を 3 箇所から外す** |
| 5 | 必読規約の**総量予算（50KB）**内に収まることを確認する |
| 6 | 🔴 **追随した `--require-planning` を `ci.yml` で実際に使う**（下記） |

### 決定6 — 追随したフラグは、その場で使う

`check-feedback-status-sync.js` が得た `--require-planning`（参照できないとき skip ではなく fail）は、
**取り込むだけでは fail-open を閉じない**。`ci.yml` の本検査へ渡す。
**同時に「フラグを持たない」と書いていたジョブのコメントを訂正する**——
従前は「ジョブ側の populate 確認が**唯一の歯止め**」と書いてあり、**もう唯一ではない**。

**populate の明示確認は残す**（フラグは「参照できない」ことしか見ず、
`planning/draft/feedback` が無いという具体的な壊れ方を名指しできない）。**二重に塞ぐ。**

### 決定3 の根拠 — なぜ A なのか

- **置換点を持たない。** C の定義（「置換点を持つ配布物」）に当たらない。
- **雛形から書き起こした実体でもない。** C のもう一方の定義にも当たらない。
- **ファイル自身がバイト一致を要求している。** A の定義そのものである。
- **companion 機構が既にある。** 固有規約の置き場が別にあるため、直接編集する必要が構造的に無い。

### 決定4 の対象（**全数**。規則 6）

| # | 箇所 | 変更 |
| --- | --- | --- |
| 1 | `.claude/rules/traceability.repo.md` の「検査の置換点」コードブロック | `CROSS_REPO_EXCLUDES` から除外を落とす |
| 2 | `.claude/rules/traceability.repo.md` の「除外とその理由」表 | 行を削除し、**外した経緯を残す**（黙って消さない） |
| 3 | `scripts/scripts.repo.test.js` の env | 同上 |
| 4 | `docs/adr/IADR-0200_cross-repo-ref-notation.md` 残余リスク | 「外すこと」→ **外した**（本 PR）へ更新 |

**`scripts/check-commit-messages.js` の直書きは対象外である。** あちらは `.md` のパス除外を持たない
（コミット件名・本文・PR タイトルという**パスの無い面**を検査するため）。実測で確認した。

## 受け入れ基準

- [ ] `node scripts/check-kit-sync.js` が **0 件**（分類 A のドリフトが無い）
- [ ] `.claude/rules/traceability.md` がキット版と**バイト一致**する
- [ ] `kit-sync-classification.json` の `classes.A` に `.claude/rules/traceability.md` が在り、`classes.C` から消えている
- [ ] **除外なし**でクロスリポ検査が通る（`CROSS_REPO_EXCLUDES` に `traceability.md` を含めない）
- [ ] `node scripts/scripts.repo.test.js` が緑
- [ ] 必読規約の総量が **50KB 未満**
- [ ] `node scripts/check-doc-links.js` が緑（IADR-0200 の記述変更に伴うリンク切れが無い）
- [ ] `ci.yml` の本検査が **`--require-planning` 付き**で走り、回帰テストがそれを固定している

### 実測（すべて実走）

| 受け入れ基準 | 実測 |
| --- | --- |
| キット追随 | `A 81 件はバイト一致 / B 9 件 / C 16 件 / 対象外 9 件`（**A 80 → 81・C 17 → 16**） |
| バイト一致 | `cmp` → **identical** |
| 除外なしのクロスリポ検査 | `OK: 293 件の Markdown に…違反はありません` |
| リポテスト | **231 tests passed**（+1。新設の分類テスト） |
| 必読規約の総量 | ~~**45,082 バイト**（`CLAUDE.md` 15,678 ＋ `AGENTS.md` 4,434 ＋ `traceability.md` 20,912 ＋ companion 4,058）。予算 50KB に対し**残り約 10%**~~ 🔴 **【訂正・2026-08-15／[#519](https://github.com/endazon/ai-stock-trading/issues/519)・[IADR-0204](../adr/IADR-0204_reading-budget-mother-set.md)】この値は誤りである。** `AGENTS.md` は「**Claude 以外の AI エージェント**が読み込む共通指示」（同ファイル冒頭）であり、**Claude は読まない**。**異なるエージェントの集合を足していた。** 正しくは **40,648 バイト（81.3%）**（`CLAUDE.md` ＋ `.claude/rules/*.md`） |
| リンク | `OK: 489 件の Markdown に破損した相対リンクはありません` |
| 環流の状態突合 | `OK: 記録 37 件のうち 27 件を計画側と突合` |

> **`traceability.md` は 20,329 → 20,912 バイトへ増えた**（規則 8 の分）。
> **予算の残りは約 5,000 バイト**であり、次にキットが規則を足すと**予算に当たる可能性がある**。

## 対照実験（**実走した実測**。規則 1「誤りの側から引く」）

除外を外してよいことを、**除外を外した状態で違反が出るか／出ないか**の両方向で確かめる。

| # | 操作 | 期待 | 実測 |
| --- | --- | --- | --- |
| A | **同期前**の写し ＋ **除外なし** | 検出されること（除外が実際に 1 件を隠していた） | 🔴 **違反 1 件**（`traceability.md:153 [空白区切りの修飾] planning issue #202 → planning#202`） |
| B | **同期前**の写し ＋ **除外あり**（従来の状態） | 緑（隠れている） | ✅ OK・**292 件**の Markdown |
| C | **同期後**の写し ＋ **除外なし** | 緑（是正済みだから通る） | ✅ OK・**293 件**の Markdown |

**B → C で走査対象が 292 → 293 に増えている。** これが「除外を外したこと」の実測であり、
**A は「外したうえで、もし古いままなら赤くなる」ことの実測**である。
C だけを見ると「除外を外しても緑」としか言えず、**外れているのかどうかが区別できない**。

## 母集合の取り方（規則 5 / 6 / 8）

**軸を 2 本引いた**（規則 5）。いずれも `git grep`＝**パスの除外だけ**で取り、拡張子で絞らず、
行フィルタを継がず、出力を `head` / `sed` で加工していない（規則 3 / 4 / 7）。

### 軸1: 除外そのもの（pathspec の形）— **2 件**

```
git grep -n ':!\.claude/rules/traceability\.md' -- ':!planning'
.claude/rules/traceability.repo.md:35
scripts/scripts.repo.test.js:1076
```

**この 2 件が「外す」実体である。** どちらも同じ 1 行を env として持っている。

### 軸2: ファイル名（除外の理由を語る箇所を取りこぼさないため）— **42 ファイル / 延べ 82 件**

軸1 だけでは、**pathspec の形をしていない記述**が落ちる（実際に落ちた）。

| 引いた先 | 件数 | 扱い |
| --- | --- | --- |
| `.claude/rules/traceability.repo.md` | 3 | **2 件を直す**（env・除外表の行）。**1 件は残す**（冒頭「配布物は直接編集しない」の説明） |
| `scripts/scripts.repo.test.js` | 2 | **2 件とも直す**（env と、その直前の理由コメント）。🔴 **軸1 では 1 件しか出なかった** |
| `docs/adr/IADR-0200_*.md` | 2 | **記述を更新する**（決定3 の分類表記・残余リスクの「外すこと」） |
| `docs/specs/`（作業仕様書 17 ファイル） | 30 | 🔴 **触らない。point-in-time の記録**である（IADR-0200 決定2 と同じ理由） |
| `feedback/`（環流記録 2 ファイル） | 8 | 🔴 **触らない。**同上 |
| `scripts/*.js`（検査器 5 本）・`changelog-overrides.json` | 15 | **触らない。**規約の所在を指す参照であり、除外とは無関係 |
| `.claude/agents/` `.claude/commands/` `CLAUDE.md` `docs/ai-workflow.md` `docs/tests/README.md` | 8 | **触らない。**同上 |
| `docs/adr/`（IADR-0200 以外 7 ファイル）・`docs/adr/README.md` | 13 | **触らない。**過去の決定の記録であり、経緯を書き換えない |
| `scripts/kit-sync-classification.json` | 1 | **分類を C → A へ移す**（決定3） |

> **規則 8 に従い、時点を明示する。** 上記は**本仕様書を書く前**の走査である
> （`develop` = `9c0eb7c` + 課題1〜3 の調査のみ）。本仕様書自身が `traceability.md` を
> 多数含むため、**コミット後の再走査では 43 ファイルへ増える**。**値はこのコミットで固定する。**

## 影響範囲

- `.gitmodules` 参照先の pin（`planning`）
- `scripts/check-feedback-status-sync.js` / `scripts/setup.sh`（キット原文で上書き）
- `.claude/rules/traceability.md`（キット原文で上書き）・`.claude/rules/traceability.repo.md`
- `scripts/kit-sync-classification.json`・`scripts/scripts.repo.test.js`
- `docs/adr/IADR-0200_*.md`（残余リスクの解消を反映）・新設 IADR-0202・`docs/adr/README.md`

**C# のコードには一切触れない。** ビルド・テストへの影響は無い。

## 環流（計画側へ返すもの）

キット版 `traceability.md` の**規則 8 の行が、表の外に落ちている**（規則 7 の行との間に空行がある。
`cat -A` で確認）。GFM は**ヘッダ行を持たない表本体を表として描画しない**ため、
**規則 8 だけが素の文字列 `| 8 | … |` として表示される**。分類 A であるため**手元では直さず環流する**。

- 環流記録: `feedback/20260815_kit-rule8-outside-table.md`
- 計画側 issue: **planning#358**

> **取り込み自体は優先した。** 表示は壊れているが**内容は届いている**——
> 取り込まないと**規則 8 そのものが本リポへ来ない**ほうが害が大きい。

## 参照

- [IADR-0191](../adr/IADR-0191_kit-sync-classification.md)（分類表と `check-kit-sync.js`）
- [IADR-0200](../adr/IADR-0200_cross-repo-ref-notation.md)（除外の起点・残余リスク）
- [IADR-0201](../adr/IADR-0201_cross-repo-refs-commit-face.md)（コミット面の配線）
- [作業仕様書 20260815_494](20260815_494_kit-sync-after-arbitration.md)（前回のキット追随）
