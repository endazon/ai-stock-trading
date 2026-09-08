---
title: 運用・文書整備 4 issue の一括対応（計画 ADR レンジ鮮度・バックログ監査の産出検証・文書と実物の乖離・SDK 自己修復）
issue: "#710, #711, #712, #709"
plan_refs:
  - NFR
adr_refs:
  - IADR-0311
status: done
created: 2026-09-09
---

# 作業仕様書: 運用・文書整備 4 issue の一括対応（#710 / #711 / #712 / #709）

## 対象 issue

1. **#710** `fix(NFR): 計画 ADR レンジ宣言 ADR-0001..0032 が計画側の実在（0035 まで）より遅れている`
2. **#711** `fix(NFR): 週次バックログ監査（backlog-audit.yml）が run success でも issue の産出・更新が無く、「指摘なし」と「黙って落ちた」が区別できない`
3. **#712** `docs(NFR): 文書と実物の乖離の掃除`（5 件）
4. **#709** `fix(NFR): scripts/setup.sh に SDK 自己修復を実装し、devcontainer（dotnet:8.0）と Node 版を CI と整合させる`

いずれも `NFR`（無採番。計画側に ID 列が無い工程のメタ作業。`.claude/rules/traceability.md` の
無採番許容 2 に該当）。重要な実装判断は [IADR-0311](../adr/IADR-0311_setup-self-heal-and-devcontainer-dotnet10.md)
に記録した（#709 のみ。#710/#711/#712 は既存の検査器・ドキュメントの是正であり新たな設計判断を伴わない）。

## #710: 計画 ADR レンジ宣言の鮮度是正

### 実測

`/home/user/project-planning/projects/ai-stock-trading/07_adr/` を `ls` した結果、
`ADR-0001` 〜 `ADR-0035` の 35 ファイルが実在した（欠番なし。最大は
`ADR-0035_cost-ratio-denominator-and-cost-total-composition.md`）。

### 変更

`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節の `ADR-0001..0032` を
`ADR-0001..0035` へ更新（2 箇所。節名・書式は変更していない）。

### 検証

- `node scripts/check-commit-messages.js --title "feat(ADR-0035): x"` → 適合（変更前は
  「ADR-0035 が実在しない」で拒否されていたはずの境界）。
- `node scripts/check-commit-messages.js --title "feat(ADR-0036): x"` → 引き続き拒否
  （実在しない ID として正しく落ちる）。
- `scripts/scripts.repo.test.js` に回帰テストを追加した（`#710: 計画 ADR レンジ宣言が ADR-0035 を
  実在として通し、ADR-0036 は依然として拒否する`）。CLI バイナリ（`--title` 経路）を直接叩き、
  規約ファイルの宣言と検査器の実効を両方まとめて固定する——純関数の回帰テストだけでは
  「キット側の拡張点解決」が壊れても検出できないため（既存の #532 の教訓を踏襲）。

### `.github/workflows/backlog-audit.yml` への監査観点の追加

「計画 ADR レンジ鮮度」の点検手順をプロンプトの監査観点へ追加した（#711 と同一ファイルのため
本 issue の項でまとめて記載する）。許可リストにある `mcp__github__get_file_contents` で
`projects/ai-stock-trading/07_adr/` のディレクトリ一覧を読み、`.claude/rules/traceability.repo.md`
の宣言レンジと突き合わせる手順を追記した。パイプ・複合コマンド等、許可されない形は使っていない
（元のプロンプトの「使ってよいコマンドの形」節に従う）。

## #711: バックログ監査の産出検証

### 設計

- 判定ロジックを `scripts/check-backlog-audit-output.js`（新設・Node 標準のみ）に置く。
  - `fetchIssue(issueNumber, execFn)`: `gh issue view <n> --json updatedAt,title,number` を叩く
    （`execFn` を差し替え可能にし、テストでは実際に `gh` を呼ばない）。
  - `verdict({updatedAt, runStart})`: 純関数。`updatedAt` が `runStart` より**厳密に後**であれば
    合格。同時刻は不合格側に倒す（実運用ではあり得ない一致だが、見逃す方向より誤って fail する
    方向を安全とみなす）。
  - `evaluate({issueNumber, runStart, fetchFn})`: `fetchIssue` の失敗（issue 不在・gh 認証失敗等）
    も含めて評価する結合点。
  - `--self-test` で 6 ケース（更新あり／更新なし／境界／issue 不在／run 開始時刻が壊れている／
    `fetchIssue` の JSON パース）を固定する。
- run 開始時刻はワークフロー先頭で `date -u +%Y-%m-%dT%H:%M:%SZ` を取り、`$GITHUB_OUTPUT`
  経由で後段ステップへ渡す（本スクリプト自身は時刻を生成しない——Claude ステップの実行時間ぶん
  比較がずれるのを避けるため）。

### `backlog-audit.yml` への配線

- 先頭に `Record run start time` ステップを追加し `run_start` を出力する。
- Claude ステップの後（`if: always()`。既存の権限拒否検査と同じ扱い）に
  `Verify backlog audit output` ステップを追加し、`RUN_START_TIME` を渡して
  `node scripts/check-backlog-audit-output.js` を実行する。産出が無ければ exit 1 でジョブを落とす。
- プロンプトに「本文冒頭に実施日（本日）を必ず書き換えること」を明記した
  （「該当なしは明記」の既存指示に追記する形）。

### 検証

- `node scripts/check-backlog-audit-output.js --self-test` → 6 件すべて合格。
- 「意図的に壊す（模擬）と fail する」は上記自己試験の「更新なし→exit 1」で担保した。
  **実 schedule での確認（本物の run で実際に issue が更新されない状況を作って fail することの
  確認）は未実施**——スケジュール実行を待つか `workflow_dispatch` での手動実行が必要であり、
  本セッションの作業範囲外のため残件として報告する。

## #712: 文書と実物の乖離の掃除（5 件）

1. **`scripts/README.md` の `check-test-traceability.js --require-planning` の記載**を修正した。
   `ci.yml` の実物（`node scripts/check-test-traceability.js`。フラグ無し）と検査器の実装
   （`planningPopulated()` は `planning/projects` の存在を見るが、ADR-0029 で submodule 自体が
   撤去済みのためどの環境でも存在し得ない）を確認し、「ADR-0029 以降は恒久的に `exit 1` になる
   ため使えない。CI はフラグ無しで実行する」旨へ書き換えた。`check-test-traceability.js` 自身の
   使い方コメントにも同旨の注記を足した（挙動は変更していない。コメントのみ）。
2. **`ci.yml` のコメント「101 個の csproj」「テストアセンブリ 51 本」**を実測した。
   `find backend -name '*.csproj' | wc -l` → **40**。`node scripts/list-test-projects.js --count`
   → **20**。両方とも VSA 単一プロジェクト化（IADR-0259）でサービス配下が集約された結果、
   導入当時（キャッシュ・シャーディングを入れた時点）の実測値から減少している。
   **元の数値は削除せず「導入当時の実測値」として残し**、現在値を注記として併記した
   （導入の動機となった歴史的事実と、現在の値を読み違えさせないための注記を両立させる）。
3. **`.claude/settings.json` の撤去済み許可を削除**した: `Bash(git -C planning log:*)` /
   `show:*` / `diff:*` / `ls-tree:*` / `grep:*`（5 件）と `Bash(git submodule status:*)`。
   `node scripts/check-ai-workflow-config.js` を実行したところ、**`claude-coding.yml` /
   `claude-code-review.yml` の 2 系統に `Bash(git submodule status:*)` が残っている**ことが
   `warn` で判明した（`git -C planning` 系はこの 2 系統には無かった）。STRICT モード
   （`STRICT_AI_WORKFLOW_CONFIG=1`。CI で有効化済み）では `exit 1` になることを確認した上で、
   同許可を 2 系統からも削除し、関連コメント（「submodule pin の確認」等、もはや成立しない理由づけ）
   も是正した。`STRICT_AI_WORKFLOW_CONFIG=1 node scripts/check-ai-workflow-config.js` → 緑を再確認。
4. **`docs/blocked-tasks.md` の計画 ADR 状態表**を是正した。旧表は submodule pin `a4616a8` 基準
   （ADR-0016/0018/0023 が `Proposed` 等）だったが、**pin という概念自体が ADR-0029 で撤去済み**
   のため、方式そのものを「隣接クローンまたは GitHub で都度確認する」へ切り替えた。隣接クローンで
   2026-09-09 に実測したところ、**対象 9 件（ADR-0002/0016/0018/0019/0021/0022/0023/0024/0025）
   すべてが `Accepted`** だった。旧表は本文書の既存ポリシー（「旧記述は削除せず訂正として残す」）に
   従い打ち消し線で残し、新しい訂正ブロックを追記した。`check-trace-blocks.js` の除外リストで
   本ファイルは「ID をキーとする作業台帳」として明示的に対象外（可視本文への ID 記載が許容される）
   であることを確認済み。
5. **`check-cross-repo-refs.js` のフルツリー走査を CI へ配線**した。`ci.yml` の `static-checks`
   ジョブへ、`check-plan-id-qualification` の直後に自己試験＋本検査の 2 ステップを追加した
   （置換点は `.claude/rules/traceability.repo.md` と同じ値を明示: `CROSS_REPO_NAMES` /
   `CROSS_REPO_SELF_NAMES` / `CROSS_REPO_EXCLUDES`）。これまで `scripts.repo.test.js` の中でしか
   本走していなかった（実データに対する走査が CI の独立ステップとしては存在しなかった）。
   実測: 走査 2093 件・除外 51 件（`scripts/` の非 Markdown）・違反 0 件・exit 0。
   `scripts/README.md` の CI 表へ `cross-repo-refs` 行を追加した。

## #709: setup.sh の SDK 自己修復・devcontainer・Node 版

設計判断は [IADR-0311](../adr/IADR-0311_setup-self-heal-and-devcontainer-dotnet10.md) に記録した。
要点のみ再掲する。

- `.devcontainer/devcontainer.json`: `mcr.microsoft.com/devcontainers/dotnet:8.0` →
  `mcr.microsoft.com/devcontainers/dotnet:10.0`（Docker Registry API でタグ実在を確認。
  プレビュー接尾辞なしの GA タグ）。Node feature は既に `20` のため無変更。
- `scripts/setup.sh`: `command -v dotnet` が偽のとき、① `$HOME/.dotnet/dotnet` があれば
  このプロセスの PATH へ追加、② 無ければ `global.json`/`Directory.Build.props` から channel を
  導出し `dotnet-install.sh` で導入を試みる（fail-open）。`DOTNET_INSTALL_DRY_RUN=1` で
  実ネットワークを叩かない分岐を用意した。
- Node バージョン: `ci-latency-watch.yml` の `22` を `20` へ統一し、`.nvmrc`（`20`）を新設した。
- `npm ci` は `setup.sh` の必須範囲に含めない（`frontend/` は個別導入する既存運用のため）。

### 実測（このセッションでの実走）

- `bash -n scripts/setup.sh` → 構文エラーなし。
- PATH から `dotnet` を外した状態で `bash scripts/setup.sh` を実行 → `$HOME/.dotnet/dotnet`
  （SDK `10.0.400`）を検出して PATH へ追加し、`dotnet restore backend/backend.slnx`
  （40 プロジェクト）が成功した。
- 空の `$HOME` で `DOTNET_INSTALL_DRY_RUN=1` を付けて実行 → channel `10.0`（`global.json`
  優先）を導出し、curl を実行せずログのみで完了した。
- 同じく空の `$HOME` で `Directory.Build.props` のみ（`global.json` 無し）のツリーを合成して
  dry-run → channel `10.0`（`net10.0` から導出）を確認した。
- 空の `$HOME` で **dry-run を外して実行**（実ネットワーク）→ `dotnet-install.sh` の取得・実行が
  成功し SDK `10.0.401` が導入され、その場で `dotnet restore` まで通った。`dot.net` は
  `HTTP/2 301` で `builds.dotnet.microsoft.com` へリダイレクトしており、`curl -fsSL` はそれを
  辿って到達する（本セッションのプロキシはこの経路を通す。詳細は IADR-0311 参照）。

## 引いた母集合と、除外したものと理由

- `git submodule status` の残置確認は `.claude/settings.json` / `claude-coding.yml` /
  `claude-code-review.yml` の 3 系統に絞った（`check-ai-workflow-config.js` が比較する母集合と
  同じ）。`.example.yml` の類は本リポジトリに存在しないため対象外（`find` で確認済み）。
- `git -C planning` 系の許可は `.claude/settings.json` にのみ存在し、他 2 系統には無かった
  （grep で確認）。
- `node-version` の乖離チェックは `.github/workflows/*.yml` 全件を grep して洗った
  （`ci-latency-watch.yml` の `'22'` のみが異物）。
- csproj 数・テストアセンブリ数の実測は `backend/` 配下限定（`find backend -name '*.csproj'`）。
  ルート直下やツール類の `.csproj`（存在しない）は対象外。

## 検証

- `node scripts/check-backlog-audit-output.js --self-test` → 6 件合格。
- `node scripts/check-commit-messages.js --title "feat(ADR-0035): x"` → 適合。
  `--title "feat(ADR-0036): x"` → 拒否（実在性検査が正しく機能）。
- `node scripts/check-ai-workflow-config.js` / `STRICT_AI_WORKFLOW_CONFIG=1 node scripts/check-ai-workflow-config.js`
  → いずれも緑。
- `node scripts/check-cross-repo-refs.js --self-test` → 86 件合格。実データ走査（env 明示）
  → 違反 0 件・exit 0。
- `node scripts/check-workflow-job-refs.js` → 乖離なし（新設ステップは `static-checks` の
  step であり、ブランチ保護の必須 check 名を増やしていないため影響なし）。
- `node scripts/check-adr-index-sync.js` → 実行済み（git diff ベースのためコミット後に再確認）。
- `bash -n scripts/setup.sh` → 構文エラーなし。`bash scripts/setup.sh` の実走は上記のとおり。
- 横断テスト（`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 等）は本仕様書末尾の
  完了報告にまとめて記載する。
