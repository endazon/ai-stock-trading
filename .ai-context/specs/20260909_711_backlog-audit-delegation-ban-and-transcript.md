---
title: 週次バックログ監査のサブエージェント委任を塞ぎ、実行記録を artifact に残す（#711 追補）
issue: "#711"
plan_refs:
  - NFR
adr_refs: []
status: done
created: 2026-09-09
---

# 作業仕様書: 週次バックログ監査のサブエージェント委任を塞ぎ、実行記録を artifact に残す（#711 追補）

前段の作業仕様書 [20260909_709_712_ops-hygiene-bundle.md](20260909_709_712_ops-hygiene-bundle.md)
§#711 で配線した産出検証（`check-backlog-audit-output.js`）を、受け入れ基準 3
「手動 dispatch して産出が実際に生成されることを確認する」で実走させた結果への追補である。

## 実測（2026-09-09）

| run | 契機 | ターン | 所要 | 権限拒否 | #483 の更新 | 結論 |
| --- | --- | ---: | ---: | --- | --- | --- |
| 33357546601（08-31） | schedule | 36 | 10 分 | `git -C <絶対パス>` 3 件 | あり | 正常 |
| 34080423564（09-07） | schedule | 15 | 3 分 20 秒 | `search_pull_requests` 2 件 | **なし** | success（黙って落ちた。#711 の起点） |
| 34301133063（09-09） | dispatch（develop `64117d0`） | 16 | 2 分 12 秒 | `Bash(ls)` 1 件・`search_repositories` 1 件 | **なし** | **failure**（新設の産出検証が検出） |

- 産出検証は設計どおり動いた: 「`updated_at`（2026-09-02T15:27:47Z）が run 開始時刻
  （2026-09-09T01:55:41Z）以降に更新されていない」で exit 1（受け入れ基準 2 を実走で満たした）。
- しかし監査そのものは 09-07 から 2 回連続で 3 分未満・15〜16 ターンで終わっており、
  08-31（36 ターン・10 分）と明確に異なる。アクションは実行内容を「full output hidden for
  security」で隠すため、**なぜ書けなかったかはジョブログから追えない**。

## 原因（実行記録で確定・2026-09-09）

第 1 版（本追補の初回 push）では「Claude ステップに `env: GH_TOKEN` が無い」を原因候補とし、
GH_TOKEN の付与と実行記録の artifact 保全を入れて本ブランチを dispatch した（run 34303318956）。
結果は **2 ターン・55 秒・権限拒否 0 件で success、#483 は未更新**。GH_TOKEN 仮説は外れた。

保全した実行記録（artifact `backlog-audit-transcript-34303318956`）を読むと原因は次であった。

1. AI は最初のターンで「調査主体のエージェントに委任します」と述べ、**Agent ツールで監査全体を
   バックグラウンドのサブエージェントへ委任**した（`description: "Repo backlog audit and issue
   upsert"`。ツール結果は「Async agent launched successfully … You will be notified automatically
   when it completes」）。
2. 2 ターン目で「完了したら結果をお知らせします」と応答して**親セッションが終了**した
   （`subtype: success`・`num_turns: 2`）。
3. headless の run は親の応答終了で終わり、子は待たれない。ジョブ末尾の
   「Terminate orphan process: pid (dotnet)」がそれである。issue へは何も書かれない。

09-07（15 ターン）・09-09 develop（16 ターン）の 2 回も同型と見る（実行記録は無いが、
`search_pull_requests` / `search_repositories` / `Bash(ls)` の拒否は許可リストに無い迂回であり、
08-31 の正常回〔36 ターン・10 分・委任なし〕には無かった）。アクションが入れる Claude Code は
floating（`@v1`。09-09 時点で v2.1.266）であり、サブエージェントの既定挙動の変化に本ワークフローが
追随していなかった。

## 変更

1. **委任の禁止（機械）**: `claude_args` へ `--disallowedTools "Agent,Task"` を追加
   （`Task` は Agent ツールの旧名。両方塞ぐ）。`--allowedTools` は変えていない。
2. **委任の禁止（プロンプト）**: 冒頭に「実行の形（最重要）」節を追加し、サブエージェントへの委任と
   「起動しました。完了したらお知らせします」で終えることを禁じ、その理由（headless・後段で fail）を書いた。
3. **`GH_TOKEN` の付与**: 原因ではなかったが、姉妹ワークフロー（`claude-coding.yml` 116 行・
   `claude-code-review.yml` 213 行）と同じ配線として残す。`with.github_token` は MCP へ渡るだけで
   Bash の gh には届かないため、揃えておく方が安全である。
4. **実行記録の artifact 保全**: Claude ステップ直後に `actions/upload-artifact@v7`（`if: always()`・
   30 日）で `steps.claude.outputs.execution_file` を残す。**本追補で原因を特定できたのはこれである。**
   失敗時のみにしない（「success だがおかしい」回こそ正常回との比較対象が要る）。
5. **fail 時の手掛かり**: `check-backlog-audit-output.js` が env `EXECUTION_FILE` を読めれば、
   fail の理由に「サブエージェントへ委任している（ツール名・description）」「ターン数」を併記する
   （`inspectExecution` / `describeExecution` の純関数。読めない・形が違えば空。判定は変えない）。
   `--self-test` は 6 → 9 件。
6. **回帰テスト**: `scripts/scripts.repo.test.js` に配線検査（`--disallowedTools`・プロンプトの禁止文・
   GH_TOKEN・artifact・EXECUTION_FILE）と手掛かり抽出の検査を追加
   （`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 335 件 pass）。

## 検証

- `node scripts/check-backlog-audit-output.js --self-test` → 9 件 pass。
- `node scripts/check-ai-workflow-config.js` → 3 件検査・問題なし（`--disallowedTools` の追加は
  引用符付き 1 引数であり、記法検査に掛からない）。
- YAML 構文（`yaml.safe_load`）でステップ順と env を確認。
- 本ブランチを再度 `workflow_dispatch` で実走し、`Verify backlog audit output` の合否と #483 の
  `updated_at` を確認する（結果は #711 と PR #716 のコメントに記録する）。

## 変更しないこと

- 許可リスト（`--allowedTools`）は変えない。09-09 の拒否 2 件（`Bash(ls)`・`search_repositories`）は
  委任先のサブエージェントの迂回と見る。許可リストの拡張は再発時の実行記録で判断する。
- `check-backlog-audit-output.js` の判定（`updated_at` と run 開始時刻の比較）は変えない
  （検出は正しく機能した。手掛かりの併記は判定に影響しない fail-open の補助である）。
