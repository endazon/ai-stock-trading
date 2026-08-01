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
  - `planning` submodule の pin 前進（`10d8ce2` → `bf94477`。作業中に計画側 #98 がマージされたため kit は 2 度取り込み、その後 pin だけ tip へ追従した）
  - `repo-template/` と本リポジトリ直下の全差分の解消（採否の判断と反映）
  - テンプレート側の不足・混入のフィードバック起票
- 対象外:
  - フロントエンドの npm スクリプト追加（`frontend*.example.yml` は `.example` のまま取り込み、
    有効化しない。frontend の検査は `ci.yml` の `frontend` / `frontend-e2e` ジョブが担う）
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
  （`openapi.yml` の `paths:` は `src/**` ではなく本リポの `backend/**` を維持。
  テンプレートが追加した【置換点】コメントは取り込む）
- `scripts/commit-allowlist.json`（テンプレートの `_categories` / `_rules` 構造を採用し、
  `allow` は本リポの実コミット 2 件を維持する。テンプレートの配布既定は空）
- `scripts/gen-changelog.js`（テンプレート版は呼び出し側にクラッシュ不具合があるため、
  そこだけ本リポの是正を維持する。後述「計画書との差異」参照）

**アクションのバージョンは本リポの値を維持する**（テンプレートが後追いのため）:
`actions/setup-node@v7` / `actions/setup-dotnet@v6` / `peter-evans/create-pull-request@v8`。

**本リポを維持するもの**: `CHANGELOG.md` / `docs/adr/README.md` / `docs/operations/operations.md` /
`docs/tech/tech-requirements.md` / `scripts/commit-allowlist.json` / `scripts/changelog-overrides.json` /
`.gitignore`（テンプレートの全行を含む真の上位集合であることを機械確認済み）/
`CLAUDE.md`・`docs/README.md` の AST 固有節（技術スタック別ルール・必須仕様書の網羅裁定 #211・Runbook の実例リンク）。

### 併せて是正する不整合

- `.github/workflows/copilot-setup-steps.yml` の `dotnet-version` が `8.0.x`（テンプレート既定のまま）
  だったが、本リポの対象は net10.0 である。他ワークフローと同じ `10.0.x` へ揃える
  （HOWTO Part B-5 の「スタックに合わせて差し替える」置換点）。

## 受け入れ基準

- [ ] `planning` submodule が `origin/main` の先端（`bf94477`）を指す
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

- 差異: あり（テンプレート側の不足・混入。`/plan-feedback` で計画リポへ環流する）

### 第 1 巡（pin `6a1cc9f`）で起票し、計画側 [#97](https://github.com/endazon/project-planning/issues/97) → PR #98 で**すべて反映済み**

| # | 指摘 | 反映結果（pin `12cc9b8`） |
| --- | --- | --- |
| 1 | アクションのバージョンがテンプレートで古く、同期のたびに巻き戻る | `setup-node@v7` / `setup-dotnet@v6` / `create-pull-request@v8` へ追随。本リポとの差分が消えた |
| 2 | `verify-qdrant-attribute-payload.sh` が他プロジェクト固有（Qdrant / ABAC）で混入 | テンプレートから削除 |
| 3 | `frontend*.example.yml` が他プロジェクト ID を含み、前提の npm スクリプトも同梱されず適用不能 | ID を一般化し、前提スクリプトを見出しと HOWTO Part B-5 に明記。**本リポも `.example` のまま取り込んだ**（有効化はしない。frontend は `ci.yml` で検査済み） |
| 4 | `gen-changelog.js` の `applyOverride` に overrides を注入できない | 第 2 引数で注入可能に。ただし**呼び出し側に不具合が残った**（下記の新規指摘） |
| 5 | `commit-allowlist.json` の運用強化（カテゴリ A/B・幻 SHA 検出）が無い | `_categories` / `_rules` として明文化し、テストも汎用化（特定 SHA のハードコードを廃止） |
| 6 | `openapi.yml` の `paths: src/**` が置換点として文書化されていない | HOWTO Part B-5 の差し替え表に追加。ワークフローにも【置換点】コメント |
| 7 | `runbook` 種別が `/new-spec` に無い | `runbook` に加え `how-to`（`docs/how-to/`）も新設 |

### 第 2 巡（pin `12cc9b8`）で新たに判明した指摘

**[🔴 重大] テンプレートの `gen-changelog.js` が全消費者でクラッシュする。**
指摘 4 の反映で `applyOverride(c, overrides = OVERRIDES)` と第 2 引数を足した一方、
呼び出し側が point-free の `.map(applyOverride)` のまま残っている。`Array.prototype.map` は
コールバックへ `(element, index, array)` を渡すため、`index`（数値）が `overrides` を上書きし、
既定値が効かず 1 件目から `TypeError: overrides.find is not a function` になる。
テンプレートの `scripts/` を取り出して実測し確認済み。

```
$ node <kit>/scripts/gen-changelog.js --out /tmp/CL.md
TypeError: overrides.find is not a function
    at applyOverride (gen-changelog.js:43:24)
    at Array.map (<anonymous>)
    at commits (gen-changelog.js:112:6)
```

テンプレートの `scripts.test.js` は `applyOverride` **単体**しかテストしておらず、`gen-changelog.js` を
起動しないためこの形を検出できない。本リポでは呼び出し側を `.map((c) => applyOverride(c))` に戻し、
**実際に起動して CHANGELOG が生成できることを確かめる回帰テスト**を追加した（point-free へ戻す変異で
赤になることを実測済み）。テンプレートにも同じ是正とテストが要る。


**[🟡 推奨] 計画 ADR の実在性検査が、他プロジェクトの ADR 番号を「実在」として受理する。**
テンプレートの `loadExistingPlanAdrIds` は `projects/` 配下を全走査して和集合を作る。ところが
計画 ID はプロジェクトごとに独立採番のため番号帯が丸ごと重複し、他プロジェクトにしか存在しない ID も
実在扱いになる。本リポの planning submodule には `ai-stock-trading`（ADR-0001〜0015）と
`microservices-platform`（ADR-0001〜0032）が入っており、実測で和集合 32 件・MSP 固有の
`ADR-0019` / `ADR-0032` が素通りした。これでは本検査の目的（実体と別内容の ADR を名乗る件名の混入検出）が働かない。
テンプレートが同時に追加した `.claude/rules/traceability.md` の文言
（「実在性検査は**本リポジトリの名前空間しか解決できない**」）とも実装が矛盾する。

本リポでは `PLAN_PROJECT`（既定 `ai-stock-trading`・置換点）で**自プロジェクトの名前空間だけ**を
実在集合とし、自プロジェクトを解決できない構成では従来どおり全走査へ退避する（fail-open）ように是正した。
実測で和集合 32 件 → 自名前空間 15 件になり、`ADR-0019` / `ADR-0032` が正しく違反として検出される。
合成 `projects/` を使った回帰テストも追加した。テンプレート側にも同じ設計が要る。

### 第 3 巡（残差分の棚卸し）で判明した指摘

`repo-template/` と本リポジトリの残差分を 1 件ずつ「固有の実コンテンツ」「スタック依存の置換点」
「キット側の課題」に分類したところ、前 2 者で説明できないものが 4 件残った。
[project-planning#106](https://github.com/endazon/project-planning/issues/106) として起票済み。

| # | 指摘 | 性質 |
| --- | --- | --- |
| 1 | 削除済み `verify-qdrant-attribute-payload.sh` の行が `scripts/README.md` に残る | #97 指摘 2 の追随漏れ |
| 2 | 他プロジェクト固有の Issue 番号・SHA が **9 ファイル**に残り、同期のたびに再一般化が要る（実際に本作業で同じ箇所を 2 度一般化した） | #97 指摘 1 と同じ「巻き戻る」類型 |
| 3 | `changelog-overrides.json` の配布既定が他プロジェクトの実コミット 2 件のまま（`commit-allowlist.json` は空に是正済みで非対称）。`hash` は前方一致のため誤爆すると生成物を黙って誤る | #97 指摘 5 の非対称 |
| 4 | `docs/ai-workflow.md` に「`paths:` フィルタ付きワークフローを必須チェックにすると**永久 pending** でマージ不能になる」注意が無い | 一般則の不足 |

なお planning の tip は作業中にさらに 4 コミット進んだが、いずれも計画リポ自身の workflow への
Dependabot 更新で `tools/impl-handoff-kit/` には無変更である（差分ファイル一覧が pin 前後で完全一致することを確認）。
pin だけ tip（`bf94477`）へ追従させた。

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
