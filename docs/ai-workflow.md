# AI 駆動の実装ワークフロー（Runbook）

このリポジトリは **実装の大半を生成 AI に任せる**前提で構成している。本書は、計画書から実装・マージまでを AI 中心で回すための運用手順と、全自動化に有用なツールをまとめる。

## 全体フロー

```text
計画リポ(project-planning)
   │  gen-issues / 手動
   ▼
GitHub Issue（[ai-impl] / ai-implement ラベル）
   │  @claude メンション
   ▼
AI 実装（claude-code-action）
   ├─ 作業仕様書を作成（/new-spec）         ← 着手前に必須
   ├─ 必須仕様書・実装ADR を作成/更新
   ├─ ブランチ作成・実装・テスト（起点ID をトレース）
   └─ /verify（ビルド/テスト/lint・受け入れ基準照合）
   ▼
Pull Request
   ├─ AI 自動レビュー（claude-code-review）
   ├─ CI ゲート（ci: lint/build/test/coverage、security: gitleaks/dependency-review、CodeQL）
   └─ 人間レビュー（CODEOWNERS、AI特有リスクの確認）
   ▼
マージ → CHANGELOG / OpenAPI 自動更新
```

## 手順

### 0. 初期セットアップ（プロファイル選択 ＋ `.example` の有効化）

**初回チェックリスト**（着手前に上から順に確認する）:

- [ ] repo-template の中身をこのリポジトリ直下にコピー済みである（`.claude/` `.github/` `docs/` など）。
- [ ] 計画リポ（`project-planning`）を参照できる（git submodule か隣接クローン。既定パス `../project-planning`）。`/sync-plan` または計画書の該当 ID を開いて確認する。
- [ ] `AI_SETUP.md` で利用可能な AI を宣言し、`bash scripts/apply-profile.sh <profile>` を実行済みである。
- [ ] CI 系を有効化済みである（`ci.example.yml` / `codeql.example.yml` の `.example` を外す）。
- [ ] GitHub Secrets（`CLAUDE_CODE_OAUTH_TOKEN` か `ANTHROPIC_API_KEY`）を登録済みである（Copilot 利用時はリポジトリで Copilot を有効化）。
- [ ] 環境セットアップ（`scripts/setup.sh`）が通り、ビルド・テストが実走できる。

**最初に `AI_SETUP.md` で利用可能な AI（プロファイル）を宣言する。** プロファイルにより有効化するファイルとシークレットが変わる。`*.example` ファイルは拡張子から `.example` を外すと有効になる（GitHub Actions は `.github/workflows/*.yml` のみ実行する）。`scripts/apply-profile.sh` で自動化できる。

技術非依存の CI 系は全プロファイル共通で有効化する。

```bash
git mv .github/workflows/ci.example.yml     .github/workflows/ci.yml
git mv .github/workflows/codeql.example.yml .github/workflows/codeql.yml
```

`security.yml`・`changelog.yml`・`openapi.yml` はそのまま有効。ベンダー起動系はプロファイルで分岐する。

| プロファイル | 有効化するファイル | シークレット |
| --- | --- | --- |
| `claude-code`（サブスク） | `claude-coding.example.yml` / `claude-code-review.example.yml` | `CLAUDE_CODE_OAUTH_TOKEN`（`claude setup-token`） |
| `api` | 同上 | `ANTHROPIC_API_KEY` |
| `copilot` | `copilot-setup-steps.example.yml` | （リポジトリで Copilot を有効化） |

```bash
# 例: Claude（サブスク or API）— apply-profile.sh が claude*.yml を有効化
bash scripts/apply-profile.sh claude-code   # or: api

# 例: GitHub Copilot
bash scripts/apply-profile.sh copilot
```

### 1. タスクを起票する

- 計画リポ側で `/handoff <project>`（または `node tools/impl-handoff-kit/generators/handoff.js <project>`）を実行し、`ai-context/<project>/issues.json` を生成 → `gh` / GitHub MCP で起票。
- または「AI 実装タスク」テンプレート（`.github/ISSUE_TEMPLATE/ai-implementation.yml`）で起票。

### 2. AI に着手させる（プロファイル別）

- **Claude（サブスク / API）**: Issue / PR で `@claude このタスクを実装してください` とコメントする（`claude-coding.yml` が応答）。
- **GitHub Copilot**: Issue を Copilot にアサインする（coding agent が `copilot-setup-steps.yml` の環境で起動）。
- いずれも AI は次を行う: 計画書を読む → 作業仕様書を作成 → 必須仕様書・実装ADR を整備 → 実装 → テスト → 検証（Claude は `/verify`、Copilot は CI / DoD）。

### 3. レビューとゲート

- PR を開くと AI 自動レビュー（`claude-code-review.yml`）が走る。
- `ci.yml` / `pr-title.yml` / `security.yml` / `codeql.yml` の各ジョブが green であることを必須にする。
  **required status check として設定する名前はワークフロー名ではなく check 名**（`build-and-test` 等。
  実名の表は後述「必須チェックの有効化」）である。
  `pr-title` はスカッシュ後件名の唯一の予防線であり（中間コミットは force push 禁止で事後修正できない）、
  全 PR で起動するため必須チェックに指定してよい（後述「必須チェックに指定する際の注意」）。
- Helm chart を変更した PR では `helm.yml`（`helm lint` / `helm template`）も green にする。
- 人間は PR テンプレートの「レビュアー向け（AI実装の確認観点）」で最終確認する。

### 4. マージ後

- `changelog.yml` が `CHANGELOG.md` を、`openapi.yml` が `docs/api/openapi.yaml` を自動更新する。

### 5. 週次（マージとは独立）

- **`backlog-audit.yml` が週 1 回（月曜 00:00 UTC）バックログを監査する**（#439 / [IADR-0170](adr/IADR-0170_backlog-audit-automation.md)）。
  クローズ漏れ・重複起票・`docs/blocked-tasks.md` との突き合わせ・エピック進捗を見て、
  **単一の追跡 issue へ upsert** する。**issue を自動クローズしない**（提案のみ・判断は人間）。
  前段で `check-feedback-reflux.js` が**環流記録の未起票の滞留**を warn として出す。
  **監査が増える主因は実装速度ではなく「閉じるより速く増えること」だった**という実測が起点である。

## 全自動化のための推奨ツール・設定

| 目的 | ツール / 設定 | 備考 |
| --- | --- | --- |
| AI 実装の起動（Claude） | `anthropics/claude-code-action@v1`（`claude-coding.yml` / `claude-code-review.yml`） | サブスク=`CLAUDE_CODE_OAUTH_TOKEN` / API=`ANTHROPIC_API_KEY` のいずれか |
| AI 実装の起動（Copilot） | Copilot coding agent（Issue 割当）＋ `copilot-setup-steps.yml` | リポジトリで Copilot を有効化 |
| 対話的に AI 実装 | Claude Code（CLI / Web / IDE）/ Copilot（IDE） | Web は SessionStart hook（`scripts/setup.sh`）で環境準備 |
| 再現可能な環境 | devcontainer / GitHub Codespaces（`.devcontainer/`） | AI がビルド・テストを実走できる |
| 暴走防止（ローカル） | `.claude/hooks/`（guard-bash / guard-secrets / check-impl）＋ `settings.json` の permissions | 破壊的操作・秘密情報・仕様書なし実装を抑止 |
| 品質ゲート | CI 必須チェック ＋ ブランチ保護 | 下記「必須チェックの有効化」 |
| 秘密情報 | gitleaks（`security.yml`）＋ `.gitignore`（`.env` 等） | 鍵の混入・コミットを防ぐ |
| 脆弱性 | dependency-review（`security.yml`）＋ CodeQL ＋ Dependabot | 供給網・SAST |
| 完了の定義 | `docs/DEFINITION_OF_DONE.md` ＋ `/verify` | AI 自身の完了前検証 |
| トレーサビリティ | `/trace-check`・`/adr-check`・`.claude/rules/traceability.md` | 計画と実装の整合 |
| 計画への環流 | `/plan-feedback`（実装→計画） | 計画書の誤り・不足を戻す |

### 必須チェックの有効化（人手の検証を最小化する要）

> **これは推奨設定であり、現在の状態ではない。** ブランチ保護は**未配備**である（実測 2026-08-14。
> 承認レビュー無し・claude-review が赤のままの PR がマージできている。`docs/blocked-tasks.md` B-2 に
> **blocked:human** として記録済み）。**配備までの暫定手段**: マージ操作は人間が行い、マージ前に
> PR の Checks タブで `build-and-test` と `claude-review` の完走（green）を目視確認する。

GitHub の **ブランチ保護ルール**（Settings → Branches → Add rule）で以下を推奨設定する。

- Require a pull request before merging（直接 push 禁止）
- Require status checks to pass before merging → 下表の **check 名**を必須に
- Require review from Code Owners（`CODEOWNERS` を配置）
- Require conversation resolution before merging

**必須に指定するのは「check の名前（ジョブ側の名前）」である。ワークフロー名（`name:`）は
status check の context として存在しない**（IADR-0185 決定1）。従前の本節は `CI`・`Security` を
挙げていたが、**どちらもワークフロー名であり check として report されない**——そのとおり設定すると
**存在しないチェックを待ち続け、develop が恒久的にマージ不能になる**。誤りは消さず訂正として残す。

| 必須にする check 名 | 定義元（ジョブ） | 備考 |
| --- | --- | --- |
| `build-and-test` | `ci.yml` の `build-and-test` | バックエンドのビルド・テスト・カバレッジ |
| `lint` | `ci.yml` の `lint` | `dotnet format` 検証 |
| `commit-messages` | `ci.yml` の `commit-messages` | コミット規約 |
| `pr-title` | `pr-title.yml` の `pr-title` | スカッシュ後件名の唯一の予防線 |
| `secret-scan` | `security.yml` の `secret-scan` | gitleaks |
| `dependency-review` | `security.yml` の `dependency-review` | PR でのみ起動（push では if で skip） |
| ~~`Analyze (csharp)` 等~~ | `codeql.yml` の `analyze`（matrix 展開名） | **必須にしない（#481 で除外へ変更）**。`pull_request` に `paths:` を持つため、コード変更の無い PR では check 自体が report されず、必須指定すると恒久 pending になる。網羅は push（develop/main）と週次 schedule の全量解析が担保する |
| `claude-review` | `claude-code-review.yml` の `claude-review` | **完走**の担保であり「指摘なし」の担保ではない（下記） |

**`CI` / `Security` / `CodeQL`（ワークフロー名）を書いてはならない。**

- **`claude-review` の必須化で担保できるのは「レビューが完走した」ことだけである。** 🔴 の指摘が
  あっても success を返す（採否は人間の判断）ため、**必須にしても 🔴 のままのマージは止まらない**。
  あわせて、AI 基盤の停止・トークン失効・レート超過で**全 PR がマージ不能になる**副作用を持つ。
- `pr-size`（`pr-size.yml`）は **warn 方式の趣旨に反するため必須にしない**（IADR-0184）。

これにより、AI が作成した PR も「機械チェック green ＋ 必要なレビュー承認」を満たさない限りマージされない。

#### 必須チェックに指定する際の注意

- **`paths:` フィルタを持つワークフローを必須チェックにしてはならない。** GitHub は必須チェックが
  report されるまでマージを許さないが、対象パスに触れない PR ではそのチェックが**起動しない**ため、
  **永久に pending のままマージ不能**になる。デプロイ用・フロントエンド用など特定ディレクトリだけを
  対象にするワークフローが該当する。必須にするのは全 PR で起動するものに限る。
- **`pr-title.yml` は必須チェックに指定してよい。** 全 PR で起動し、かつスカッシュ後件名の唯一の
  予防線である（中間コミットは force push 禁止で事後修正できない）。
- **bot 作成 PR で `if:` によりジョブごとスキップされたチェックは、必須チェック上「合格」として扱われる**
  ためマージは止まらない。bot を除外する条件を書いてもブランチ保護と矛盾しない。
  - 本リポジトリでは `helm.yml`（`paths: deploy/helm/**`・[IADR-0058](adr/IADR-0058_helm-chart-ci-gate.md)）が該当するため必須チェックに指定しない。chart 変更 PR ではレビューで green を確認する。

## よくある詰まり（FAQ）

| 症状 | 対処 |
| --- | --- |
| スラッシュコマンド（`/new-spec` 等）が出ない | repo-template の `.claude/` をリポ直下にコピーしたか確認し、Claude Code を再起動して読み直す。 |
| 計画書（`projects/<name>`）を参照できない | git submodule か隣接クローンを設定する（既定パス `../project-planning`）。`/sync-plan` で `.ai-context/` に再生成して確認する。 |
| CI / AI ワークフローが起動しない | `.example` を外して有効化したか（`scripts/apply-profile.sh`）、必要な Secrets を登録したか確認する。Actions のログでトリガ条件を確認する。 |
| `@claude` が反応しない | `claude-coding.yml` が有効化済みか、`CLAUDE_CODE_OAUTH_TOKEN` か `ANTHROPIC_API_KEY` のいずれかが登録済みかを確認する。 |
| ビルド・テストが C#/.NET 前提で合わない | 技術スタック別の差し替え対象（`ci.yml` / `setup.sh` / `.devcontainer/` / `settings.json` の permissions）を使用言語へ直す。一覧は計画リポの `tools/impl-handoff-kit/README.md`「技術スタック別の差し替え対象」。 |

## 安全に任せるための原則

- AI は**着手前に作業仕様書を作成**し、それに沿って実装する（hook が警告）。
- 破壊的操作・秘密情報コミットは hook と権限設定でブロックする。
- マージ前に **CI ゲート ＋ 人間の最終レビュー** を必ず通す（全自動でも最後の人間ゲートは残す）。
- 計画書に反する判断は実装で押し通さず、`/plan-feedback` で計画側へ戻す。
