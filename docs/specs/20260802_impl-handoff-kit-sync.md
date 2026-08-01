---
title: 作業仕様書 — planning submodule の最新化と impl-handoff-kit の全面反映（第 14〜15 巡）
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

# 作業仕様書: planning submodule の最新化と impl-handoff-kit の全面反映（第 14〜15 巡）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（開発基盤・運用規約の同期。NFR 相当）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（新規 IADR も作らない。本作業は上流テンプレートの取り込みであり、本リポジトリ固有の設計判断を行わない）
- 計画書リンク: [impl-handoff-kit README](../../planning/tools/impl-handoff-kit/README.md) / [HOWTO](../../planning/tools/impl-handoff-kit/HOWTO.md)
- 前巡（第 1〜13 巡）の記録: [20260801_impl-handoff-kit-sync.md](./20260801_impl-handoff-kit-sync.md)

## 目的・背景

前回の同期（[#322](https://github.com/endazon/ai-stock-trading/pull/322)、pin `9cd3499`）以降、計画リポジトリで
impl-handoff-kit の是正が進んだ。本作業はその内容を本リポジトリへ取り込む。

本作業は 2 巡に分かれる。**第 14 巡**（pin `0847687`）で計画側 PR
[project-planning#145](https://github.com/endazon/project-planning/pull/145)「権限拒否で潰れた AI 実行を緑にせず、拒否ツールを名前で報告する」を取り込み、
その過程で本リポから 2 件を起票した。それが計画側 PR [project-planning#150](https://github.com/endazon/project-planning/pull/150) で即日反映されたため、
続けて**第 15 巡**（pin `3b0deb2`。#150 ＋ [project-planning#147](https://github.com/endazon/project-planning/pull/147)）まで取り込んでいる。

第 14 巡が解決している失敗モードは次のとおり。

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
  - `planning` submodule の pin 前進（`9cd3499` → `0847687` → `3b0deb2`。第 14 巡の起票 2 件が
    計画側 PR [project-planning#150](https://github.com/endazon/project-planning/pull/150) で即日反映されたため、続けて第 15 巡を取り込んでいる）
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

### 第 15 巡（pin `3b0deb2`）で追加した反映

計画側 PR [project-planning#147](https://github.com/endazon/project-planning/pull/147)（issue #146）と
PR [project-planning#150](https://github.com/endazon/project-planning/pull/150)（issue [#148](https://github.com/endazon/project-planning/issues/148) / [#149](https://github.com/endazon/project-planning/issues/149)＝**本作業の第 14 巡で起票した 2 件**）の内容を取り込む。

**そのままテンプレートを採用するもの**（コピー後にバイト一致を確認済み）

| ファイル | 内容 |
| --- | --- |
| `scripts/check-action-versions.js` | **新規**。ワークフローの `uses: <action>@vN` を集め、`action-versions.json` の下限（または `--compare-with` 先）を下回るメジャーを ERROR にする。`--check-latest` は GitHub API で新メジャーを確認（warn のみ）。`--self-test` 14 件 |
| `scripts/check-ai-workflow-config.js` | `--append-system-prompt` を含む新しい `claude_args` を正しく扱えるよう更新（自己試験 19 → 23 件） |
| `scripts/check-permission-denials.js` | Bash の拒否を `Bash(git status)` のように**コマンド名まで**出すよう更新（許可リストの粒度がコマンド単位のため、ツール名だけでは何を足すか決められない。引数は出さない）。自己試験 11 → 15 件 |
| `scripts/scripts.test.js` | 上記 2 件のテストを追加（92 → 103 件）。**キットとバイト一致を維持** |
| `.github/workflows/claude-code-review.yml` | `--allowedTools` に `Bash(git status:*)` を追加。アクションの**組み込みプロンプト自身**が `git status` / `git diff origin/main...HEAD` を差分取得手段として指示するため、許可しないと**差分の内容と無関係に毎回拒否が出る**（キット issue #146） |
| `.github/workflows/claude-coding.yml` | `claude_args` に `--append-system-prompt` を追加し、サブエージェント禁止を明示（第 14 巡で起票した #149 の反映。`prompt:` を持たない構造のため置き場所がここしか無い） |

**本リポジトリ側の判断で足したもの**

| 対象 | 内容 | 理由 |
| --- | --- | --- |
| `ci.yml` の `ai-workflow-config` ジョブ | `node scripts/check-action-versions.js`（実ツリー検査）を追加 | キットの `ci.example.yml` には本ステップが無く、実装リポでは検証器の**自己試験しか走らない**。しかし巻き戻りが実際に起きるのは「キットを同期した実装リポ」である（後述の指摘 1）。`STRICT_AI_WORKFLOW_CONFIG` / `REQUIRE_REPO_TESTS` と同じ opt-in 方式 |
| `scripts/action-versions.json` | `azure/setup-helm: 5` を追記（キットの表には無い本リポ固有アクション） | 追記しないと `warn` が毎回 CI アノテーションとして出続ける。`commit-allowlist.json` / `changelog-overrides.json` と同じ「リポジトリが自分の分を埋めるデータファイル」として扱う（後述の指摘 2） |
| `ci.yml` のコメント例 | `actions/setup-python@v5` → `@v7`（キットの `ci.example.yml` に追随） | コメント中の参考例。実使用は無い |

### CI ジョブの追加は不要

`check-permission-denials.js` は `ci.yml` のジョブではなく **AI ワークフロー 2 本の末尾ステップ**として動く。
検証器自体の自己試験は `scripts.test.js` 経由で既存の `scripts-tests` ジョブが実行するため、`ci.yml` は無変更でよい
（`scripts.test.js` が `--self-test` を子プロセス起動していることを実測で確認した）。

## 受け入れ基準

- [x] `planning` submodule が `origin/main` の先端（`3b0deb2`）を指す
- [x] `repo-template/` と本リポジトリの残差分が、判断ルールで説明できるものだけになる
- [x] キット由来のファイル（第 14 巡 4 件＋第 15 巡 7 件）がキットと**バイト一致**である（`cmp` で確認）
- [x] `node scripts/check-permission-denials.js --self-test` が合格する（15 件）
- [x] `node scripts/check-action-versions.js --self-test` と実ツリー検査が合格する（14 件・退行 0・warn 0）
- [x] `node scripts/check-ai-workflow-config.js --self-test` と本検査が合格する（23 件・不備 0 件。`STRICT_AI_WORKFLOW_CONFIG=1` でも exit 0）
- [x] `node scripts/scripts.test.js` が全件合格する（103 件。ローカルと `GITHUB_ACTIONS=true` の両モード）
- [x] `node scripts/check-doc-links.js` の結果が pin 前進の前後で不変である（既存 20 件のみ・新規破損なし）
- [x] `dotnet build backend/backend.slnx` が通る（コード無変更の回帰確認）
- [x] 新ステップの実効性を変異テストで実測する（拒否あり=exit 1 / 拒否なし=exit 0 / ログ無し=fail-open）
- [x] テンプレート側の不足・混入が `/plan-feedback` として起票されている
      （第 14 巡: [project-planning#148](https://github.com/endazon/project-planning/issues/148) /
      [project-planning#149](https://github.com/endazon/project-planning/issues/149)。
      第 15 巡: [project-planning#152](https://github.com/endazon/project-planning/issues/152) /
      [project-planning#153](https://github.com/endazon/project-planning/issues/153)）

## テスト方針

本作業はコードの振る舞いを変えないため、xUnit の追加は行わない。検証は前巡と同じ 3 系統で行う。

1. **Node 製チェッカの自己試験・単体テスト**（`scripts.test.js` ＋ 各 `--self-test`）
2. **実ツリーに対する機械検査**（`check-ai-workflow-config.js` / `check-doc-links.js`）
3. **回帰しないことの確認**（`dotnet build backend/backend.slnx`）

加えて、新規に取り込んだ検査器は**合成した実行ログに対する変異テスト**で実効性を確認する。

## 検証（実測）

| 検証 | 結果（最終＝第 15 巡取り込み後） |
| --- | --- |
| `check-permission-denials.js --self-test` | ✅ 15 件合格（第 14 巡は 11 件） |
| `check-action-versions.js --self-test` | ✅ 14 件合格 |
| `check-action-versions.js`（実ツリー・11 アクション） | ✅ 退行なし・warn 0（`azure/setup-helm` を表へ追記する前は warn 1 件） |
| `check-ai-workflow-config.js --self-test` | ✅ 23 件合格（第 14 巡は 19 件） |
| `check-ai-workflow-config.js`（実ツリー・2 ファイル） | ✅ 問題なし。`STRICT_AI_WORKFLOW_CONFIG=1` でも exit 0 |
| `scripts.test.js`（ローカル） | ✅ 103 件合格（前巡 85 → 第 14 巡 92 → 第 15 巡 103） |
| `scripts.test.js`（`GITHUB_ACTIONS=true` ＋ `REQUIRE_REPO_TESTS=1`） | ✅ 103 件合格 |
| `dotnet build backend/backend.slnx` | ✅ 0 警告 0 エラー |
| `check-doc-links.js`（planning populate 済み） | 破損 **20 件**（pin 前進の前後で不変。`git -C planning diff 9cd3499..3b0deb2 -- projects/` が空＝計画書本体は無変更）。20 件すべてが `planning/` 配下＝PR CI（submodule 未取得）では対象外 |

**新ステップの変異テスト**（合成した `execution_file` を与えて実測）

| 入力 | 出力 | 終了コード |
| --- | --- | --- |
| `permission_denials: [Task, Task, Bash]`（3 件） | `error … 権限拒否が 3 件発生した … Task（2 件） / Bash（1 件）` | **1** |
| `permission_denials: []`（0 件） | `✓ ツールの権限拒否は発生していない` | 0 |
| パス未指定（ログを読めない） | `warn … 検査していない` | 0（fail-open） |
| （第 15 巡）`Bash` の拒否に `command: "git status --short"` を添えたログ | `error … Bash(git status)（1 件）`＝**コマンド名まで出る**（引数は出ない） | **1** |

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

### 第 15 巡（pin `3b0deb2`）— 第 14 巡の 2 件は即日反映済み。新たに 2 件を起票

第 14 巡で起票した [#148](https://github.com/endazon/project-planning/issues/148) / [#149](https://github.com/endazon/project-planning/issues/149) は、計画側 PR
[project-planning#150](https://github.com/endazon/project-planning/pull/150) で**いずれも提案どおり反映**された
（#148 → `check-action-versions.js` ＋ `action-versions.json` ＋ planning 自身の CI ジョブ、
#149 → `claude-coding` への `--append-system-prompt`）。取り込んだうえで、新たに 2 件が見つかった。

**[🟡 1 件目・[project-planning#152](https://github.com/endazon/project-planning/issues/152)] `check-action-versions.js` を配布しながら、実装リポの CI で実ツリーを検査する口が無い。**

キットは検証器と表を `repo-template/scripts/` に配布する一方、`ci.example.yml` には本検査のステップが無い。
実装リポで走るのは `scripts.test.js` 経由の `--self-test`（＝検証器自身の試験）だけであり、
**そのリポジトリのワークフローが実際に退行していないかは検査されない**。

これは「誰かが手で叩いたときだけ走る」形であり、キット自身が `scripts.test.js` を CI に載せる理由として
繰り返し戒めている型である。しかも #148 が記述する巻き戻りが実際に起きるのは
「**キットを同期した実装リポ**」の側である。実測でも、本作業の第 14 巡（pin `0847687`）の時点で
キットの `frontend-tests.example.yml` は `upload-artifact@v4`・本リポは `@v7` であり、
キットの方針（「テンプレートを正とする」）どおり素直にコピーしていれば**3 メジャー分の退行を持ち込んでいた**。
本作業では手作業のバージョン走査で気付いたにすぎない。

さらに、仮にステップを足しても検出できない形が残る。表（`action-versions.json`）はキットの下限であり、
実装リポが Dependabot で**下限より先へ進んだ**あとにキットのファイルをコピーすると、
実装リポにとっては退行なのに表の上では合格する（キットが v7、実装リポが v8 だった場合など）。
`--compare-with` は 2 つのワークフローディレクトリを比べる仕組みで、単一ディレクトリの実装リポでは使えない。
同期前後の比較（git 由来）でしか捉えられない。

提案:
1. `ci.example.yml` に `check-action-versions.js`（実ツリー）のステップを足し、`scripts/README.md` の
   「検査（CI）」表にも載せる（本リポジトリは先行して `ai-workflow-config` ジョブへ opt-in で追加済み）。
2. 可能なら、同期時の退行（表より先へ進んだ実装リポがキットのコピーで巻き戻る形）を
   `--compare-with <git ref>` 等で検出できるようにする。少なくとも HOWTO Part B-5 に
   「キットのワークフローをコピーする際は `uses:` のバージョンを実装リポ側の値で維持する」ことを
   置換点として明記する。

**[🟡 2 件目・[project-planning#153](https://github.com/endazon/project-planning/issues/153)] `action-versions.json` に、実装リポ固有アクションの受け口が無い。**

`MANIFEST_PATH` は `__dirname/action-versions.json` に固定で、companion／上書きの口が無い。
そのため実装リポがキットに無いアクションを使うと、次のどちらかしか選べない。

| 選択 | 帰結 |
| --- | --- |
| キットの表を編集して足す | **バイト一致が崩れ**、以後の同期で毎回手動マージが要る |
| 足さない | `… は action-versions.json に無いため下限を検査していない` の `warn` が CI アノテーションとして**毎回出続ける** |

本リポジトリでの実測: `azure/setup-helm`（`helm.yml`）で warn 1 件。前者を選び、
`expected` に `"azure/setup-helm": 5` を追記した（`$comment` にも本リポの追記である旨を明記）。

これは `scripts.test.js` が第 6〜7 巡（[#112](https://github.com/endazon/project-planning/issues/112) / [#115](https://github.com/endazon/project-planning/issues/115)）で通ったのと**同型の問題**であり、
そのときは companion（`scripts.repo.test.js`）を設けてキットとのバイト一致を回復した。
同じ設計を表にも適用できる（例: `action-versions.repo.json` があればマージして読む）ことを提案する。
なお `$exempt` に `github/codeql-action` が入っている点から、キットは実装リポ固有の事情を
既に一部この表で吸収する設計であり、受け口を設けるのは既存方針と整合すると考える。

## 未決事項

- 上記フィードバック 4 件（第 14 巡 2 件は反映済み・第 15 巡 2 件は起票）の採否は計画側の `/triage-feedback` の判断に委ねる。
