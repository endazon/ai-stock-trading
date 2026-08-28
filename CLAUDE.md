# CLAUDE.md — 実装作業リポジトリ

このリポジトリは、上流工程リポジトリ（`project-planning`）で確定した計画書を**実装する**ための作業リポジトリである。Claude はこのファイルを毎セッション読み込む。指示は具体・簡潔に保つ。

> 技術スタック依存の規約は末尾の「技術スタック別ルール」へ追記する。**キット（`impl-handoff-kit`）は bootstrap 専用であり、既存リポジトリに追随義務は無い**（ADR-0029 決定 6。同期のバイト一致検査は退役済み）。
>
> **最初に `AI_SETUP.md` を読む**。利用可能な AI（Claude Code サブスク / Anthropic API / GitHub Copilot）の宣言と、有効化するファイル・シークレットがそこで決まる。

## 目的

- 計画書（要求・ユースケース・画面・技術検討・ADR）に忠実に実装する
- 計画と実装の**トレーサビリティ**（追跡可能性）を保つ
- 生成 AI を活用しつつ、人間がレビューできる変更単位を維持する
- **資料の主従をディレクトリ構造で示す**（ADR-0029 決定 1）—— **`docs/` ＝人が読む生きた文書**、**`.ai-context/` ＝ AI 向け文脈資料・凍結記録**（実装ADR・作業仕様書。本文プロズを後から書き換えない）。[`.ai-context/README.md`](.ai-context/README.md) 参照
- **リポジトリの位置づけ（主たる成果物と付随成果物の主従）を README 冒頭で明示し、計画書（ビジョン・ADR）と一致させ続ける**（位置づけの漂流は実装・文書の齟齬の温床になる）

## 計画リポジトリとの関係

- **計画書（要求・UC・画面設計・技術検討・ADR）と裁定の記録は `project-planning` の `projects/ai-stock-trading/`（`00_vision` 〜 `07_adr`）にある。** 起点 ID（`FR-xx` / `NFR` / `UC-xx` / `SC-xx` / `ADR-xxxx`）の意味とレンジは `.claude/rules/traceability.md` と `traceability.repo.md`（自動適用）が正本であり、ここへ複写しない。
- **本リポジトリは planning に依存しない（submodule は張らない。ADR-0029 決定 2）。** 参照手段は **GitHub 上の URL**（`https://github.com/endazon/project-planning/...`）か**隣接クローン**（既定パス `../project-planning`）で、いずれも**読み取り専用・pin 固定なし**である。実装着手前に該当 ID の計画書を必ず読む。
- **計画への指摘（誤り・不足・新たな制約）は project-planning の GitHub issue で起票する**（`feedback.yml` テンプレート・`feedback` / `decision-needed` ラベル。ADR-0029 決定 5）。**起票前に同件の既存 issue を必ず検索する。****裁定の完了記録は planning 側 `projects/ai-stock-trading/10_feedback/` に残り、本リポジトリには残さない。**
- **新規の実装ADR（IADR）・作業仕様書の起草は従来どおり本リポジトリ内 `.ai-context/` で行う**（ADR-0029 決定 7）—— 実装判断の記録は実装変更と同一 PR に置く。

## 実装の進め方（AI 活用の基本フロー）

実装の起点となる ID（FR/UC）が与えられたら、**まず仕様書を作成してから**、以下の順で進める。

1. **計画書を読む**: 対象の要求・UC・画面設計を読み、受け入れ基準を把握する（参照手段は前掲「計画リポジトリとの関係」）。
2. **ADR 制約を確認する**: 関連する ADR を読み、確定済みの技術・設計上の制約に違反しないことを確認する。曖昧な場合は実装を止め、人間に確認する。
3. **仕様書を作成する（必須・着手前）**: `.ai-context/specs/<YYYYMMDD>_<概要>.md` に作業仕様書を作成する（`/new-spec`）。以降の実装は必ずこの仕様書に沿って進める。**仕様書なしで実装へ着手しない。** 該当する必須仕様書の作成・更新と実装ADR（`IADR`）は後述「仕様書」に従う。
4. **タスクに分解する**: 影響範囲・必要なテストを洗い出す（`/plan-to-tasks` を活用）。
5. **実装する**: 仕様書・計画書に忠実に実装する。計画外の機能追加・過剰な抽象化を行わない。
6. **テストを書く**: 受け入れ基準をテストケースへ写像する（`test-author` エージェントを活用）。
7. **検証する（完了前）**: `/verify` でビルド・テスト・lint を実行し、受け入れ基準と `docs/DEFINITION_OF_DONE.md` を満たすことを確認する。
8. **トレーサビリティを残す**: 後述の規約に従い、起点 ID をブランチ名・コミット・コード・PR に残す。
9. **計画へ環流する**: 実装中に計画書の誤り・不足・新たな制約を見つけたら、**project-planning へ GitHub issue を起票する**（前掲「計画リポジトリとの関係」）。

## 実装作業の進め方（計画リポの運用ガイド）

実装作業の運用標準は **project-planning の `docs/ai-implementation-workflow-guide.md`（fixed）を正本**とする（submodule では張らない。参照手段は前節のとおり）。拘束点の要約:

- **並列作業**はファイル領域の非重複で判定し、マージは **FIFO**（rebase → CI → merge）で流す。
- **同型・低リスクの変更は 1 PR に束ねる**（PR を刻まない）。
- **フェーズ末監査**は実装と別のエージェントが **diff＋受け入れ基準のみ**で実施し、**証跡を必ず残す**。
- **裁定依頼は小さく高頻度**に出す。**blocked 判定は棚卸しごとに再検証**する。
- **検査器・規約の追加は同型事故 2 回から**。毎セッション必読の規約は**総量 50KB＝51,200 バイトの予算**内に収める（正本: 運用ガイド §8。planning#364）。**母集合はエージェントごとに分けて測り、合算しない**（`AGENTS.md` は Claude 以外が読み、Claude は読まない）。**予算の増減を伴う作業に入る前に [`docs/ai-workflow.md`](docs/ai-workflow.md) §必読規約の総量予算の測り方 を読む。** CI では `scripts/check-reading-budget.js` が同じ母集合を測る。
- **人間の関与**はフェーズ計画承認・監査サンプリング・裁定の 3 点（＋required check 配備までのマージ操作）。
- **kit との乖離は受容する**（ADR-0029 決定 6）。リポ個別に直した運用装備を kit へ環流する義務も追随 issue も無い。**乖離に気付いたら受容として記録する**。

## トレーサビリティ規約

実装と計画書を相互に追跡できるよう、起点となる ID を以下の箇所に残す。

- **ブランチ名**: `feat/FR-012-<概要>` のように起点 ID を含める。
- **コミットメッセージ**: 先頭に種別と ID を付ける。例: `feat(FR-012): ログイン画面のバリデーションを実装`。
- **コード**: 計画書由来の実装には、該当箇所のコメントに ID を残す。例: `// FR-012, UC-03: 入力バリデーション`。
- **PR**: PR テンプレートの該当欄に実装した FR/UC・関連 ADR・受け入れ基準のチェックを記入する。
- 🔴 **`docs/` 配下だけは書き方が違う。** 計画 ID（FR/UC/SC/ADR/NFR）・IADR・仕様書・修飾付き issue 参照を**表示テキストへ書かず**、frontmatter 終端直後・H1 の直前の **trace ブロック**（HTML コメント。1 文書 1 個）へ非表示メタデータとして持つ（ADR-0029 決定 4）。**書式の正本は同決定、機械検査は `scripts/check-trace-blocks.js`**（CI の `trace-blocks` ジョブ）、運用の詳細は [`docs/traceability-appendix.md`](docs/traceability-appendix.md) §trace ブロック。**`.ai-context/` の凍結記録には適用しない**（本文にそのまま書く）。
- 詳細な書式は `.claude/rules/traceability.md` および `traceability.repo.md` を参照（自動適用）。

## 仕様書（`docs/` と `.ai-context/`）

計画書を実装向けに詳細化した資料は、上記の主従に従い 2 箇所に分かれる（ADR-0029 決定 1）。`/new-spec <種別> <ID|topic>` で作成する。

- **着手前に必須**なのは**作業仕様書**（`.ai-context/specs/<YYYYMMDD>_<概要>.md`）である。重要な実装判断は**実装ADR**（`.ai-context/adr/`、`IADR-XXXX`）に必ず残す（計画ADR `ADR-XXXX` と区別する）。
- **種別の一覧（必須 10 / 任意 9）と出力先・粒度は [`docs/README.md`](docs/README.md) が正本である。ここへ複写しない**（2 箇所に置くと片方が古くなる）。

> **機能仕様書・テスト仕様書の必須範囲は [`docs/README.md`](docs/README.md) の裁定（網羅裁定 #211）が正本**。安全・統制の中核 FR（リスク統制・ペーパートレード・バックテスト・取引ガード・段階ゲート）のみ必須、他は作業仕様書と xUnit テストを正の記録として任意とする。

- **`docs/` 配下の仕様書は、起点 ID・計画書参照・関連仕様書リンクを表示テキストに書かない**（前節の trace ブロック）。可視のリンクとして張ってよいのは**同一リポジトリの `docs/` 内**に限る。`.ai-context/` 配下は従来どおり本文に書く。

## 補助成果物の自動生成

補助成果物は生成可能なら必ず生成し、CI で自動更新する（`scripts/` ＋ `.github/workflows/`）。

- **CHANGELOG**: コミット履歴（`種別(起点ID): 要約`）から `CHANGELOG.md` を生成（`changelog.yml`）。タグ push でリリースノートも生成。
- **OpenAPI**: コードからの生成コマンドがあればそれを、無ければ通信仕様書から雛形を生成し `docs/api/openapi.yaml` を更新（`openapi.yml`）。

## 生成 AI の活用

- 実装・レビュー・テスト生成にサブエージェントとスラッシュコマンドを活用する。一覧は `.claude/agents/` `.claude/commands/` を参照。
- 他の AI（Cursor / Codex / GitHub Copilot）を使う場合も、本ファイルおよび `AGENTS.md` の方針（**特にトレーサビリティ最優先**）に従う。Copilot 固有の運用は `.github/copilot-instructions.md`。
- 役割スロット（orchestrator / worker / reviewer）の配役とフォールバック連鎖は `ai-roster.json` で宣言する。差し替えの契約・切り戻しの正本は `docs/ai-orchestration.md`（都度読み）。
- **運用全体（起票→実装→検証→レビュー→マージ）と推奨ツールは `docs/ai-workflow.md`、AI の有効化・認証・ブラウザ操作の統一（対話的操作は Playwright CLI に一本化し MCP は入れない。CI の E2E ランナーは別の関心事で既存選択を覆さない）は `AI_SETUP.md` が正本である**（GitHub 上の `@claude` 呼び出しと自動 AI レビューの配線もそちら）。

## 自動化・検証・安全

実装の大半を AI に委ねるための仕組みを備える。

- **ガードレール（hooks）**: `.claude/hooks/` が破壊的コマンド（`guard-bash.js`）・秘密情報の混入（`guard-secrets.js`）をブロックし、仕様書なし実装・フロントマター欠如を警告（`check-impl.js`）する。
- **完了前検証**: `/verify` でビルド・テスト・lint を実行し、受け入れ基準と `docs/DEFINITION_OF_DONE.md` を満たすことを確認してから PR を出す。
- **再現可能な環境**: `.devcontainer/` と `scripts/setup.sh`（SessionStart hook が実行）で、AI がビルド・テストを実走できるようにする。
- **CI ゲート**: `ci`（lint/build/test/coverage ＋ 文書系検査）・`security`（gitleaks/dependency-review）・`codeql` をブランチ保護で制御する（必須にする check 名と配備状況は `docs/ai-workflow.md`）。
- **文書・トレーサビリティの機械検査**（一覧と挙動は `scripts/README.md`）: 資料再編で **`check-trace-blocks`**（trace ブロック規約）と **`gen-knowledge-graph --check`**（参照の in-repo 実在）を新設し、🔴 **planning 依存の検査器（pin 鮮度・kit 同期のバイト一致・環流の未送付／status 突合）は退役させた。復活させない**（ADR-0029 決定 2・5・6）。乖離の検知は issue 運用と定期棚卸し（`backlog-audit.yml`）に委ねる。

## Git 運用

- `main` を安定版とし、直接コミットしない。作業ブランチ → プルリクエスト経由でマージする。
- 1 コミット = 1 論理変更。コミットメッセージ先頭に種別（`feat:` `fix:` `refactor:` `test:` `docs:` `chore:` 等）と起点 ID を付ける。
- 破壊的な git 操作（force push, `reset --hard`）は行わない。

## 禁止事項

- **仕様書（`.ai-context/specs/`）を作成せずに実装へ着手すること**。
- 計画書（特に fixed / Accepted）に反する実装。差異が必要な場合は、新 IADR または planning への issue 起票で根拠を残す。
- ADR で確定した制約（技術スタック・アーキテクチャ等）の無断逸脱。
- **planning への依存の再導入**（submodule 化・pin 固定・計画書をビルド／CI の前提にすること）。ADR-0029 決定 2 を覆すには新しい計画 ADR が要る。
- **`docs/` 配下の表示テキストへ計画 ID・IADR・修飾付き issue 参照を書くこと**（trace ブロックへ入れる）。
- 機密情報（個人情報・認証情報）のコミット。個人設定は `CLAUDE.local.md`（gitignore 推奨）へ。
- 計画外の大規模リファクタ・過剰な抽象化・起こり得ないケースへの防御的実装。

---

## 技術スタック別ルール

### C# / .NET

- 対象: **ai-stock-trading**（生成AI株取引自動化）。計画書の所在と参照手段は前掲「計画リポジトリとの関係」。
- 基盤: microservices-platform の拡張（可変部分への組み込み。基盤無改修）。リポ構成・規約は基盤実装リポ `../microservices-platform` に揃える（IADR-0001）。
- ターゲット: **net10.0 / C# 13**（ルート `Directory.Build.props`＝単独ビルド用 import-chain フォールバック。IADR-0046）。パッケージは Central Package Management（ルート `Directory.Packages.props`）で一元管理し、バージョンは基盤リポと揃える。
- ソリューション: `backend/backend.slnx`（ユニットリポジトリレイアウト。IADR-0046）。共有物は `backend/Shared/AiStockTrading.Shared.*`。**サービスは単一プロジェクト＋VSA へ全面移行中**（[IADR-0259](.ai-context/adr/IADR-0259_single-project-vsa-structure.md)。MSP も同樹形へ移行中＝**逸脱ではなく整合**）—— 新は `Services/<Name>/` 直下の `<Name>.csproj` ＋ `Features/<集約>/ Domain/ Infrastructure/ Hosted/ Common/ Tests/<Name>.Tests.csproj`（3 段目のスライス分割は MSP も未実装のため採らない）、旧は `Services/<Svc>/{src,tests}` の `<Svc>.{Api,Application,Domain,Infrastructure}`。🔴 **サービス単位で新旧が混在する**（現物を見てから触る。**移送波の完了までは新規コードも現行配置で書く**）。依存方向は自前検査器（IADR-0256）のみで強制する（NsDepCop は導入しない）。**移送波そのものでは `namespace` を変えないが、完全整合（`<Svc>Service.*` へ。MSP と同規則）は独立した後続波として決定済み**（IADR-0259 決定5）。
- 命名規約: 名前空間プレフィックスは現状 `AiStockTrading`（IADR-0259 決定5③の後続波で `<Svc>Service.*` へ整合予定）。公開メンバは PascalCase、private フィールドは `_camelCase`。テストメソッド名は日本語可（ただし識別子に全角記号は使えない）。
- ビルド/テスト: `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が通ること。テストは **xUnit v3**（ID は `xunit.v3`。`xunit` は v2 系）+ **AwesomeAssertions**（`using Xunit;` を忘れない）。**FluentAssertions は不採用**（v8 の商用化。`scripts/check-banned-libraries.js` が再混入を止める。platform ADR-0030 / #351）。
  - v3 での注意（#352 / 作業仕様書 `20260803_352_xunit-v3-migration`）: `IAsyncLifetime` は `ValueTask` を返す。`ITestOutputHelper` は `Xunit` 名前空間（`Xunit.Abstractions` は無い）。テストアセンブリは実行可能（`OutputType=Exe` は自動設定・`.csproj` への記述不要）。実行は `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio` 3.x の **VSTest 経路**を維持する（`dotnet test` のフィルタとカバレッジ収集を使うため）。
- フォーマット: `dotnet format` で整形する。`nullable` 有効・警告ゼロを保つ。
- 受け入れ基準は `[Fact]`/`[Theory]` のテストケースに写像し、コメントに起点 ID（FR/UC/ADR）を残す。
- リスク統制・取引ガードの既定値は計画書の全体前提条件（05_trading-assumptions §5）と一致させ、`TradingDefaults` のテストで固定する。
