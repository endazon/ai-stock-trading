# AI_SETUP — 利用可能な AI を宣言し、プロファイルを選ぶ

このリポジトリは、**どの AI ライセンス/機能が使えるか**に応じて有効化するファイル・設定・シークレットを切り替える。
**実装に着手する前に、まず本ファイルで「使えるもの」を宣言し、対応するプロファイルを適用する。**

`CLAUDE.md` / `AGENTS.md` / `.github/copilot-instructions.md` は、いずれも最初に本ファイルを参照する。

## 1. 使える AI を宣言する

該当するものを `[x]` にする（複数選択可）。少なくとも 1 つを選ぶ。

- [x] **Claude Code サブスクリプション**（Pro / Max）… プロファイル `claude-code`
- [ ] **Anthropic API キー**（従量課金）… プロファイル `api`
- [ ] **GitHub Copilot**（coding agent / IDE 補完）… プロファイル `copilot`

> `claude-code` と `api` は配置ファイルが同一で、**設定するシークレットと課金方式だけ**が異なる。
> Copilot は別系統（`.claude/` を読まない。専用の指示ファイルと環境セットアップを使う）。

## 2. 能力マトリクス

| 能力 | Claude Code（Pro/Max） | Anthropic API | GitHub Copilot |
| --- | --- | --- | --- |
| ローカル AI 実装 | Claude Code CLI / Web / IDE | Claude Code CLI / Web / IDE | Copilot（IDE 補完・チャット） |
| GitHub 上の自律実装 | `claude-coding.yml`（`@claude` メンション） | `claude-coding.yml`（`@claude` メンション） | Copilot coding agent（Issue を割当） |
| AI 自動 PR レビュー | `claude-code-review.yml` | `claude-code-review.yml` | Copilot code review |
| AI 設定ファイル | `.claude/` ＋ `CLAUDE.md` | `.claude/` ＋ `CLAUDE.md` | `.github/copilot-instructions.md` |
| 環境準備 | `.devcontainer/` ＋ SessionStart hook | 同左 | `.github/workflows/copilot-setup-steps.yml` |
| 認証シークレット | `CLAUDE_CODE_OAUTH_TOKEN` | `ANTHROPIC_API_KEY` | （リポジトリで Copilot を有効化） |
| 共通（CI / 生成物 / docs / generators / `AGENTS.md`） | ✓ | ✓ | ✓ |

すべてのプロファイルで、CI（`ci` / `security` / `codeql`）・補助成果物生成（`changelog` / `openapi`）・
仕様書群（`docs/`）・トレーサビリティ規約は共通で使える（AI ベンダーに依存しない）。

## 3. プロファイル別セットアップ

### 共通（どのプロファイルでも実施）

1. 計画リポジトリ `project-planning` を参照可能にする（submodule か隣接クローン。既定パス `../project-planning`）。
2. 技術スタックに合わせて `*.example` の CI 系（`ci.example.yml` / `codeql.example.yml`）を有効化する。
3. ブランチ保護で必須ステータスチェックを設定する（手順は `docs/ai-workflow.md`）。

### プロファイル `claude-code`（サブスクリプション）

| 対象 | 操作 |
| --- | --- |
| `.claude/` ＋ `CLAUDE.md` | そのまま使う（Claude Code が読み込む） |
| `.github/workflows/claude-coding.example.yml` | `.example` を外して有効化（任意・GitHub 自律実装が必要なら） |
| `.github/workflows/claude-code-review.example.yml` | 同上（自動 PR レビューが必要なら） |
| シークレット | `claude setup-token` で OAuth トークンを発行し、`CLAUDE_CODE_OAUTH_TOKEN` を登録 |

### プロファイル `api`（Anthropic API キー）

`claude-code` と同じファイルを使う。違いはシークレットのみ。

| 対象 | 操作 |
| --- | --- |
| `.claude/` ＋ `CLAUDE.md` / `claude*.example.yml` | `claude-code` と同じ手順で有効化 |
| シークレット | `ANTHROPIC_API_KEY`（`sk-ant-api03-...`）を登録（従量課金） |

> `CLAUDE_CODE_OAUTH_TOKEN` と `ANTHROPIC_API_KEY` は**どちらか一方のみ**を設定する（両方設定すると競合する）。

### プロファイル `copilot`（GitHub Copilot）

| 対象 | 操作 |
| --- | --- |
| `.github/copilot-instructions.md` | そのまま使う（Copilot が読み込む） |
| `.github/workflows/copilot-setup-steps.example.yml` | `.example` を外して有効化（coding agent の環境準備） |
| `.claude/` ＋ `claude*.yml` | 使わない（残しても無害。不要なら `--prune` で削除可） |
| 起票 | Issue を Copilot にアサインすると自律実装が始まる |

## 4. 自動適用（任意）

宣言したプロファイルのファイルをまとめて有効化するヘルパーを同梱する。

```bash
# 例: Claude Code サブスクリプションのみ
bash scripts/apply-profile.sh claude-code

# 例: Copilot のみ（Claude 系ファイルも削除する場合）
bash scripts/apply-profile.sh --prune copilot

# 複数宣言も可
bash scripts/apply-profile.sh claude-code copilot
```

- 既定は**非破壊**（必要な `.example` を外すだけ）。`--prune` 指定時のみ、宣言しなかったプロファイルの
  ベンダー固有ファイルを削除する。
- 適用したプロファイルは `.ai-profile` に記録される。
