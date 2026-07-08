# CLAUDE.md — 実装作業リポジトリ

このリポジトリは、上流工程リポジトリ（`project-planning`）で確定した計画書を**実装する**ための作業リポジトリである。Claude はこのファイルを毎セッション読み込む。指示は具体・簡潔に保つ。

> 本ファイルは `impl-handoff-kit` のテンプレートから生成された。技術スタックに依存する規約は末尾の「技術スタック別ルール」に追記すること。
>
> **最初に `AI_SETUP.md` を読む**。利用可能な AI（Claude Code サブスク / Anthropic API / GitHub Copilot）の宣言と、有効化するファイル・シークレットがそこで決まる。

## 目的

- 計画書（要求・ユースケース・画面・技術検討・ADR）に忠実に実装する
- 計画と実装の**トレーサビリティ**（追跡可能性）を保つ
- 生成 AI を活用しつつ、人間がレビューできる変更単位を維持する

## 計画書の参照

- 計画リポジトリ `project-planning` を **git submodule** もしくは**隣接クローン**として参照する。既定パスは `../project-planning`（submodule の場合は `planning/`）。
- 計画書は `projects/<name>/00_vision 〜 07_adr` に格納されている。各 ID の意味は以下。
  - `FR-xx`: 機能要求（`02_requirements/`）
  - `NFR`: 非機能要件（`02_requirements/`）
  - `UC-xx`: ユースケース（`03_usecases/`）
  - `SC-xx`: 画面（`05_screens/`）
  - `ADR-xxxx`: 意思決定記録（`07_adr/`）
- 最新の計画書サマリは `/sync-plan` で `.ai-context/` に再生成する。実装着手前に該当 ID の計画書を必ず読む。

## 実装の進め方（AI 活用の基本フロー）

実装の起点となる ID（FR/UC）が与えられたら、**まず仕様書を作成してから**、以下の順で進める。

1. **計画書を読む**: 対象の要求・ユースケース・画面設計を読み、受け入れ基準を把握する。
2. **ADR 制約を確認する**: 関連する ADR を読み、確定済みの技術・設計上の制約に違反しないことを確認する。曖昧な場合は実装を止め、人間に確認する。
3. **仕様書を作成する（必須・着手前）**: `docs/specs/<YYYYMMDD>_<概要>.md` に作業仕様書を作成する（`/new-spec`）。以降の実装は必ずこの仕様書に沿って進める。仕様書なしで実装へ着手しない。該当する必須仕様書（機能/画面/通信/データ/技術/テスト/運用/セキュリティ）を作成・更新し、重要な実装判断は実装ADR（`IADR`）に残す（後述「仕様書」参照）。
4. **タスクに分解する**: 影響範囲・必要なテストを洗い出す（`/plan-to-tasks` を活用）。
5. **実装する**: 仕様書・計画書に忠実に実装する。計画外の機能追加・過剰な抽象化を行わない。
6. **テストを書く**: 受け入れ基準をテストケースへ写像する（`test-author` エージェントを活用）。
7. **検証する（完了前）**: `/verify` でビルド・テスト・lint を実行し、受け入れ基準と `docs/DEFINITION_OF_DONE.md` を満たすことを確認する。
8. **トレーサビリティを残す**: 後述の規約に従い、起点 ID をブランチ名・コミット・コード・PR に残す。
9. **計画へ環流する**: 実装中に計画書の誤り・不足・新たな制約を見つけたら、`/plan-feedback` で計画リポジトリへフィードバックする。

## トレーサビリティ規約

実装と計画書を相互に追跡できるよう、起点となる ID を以下の箇所に残す。

- **ブランチ名**: `feat/FR-012-<概要>` のように起点 ID を含める。
- **コミットメッセージ**: 先頭に種別と ID を付ける。例: `feat(FR-012): ログイン画面のバリデーションを実装`。
- **コード**: 計画書由来の実装には、該当箇所のコメントに ID を残す。例: `// FR-012, UC-03: 入力バリデーション`。
- **PR**: PR テンプレートの該当欄に実装した FR/UC・関連 ADR・受け入れ基準のチェックを記入する。
- 詳細な書式は `.claude/rules/traceability.md` を参照（自動適用）。

## 仕様書（docs/）

計画書（`project-planning` の上流ドキュメント）を実装向けに詳細化した仕様書を `docs/` に置く。`/new-spec <種別> <ID|topic>` で作成する。各仕様書には起点 ID（FR/UC/SC/ADR）と計画書リンク、関連仕様書への相互リンクを必ず記入する。

**必須**（対象が存在する限り作成・維持する）:

| 種別 | 文書 | 出力先 | 粒度 |
| --- | --- | --- | --- |
| `work` | 作業仕様書（横断） | `docs/specs/` | 作業/PR 単位（着手前に必須） |
| `functional` | 機能仕様書 | `docs/functional/` | 機能（FR）単位 |
| `screen` | 画面仕様書 | `docs/screens/` | 画面（SC）単位 |
| `api` | 通信仕様書 | `docs/api/` | API/IF 単位 |
| `data` | データ仕様書（DB） | `docs/data/` | エンティティ/集約単位 |
| `tech` | 技術要件書 | `docs/tech/` | リポ単位（原則1つ） |
| `test` | テスト仕様書 | `docs/tests/` | 機能（FR）単位 |
| `operations` | 運用仕様書 | `docs/operations/` | リポ単位（原則1つ） |
| `security` | セキュリティ仕様書 | `docs/security/` | リポ単位（原則1つ） |
| `adr` | 実装ADR（`IADR-XXXX`） | `docs/adr/` | 決定単位（重要判断ごとに必須） |

**任意**（必要に応じて作成）:

| 種別 | 文書 | 出力先 |
| --- | --- | --- |
| `observability` | ログ・可観測性仕様書 | `docs/observability/` |
| `authz` | 権限・認可仕様書 | `docs/authz/` |
| `integration` | 外部連携仕様書 | `docs/integration/` |
| `batch` | バッチ・ジョブ仕様書 | `docs/batch/` |
| `migration` | 移行仕様書 | `docs/migration/` |
| `error` | エラー・メッセージ仕様書 | `docs/errors/` |
| `infra` | インフラ・構成仕様書 | `docs/infra/` |

- 詳細・計画書との対応は `docs/README.md` を参照。実装着手前に少なくとも作業仕様書を作成する。
- 重要な実装判断（内部設計・ライブラリ選定等）は**実装ADR（`docs/adr/`、`IADR-XXXX`）に必ず残す**。計画に影響する決定は `/plan-feedback` で計画側へ環流する（計画ADR `ADR-XXXX` と区別する）。

## 補助成果物の自動生成

補助成果物は生成可能なら必ず生成し、CI で自動更新する（`scripts/` ＋ `.github/workflows/`）。

- **CHANGELOG**: コミット履歴（`種別(起点ID): 要約`）から `CHANGELOG.md` を生成（`changelog.yml`）。タグ push でリリースノートも生成。
- **OpenAPI**: コードからの生成コマンドがあればそれを、無ければ通信仕様書から雛形を生成し `docs/api/openapi.yaml` を更新（`openapi.yml`）。

## 生成 AI の活用

- 実装・レビュー・テスト生成にサブエージェントとスラッシュコマンドを活用する。一覧は `.claude/agents/` `.claude/commands/` を参照。
- GitHub 上では `@claude` メンションで Issue/PR に AI を呼び出せる（`.github/workflows/claude-coding.yml`。既定は `.example`。`AI_SETUP.md` のプロファイルで有効化する）。PR には自動 AI レビューが走る（`claude-code-review.yml`）。
  - 認証は **サブスクリプション＝`CLAUDE_CODE_OAUTH_TOKEN`（`claude setup-token` で発行）/ API＝`ANTHROPIC_API_KEY`** のいずれか一方を登録する。サブスクのみでも GitHub 上の自律実装が可能。
- 他の AI（Cursor / Codex / GitHub Copilot）を使う場合も、本ファイルおよび `AGENTS.md` の方針（特にトレーサビリティ最優先）に従う。Copilot 固有の運用は `.github/copilot-instructions.md` と `AI_SETUP.md` を参照。
- **実装を AI に任せる前提の運用全体（起票→実装→検証→レビュー→マージ）と推奨ツールは `docs/ai-workflow.md` を参照する。**

## 自動化・検証・安全

実装の大半を AI に委ねるための仕組みを備える。

- **ガードレール（hooks）**: `.claude/hooks/` が破壊的コマンド（`guard-bash.js`）・秘密情報の混入（`guard-secrets.js`）をブロックし、仕様書なし実装やフロントマター欠如を警告（`check-impl.js`）する。
- **完了前検証**: `/verify` でビルド・テスト・lint を実行し、受け入れ基準と `docs/DEFINITION_OF_DONE.md` を満たすことを確認してから PR を出す。
- **再現可能な環境**: `.devcontainer/` と `scripts/setup.sh`（SessionStart hook が実行）で、AI がビルド・テストを実走できる環境を用意する。
- **CI ゲート**: `ci`（lint/build/test/coverage）・`security`（gitleaks/dependency-review）・`codeql` を必須チェックにし、ブランチ保護でマージを制御する（手順は `docs/ai-workflow.md`）。

## Git 運用

- `main` を安定版とし、直接コミットしない。作業ブランチ → プルリクエスト経由でマージする。
- 1 コミット = 1 論理変更。コミットメッセージ先頭に種別（`feat:` `fix:` `refactor:` `test:` `docs:` `chore:` 等）と起点 ID を付ける。
- 破壊的な git 操作（force push, `reset --hard`）は行わない。

## 禁止事項

- **仕様書（`docs/specs/`）を作成せずに実装へ着手すること**。
- 計画書（特に fixed / Accepted）に反する実装。差異が必要な場合は、新 ADR または計画リポへの変更提案（`/plan-feedback`）で根拠を残す。
- ADR で確定した制約（技術スタック・アーキテクチャ等）の無断逸脱。
- 機密情報（個人情報・認証情報）のコミット。個人設定は `CLAUDE.local.md`（gitignore 推奨）へ。
- 計画外の大規模リファクタ・過剰な抽象化・起こり得ないケースへの防御的実装。

---

## 技術スタック別ルール

<!-- ここに技術スタックに依存する規約を追記する。以下は C#/.NET の例。不要な言語は削除し、自プロジェクトに合わせて書き換えること。 -->

<!--
### C# / .NET（例）
- ターゲット: .NET 8 / C# 12 を既定とする。
- 命名規約: 公開メンバは PascalCase、ローカル変数・引数は camelCase、private フィールドは `_camelCase`。
- ビルド/テスト: `dotnet build` / `dotnet test` が通ること。テストは xUnit を既定とする。
- フォーマット: `dotnet format` で整形する。`nullable` を有効化し、警告ゼロを保つ。
- 受け入れ基準は `[Fact]`/`[Theory]` のテストケースに写像する。
-->
