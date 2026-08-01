---
title: 作業仕様書 — planning submodule の最新化と impl-handoff-kit の全面反映
type: work
status: review
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-01
updated: 2026-08-01
plan_refs:
  - ../../planning/tools/impl-handoff-kit/README.md
  - ../../planning/tools/impl-handoff-kit/HOWTO.md
related_specs:
  - ../README.md
  - ../ai-workflow.md
  - ../../.claude/rules/traceability.md
---

# 作業仕様書: planning submodule の最新化と impl-handoff-kit の全面反映

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（開発基盤・運用規約の同期。NFR 相当）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（新規 IADR も作らない。本作業は上流テンプレートの取り込みであり、本リポ固有の設計判断を行わない）
- 計画書リンク: [impl-handoff-kit README](../../planning/tools/impl-handoff-kit/README.md) / [HOWTO](../../planning/tools/impl-handoff-kit/HOWTO.md)

## 目的・背景

`planning` submodule の pin が `10d8ce2` に固定されており、計画リポジトリ側で確定した
impl-handoff-kit の是正（`repo-template/` 配下）が本リポジトリへ届いていない。とくに
計画リポ側 PR #95「impl-handoff-kit の Claude 設定・GitHub 設定を是正する」で修正された
**`claude_args` の `--allowedTools` 記法の不具合**が本リポジトリにそのまま残っており、
AI 実装・AI レビューが「ジョブは success なのに検証を実行できない」状態になっている。

本作業の方針は「**impl-handoff-kit を正とする**」である。テンプレートで賄える記述は
テンプレートの文面へ寄せ、本リポジトリ独自の記述は次の 2 つに限定する。

1. **技術スタック・レイアウト依存の置換点**（HOWTO 付録 Part B-5 が明示的に「差し替える」と
   指定している箇所。例: `dotnet ... backend/backend.slnx`・`paths: backend/**`）。
2. **本リポジトリ固有の成果物・裁定**（AST 固有の CI ジョブ、`docs/adr/`・`CHANGELOG.md`・
   `docs/operations/` 等の実コンテンツ、必須仕様書の網羅裁定 #211）。

テンプレート側の不足・混入は実装リポ側で握り潰さず、`/plan-feedback` で計画リポへ環流する。

## 対象範囲

- 対象:
  - `planning` submodule の pin 前進（`10d8ce2` → `6a1cc9f`）
  - `repo-template/` と本リポジトリ直下の全差分の解消（採否の判断と反映）
  - テンプレート側の不足・混入のフィードバック起票
- 対象外:
  - フロントエンドの npm スクリプト追加（テンプレートの `frontend*.example.yml` を
    適用可能にするための改修。後述「計画書との差異」参照）
  - 本リポジトリの機能実装・振る舞いの変更

## 設計

### 判断ルール（差分 1 件ごとの採否）

| 種別 | 判断 |
| --- | --- |
| テンプレートが前進しており、本リポが取りこぼしている | **テンプレートを採用**（本リポ固有の issue 番号等の付記は落とす） |
| テンプレートが汎用文面、本リポが固有 issue 番号で書き換えている | **テンプレートを採用**（固有番号は履歴・仕様書側に残るため文面から落として支障ない） |
| HOWTO Part B-5 が指定するスタック依存の置換点 | **本リポの値を維持**（`backend/backend.slnx` 等） |
| 本リポ固有の実コンテンツ（ADR 索引 / CHANGELOG / 運用仕様 / 固有 CI ジョブ） | **本リポを維持** |
| 本リポにありテンプレートに無い改善 | **本リポを維持し、テンプレートへフィードバック** |
| テンプレートにあり本リポに適用できない（他プロジェクト固有の混入） | **取り込まず、テンプレートへフィードバック** |

### 主な反映内容

**そのままテンプレートを採用するもの**

- `.claude/settings.json`（本リポ版は kit の真部分集合だった。`Grep`/`Glob`/`Bash(git show:*)`/
  `Bash(gh issue view:*)`/`Bash(gh pr view:*)`/`Bash(gh run list:*)` が欠落）
- `.claude/hooks/check-impl.js`（統合ブランチ候補に `origin/main`/`main` を追加）
- `.claude/rules/traceability.md`（複数プロジェクト跨ぎの ID 修飾・クロスリポジトリ issue 番号の
  修飾という 2 節がテンプレートにのみ存在した）
- `.claude/commands/plan-feedback.md` / `AGENTS.md` / `scripts/setup.sh` /
  `scripts/check-doc-links.js` / `scripts/gen-openapi-skeleton.js` / `.github/dependabot.yml`
- `scripts/check-commit-messages.js`（`loadExistingPlanAdrIds` が `projects/` 配下を走査して
  全プロジェクトの計画 ADR を集める汎用版。本リポ版は `ai-stock-trading` 決め打ちだった）
- `scripts/check-ai-workflow-config.js`（**新規**。`claude_args` の記法不具合・ブロック内コメント・
  「SDK を用意して実行ツールを許可していない」不一致を機械検出する）と、それを走らせる
  `ci.yml` の `ai-workflow-config` ジョブ

**テンプレートを採用しつつスタック依存部だけ維持するもの**

- `.github/workflows/ci.yml`（共通ジョブはテンプレート文面へ。`dotnet ... backend/backend.slnx`・
  `--filter "Category!=Integration"`・AST 固有ジョブ（`shell-scripts` / `runtime-scaffold` /
  `consumer-endpoint-names` / `frontend` / `frontend-e2e`）は維持）
- `.github/workflows/claude-coding.yml` / `claude-code-review.yml`
  （`--allowedTools` を**引用符付き 1 引数・カンマ区切り**へ是正。`concurrency`・`timeout-minutes`・
  レビュー側への実行系ツール複製・計画書の探索順（`planning/` を先に）・`.claude-pr/` の注記を取り込む）
- `.github/workflows/changelog.yml`（`AUTOMATION_PR_TOKEN` フォールバックと既知制約の注記）
- `.github/workflows/doc-links-planning.yml`（失敗時の issue 起票による気付き導線）
- `.github/workflows/security.yml` / `pr-title.yml` / `openapi.yml`
  （`openapi.yml` の `paths:` は `src/**` ではなく本リポの `backend/**` を維持）

**アクションのバージョンは本リポの値を維持する**（テンプレートが後追いのため）:
`actions/setup-node@v7` / `actions/setup-dotnet@v6` / `peter-evans/create-pull-request@v8`。

**本リポを維持するもの**: `CHANGELOG.md` / `docs/adr/README.md` / `docs/operations/operations.md` /
`docs/tech/tech-requirements.md` / `scripts/commit-allowlist.json` / `scripts/changelog-overrides.json` /
`.gitignore`（テンプレートの全行を含む真の上位集合であることを機械確認済み）/
`CLAUDE.md`・`docs/README.md` の AST 固有節（技術スタック別ルール・必須仕様書の網羅裁定 #211・`runbook` 種別）。

### 併せて是正する不整合

- `.github/workflows/copilot-setup-steps.yml` の `dotnet-version` が `8.0.x`（テンプレート既定のまま）
  だったが、本リポの対象は net10.0 である。他ワークフローと同じ `10.0.x` へ揃える
  （HOWTO Part B-5 の「スタックに合わせて差し替える」置換点）。

## 受け入れ基準

- [ ] `planning` submodule が `origin/main` の先端（`6a1cc9f`）を指す
- [ ] `repo-template/` と本リポジトリの残差分が、上表の判断ルールで説明できるものだけになる
- [ ] `node scripts/check-ai-workflow-config.js --self-test` と `node scripts/check-ai-workflow-config.js` が合格する
- [ ] `node scripts/scripts.test.js` が全件合格する（テンプレート由来のテスト＋本リポ固有のテスト双方）
- [ ] `node scripts/check-doc-links.js` が **PR CI と同条件（planning 未 populate）で** 合格する
      （planning を populate すると別件の既存破損 20 件が出る。後述「本作業で扱わない既存不具合」参照）
- [ ] `node scripts/check-commit-messages.js --title "<本 PR タイトル>"` が合格する
- [ ] `dotnet build backend/backend.slnx` が通る（本作業はコードを変更しないため回帰しないことの確認）
- [ ] テンプレート側の不足・混入が `/plan-feedback` として起票されている

## テスト方針

本作業はコードの振る舞いを変えないため、xUnit の追加は行わない。検証は次の 3 系統で行う。

1. **Node 製チェッカの自己試験・単体テスト**: `scripts.test.js`（テンプレート由来のケースと
   AST 固有ケースを併合）と `check-ai-workflow-config.js --self-test`。
2. **実ツリーに対する機械検査**: `check-ai-workflow-config.js`（実ワークフロー）・
   `check-doc-links.js`・`check-consumer-endpoint-names.js`・`validate-pipeline-config.js`。
3. **回帰しないことの確認**: `dotnet build backend/backend.slnx`。

`scripts.test.js` は「実ツリー: ワークフローのツール許可設定に不備が無い」ケースを含むため、
`--allowedTools` の記法是正が入るまで赤になる。これを是正前後で実測し、PR 本文に対比を残す。

## 計画書との差異

- 差異: あり（テンプレート側の不足・混入。いずれも `/plan-feedback` で計画リポへ環流する）
  1. **アクションのバージョンがテンプレートで古い**（`setup-node@v6` / `setup-dotnet@v5` /
     `create-pull-request@v7` / `upload-artifact@v4`）。実装リポ側では Dependabot が前進させるため、
     テンプレートを取り込むたびに巻き戻る。
  2. **他プロジェクト固有物の混入**: `scripts/verify-qdrant-attribute-payload.sh` と
     `scripts/README.md` のその行は microservices-platform 固有（IADR-0014 / Qdrant）であり、
     テンプレートの配置物として一般性が無い。本リポには取り込まない。
  3. **`frontend.example.yml` / `frontend-tests.example.yml` がそのままでは適用できない**。
     見出しコメントが他プロジェクトの ID（`Issue #126` / `IADR-0033` / `IADR-0034`）を指すうえ、
     `npm run build` / `test:coverage` / `test:e2e` を前提とするが、テンプレートは
     `frontend/` の雛形も package.json も同梱していない。本リポは同等の検査を `ci.yml` の
     `frontend` / `frontend-e2e` ジョブで実施済みのため取り込まない。
  4. **`gen-changelog.js` の `applyOverride` に overrides を注入できない**。テンプレート版は
     モジュールスコープの `OVERRIDES` に固定されるため、単体テストが
     `changelog-overrides.json` の実データに依存する（実データを空にするとテストが書けない）。
     本リポは第 2 引数で注入できるよう後方互換で拡張済み。
  5. **`commit-allowlist.json` の運用強化がテンプレートに無い**: 登録カテゴリ（A: 規約導入前 /
     B: 規約導入後・書き換え不可）の区別と、各エントリが「実在・統合ブランチから到達可能・
     実際に規約違反」であることを検証する回帰テスト（幻 SHA の再発防止）。
  6. **`openapi.yml` の `paths:` にある `src/**` が置換点として文書化されていない**。
     HOWTO Part B-5 の差し替え表に `openapi.yml` の行が無く、リポジトリレイアウトが
     `src/` でないプロジェクトでは黙って起動しなくなる。
  7. **`runbook` 種別が新規 `/new-spec` に無い**: 運用 Runbook（運用仕様書の下位の手順書）は
     汎用的な文書種別だが、テンプレートの `CLAUDE.md` / `docs/README.md` の任意仕様書表にも
     `.claude/commands/new-spec.md` にも定義が無い。

## 本作業で扱わない既存不具合（別 issue へ切り出す）

submodule を populate した状態で `node scripts/check-doc-links.js` を実行すると、**planning 配下への
破損リンク 20 件**が検出される。これは本作業が作り込んだものではなく、**pin 前進の前後で同一**である
（`07_adr/` のファイル名一覧を新旧 pin で突き合わせた結果は完全一致で、参照されている
`ADR-0008_staged-rollout.md` 等の名前はどちらの pin にも存在しない）。PR CI の `doc-links` ジョブは
submodule を取得しないため planning リンクを検査対象外にしており、この隙間で蓄積したものである。

破損は 2 種類に分かれ、後者は機械的な置換で直してはならない。

1. **ファイル名の綴りだけが古いもの**（計画側の実体は存在する。例:
   `ADR-0008_staged-rollout.md` / `ADR-0008_backtest-and-staged-rollout.md` →
   `ADR-0008_staged-gates-and-backtest.md`、`ADR-0009_pause-resume.md` →
   `ADR-0009_pause-resume-and-lockout-states.md`、`03_usecases/UC-01_*.md` →
   `03_usecases/01_usecases.md`）。
2. **参照先 ADR そのものを取り違えているもの**（内容の誤帰属）。`ADR-0007` を
   「kill switch 認可」として引用している箇所があるが、計画側の ADR-0007 は
   「取引商品は現物基本＋信用を設定で有効化し、取引ガードをソフト設定で強制する」であり、
   認可の ADR ではない。パスだけ差し替えると「壊れたリンク」が「誤ったリンク」に変わるため、
   引用の妥当性を判断したうえで直す必要がある。

本作業（ツール同期）に 20 件の文書引用監査を混ぜるとレビュー単位が濁るため、別 issue とする。
なお本作業で取り込む `doc-links-planning.yml` の失敗通知ステップにより、この既存の赤は
夜間ジョブから issue として可視化されるようになる（キットが意図した「気付き導線」）。

## 未決事項

- なし（上記フィードバックの採否は計画側 `/triage-feedback` の判断に委ねる）
