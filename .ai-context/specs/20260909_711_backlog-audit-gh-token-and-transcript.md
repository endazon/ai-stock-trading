---
title: 週次バックログ監査の gh 未認証是正と実行記録の artifact 保全（#711 追補）
issue: "#711"
plan_refs:
  - NFR
adr_refs: []
status: done
created: 2026-09-09
---

# 作業仕様書: 週次バックログ監査の gh 未認証是正と実行記録の artifact 保全（#711 追補）

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

## 原因の候補と根拠

`.github/workflows/backlog-audit.yml` の Claude ステップだけ **`env: GH_TOKEN` を持たない**。
姉妹ワークフロー `claude-coding.yml`（116 行）・`claude-code-review.yml`（213 行）は
「gh CLI（Issue 発行等）を既定トークンで認証する」として同 env を持つ。`with.github_token` は
アクション内の MCP GitHub ツールへ渡るだけで、Bash から呼ぶ `gh issue list / edit` には届かない。
プロンプトは「長い本文は `gh issue edit <番号> --body-file <path>` で渡す」と gh 経由の書き込みを
唯一の形として指示しているため、gh が未認証だと**書き込み経路そのものが無い**。

08-31 まで動いていた理由は確定できない（アクション側が以前は GITHUB_TOKEN を子プロセスへ
書き出していた可能性があるが、隠された実行記録が無いため実測できない）。**この不確かさこそが
2 つ目の変更（実行記録の保全）の理由である。**

## 変更

1. **`GH_TOKEN` の付与**: Claude ステップへ `env: GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}` を追加
   （姉妹ワークフローと同じ配線）。`permissions.issues: write` は既にある。
2. **実行記録の artifact 保全**: Claude ステップ直後に `actions/upload-artifact@v7` を `if: always()`
   で追加し、`steps.claude.outputs.execution_file` を `backlog-audit-transcript-<run_id>` として
   30 日保管する。失敗時のみにしない——「success だがおかしい」回（15 ターン）こそ正常回との
   比較対象が要る。記録には GITHUB_TOKEN は含まれない（アクションが env を書き出さない）。
3. **回帰テスト**: `scripts/scripts.repo.test.js` に上記 2 点の配線検査を 1 件追加
   （`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 334 件 pass）。

## 検証

- `python3 -c "yaml.safe_load(...)"` でワークフローの構文を確認（ステップ順:
  Checkout → Record run start time → setup-node → Run backlog audit → Upload audit transcript →
  Check permission denials → Verify backlog audit output）。
- 本ブランチを `workflow_dispatch` で実走し、`Verify backlog audit output` の合否と #483 の
  `updated_at` を確認する（結果は #711 のコメントに記録する）。**gh 認証が原因でなかった場合は
  artifact の実行記録から次の原因を特定する**——それが本追補の主目的である。

## 変更しないこと

- プロンプト本文・許可リスト（`--allowedTools`）は変えない。09-09 の拒否 2 件
  （`Bash(ls)`・`search_repositories`）は 08-31 の正常回にも無かった迂回の試みであり、
  書き込み経路が塞がった結果と見る。許可リストの拡張は原因の実測後に判断する。
- `check-backlog-audit-output.js` は変えない（検出は正しく機能した）。
