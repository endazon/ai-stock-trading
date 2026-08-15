---
title: 必読規約の総量予算に母集合の定義と検査器を与える — 自分の書いた数値が 2 回とも誤りだった
type: spec
status: approved
related_ids: [NFR, IADR-0202, IADR-0203, IADR-0204]
author: endazon (with Claude Code)
created: 2026-08-15
updated: 2026-08-15
---

# 仕様書: 必読規約の総量予算の母集合と検査器（#519）

> 本仕様書は実装着手前に作成する。

## 起点

- 起点 issue: [#519](https://github.com/endazon/ai-stock-trading/issues/519)
- 起点 ID: **NFR**（無採番。工程の統制であり計画側の非機能要件表に当たる番号が無い）
- 実測時点: `develop` = `392fff7`

## 課題

運用標準（`planning/docs/ai-implementation-workflow-guide.md`・`CLAUDE.md` 50 行目）は
「**毎セッション必読の規約は総量 50KB の予算内に収める**」と定めるが、**母集合の定義も検査器も無い。**

### 🔴 その結果、自分の書いた数値が 2 回とも誤りだった

| # | どこ | 書いた値 | 実際 |
| --- | --- | --- | --- |
| 1 | [PR #518](https://github.com/endazon/ai-stock-trading/pull/518) 受け入れ基準・[作業仕様書 20260815_517](20260815_517_kit-sync-and-exclusion-removal.md) | 45,082 バイト（90.2%） | **40,648 バイト（81.3%）** |
| 2 | [PR #522](https://github.com/endazon/ai-stock-trading/pull/522)・[IADR-0203](../adr/IADR-0203_class-c-requires-local-delta.md) 残余リスク・[#519](https://github.com/endazon/ai-stock-trading/issues/519) | 45,760 バイト（91.5%） | **41,326 バイト（82.7%）** |

> **2 つの「実際」が違うのは、`traceability.md` が 20,912 → 21,590 バイトへ増えたためである**
> （PR #522 のキット追随。規則 8 の追加分）。
> **`CLAUDE.md` 15,678 と companion 4,058 は両時点で同じである**（`git show 30191c1:` で確認）。
>
> 🔴 **【訂正・AI レビューの指摘】初版はこの表の 1 行目にも `41,326（82.7%）` と書いていた。**
> **2 つの時点の値を同じにしてしまった** —— **数値の取り違えを主題にする文書で、数値を取り違えた。**
> 本 PR の他の記述（[IADR-0204](../adr/IADR-0204_reading-budget-mother-set.md) の表・
> 実際に適用した訂正・PR 本文）は最初から `40,648` で正しく、**この 1 セルだけがずれていた** ——
> **同じ値を 2 度書く形は、片方だけ直す事故（規則 7）と同型である。**

**どちらも `AGENTS.md`（4,434 バイト）を足していた。**

`AGENTS.md` の冒頭はこう書いている ——

> このファイルは、**Claude 以外の AI エージェント**（Cursor / Codex / Aider 等）や、
> `AGENTS.md` 規約に対応するツールが読み込む共通指示である。

**Claude は読まない。** 逆に `AGENTS.md` を読むツールは `.claude/rules/` を読まない。
**1 つのセッションが両方を読み込むことはない。** つまり **異なるエージェントの集合を足していた。**

> 🔴 **さらに悪い形で効いていた。** #519 は「**90% を超えたら着手する**」という着手条件を書いており、
> **誤った 91.5% がその条件を満たしたことにしていた。**
> **正しくは 82.7% であり、条件は満たされていない。**
> **母集合を取り違えると、「いつ動くか」の判断まで狂う。**

### AI レビューは、この 1 項目だけを「未検証」とした

> 必読規約の**総量予算 45,082 バイト**という合計値は、対象ファイル集合を定義する自動検査器が
> 見当たらず、手動集計は規約が戒める「**母集合の取り違え**」のリスクがあるため追試しなかった。

**レビュアーの警戒が正しかった。** 追試を断った理由がそのまま、誤りの原因だった。

## 決定

### 決定1: 🔴 **予算はエージェントごとに判定する。集合を足さない**

**「毎セッション必読」は、読む主体によって中身が違う。** 制約は**セッション 1 本が背負う量**に掛かるため、
**エージェントごとの集合それぞれを予算と比べる**のが正しい。**合算は、誰も背負わない量を作る。**

| エージェント | 自動読み込みの集合 | 根拠 | 実測 |
| --- | --- | --- | --- |
| **Claude Code** | `CLAUDE.md` ＋ `.claude/rules/*.md` | `CLAUDE.md`「Claude はこのファイルを毎セッション読み込む」／`.claude/rules/traceability.md`「同ディレクトリの `*.md` は自動適用される」 | **41,326**（82.7%） |
| **`AGENTS.md` 対応**（Codex / Cursor / Aider） | `AGENTS.md` | `AGENTS.md` 冒頭「Claude 以外の AI エージェント…が読み込む共通指示」 | **4,434**（8.9%） |
| **GitHub Copilot** | `.github/copilot-instructions.md` | `CLAUDE.md`「Copilot 固有の運用は `.github/copilot-instructions.md`」 | **2,850**（5.7%） |

**拘束するのは最大の集合＝Claude Code の 41,326 バイトである。**

### 決定2: **検査器を置く。超過で fail・接近（90%）で warn**

`scripts/check-reading-budget.js`。**超えてから気づくと、減量を迫られる場面で減らせるものが残っていない。**

### 決定3: **母集合の定義は検査器の中に持たせ、根拠を併記する**

**「なぜこの集合なのか」を検査器の中に書く。** 定義を別ファイルへ置くと、
**定義と実装がずれても誰も気づかない**（本リポで繰り返している型である）。

### 決定4: **既存の誤った数値は、消さずに訂正として残す**

`docs/specs/`・`docs/adr/` の該当 4 箇所は **point-in-time の記録**である。
**書き換えず、訂正の追記を付ける**（[IADR-0200](../adr/IADR-0200_cross-repo-ref-notation.md)・[IADR-0201](../adr/IADR-0201_cross-repo-refs-commit-face.md) の訂正と同じ作法）。
**「間違えた」ことが消えると、次に同じ間違いをする。**

### 決定5: **配線先は `ci.yml` の既存ジョブへ相乗りする**

`scripts-tests` ジョブが `scripts.test.js` を走らせているため、**回帰テストから呼ぶ**。
新しいジョブを作らない（[IADR-0189](../adr/IADR-0189_plan-id-qualification-and-traceability-kit-sync.md) 決定2 と同じ形）。

## 受け入れ基準

- [ ] `node scripts/check-reading-budget.js` が 3 つの集合それぞれの実測とパーセントを出す
- [ ] **超過で exit 1**・**90% 以上で warn（exit 0）**
- [ ] `--self-test` を持つ（本リポの検査器の既定）
- [ ] 回帰テストが `scripts.repo.test.js` にあり、CI から走る
- [ ] **変異試験**: 予算を実測値より小さくすると赤くなる／warn 閾値をまたぐと warn が出る
- [ ] 誤った数値 4 箇所すべてに訂正が付いている（**全走査で確認**）
- [ ] #519 の「90% を超えた」という記述が訂正されている

## 母集合の取り方（規則 1 / 5 / 6 / 7）

### 軸1: 誤った数値そのもの（規則 1「誤りの側から引く」）

```
git grep -n "45,760\|45760\|91\.5%\|45,082\|45082\|90\.2%" -- ':!planning'
docs/adr/IADR-0203_class-c-requires-local-delta.md:143
docs/adr/README.md:247
docs/specs/20260815_517_kit-sync-and-exclusion-removal.md:131
docs/specs/20260815_521_kit-sync-and-class-c-audit.md:131
```

**4 件。** うち `docs/adr/README.md` は IADR-0203 の索引行（本文と対で直す。規則 7）。

### 軸2: 予算そのものへの言及（規則 5「軸を 1 本で終わらせない」）

`50KB` / `総量` で引き直し、**条文の側**（`CLAUDE.md`・`AGENTS.md`）に誤りが無いことを確認する。

### 除外したものと理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `planning/` | 別リポジトリの実体。予算の条文は計画側にあるが、本 PR は**本リポの数値**を直す |
| GitHub 上の PR #518 / #522 本文 | **マージ済み**。本文は編集できるため**訂正を追記する**（除外しない） |
| issue #519 本文 | **訂正する**（着手条件の判定そのものが誤っていたため） |

## 影響範囲

- 新設 `scripts/check-reading-budget.js`・`scripts/scripts.repo.test.js`
- 訂正 4 箇所（`docs/specs/` 2 件・`docs/adr/` 2 件）
- 新設 IADR-0204・`docs/adr/README.md`

**C# のコードには一切触れない。**

## 環流

**母集合の定義を計画側へ返す。** 予算はキット／計画側の運用標準であり、
**各リポが勝手に母集合を決めると比較できない。** 「エージェントごとに判定し、合算しない」
という原則と、本リポで踏んだ**合算による 2 度の誤り**を証拠として渡す。

## 参照

- [IADR-0203](../adr/IADR-0203_class-c-requires-local-delta.md) 残余リスク（誤った 91.5% を書いた箇所）
- [作業仕様書 20260815_517](20260815_517_kit-sync-and-exclusion-removal.md) / [20260815_521](20260815_521_kit-sync-and-class-c-audit.md)
