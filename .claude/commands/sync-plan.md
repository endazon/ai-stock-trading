---
description: 計画リポジトリの最新ドキュメントを取得し、AI 参照用サマリを再生成する
argument-hint: （省略可）計画リポのプロジェクト名
allowed-tools: Read, Grep, Glob, Bash(git submodule:*), Bash(git pull:*), Bash(ls:*), Bash(find:*), Write
---

引数 `$ARGUMENTS`: 計画リポのプロジェクト名（省略時は検出した全プロジェクト）。

目的: 計画リポジトリ（既定 `../project-planning`、submodule の場合 `planning/`）の最新の計画書を取り込み、`.ai-context/` 配下に AI が素早く参照できるサマリを生成する。

手順:

1. 計画リポの参照形態を確認する。
   - submodule の場合: `git submodule update --remote planning` で最新化する。
   - 隣接クローンの場合: 計画リポ側で `git pull` 済みであることを前提とする（無ければ案内する）。
2. `projects/<name>/` を走査し、各フェーズ（00〜07）の計画書からメタ情報・主要セクション・ID（FR/UC/SC/ADR）を抽出する。
3. `.ai-context/<name>.md` に以下を要約・出力する。
   - 要求一覧（FR-xx と要旨・優先度・受け入れ基準の所在）
   - ユースケース一覧（UC-xx と概要）
   - 画面一覧（SC-xx と概要）
   - ADR 一覧（ADR-xxxx と状態・決定の要旨。Superseded 関係を併記）
   - トレーサビリティの対応表（要求 ↔ UC ↔ 画面/技術 ↔ ADR）
4. 生成物のパスと、前回からの差分の要点を報告する。

補足: 機械生成を好む場合は、計画リポ側で `tools/impl-handoff-kit/generators/gen-traceability.js` 等を実行して `.ai-context/` に出力してもよい。`.ai-context/` は生成物のため gitignore を推奨する。
