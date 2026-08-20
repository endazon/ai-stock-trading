---
title: クロスリポ参照の検査を「実害の出る面」（コミット件名・本文・PR タイトル）へ配線する
type: spec
status: approved
related_ids: [NFR, IADR-0189, IADR-0200, IADR-0201]
author: endazon (with Claude Code)
created: 2026-08-15
updated: 2026-08-15
---

# 仕様書: クロスリポ参照検査をコミット面へ配線する（#515）

> 本仕様書は実装着手前に作成する。

## 起点

- 起点 issue: [#515](https://github.com/endazon/ai-stock-trading/issues/515)（[#487](https://github.com/endazon/ai-stock-trading/issues/487) / [PR #514](https://github.com/endazon/ai-stock-trading/pull/514) から分離）
- 起点 ID: **NFR**（無採番。工程の統制であり計画側の非機能要件表に当たる番号が無い／環流しない）
- 規約: `.claude/rules/traceability.md`「クロスリポジトリの issue / PR 番号の修飾」

## 課題 — **配線したのは、実害の小さいほうだった**

[PR #514](https://github.com/endazon/ai-stock-trading/pull/514) は `.md` の面だけを配線した。
しかし規約は**面によって壊れ方が違う**と明記している。

| 面 | 裸の `#NNN` の扱い | 違反の実害 | #514 の配線 |
| --- | --- | --- | --- |
| **issue / PR 本文・コミットメッセージ** | **本リポジトリの issue / PR へ自動リンクする** | 🔴 **誤リンク** | ❌ 未配線 |
| `.md` ファイル | 自動リンクにならない | 表記ゆれ | ✅ 配線済み |

そのうえで規約は「**優先して直すのは issue / PR / コミットメッセージ側**」と定めている。

### 🔴 さらに、検査器の docstring が「配線済み」と書いている

`check-cross-repo-refs.js` の冒頭:

```
 *   - scripts/check-commit-messages.js から件名・本文・PR タイトル（ci.yml の commit-messages / pr-title.yml）
```

**実測で呼ばれていない**（`grep -c 'cross-repo\|CROSS_REPO' scripts/check-commit-messages.js` → **0**）。
[IADR-0198](../adr/IADR-0198_fx-expired-visibility.md) で実測した
「**維持されない記述は、あるだけ有害である**」（`Program.cs` が存在しないテストを担保として書いていた）と**同型**である。

## 調査でわかったこと（実測）

### キット同期の分類が、実装方針を決める

| ファイル | 分類 | 編集可否 |
| --- | --- | --- |
| `scripts/check-cross-repo-refs.js` | **A**（キットとバイト一致） | 🔴 **編集不可**（環流のみ） |
| `scripts/check-commit-messages.js` | **C**（本リポの中身・**置換点を持つ配布物**） | ✅ **編集してよい** |
| `scripts/scripts.repo.test.js` | リポ固有（キットに無い） | ✅ 編集してよい |

**分類 C は「置換点を持つ配布物」であり、各リポが自分の値を埋める前提**である
（`kit-sync-classification.json` の `$comment`）。したがって**設定を直書きしてよい。**

### 🔴 置換点を直書きにする理由は「env を忘れられないこと」である

env で渡す形は、**env を落とすと検査が緑のまま素通りする**（fail-open）。
[IADR-0200](../adr/IADR-0200_cross-repo-ref-notation.md) の対照実験 4 が実測したとおり、
**キット既定のマップは `project-planning` しか持たない**ため `microservices-platform#123` は素通りする。

**直書きなら、CI・ローカル・`--title` モードのどの経路から呼ばれても同じ設定で効く。**

> 🔴 **【訂正】初版は「`.github/workflows/` を編集できないため env を足せない」を理由に挙げていたが、
> これは事実に反する。** ワークフローは**本リポで 65 コミット分、実際に変更されている**
> （うち `889e41f` は本セッション群の作業）。`docs/specs/20260801_impl-handoff-kit-sync.md` も
> **2026-08-01 に「解消した」と記録していた。決定は変えないが、根拠を差し替えた。**

## 決定

### 決定1: **`check-commit-messages.js` から検査器を呼ぶ**（docstring の主張を実装で満たす）

`check-cross-repo-refs.js` が公開する `createChecker` / `findViolations` を使う。
**キット配布物には手を触れない。**

### 決定2: **置換点は `check-commit-messages.js` へ直書きする**

**env を落としたときに素通りさせないため**（fail-open の回避）。**env でも上書きできる**ようにはしておく（試験のため）。

### 決定3: **検査するのは PR 範囲のコミット（件名＋本文）と PR タイトル**

**既存履歴は検査しない**（`base..HEAD` のみ。履歴不変・force push 禁止。
`check-commit-messages.js` の既存方針と同じ「再発防止のみ」）。

**本文も見る** —— `Refs #NNN` を footer に書く形は規約が名指しした実害例である。
そのため `collectCommits` が `%B` も取るようにする。

### 決定4: **除外は既存の仕組みへ相乗りする**

bot 著者・`Revert`・`[skip ci]`・allowlist は**既存の判定をそのまま使う**（新しい除外規則を作らない）。

## やること

1. `collectCommits` が本文（`%B`）も取る
2. 置換点（本リポの値）と `validateCrossRepoRefs()` を足す
3. コミット（件名＋本文）と PR タイトルの両方で呼ぶ
4. 回帰テストを `scripts.repo.test.js` へ置く（**対照実験で「配線が外れたら赤くなる」ことを実測**）
5. **キット配布物の docstring は直さない**（分類 A）。実装が主張に追いついたことを IADR へ記す

## やらないこと

- **既存コミット件名の是正**（履歴不変。生成物は `changelog-overrides.json`）
- **`.md` 面の再配線**（PR #514 で完了済み）
- **`check-cross-repo-refs.js` の編集**（分類 A）
- **`.github/workflows/` の編集**（**編集はできるが、直書きで足りるため不要**）

## 受け入れ基準

- [ ] 規約違反を含む**コミット件名**が検出される
- [ ] 規約違反を含む**コミット本文**が検出される
- [ ] 規約違反を含む **PR タイトル**が検出される
- [ ] **否定形**: 正しい短縮形（`planning#329`）は通る
- [ ] **否定形**: 自リポの裸参照（`#487`）とスカッシュ末尾 `(#123)` は通る
- [ ] **否定形**: bot / Revert / `[skip ci]` は従来どおり除外される
- [ ] **既存履歴は 1 件も書き換えていない**
- [ ] 対照実験で**配線を外すと赤くなる**ことを実測した

## テスト方針

| 何を守るか | どう守るか |
| --- | --- |
| 3 つの面で検出すること | 件名・本文・PR タイトルそれぞれで違反を検出 |
| 正当な参照を止めないこと（否定形） | `planning#329` / 裸の `#487` / `(#123)` が通る |
| 除外が生きていること（否定形） | bot・Revert・`[skip ci]` は素通り |
| 配線が実効していること | 対照実験（検査を外すと赤が消える） |
