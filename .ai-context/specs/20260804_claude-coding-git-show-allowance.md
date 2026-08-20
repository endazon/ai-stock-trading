---
title: 作業仕様書 — claude-coding.yml の許可リストに `Bash(git show:*)` を足し、3 系統の非対称を解消する
type: work
status: review
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - planning:tools/impl-handoff-kit/README.md
  - planning:tools/impl-handoff-kit/HOWTO.md
related_specs:
  - ./20260802_impl-handoff-kit-sync.md
  - ../../docs/ai-workflow.md
  - ../adr/IADR-0047_kit-template-sync-policy.md
---

# 作業仕様書: `claude-coding.yml` の許可リストに `Bash(git show:*)` を足す

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（AI 実行環境の権限設定。NFR 相当）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし。**新規 IADR も作らない**（後述「IADR を作らない判断」）
- 計画書リンク: impl-handoff-kit README（計画リポ） /
  HOWTO（計画リポ）
- 上流の起点: 計画リポの Issue
  [#163](https://github.com/endazon/project-planning/issues/163) 提案 3 と、その反映 PR
  [#176](https://github.com/endazon/project-planning/pull/176)（本作業の時点で **OPEN・未マージ**）

## 目的・背景

本リポジトリは AI 実行のツール許可を **3 系統**（実装用ワークフロー `claude-coding.yml`・レビュー用
ワークフロー `claude-code-review.yml`・ローカルの `.claude/settings.json`）に分けて持つ。同じ読み取り
コマンドが片方にしか無い「非対称」は、AI がそのコマンドを実行できずに実走が止まる形で表面化する。

同型の欠落は過去 3 度繰り返された（`cat`/`head`/`tail`、`cmp`/`diff`、`grep`/`sort`）。計画リポの Issue
[#163](https://github.com/endazon/project-planning/issues/163) 提案 3 はこれを機械検出する検査
`genericBashDrift` を提案し、PR [#176](https://github.com/endazon/project-planning/pull/176) で
`check-ai-workflow-config.js` に **ERROR** として実装された。

その検査を本リポジトリの現行ワークフローへ掛けたところ、**真陽性が 1 件**出る。

```
claude-coding.yml: レビュー用にある汎用 Bash 指定が欠けている: Bash(git show:*)
```

`Bash(git show:*)` はレビュー用（[claude-code-review.yml](../../.github/workflows/claude-code-review.yml)）
には既にあり、実装用（[claude-coding.yml](../../.github/workflows/claude-coding.yml)）にだけ無い。
どちらも読み取り専用の git サブコマンドであり、**意図的な非対称ではなく単純な入れ忘れ**である。
microservices-platform は PR
[#461](https://github.com/endazon/microservices-platform/pull/461) のレビュー指摘で同じ欠落を既に塞いで
いるが、本リポジトリは未対応のまま残っていた。

本作業はこの 1 エントリを足して非対称を解消する。副次的な効果として、PR #176 のマージ後に行うキット
同期 PR が、この件を理由に `ai-workflow-config` ジョブで赤くなることを未然に防ぐ。

## 対象範囲

- **対象**: [.github/workflows/claude-coding.yml](../../.github/workflows/claude-coding.yml) の
  `--allowedTools` に `Bash(git show:*)` を 1 エントリ追加する。
- **対象外**:
  - `scripts/check-ai-workflow-config.js` へのキット同期（`genericBashDrift` の取り込み）。上流 PR #176
    が未マージのため、本作業では取り込まない。取り込みは後続のキット同期 PR で行う。
  - `planning` submodule のポインタ更新。本作業では動かさない。
  - `.claude/settings.json` の変更。`Bash(git show:*)` は既に許可済みで非対称は無い。
  - microservices-platform 側の暫定デルタ撤去（別リポジトリ・別セッションの作業）。
  - `claude-coding.yml` 冒頭のコメント「**ドリフト検査（check-ai-workflow-config）はスタック別の実行
    ツールしか見ないため、この種の非対称は機械的に検出されない。**手で揃えること」（issue #160 の
    注記）の書き換え。この記述は**本 PR の時点では依然として正しい**（本リポジトリの検査器には
    `genericBashDrift` がまだ無い）。検出できるようになるのはキット同期の時点であり、注記の更新は
    その PR で検査器の取り込みと同時に行うのが正しい。先に消すと、同期までのあいだ「検出される」と
    誤読させる。

## 設計

`claude-code-review.yml` の並びは `Bash(git diff:*)` → `Bash(git log:*)` → `Bash(git show:*)` →
`Bash(git status:*)` である。`claude-coding.yml` は `Bash(git status:*)` → `Bash(git diff:*)` →
`Bash(git log:*)` と並ぶため、**`Bash(git log:*)` の直後**へ挿入する。これで両ファイルとも「読み取り系
git がひとかたまりで並ぶ」形が保たれ、次に読む人が非対称を目視でも見つけやすい。

追加するのは 1 エントリのみで、他の要素の並び替え・整形は行わない（差分をレビュー可能な最小に保つ）。

### `REVIEW_ONLY_BASH` へ宣言しない理由

`genericBashDrift` は、意図的な非対称を `CODING_ONLY_BASH` / `REVIEW_ONLY_BASH` へ宣言して黙らせる
逃げ道を用意している。本件はそれを**使わない**。`git show` は作業ツリーを変更しない読み取りコマンドで
あり、実装用の AI がコミット内容やファイルの過去版を確認する用途は正当である。レビュー用にだけ必要と
する理由が無いため、宣言による抑止ではなく欠落そのものを塞ぐのが正しい。

### IADR を作らない判断

本作業は設計判断ではなく、**上流で機械検出された非対称の是正**である。新たな技術選定・内部設計・
ライブラリ選定を伴わず、`Bash(git show:*)` を足すか否かに選択肢が無い（宣言による抑止を採らない理由は
上に記した）。よって実装 ADR は起こさず、判断の根拠は本作業仕様書に残す。キット由来の変更に IADR を
起こさない扱いは [IADR-0047](../adr/IADR-0047_kit-template-sync-policy.md) の方針とも整合する。

## 受け入れ基準

- [x] `claude-coding.yml` の `--allowedTools` に `Bash(git show:*)` が含まれる。
- [x] 上記以外の `--allowedTools` エントリに増減・並び替えが無い
      （エントリ数 44 → 45、entry 単位の diff は `> Bash(git show:*)` の 1 行のみ）。
- [x] PR #176 ブランチの `check-ai-workflow-config.js`（`genericBashDrift` 実装済み）を本リポジトリの
      `.github/workflows` へ掛けて、**修正前 ERROR 1 件 → 修正後 ERROR 0 件**が実測できる。
- [x] 本リポジトリ現行の `scripts/check-ai-workflow-config.js`（`genericBashDrift` 未同期）でも
      ERROR 0 件のまま（退行が無いこと）。
- [x] `planning` submodule のポインタが変わっていない。

### 実測結果

| 手順 | 期待 | 実測 |
| --- | --- | --- |
| 取得した検査器の `--self-test` | 30 件合格 | **30 件合格** |
| 修正**前**・`--dir` 実走（取得した検査器） | ERROR 1 件 | **ERROR 1 件**（`claude-coding.yml: レビュー用にある汎用 Bash 指定が欠けている: Bash(git show:*)`）・exit 1 |
| 修正**後**・`--dir` 実走（取得した検査器） | ERROR 0 件 | **問題なし**・exit 0 |
| 現行 `scripts/check-ai-workflow-config.js` | 修正前後とも ERROR 0 件 | **問題なし**・exit 0（修正後も退行なし） |
| `--allowedTools` のエントリ数 | +1 のみ | **44 → 45**・差分は 1 行の追加のみ |
| `git status` | `planning` に差分なし | **差分なし** |

## テスト方針

本変更はワークフローの許可リストであり、xUnit テストの対象ではない。検証は検査器の実走で行う。

現行の `scripts/check-ai-workflow-config.js` には `genericBashDrift` がまだ無く、**本件を検出できない**
（実走しても「問題なし」を返す）。したがって自リポの検査器では真陽性を再現できないため、上流 PR #176
のブランチから検査器を読み取り取得して実走し、前後を対比する。

| 手順 | 期待 |
| --- | --- |
| 取得した検査器の `--self-test` | 30 件合格（取得物が壊れていないことの確認） |
| 修正**前**の `.github/workflows` へ `--dir` 実走 | ERROR 1 件（`Bash(git show:*)` の欠落） |
| 修正**後**の `.github/workflows` へ `--dir` 実走 | ERROR 0 件 |
| 現行 `scripts/check-ai-workflow-config.js` の実走 | 修正前後とも ERROR 0 件（退行なし） |

`--dir` でリポ外を検査すると `.claude/settings.json` との乖離を告げる warn が必ず出るが、これは検査対象
ディレクトリの外を見に行けないことによるもので、本件とは無関係のため無視してよい（HOWTO 記載の既知挙動）。

## 計画書との差異

- 差異: なし。上流 Issue [#163](https://github.com/endazon/project-planning/issues/163) 提案 3 の意図
  （非対称を機械検出し、真陽性は塞ぐ）にそのまま従う。

## 未決事項

なし。

## 残作業（本 PR の対象外・記録のみ）

引継ぎ資料「Issue #163 / #165 / #167 / #168 対応の引継ぎ資料」（2026-08-04）のうち、本 PR で解消しない
ものを記録する。

| 項目 | 内容 | 担当 |
| --- | --- | --- |
| A | 計画リポ PR [#176](https://github.com/endazon/project-planning/pull/176) のレビューとマージ | 計画リポ側 |
| C | microservices-platform の暫定デルタ撤去（#176 マージ後） | MSP 側のセッション |
| D | `feedback/` の未送付 4 件を `plan-feedback` Issue として起票 | 本リポの別 PR で対応 |
| E | headlamp の ADR 起案（未解決論点があり人間の承認が要る） | 計画リポ側・人間 |
| — | キット同期（`genericBashDrift` を含む `check-ai-workflow-config.js` の取り込み） | #176 マージ後 |
