---
title: 作業仕様書 — planning submodule の最新化と impl-handoff-kit の全面反映
type: work
status: review
related_ids: [NFR]
issue: 323
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
- 起票: [#323](https://github.com/endazon/ai-stock-trading/issues/323)（bug・最優先「`claude_args` の記法誤りで @claude 実装と AI レビューがビルド・テストを実行できない」）を本作業が解消する
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
  - `planning` submodule の pin 前進（`10d8ce2` → `168f53d`。作業中に計画側で #98 / #105 / #107 / #110 / #113 / #116 / #119 / #125 / #127 / #132 / #135 が続けてマージされたため計 11 巡取り込んでいる）
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

- [ ] `planning` submodule が `origin/main` の先端（`168f53d`）を指す
- [ ] `repo-template/` と本リポジトリの残差分が、上表の判断ルールで説明できるものだけになる
- [ ] `node scripts/check-ai-workflow-config.js --self-test` と `node scripts/check-ai-workflow-config.js` が合格する
- [ ] `node scripts/scripts.test.js` が全件合格する（テンプレート由来のテスト＋本リポ固有のテスト双方）
- [ ] `node scripts/check-doc-links.js` が **PR CI と同条件（planning 未 populate）で** 合格する
      （planning を populate すると別件の既存破損 20 件が出る。後述「本作業で扱わない既存不具合」参照）
- [ ] `node scripts/check-commit-messages.js --title "<本 PR タイトル>"` が合格する
- [ ] `dotnet build backend/backend.slnx` が通る（本作業はコードを変更しないため回帰しないことの確認）
- [ ] テンプレート側の不足・混入が `/plan-feedback` として起票されている

## issue #323 の受け入れ基準（実測）

本作業は [#323](https://github.com/endazon/ai-stock-trading/issues/323) を解消する。同 issue の受け入れ基準を実測で検証した結果は次のとおり。

| 受け入れ基準 | 検証 |
| --- | --- |
| `check-ai-workflow-config.js` が両ファイルについて不備 0 件 | ✅ 是正前 16 件（coding 14 / review 2）→ 是正後 0 件。CI の `ai-workflow-config` ジョブで常時検査 |
| ジョブログの `SDK options:` で `allowedTools` が割れていない | ✅ レビュー run `30688790059` のダンプで `"Bash(dotnet test:*)"` 等が単一要素。issue が挙げた `"Bash(gh"` / `"issue"` / `"create:*)"` の 3 分割は解消 |
| AI レビューが `dotnet test` を実走し結果を報告に含めている | ✅ 同 run で `node scripts/scripts.test.js`（18 回）・`dotnet build backend/backend.slnx`（5 回）等を実行。「承認待ちでブロック」の報告は消えた |
| 1 PR に対するレビュー起動が 1 本に収まる | ✅ push 9 回に対しレビュー run 9 件（1 push = 1 run）。`concurrency` ＋ `cancel-in-progress: true` |
| （追記）両ワークフローに `git -C planning` の読み取り専用 4 件 | ✅ `log` / `show` / `diff` / `ls-tree` を両ファイルに（キット `cff9b6c` 由来） |
| （追記）レビュー側に書き込み系 git が入っていない | ✅ `push` / `commit` / `reset` / `add` / `switch` / `checkout` / `branch` / `rm` のいずれも無し |

**実効性の実証**: 是正後のレビューが `node` を実走し、本作業自身が持ち込んだ `gen-changelog.js` の
クラッシュ回帰をスタックトレース付きの 🔴 として検出した。是正前なら「承認待ちで検証できず」で終わっていた。

なお同 issue が指摘する「`.github/workflows/` は GitHub App の権限では編集できず、テンプレート同期の PR が
この 2 ファイルを運べない」問題は、`workflow` スコープを持つローカル認証から push することで解消した。

### 本作業の範囲外とした #323 の関連事項

- **`git -C planning …` の拒否**: [project-planning#120](https://github.com/endazon/project-planning/issues/120) として分離したうえで、計画側 PR #119 で
  読み取り専用サブコマンドの個別列挙（`log` / `show` / `diff` / `ls-tree`）として反映され、**本作業で取り込み済み**。
  素朴な `Bash(git -C:*)` は前方一致で `git -C <path> push` まで通るため採らない。
- **microservices-platform への同一是正**: 別リポジトリのため本作業では扱わない。

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

### 第 4 巡（pin `35b830a`）— 第 2・3 巡の指摘はすべて反映済み。残り 1 件を起票

計画側 PR #105（issue #103 / #104）と PR #107（issue #106）で、第 2・3 巡の指摘が**全件反映**された。
反映内容を取り込んだ結果、キットとの残差分は「本リポ固有の実コンテンツ」と
「HOWTO Part B-5 の置換点」だけになった（`scripts.test.js` はキットに対し純粋な追加のみ＝ `+120/-0`）。

反映に伴って本リポ側で追随したもの:

- `scripts/check-commit-messages.js` — キット版を採用し、`PLAN_PROJECT` の値だけ `ai-stock-trading` を維持
- `scripts/gen-changelog.js` / `changelog-overrides.json` / `check-doc-links.js` / `validate-pipeline-config.js` /
  `.claude/rules/traceability.md` / `copilot-setup-steps.yml` — キット版をそのまま採用
- `ci.yml` の `pipeline-config` ジョブ — キットの `PIPELINE_CONFIG` 環境変数方式へ寄せ、値に本リポの
  `deploy/helm/ai-stock-trading/files/pipeline.json` を設定（採用する任意コンポーネント）
- `docs/ai-workflow.md` — キットの「必須チェックに指定する際の注意」節を採用し、本リポの実例
  （`helm.yml` が `paths:` フィルタ付きのため必須チェックにしない）を 1 行だけ残す
- `scripts.test.js` — キットが取り込んだ 4 テスト（名前空間 3 件・gen-changelog 実行 1 件）が
  本リポ側と重複したため、本リポ側の複製を削除した

**[🟡 残り 1 件・[project-planning#109](https://github.com/endazon/project-planning/issues/109)]** キットの `PLAN_PROJECT` 配布既定が `'<project-name>'` というプレースホルダで、
そのディレクトリは存在しないため**コピーしただけの状態では必ず全走査へ fail-open する**＝
#103 指摘 2 の修正が既定で無効になる。しかも警告が無いため気付けない。実測:

| `PLAN_PROJECT` | 実在集合 | `ADR-0019`（他プロジェクト固有）を実在扱いするか |
| --- | --- | --- |
| `<project-name>`（配布既定） | 32 | する（誤受理） |
| `ai-stock-trading` | 15 | しない |

複数プロジェクトが見えている構成でのみ実害があり、本リポは値を設定済みのため現時点の実害は無い。
キット側で fail-open を可視化（stderr 警告・終了コードは変えない）するのが筋のため、
本リポでは独自実装せず起票のみとした。

### 第 5 巡（pin `7701d25`）— #109 も反映済み。残り 1 件を起票

計画側 PR #110（issue #108 / #109）で、`PLAN_PROJECT` の fail-open 可視化が反映された。実測:

```
warning: PLAN_PROJECT="<project-name>" に対応する <project-name>/07_adr/ が見つからないため、
         計画 ADR の実在性検査を全プロジェクト走査へ退避した（…）
```

単一プロジェクト構成では警告を出さない条件（`entries.length > 1`）も含めて提案どおり。
配布物に他プロジェクトの痕跡が 1 件も残っていないことも再スキャンで確認した。

本リポ側の追随:

- `scripts/check-commit-messages.js` / `scripts.test.js` はキット版を採用（`PLAN_PROJECT` の値のみ維持）
- `ci.yml` — キットが新設した `scripts-tests` ジョブを取り込み、`commit-messages` ジョブに
  相乗りさせていた `node scripts/scripts.test.js` をそちらへ移した（キットと同型）
- `scripts/README.md` — キットの「検査（CI）」ジョブ対応表を採用し、本リポ固有ジョブ 3 行を追記

**[🟡 残り 1 件・[project-planning#112](https://github.com/endazon/project-planning/issues/112)]** `scripts.test.js` にリポジトリ固有テストの受け口が無く、
同期のたびに手動マージが要る（本リポは `+120/-0` を抱えている）。本セッション中の 4 回の同期すべてで発生し、
うち 2 回は**キットが本リポの提案テストを取り込んだことによる重複**の手動削除が必要だった
（重複はテストが落ちないため気付きにくく、`grep '^  ok' | sort | uniq -d` で検出した）。
companion ファイル（`scripts.local.test.js`）を任意で読み込む受け口を提案した。
キット側に入れば本リポの `scripts.test.js` はキットとバイト一致にできるため、本リポでは独自実装しない。

### 第 6 巡（pin `c72dbf2`）— #112 も反映済み。`scripts.test.js` がキットとバイト一致になった

計画側 PR #113（issue #111 / #112）で、リポジトリ固有テストの受け口が新設された。
本リポの固有テスト 118 行を `scripts/scripts.local.test.js` へ移した結果、
**`scripts/scripts.test.js` はキットとバイト一致**（`diff -q` で確認）になり、
以後の同期は上書きコピー 1 回で済む（68 件全合格・重複ゼロ）。

- `codeql.yml` — autobuild が雛形ソリューションを拾って失敗するトラップの注記を取り込んだ。
  本リポは `backend/backend.slnx` 単独で雛形ソリューションを持たないため**該当しない**が、注記ごと取り込む
- `copilot-setup-steps.yml` / `scripts/README.md` — キット版を採用（README には固有スクリプト表へ
  `scripts.local.test.js` の行を追記）

**[🟡 残り 1 件・[project-planning#115](https://github.com/endazon/project-planning/issues/115)]** 受け口のファイル名 `scripts.local.test.js` が
「個人設定・コミットしない」の慣習（`.local`）と衝突する。`.gitignore` に `*.local.*` / `*local*` 系の
パターンがあると除外され、**固有テストが CI から黙って消える**（companion が無ければ何もしない設計のため
テストは減るだけで落ちない）。キット自身が `CLAUDE.local.md` / `*.gitignore` の `*.local` で
`.local` を「追跡しない」の目印に使っている点が紛らわしい。

本リポは `git check-ignore -v scripts/scripts.local.test.js` が exit 1（除外されない）で**影響を受けていない**。
予防的な報告として改名（`scripts.repo.test.js` 等）または追跡状態の警告を提案した。

### 第 7 巡（pin `30a4b78`）— #115 が提案以上の形で反映。残り 1 件を起票

計画側 PR #116（issue #114 / #115）で、受け口の改名（`scripts.local.test.js` → `scripts.repo.test.js`）に加え、
旧名の移行読み込み＋警告・未追跡の警告・空実装（1 件も登録しない）の検出・`REQUIRE_REPO_TESTS=1` による
消失検出・受け口の回帰テストの常時実効化まで入った。

本リポ側の追随:

- `scripts/scripts.local.test.js` → `scripts/scripts.repo.test.js` へ `git mv`
- `scripts/scripts.test.js` — キット版で上書き（バイト一致を維持）
- `ci.yml` の `scripts-tests` ジョブで **`REQUIRE_REPO_TESTS: "1"` を有効化**（本リポは固有テストを持つため）。
  companion を退避して実測し、消失時に exit 1 で落ちることを確認した
- `scripts/README.md` — キットの該当節を採用し、本リポで `REQUIRE_REPO_TESTS` を有効化済みである旨を追記

**[🟡 残り 1 件・[project-planning#118](https://github.com/endazon/project-planning/issues/118)]** 新旧の companion が**両方存在する**場合、
旧名側は読み込まれないうえ警告も出ない（`else if` のため）。いま起きているのは旧名から新名への移行そのものであり、
「新名を作ったが旧名の中身を移し切れていない」部分移行では旧名側のテストが落ちも警告もせず消える。
`REQUIRE_REPO_TESTS=1` でも検出できない（新名があるため `res.file` は null にならない）。
キットの `loadCompanionTests` を取り出した実測で、新名 1 件・旧名 2 件を置くと旧名 2 件が実行されないことを確認した。
本リポは `git mv` で改名したため旧名が残っておらず実害は無い。

### 第 8 巡（pin `25b4291`）— 起票した全件が反映され、キット側の残課題は無くなった

計画側 PR #125（issue #121 / #122 / #124）で、本リポから起票した最後の 2 件が反映された。

- `.claude/settings.json` に `Bash(git -C planning log|show|diff|ls-tree:*)` の 4 件が追加され、
  **3 系統乖離の warn が消えた**（`check-ai-workflow-config.js` が不備 0 件・warn 0 件）。
  本リポの `settings.json` はキットとバイト一致に戻った
- `ci.example.yml` のヘッダが是正後の実態へ更新された（`claude-code-review` を併記・
  「末尾 4 行」の記述を削除・引用符付き 1 引数の注記と `ai-workflow-config` への言及を追加）
- `docs/ai-workflow.md` §3 に `pr-title.yml` が追加された

#### 検証中に生じた誤りと再訂正（記録として残す）

`git -C planning` の 4 件（#120）について、次の 2 つの誤りを犯し、いずれも訂正した。

1. **「AI レビューが実行して検証した」と報告した**が、ログの grep ヒットは*自分が PR コメントに
   書いたコマンド例*がプロンプトへ取り込まれたものだった（タイムスタンプがモデル実行開始前）。
2. 1 の訂正時に planning#123 を引いて**「PR CI では submodule 未取得のため誤答する」と書いた**が、
   #123 は起票者自身が取り下げ済み（`NOT_PLANNED`）であり、私はその取り下げを読まずに
   **同じ見落としを繰り返した**。`actions/checkout` の `submodules:` 指定だけを見て、
   後続の `Fetch planning submodule (read-only PAT)` ステップを見落としていた。

実 run（`30690504515`）のログでは planning が pin どおり `cff9b6c` で取得されており、
`git -C planning` は CI でも正しく submodule の履歴を返す。正確な状態は
**「許可は正しく機能する。当該 run ではたまたま使われなかった」**である。

教訓として、ワークフローの挙動を判断するときは `actions/checkout` の設定だけでなく
**後続ステップまで読む**こと、および**実 run のログで裏を取る**ことを徹底する
（本作業ではログ確認によって 1 も 2 も検出できた）。

### 第 9 巡（pin `3325903`）— 実行ツールの部分的ドリフト検出。残り 1 件を起票

計画側 PR #127（issue #126）で、`check-ai-workflow-config.js` に実装用とレビュー用の
**スタック別実行ツールの差分検出**（`toolchainDrift`）が入った。従来の単一ファイル検査は
「`setup-*` があるのに実行ツールを 1 つも許可していない」＝**全滅**の形しか捉えられず、
片方にだけ一部が無い部分的ドリフトはすり抜けていた。自己試験は 8 → 11 件。

変異テストで実効性を確認した。

| 変異 | 検出 |
| --- | --- |
| レビュー側から `Bash(dotnet test:*)` を削除 | ✅ exit 1・「実装用にあるスタック別の実行ツールが欠けている」 |
| レビュー側から `Bash(node:*)` を削除 | ❌ **検出されない**（下記） |

**[🟡 残り 1 件・[project-planning#131](https://github.com/endazon/project-planning/issues/131)]** `Bash(node:*)` の複製漏れが検出されない。
比較対象が「実際に `uses:` されている `setup-*`」に対応するコマンドに限られる一方、Node は
ランナーにプリインストールのため両ワークフローとも `actions/setup-node` を `uses:` していないためである。

実害はむしろ `dotnet` より大きい。`Bash(node:*)` はレビューがキットの検査器群を実走する唯一の口であり、
実 run では `node scripts/*.js` が 59 回・`dotnet build` が 5 回だった。落ちるとキットの検査器の実走が全滅する。
2 ファイル間の比較でだけ `uses:` ゲートを外す案を、誤検知が増えないことの実測（2 ファイルの
`Bash(...)` 差は `cat` / `find` / `mkdir` / `git` 系のみ＝`TOOLCHAINS` 外）とともに提案した。

### 第 10 巡（pin `4d3eb6b`）— #131 解消。残り 1 件を起票

計画側 PR #132（issue #128 / #129 / #130 / #131）で、本リポから起票した
`Bash(node:*)` の検出漏れが解消された。2 ファイル間の比較では `uses: setup-*` で絞らず
`TOOLCHAINS` 全体を対象にする（`requireUses: false`）方式で、提案どおりである。
併せて本リポが見つけていなかった**偽陽性**（`setup-*` 構成が非対称だと `--allowedTools` が
同一でも ERROR になる・#130）も是正された。自己試験は 11 → 17 件。

変異テストで確認した。

| 変異 | 検出 |
| --- | --- |
| レビュー側から `Bash(node:*)` を削除 | ✅ exit 1・「実装用にあるスタック別の実行ツールが欠けている: Bash(node:*)」 |
| レビュー側の `claude_args` キーを壊す | ❌ **検出されない**（下記） |

あわせて計画側 PR #133 で `/sync-impl`（実装 → 計画の逆方向同期）が新設された。
実装リポの `docs/adr/` と `feedback/` を GitHub API で読み、IADR ↔ 計画 ADR の対応表を生成する。
**本リポは入力契約を満たしている**ことを確認した（IADR 126 件すべてに `title` / `related_ids` /
`status` の frontmatter があり、`feedback/` の 3 記録も `status: open` と `title` を持つ）。

**[🟡 残り 1 件・[project-planning#134](https://github.com/endazon/project-planning/issues/134)]** `claude_args` を解析できないファイルは
`applicable: false` として集計から丸ごと除外され、`driftScopeWarnings` も `files.length < 2` を
「片方だけの構成」とみなすため、**エラーも警告も出ない**。実測では `claude_args:` を `claude_arg:` に
変えるだけで「1 件を検査 ✓ 問題なし」（exit 0）になり、レビュー側の記法検査・SDK 整合・ドリフト検査が
すべて実行されなくなった。この状態からレビューの実行ツールが全部消えても検出されず、
#323（レビューが検証できず静的読解へ退行）へ**検査器が入ったまま**戻れてしまう。
既定名のファイルが存在するのに applicable でないなら警告する案を提案した。

### 第 11 巡（pin `168f53d`）— #134 解消。残り 1 件を起票

計画側 PR #135（issue #134）で、`claude_args` を解析できないファイルが検査から丸ごと消える経路が
可視化された。既定名のファイルがディスクに在るのに applicable でなければ warn を出す。
「そもそも既定名のファイルが無い構成」は警告しない切り分けも入っている。自己試験は 17 → 19 件。

変異テストで確認した。`claude_args:` を `claude_arg:` に変えると
「claude-code-review.yml は存在するが claude_args を解析できず、検査対象から外れている」と警告される
（exit 0 のまま＝fail-open は維持）。

**[🟡 残り 1 件・[project-planning#137](https://github.com/endazon/project-planning/issues/137)]** キットの Node 製検査器が出す warn / notice は
すべて素の stderr 行であり、**GitHub のアノテーションにならない**。exit 0 のためジョブは緑で終わり、
Actions の UI には何も現れない。キット自身は `security.yml` で `::error::` を使っているため、
Node 製検査器の側だけがこの可視化手段を使っていない状態である。

実例として、[project-planning#122](https://github.com/endazon/project-planning/issues/122) の 3 系統乖離は修正までのあいだ CI で毎回 warn が出ていたが、
気付いたのは「ローカル実行」と「AI レビューが自分で実走した」の 2 経路だけで、**CI ログ経由ではなかった**。
`GITHUB_ACTIONS` が立っているときだけ `::warning::` / `::notice::` で出す案（exit コードは変えない）を提案した。

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
