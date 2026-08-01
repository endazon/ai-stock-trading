---
title: 作業仕様書 — planning submodule の最新化と impl-handoff-kit の全面反映（第 14 巡）
type: work
status: review
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-02
updated: 2026-08-02
plan_refs:
  - ../../planning/tools/impl-handoff-kit/README.md
  - ../../planning/tools/impl-handoff-kit/HOWTO.md
related_specs:
  - ./20260801_impl-handoff-kit-sync.md
  - ../README.md
  - ../ai-workflow.md
  - ../../.claude/rules/traceability.md
---

# 作業仕様書: planning submodule の最新化と impl-handoff-kit の全面反映（第 14 巡）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（開発基盤・運用規約の同期。NFR 相当）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（新規 IADR も作らない。本作業は上流テンプレートの取り込みであり、本リポジトリ固有の設計判断を行わない）
- 計画書リンク: [impl-handoff-kit README](../../planning/tools/impl-handoff-kit/README.md) / [HOWTO](../../planning/tools/impl-handoff-kit/HOWTO.md)
- 前巡（第 1〜13 巡）の記録: [20260801_impl-handoff-kit-sync.md](./20260801_impl-handoff-kit-sync.md)

## 目的・背景

前回の同期（[#322](https://github.com/endazon/ai-stock-trading/pull/322)、pin `9cd3499`）以降、計画リポジトリで
impl-handoff-kit の是正が 1 巡進んだ。本作業はその内容を本リポジトリへ取り込む。

上流の前進は計画側 PR [project-planning#145](https://github.com/endazon/project-planning/pull/145)
「権限拒否で潰れた AI 実行を緑にせず、拒否ツールを名前で報告する」の 1 件である。解決している失敗モードは次のとおり。

**claude-code-action は、AI がツールを 1 つも実行できなくても `"subtype": "success"` / `"is_error": false` で終わる。**
そのためジョブは緑になり、PR には「並列精査中」等の進行中コメントだけが残る。CI には承認する人間が居ないため、
権限拒否は「待たされた」ではなく **「その作業は永久に実行されない」** を意味するが、それが誰にも見えない。
上流の実測では 21 ターン中 17 件が拒否され、レビューは実質未実施のまま CI は緑だった。

既存の `check-ai-workflow-config.js` は**設定を静的に検査**するため「設定の書き方の誤り」しか見つけられない。
「設定は正しいが AI が要求したツールが揃っていなかった」型は実行してみるまで判らない。そこで
**実行結果を事後に検査**する第 2 系統として `check-permission-denials.js` が加わった。

方針は前巡と同じく「**impl-handoff-kit を正とする**」であり、本リポジトリ独自の記述は
(1) HOWTO 付録 Part B-5 が指定するスタック依存の置換点、(2) 本リポジトリ固有の成果物・裁定、の 2 つに限定する。

## 対象範囲

- 対象:
  - `planning` submodule の pin 前進（`9cd3499` → `0847687`）
  - `repo-template/` と本リポジトリ直下の全差分の解消（採否の判断と反映）
  - テンプレート側の不足・混入のフィードバック起票
- 対象外:
  - 本リポジトリの機能実装・振る舞いの変更（C# コードは 1 行も触らない）
  - submodule を populate したときに出る **planning 配下への破損リンク 20 件**。前巡の仕様書
    「本作業で扱わない既存不具合」で別 issue へ切り出し済みであり、本作業でも件数・内容ともに不変であることを実測した
    （pin 前進が `projects/` を 1 バイトも変えていないため。後述「検証」参照）

## 設計

### 判断ルール（差分 1 件ごとの採否）

前巡と同一のルールを適用する（[20260801_impl-handoff-kit-sync.md](./20260801_impl-handoff-kit-sync.md) の
「判断ルール」表）。要点は「テンプレートが前進していれば採用」「置換点と固有コンテンツのみ維持」
「本リポにありテンプレートに無い改善は維持してフィードバック」である。

### 反映内容

**そのままテンプレートを採用するもの**（コピー後にキットとのバイト一致を確認済み）

| ファイル | 内容 |
| --- | --- |
| `scripts/check-permission-denials.js` | **新規**。claude-code-action の実行ログ（`outputs.execution_file`）を読み、権限拒否で実行できなかったツールを名前と件数で報告する。1 件でもあれば終了コード 1。実行ログを読めない構成では `warn` を出して終了コード 0（fail-open）。`--self-test` あり |
| `scripts/scripts.test.js` | 上記の単体テスト 7 件（`--self-test` の起動を含む）が追加。**キットとバイト一致を維持** |
| `.github/workflows/claude-code-review.yml` | `permissions:` に `actions: read`・claude ステップに `id: claude`・末尾に `Check permission denials` ステップ（`if: always()`）を追加 |
| `.github/workflows/claude-coding.yml` | 同上 |

`.claude/settings.json` に差分は無く、バイト一致を維持している。

`actions: read` は 2 か所が同時に依存する。(1) アクションが注入する `mcp__github_ci__*` サーバーは
**導入前にワークフロートークンが `actions: read` を持つか実際に検証**し、無ければ
`Skipping CI server installation` と警告して導入を取り止める（許可済みのはずのツールが存在しない状態になる）。
(2) `--allowedTools` の `Bash(gh run list:*)` は gh CLI 側が `actions: read` を要求する。
`additional_permissions` は使わない（あれはアプリトークンのスコープ用）。

**テンプレートを採用しつつ本リポジトリの構成へ合わせるもの**

- `scripts/README.md` — キットが追加した 2 行（スクリプト表の `check-permission-denials.js` 行・
  ローカル実行例の 1 行）を、本リポジトリで再構成した「キット共通 / 本リポジトリ固有」の 2 表構成へ差し込む

**本リポジトリを維持するもの**（前巡から変化なし）

`CHANGELOG.md` / `docs/adr/README.md` / `docs/operations/operations.md` / `docs/tech/tech-requirements.md` /
`scripts/commit-allowlist.json` / `.gitignore` / `AI_SETUP.md` / `CLAUDE.md`・`docs/README.md` の AST 固有節、
および置換点（`check-commit-messages.js` の `PLAN_PROJECT=ai-stock-trading`・`openapi.yml` の `paths: backend/**`）。

**アクションのバージョンは本リポジトリの値を維持する**: `.github/workflows/frontend-tests.example.yml` の
`actions/upload-artifact@v7`（キットは `@v4`）。`github/codeql-action` の `@v4.37.3` ピンも本リポジトリの
Dependabot が管理するため維持する（キットの `@v4` はテンプレート既定として妥当）。前者は後述のとおり環流する。

### CI ジョブの追加は不要

`check-permission-denials.js` は `ci.yml` のジョブではなく **AI ワークフロー 2 本の末尾ステップ**として動く。
検証器自体の自己試験は `scripts.test.js` 経由で既存の `scripts-tests` ジョブが実行するため、`ci.yml` は無変更でよい
（`scripts.test.js` が `--self-test` を子プロセス起動していることを実測で確認した）。

## 受け入れ基準

- [x] `planning` submodule が `origin/main` の先端（`0847687`）を指す
- [x] `repo-template/` と本リポジトリの残差分が、判断ルールで説明できるものだけになる
- [x] キット由来の 4 ファイルがキットと**バイト一致**である（`cmp` で確認）
- [x] `node scripts/check-permission-denials.js --self-test` が合格する（11 件）
- [x] `node scripts/check-ai-workflow-config.js --self-test` と本検査が合格する（19 件・不備 0 件）
- [x] `node scripts/scripts.test.js` が全件合格する（92 件。ローカルと `GITHUB_ACTIONS=true` の両モード）
- [x] `node scripts/check-doc-links.js` の結果が pin 前進の前後で不変である（既存 20 件のみ・新規破損なし）
- [x] `dotnet build backend/backend.slnx` が通る（コード無変更の回帰確認）
- [x] 新ステップの実効性を変異テストで実測する（拒否あり=exit 1 / 拒否なし=exit 0 / ログ無し=fail-open）
- [x] テンプレート側の不足・混入が `/plan-feedback` として起票されている
      （[project-planning#148](https://github.com/endazon/project-planning/issues/148) /
      [project-planning#149](https://github.com/endazon/project-planning/issues/149)）

## テスト方針

本作業はコードの振る舞いを変えないため、xUnit の追加は行わない。検証は前巡と同じ 3 系統で行う。

1. **Node 製チェッカの自己試験・単体テスト**（`scripts.test.js` ＋ 各 `--self-test`）
2. **実ツリーに対する機械検査**（`check-ai-workflow-config.js` / `check-doc-links.js`）
3. **回帰しないことの確認**（`dotnet build backend/backend.slnx`）

加えて、新規に取り込んだ検査器は**合成した実行ログに対する変異テスト**で実効性を確認する。

## 検証（実測）

| 検証 | 結果 |
| --- | --- |
| `check-permission-denials.js --self-test` | ✅ 11 件合格 |
| `check-ai-workflow-config.js --self-test` | ✅ 19 件合格 |
| `check-ai-workflow-config.js`（実ツリー・2 ファイル） | ✅ 問題なし |
| `scripts.test.js`（ローカル） | ✅ 92 件合格（前巡 85 件 → キットの新規 7 件） |
| `scripts.test.js`（`GITHUB_ACTIONS=true` ＋ `REQUIRE_REPO_TESTS=1`） | ✅ 92 件合格 |
| `dotnet build backend/backend.slnx` | ✅ 0 警告 0 エラー |
| `check-doc-links.js`（planning populate 済み） | 破損 **20 件**（pin 前進の前後で不変。`git -C planning diff 9cd3499..0847687 -- projects/` が空＝計画書本体は無変更）。20 件すべてが `planning/` 配下＝PR CI（submodule 未取得）では対象外 |

**新ステップの変異テスト**（合成した `execution_file` を与えて実測）

| 入力 | 出力 | 終了コード |
| --- | --- | --- |
| `permission_denials: [Task, Task, Bash]`（3 件） | `error … 権限拒否が 3 件発生した … Task（2 件） / Bash（1 件）` | **1** |
| `permission_denials: []`（0 件） | `✓ ツールの権限拒否は発生していない` | 0 |
| パス未指定（ログを読めない） | `warn … 検査していない` | 0（fail-open） |

## 計画書との差異

- 差異: あり（テンプレート側の不足。`/plan-feedback` で計画リポへ環流する）

### 第 14 巡（pin `0847687`）で判明した指摘

**[🟡 1 件目・[project-planning#148](https://github.com/endazon/project-planning/issues/148)] キットの Dependabot 設定がテンプレート配下のワークフローに効いておらず、Actions の巻き戻りが再発している。**

計画側 [#97](https://github.com/endazon/project-planning/issues/97) 指摘 1（「同期のたびに Actions のバージョンが巻き戻る」）は、
PR #98 で planning の `.github/dependabot.yml` に **テンプレート配下を対象とする 2 つ目の `updates` エントリ**
（`directory: "/tools/impl-handoff-kit/repo-template"`・`commit-message.prefix: "template"`）を足すことで
構造的に防ぐ方針となった。しかし実測ではその経路が動いていない。

| 観測 | 値 |
| --- | --- |
| キットの `frontend-tests.example.yml` | `actions/upload-artifact@v4` |
| 本リポジトリの同ファイル | `actions/upload-artifact@v7`（[#325](https://github.com/endazon/ai-stock-trading/pull/325) で Dependabot が bump 済み） |
| dependabot.yml にテンプレート用エントリが入った時点 | planning `12cc9b8` |
| それ以降に planning で Dependabot が作った PR | 4 件（`#99` / `#100` / `#101` / `#102`）。**すべて `chore:` 接頭辞**＝ルート（`directory: "/"`）のエントリ由来 |
| 同じく `template:` 接頭辞の Dependabot PR | **0 件**（`template:` で始まるコミットはいずれも人手/AI の PR であり Dependabot 著者ではない） |

`github-actions` エコシステムは `directory` にワークフローの置き場を指定しても、**ワークフローファイルは
リポジトリ直下の `.github/workflows/` しか走査しない**（指定ディレクトリで探すのは複合アクションの
`action.yml`）ため、当該エントリは実質 no-op と考えられる。結果として #97 指摘 1 の再発が
`upload-artifact` という形で既に起きており、本リポジトリは今回も手作業で差し戻している。

キット側の対処案は 2 通り。(a) テンプレート配下のワークフローを Dependabot が拾える形（例: 配布物を
`.github/workflows/` 相当のパスに置くのではなく、CI で「キットとリポジトリの Actions バージョン差」を検査する
スクリプトを足す）にする。(b) 少なくとも `dependabot.yml` の当該エントリに「現時点では効かない」ことを
コメントで明記し、`kit-scripts-tests.yml` 等でバージョン差を検出して気付けるようにする。**本リポジトリでは
独自実装せず起票のみとする**（キットが単一情報源であるべき箇所のため）。

**[🟡 2 件目・[project-planning#149](https://github.com/endazon/project-planning/issues/149)] 実装用ワークフロー（`claude-coding.yml`）には検出器だけが入り、対策が入っていない。**

HOWTO は `Task` 拒否への恒久対処を 2 択（(a) `Task` を `--allowedTools` に加える / (b) プロンプトで
「単一セッションで完結。サブエージェント禁止」を明示する）とし、**キットは (b) を採る**と明記している。
ところが (b) が適用されているのはレビュー用ワークフローだけである。

| ファイル | 拒否検出ステップ | サブエージェント禁止の明示 |
| --- | --- | --- |
| `claude-code-review.example.yml` | あり | あり（`prompt:` の【実行制約・最重要】節。実測: 「サブエージェント」の言及 3 箇所） |
| `claude-coding.example.yml` | あり | **なし**（実測: 言及 0 箇所。そもそも `prompt:` を持たず、`@claude` メンション本文で駆動する構造のため置き場所が無い） |

このため実装ワークフローは「AI が `Task` を試みる → 拒否される → **その後 Task 抜きで実装を完遂しても**
ジョブは exit 1 で赤」になり得る。従来（緑で黙って劣化）よりは望ましいが、成果物が正しくても赤くなる
偽陽性が新たに生じる。`prompt:` を持たない構造上、対策を置くには `.github/workflows` 側で
`--append-system-prompt` を使うか、`Task` を許可する（(a) 側）かのいずれかが要る。キット側の設計判断が要る事項のため起票する。

## 未決事項

- 上記フィードバック 2 件の採否は計画側の `/triage-feedback` の判断に委ねる。
