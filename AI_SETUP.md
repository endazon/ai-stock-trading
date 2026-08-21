# AI_SETUP — 利用可能な AI を宣言し、プロファイルを選ぶ

このリポジトリは、**どの AI ライセンス/機能が使えるか**に応じて有効化するファイル・設定・シークレットを切り替える。
**実装に着手する前に、まず本ファイルで「使えるもの」を宣言し、対応するプロファイルを適用する。**

`CLAUDE.md` / `AGENTS.md` / `.github/copilot-instructions.md` は、いずれも最初に本ファイルを参照する。

## 0. 役割スロット（プロファイルとロースターの区別）

AI の使い方は 2 つの宣言で決まる。**混同しないこと。**

| 宣言 | ファイル | 意味 |
| --- | --- | --- |
| **プロファイル**（能力） | 本ファイル §1 → `.ai-profile` | **何が使えるか**（ライセンス・シークレット） |
| **ロースター**（配役） | `ai-roster.json` | **どの役割スロット（orchestrator / worker / reviewer）にどのエンジンを挿すか**と、失敗時のフォールバック連鎖 |

既定はどのスロットも Claude であり、**Claude だけを使うならロースターを触る必要はない**。
worker を Codex 等へ差し替える場合や、失敗時の切り戻しを設計する場合に
[`docs/ai-orchestration.md`](docs/ai-orchestration.md)（スロットの正本）を読む。

🔴 **ロースターは宣言であって実装ではない。** 各割当の `implemented_by` 欄（必須）が現在の実現手段で
あり、実体（アダプタ・ワークフロー）の無い割当は無効である。差し替えたことにするには、
アダプタ（`scripts/ai-adapters/`）またはワークフローの実体と、プロファイル側の能力（本ファイル §1）が要る。

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

1. 計画リポジトリ `project-planning` を参照できる状態にする。**submodule は張らない**（資料再編 ADR-0029 決定 2）。GitHub 上の URL を直接開くか、**隣接クローン**（既定パス `../project-planning`。読み取り専用・pin 固定なし）を用意する。参照専用のトークン・シークレットは不要である。
2. 技術スタックに合わせて `*.example` の CI 系（`ci.example.yml` / `codeql.example.yml`）を有効化する。
   - **注意: `.github/workflows/` 配下では `.example` を挟んでも無効にならない。** GitHub Actions は
     同ディレクトリ内の `*.yml` をファイル名に関わらず実行するため、`frontend.example.yml` のような
     名前でもワークフローとして起動する（前提スクリプトが未定義なら失敗する）。無効のまま置きたい
     テンプレートは `.github/workflows/` の外へ出すか、拡張子を `.yml` 以外にすること。
     本リポジトリでは同じ理由で実行されていたフロントエンド用テンプレート 2 件を削除した（#367）。
3. ブランチ保護で必須ステータスチェックを設定する（手順は `docs/ai-workflow.md`）。
4. **`.github/CODEOWNERS.example` を `CODEOWNERS` にリネームし、レビュアを設定する。**
   AI が実装し AI がレビューする運用では、必須レビュアが不在だと「AI の実装を AI が承認して
   同一人物がマージする」ループになり、人間のレビュー関門が形骸化する。
5. **ビルド/テスト/フォーマットのコマンドを技術スタックへ合わせるときは、次の 3 か所すべてを
   同じ内容に揃える。** 1 か所でも漏れると AI の実装・レビューが検証できなくなる。
   - `.claude/settings.json` の `permissions.allow`（ローカルの Claude Code 用）
   - `.github/workflows/claude-coding.yml` の `claude_args`（CI の実装用）
   - `.github/workflows/claude-code-review.yml` の `claude_args`（CI のレビュー用）
   > レビュー用だけ実行系が抜けていると、AI レビューは毎回「Bash が承認待ちでブロックされた
   > ため検証できていません」と報告するだけになる（CI には承認する人間がいないため必ず失敗する）。
   > 実運用でこの退行が 2 週間継続した実績がある。

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

## 4. MCP・プラグイン・ブラウザ操作（Claude Code 系プロファイルのみ）

`claude-code` / `api` プロファイルで実施する（Copilot は対象外）。

### 4-1. MCP の承認（必須・初回のみ）

`.mcp.json`（Context7）はキットが配布する。ただし**クローンしただけでは有効にならない**。

1. `claude` を**対話モードで起動**し、ワークスペースの信頼（trust）ダイアログを承諾する。
2. `/mcp` で `context7` サーバの接続を承認・確認する。

> ヘッドレス実行（`claude -p`・CI）は承認プロンプトを出せないため、先に対話モードで一度
> 承認を済ませておくこと。Context7 は API キー不要（匿名モード）で動く。

#### 🔴 `.mcp.json` に GitHub MCP を書かない（重要）

**キットの `.mcp.json` は Context7 のみを持ち、GitHub MCP サーバを定義しない。** 定義してはならない。

- `claude-code-action` は **`github` という名前の GitHub MCP サーバを組み込みで供給する**。
  同アクションの仕様は「**組み込みと同名のカスタムサーバを定義すると、カスタム側が組み込みを上書きする**」
  と明記している。
- Claude Code の非対話モードは **cwd の `.mcp.json` を読み、アクションは `enableAllProjectMcpServers`
  を自動的に true にする**ため、リポジトリの `.mcp.json` は **CI で自動承認される**。
- `.mcp.json` に `Bearer ${GITHUB_PAT}` を書いても、**CI に `GITHUB_PAT` は無い**。Claude Code は
  「変数が無く既定値も無い場合、設定は読み込まれ、警告を出して**リテラル文字列をそのまま使う**」。
- 結果、**CI の `mcp__github__*` が認証できなくなる**。ジョブは success で終わるため、
  **AI レビューが静かに死ぬ**。

**GitHub 操作は次のとおり手段が分かれている。**

| 面 | 手段 |
| --- | --- |
| **CI（`claude-coding.yml` / `claude-code-review.yml`）** | アクションの**組み込み** GitHub MCP（`mcp__github__*`）。`.mcp.json` は関与しない |
| **ローカルの Claude Code** | 各自の**ユーザー単位設定**（`~/.claude.json` 等）で GitHub MCP を持つ。リポジトリでは配布しない |

ユーザー単位で GitHub MCP を持つ場合は、ツール定義数を減らすため `X-MCP-Toolsets` の指定を推奨する
（実装リポの運用では `repos,issues,pull_requests,labels,actions`。`actions` は CI 実行結果の参照に要る）。

### 4-2. プラグイン・スキルの各自導入（任意・推奨）

プラグインの有効化は**ユーザー単位設定**のためリポジトリでは配布できない。各自 Claude Code 内で導入する。
**開発規律系は superpowers に統一し、同種プラグイン（ECC / compound-engineering 等）を併用しない。
UI 生成系も ui-ux-pro-max に統一する**（重複導入するとスキル発火が非決定的になる。採否の判断記録は
計画リポ `draft/cross-project/20260817_skill-mcp-adoption-decision.md`）。

```text
# 開発規律（brainstorm → plan → TDD → review）
/plugin marketplace add obra/superpowers-marketplace
/plugin install superpowers@superpowers-marketplace

# UI 生成品質（フロントエンドを持つリポのみ）
npx skills add nextlevelbuilder/ui-ux-pro-max-skill

# UI・React の監査ルール（フロントエンドを持つリポのみ。生成系と補完関係）
npx skills add vercel-labs/agent-skills

# 実ブラウザでの動作確認スキル（公式）
/plugin marketplace add anthropics/skills
/plugin install example-skills@anthropic-agent-skills
```

### 4-3. AI のブラウザ操作は Playwright CLI + Skills に統一する

**AI が対話的に**ブラウザを操作する手段は **Playwright CLI**（`playwright-cli`）に統一する。
**Playwright MCP は導入しない**（公式がコーディングエージェントには CLI + Skills を推奨。両方入れると
ツール選択が不定になる）。

```bash
npm i -D @playwright/cli@latest
npx playwright-cli install --skills   # Claude Code 用スキルを配置
```

🔴 **CI の E2E テストランナーは別の関心事である。統一の対象に含めない。**

| 用途 | 手段 |
| --- | --- |
| **CI の E2E テスト** | **リポジトリの既存選択を覆さない**（`@playwright/test` 等。ADR で確定していることが多い） |
| **AI の対話的なブラウザ操作** | `playwright-cli` + Skills |
| Playwright MCP | 導入しない |

**両者は併存してよい。** 「ブラウザ操作の統一」を字義どおり読んで既存の E2E ランナーを捨てると、
**確定済み ADR の無断逸脱になる**（実測: microservices-platform が `MSP:IADR-0033` との衝突を検出し、
役割で棲み分ける `MSP:IADR-0221` を起こした。planning#409）。

- **pnpm workspace では `@playwright/cli` をルートへ入れない。** 2 つ目の Playwright が入るうえ、
  **どの CI ジョブも起動しない**。入れるならフロントエンドのワークスペースへ入れ、
  `pnpm --filter <pkg> exec` で起動する（ルート導入は `ERR_PNPM_RECURSIVE_EXEC_FIRST_FAIL` を招く）。

## 5. 自動適用（任意）

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
- **既知プロファイル以外の引数はエンジン名**とみなし、`.github/workflows/<engine>-*.example.yml` の
  有効化を試みる（役割スロットの他エンジン実装。対象が無ければ skip・exit 0）。例:
  `bash scripts/apply-profile.sh claude-code codex`。配役は別途 `ai-roster.json` で宣言する
  （正本 [`docs/ai-orchestration.md`](docs/ai-orchestration.md)）。
- 🔴 **他エンジンを主とする場合も、Claude 系を `--prune` で削除しない**（フォールバックの切り戻し先を失う）。
