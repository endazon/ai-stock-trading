# AGENTS.md — AI エージェント向け共通コンテキスト

このファイルは、Claude 以外の AI エージェント（Cursor / Codex / Aider 等）や、`AGENTS.md` 規約に対応するツールが読み込む共通指示である。詳細な規約は `CLAUDE.md` に従う。本ファイルはその要約である。

## このリポジトリの位置付け

- 上流工程リポジトリ `project-planning` で確定した計画書を**実装する**作業リポジトリ。
- 計画書・裁定は `project-planning` の `projects/ai-stock-trading/00_vision 〜 07_adr` にある。**本リポジトリは planning に依存しない（submodule は張らない。資料再編 ADR-0029 決定 2）。** 参照は GitHub 上の URL か隣接クローン（既定パス `../project-planning`。**読み取り専用・pin 固定なし**）で行う。
- 計画書への指摘は project-planning へ GitHub issue（`feedback.yml` テンプレート・`feedback` / `decision-needed` ラベル）で起票する。**起票前に同件の既存 issue を必ず検索する。** 裁定の完了記録は planning 側 `projects/ai-stock-trading/10_feedback/` に残る（本リポジトリには残さない）。
- 利用可能な AI（Claude Code サブスク / Anthropic API / GitHub Copilot）と有効化するファイルは `AI_SETUP.md` で宣言する。AI 機能の一部はプロファイル依存である。
- 成果物の主従（主たる成果物と付随成果物）は README 冒頭・`CLAUDE.md` に明示し、計画書と一致させ続ける。
- **資料の主従**: `docs/` ＝人が読む生きた文書、`.ai-context/` ＝ AI 向け文脈資料・凍結記録（実装ADR・作業仕様書。ここで起草を続ける）。詳細は `.ai-context/README.md`（ADR-0029 決定 1・7）。

## 最優先原則：トレーサビリティ

実装の起点となる計画書の ID を必ず残す。

- 起点 ID の種別: `FR-xx`（機能要求）/ `UC-xx`（ユースケース）/ `SC-xx`（画面）/ `ADR-xxxx`（計画ADR）/ `IADR-xxxx`（実装ADR・本リポ `.ai-context/adr/`）。
- 残す箇所: ブランチ名（`feat/FR-012-...`）、コミットメッセージ先頭（`feat(FR-012): ...`）、コード内コメント、PR 本文。
- 🔴 **`docs/` 配下だけは別扱い**: 計画 ID・IADR・仕様書・修飾付き issue 参照を表示テキストへ書かず、frontmatter 直後・H1 の直前の **trace ブロック**（HTML コメント。1 文書 1 個）へ持つ（ADR-0029 決定 4。検査は `scripts/check-trace-blocks.js`）。`.ai-context/` の凍結記録には適用しない。

## 実装の基本フロー

1. 対象の計画書（要求・UC・画面）を読み、受け入れ基準を把握する。
2. 関連 ADR を読み、確定済み制約に違反しないことを確認する。曖昧なら実装を止めて人間に確認する。
3. **作業着手前に必ず `.ai-context/specs/<YYYYMMDD>_<概要>.md` に作業仕様書を作成する**。該当する必須仕様書（機能/画面/通信/データ/技術/テスト/運用/セキュリティ、`docs/` 配下）も作成・更新する。重要な実装判断は実装ADR（`.ai-context/adr/`、`IADR-XXXX`）に残す。仕様書一覧は `docs/README.md` 参照。
4. 仕様書・計画書に忠実に実装する。計画外の機能追加・過剰な抽象化を避ける。
5. 受け入れ基準をテストに写像する。
6. **完了前に `/verify` でビルド・テスト・lint を実行し、`docs/DEFINITION_OF_DONE.md` を満たすことを確認する。**
7. トレーサビリティを残してコミット・PR を作成する。
8. 実装中に計画書の誤り・不足・新たな制約を見つけたら、project-planning へ GitHub issue で起票する（上記「このリポジトリの位置付け」）。

> 破壊的操作・秘密情報の混入は `.claude/hooks/` がブロックする。運用全体は `docs/ai-workflow.md` を参照。

## 守ること

- **仕様書（`.ai-context/specs/`）を作成せずに実装へ着手しない**。
- 1 コミット = 1 論理変更。コミットメッセージ先頭に種別（`feat:` `fix:` `refactor:` `test:` `docs:` `chore:`）と起点 ID。
- `main` への直接コミット禁止。作業ブランチ → PR 経由。
- 破壊的 git 操作（force push, `reset --hard`）禁止。
- 機密情報をコミットしない。
- 計画書（fixed / Accepted）に反する実装をしない。差異が必要なら根拠（新 IADR 等）を残し、planning へ issue で環流する。
- **planning への依存を再導入しない**（submodule 化・pin 固定・計画書をビルド／CI の前提にすること）。

## worker スロットとして動く場合の契約

本リポジトリは役割スロット制（orchestrator / worker / reviewer。配役は `ai-roster.json`、正本は `docs/ai-orchestration.md`）を採る。Claude 以外のエージェントが **worker**（実装担当）として起動された場合、上の「守ること」に加えて次に従う。

- **割り当てられた git worktree の中だけで作業する**。worktree の外に書かない。
- 上の基本フローどおり、**作業仕様書（`.ai-context/specs/`）を作成してから実装する**。仕様書なしの着手は禁止。
- **帰属を機械可読に残す**: コミットに `Co-Authored-By: <エンジン名> <モデル名>` トレーラを付け、PR には `agent:<engine>` / `model:<model>` ラベルを付ける。
- **中間成果物（質問票・作業指示書）をコミットしない**。force push しない。
- `.claude/hooks/` のガード（破壊的操作・秘密混入の遮断）は **Claude 専用であり、あなたには効いていない**。同等の抑止（サンドボックス・破壊的コマンドの自制）は自分と起動元アダプタの責務である。
- 失敗した場合は**非 0 で終了する**（黙って部分成果を残さない）。起動元がフォールバック連鎖に従い切り替える。

## 実装作業の進め方（要約）

正本は project-planning の `docs/ai-implementation-workflow-guide.md`（上記の参照手段で開く）。詳細は `CLAUDE.md` §実装作業の進め方 を読む。

- **並列判定はファイル領域の非重複**。マージは **FIFO で 1 本ずつ**。同型の変更は束ねる。
- **フェーズ末監査は証跡付き**（別の文脈のエージェントが diff と受け入れ基準だけで行う。宣言だけの監査は不合格）。
- **検査器・規約の追加は「同型の事故が 2 回起きたら」**。必読規約は **50KB 目安**で、足すなら同量を削るか別紙へ落とす。
- **裁定依頼は小さく高頻度に**。**blocked 判定は棚卸しごとに再検証**する。
- **人間に委ねるのは 3 点**（フェーズ計画の承認／監査のサンプリング確認／裁定）。
- **kit は bootstrap 専用**であり、既存リポジトリに追随義務は無い（ADR-0029 決定 6）。乖離は受容として記録する。

## 技術スタック別ルール

`CLAUDE.md` 末尾の「技術スタック別ルール」に従う（言語・フレームワーク固有の命名規約・ビルド/テストコマンド等）。
