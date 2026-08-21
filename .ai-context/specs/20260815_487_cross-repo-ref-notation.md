---
title: 他リポジトリ issue / PR 番号の表記を短縮形へ確定し、検査器を CI へ配線する
type: spec
status: approved
related_ids: [NFR, IADR-0189, IADR-0200, IADR-0201]
author: endazon (with Claude Code)
created: 2026-08-15
updated: 2026-08-15
---

# 仕様書: クロスリポジトリ参照の表記確定と検査器の配線（#487）

> 本仕様書は実装着手前に作成する。

## 起点

- 起点 issue: [#487](https://github.com/endazon/ai-stock-trading/issues/487)
- 起点 ID: **NFR**（無採番）。工程の統制であり、計画側の非機能要件表に当たる番号が無い
  （`.claude/rules/traceability.md` の「無採番を許す 2 つの場合」の 2。**環流はしない**）
- 規約: `.claude/rules/traceability.md`「クロスリポジトリの issue / PR 番号の修飾」「是正・追随の母集合の取り方」

## 裁定（2026-08-15・利用者）

| 論点 | 裁定 |
| --- | --- |
| **表記** | **短縮形へ寄せる**（`planning#329` / `MSP#286`） |
| **point-in-time 記録**（`docs/specs/` `feedback/`） | **是正の対象に含めない**（既定除外へ足す） |

### 🔴 裁定の前に 1 つの案が実測で消えた

**「長い表記（`project-planning#N`）へ寄せる」は、検査器が構造的に表現できない。**

`CROSS_REPO_NAMES='project-planning:project-planning'`（短縮名＝リポ名）と宣言して実走したところ、
検査器は依然として型 1 として検出し、**`project-planning#329 → project-planning#329` と
自分自身への置換を提案した**。検査器の設計が「短縮形へ寄せ、フルパス形式だけを例外として許す」
（スクリプト冒頭に明記）であり、**リポジトリ名の裸書きは定義上つねに違反**だからである。

したがって長い表記を採るには**キット配布物の改修を計画側へ環流**する必要があり、
**本リポジトリだけでは着手できない**（分類 A は手元で直さない）。この事実を提示したうえで裁定を得た。

## 実測（2026-08-15・`develop` = `9624160`）

置換点: `CROSS_REPO_NAMES='project-planning:planning,microservices-platform:MSP'` /
`CROSS_REPO_SELF_NAMES='AST,ai-stock-trading'`

| 除外 | 違反数 |
| --- | ---: |
| 既定（`:!planning` のみ） | **294** |
| ＋ `docs/specs` `feedback`（**裁定どおり**） | **128** |

### 型の内訳（128 件）

| 型 | 件数 |
| --- | ---: |
| 長い表記 `project-planning#N → planning#N` | 79 |
| 長い表記 `microservices-platform#N → MSP#N` | 21 |
| 空白区切り `MSP #N → MSP#N` | 12 |
| 空白区切り `planning PR #N → planning#N` | 3 |
| 列挙形 `planning#N / #N → planning#N / planning#N` | 3 |
| 空白区切り `microservices-platform PR #N → MSP#N` | 2 |
| 列挙形 `project-planning#N・#N` | 2 |
| 列挙形 `project-planning#N / #N` | 2 |
| 空白区切り `project-planning PR #N` | 1 |
| 空白区切り `planning issue #N` | 1 |
| 空白区切り `planning #N` | 1 |
| 空白区切り `microservices-platform #N` | 1 |

### 配置（128 件）

| 場所 | 件数 |
| --- | ---: |
| `docs/adr/` | 99 |
| `docs/blocked-tasks.md` | 10 |
| `docs/functional/` | 6 |
| `docs/screens/` | 4 |
| `docs/tests/` | 3 |
| `docs/operations/` | 2 |
| `docs/tech/` `docs/security/` | 各 1 |
| `CHANGELOG.md` | 1 |
| `.claude/rules/traceability.md` | 1 |

## 🔴 除外したものと理由（**全数**・規則 6）

**「黙って除外した」ことでも事故は起きる**ため、除外は 1 件残らず理由つきで書く。

| # | 除外 | 件数 | 理由 |
| --- | --- | ---: | --- |
| 1 | `planning`（submodule） | — | **別リポジトリの実体**であり、本リポジトリの成果物ではない（キットの既定）。 |
| 2 | `docs/specs/`（作業仕様書） | 137 | **裁定（2026-08-15）。** point-in-time の記録であり、**後から表記だけ直すと当時の記述と食い違う**。姉妹検査器 `check-plan-id-qualification.js` が同じ理由で既定除外している。 |
| 3 | `feedback/`（環流記録） | 29 | 同上。**送付・環流した時点の記録**である。 |
| 4 | `.claude/rules/traceability.md` | 1 | 🔴 **キット配布物（分類 A）であり、手元で編集してはならない**（同ファイル冒頭が明記）。直すとバイト一致が崩れ、キット同期のたびに手動マージが要る。**計画側へ環流する**（下記）。 |

> 2 と 3 の合計 166 件は `294 - 128` と一致する（引き算が合うことを確認した）。

**`CHANGELOG.md` は除外しない。** 生成物であり、規約が定める手段（`changelog-overrides.json` の
`remap`）で是正する（下記）。**除外して見なかったことにはしない。**

## やること

1. **126 件を短縮形へ是正する**（128 − キット 1 − CHANGELOG 1）
2. **`CHANGELOG.md`** は `changelog-overrides.json` へ `remap` を足し、**生成物の側を是正する**
   （履歴不変の原則。`594deed` の要約に含まれる `MSP #286` → `MSP#286`）。
   生成済みの行も同じ文字列へ揃え、**次回生成でも同じ結果になる**状態にする
3. **キット配布物の違反 1 件を計画側へ環流する**（`planning issue #202` → `planning#202`）
4. **検査器を CI へ配線する** —— `scripts/scripts.repo.test.js` へ回帰テストを置き、
   **置換点は環境変数で与える**（IADR-0189 決定2・決定6 と同じ形）
5. **置換点の既定値をリポジトリ固有規約へ明文で残す**（`.claude/rules/traceability.repo.md`）

## やらないこと

- **既存のコミットメッセージの書き換え**（履歴不変・force push 禁止）
- **`.claude/rules/traceability.md` の直接編集**（分類 A・環流する）
- **`docs/specs/` `feedback/` の是正**（裁定により対象外）
- **`.github/workflows/` の編集**（既存の呼び出し口へ相乗りするため不要）
  - 🔴 **【訂正・2026-08-15】初版は「GitHub App 権限で編集できないため」と書いていたが事実に反する。**
    ワークフローは**実測 65 コミット分、実際に変更されている**（IADR-0201 の訂正を参照）。
    **相乗りを選んだこと自体は妥当**だが、理由は「できないから」ではなく「不要だから」である。

## 受け入れ基準

- [ ] 置換点を与えた状態で違反が **0 件**である
- [ ] 表記の方針が `.claude/rules/traceability.repo.md` に**明文で残っている**
- [ ] `scripts.repo.test.js` から実データ走査が走り、**環境変数を落としても赤くなる**（対照実験で実測）
- [ ] **否定形**: `docs/specs/` `feedback/` は走査対象に入っていない（除外が効いている）
- [ ] `CHANGELOG.md` は override で是正され、**再生成しても同じ結果**になる
- [ ] キット配布物の 1 件は**環流の記録が残っている**（手元では直さない）

## テスト方針

| 何を守るか | どう守るか |
| --- | --- |
| 是正が完了していること | 置換点つきで走査 → 0 件 |
| 検査が実際に働くこと | `scripts.repo.test.js` から実データを走査 |
| 設定落ちで黙って通らないこと（否定形） | **環境変数を落とすと赤くなる**ことを対照実験で実測 |
| 除外が効いていること（否定形） | 走査対象に `docs/specs/` が含まれない |
