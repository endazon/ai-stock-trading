---
title: 実装用の AI ワークフロー（claude-coding.yml）は対象の issue / PR の書き手と PR の出所も確かめ、id-token を外し、AI のサブプロセスから秘密を落とす（ルールセットは所有者の作業）
type: spec
status: accepted
related_ids: [NFR, IADR-0635, IADR-0625]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 仕様書: claude-coding.yml の起動条件と権限を絞る

## 起点

- 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」）。非機能 `NFR`（供給網と CI の堅牢化）。
- 決定は [IADR-0635](../adr/IADR-0635_ci-claude-coding-target-author-and-least-privilege.md)。IADR-0625 §6 の宿題（`id-token: write`）を閉じる。

## 1. 現状（develop `2e95ee467`）

- 起動の条件は、4 つの句それぞれで**本文の書き手**（コメント・レビュー・issue の書き手）の `author_association` だけを見る。
  対象の issue / PR の書き手は見ない。PR がフォークから来たかも見ない。
- ジョブの権限は `contents: write`・`pull-requests: write`・`issues: write`・`id-token: write`・`actions: read`。
- AI は `Bash(dotnet build:*)`・`Bash(dotnet test:*)`・`Bash(npm run:*)` を使える。AI の起動するサブプロセスの環境には、
  アクションが CLI に渡す Anthropic の資格と、段の `GH_TOKEN` が残る。

## 2. 確かめたこと（アクションの固定の版 `4845bacb…`・v1.0.267 の原文）

- `src/github/token.ts` の `setupGitHubToken`: 入力 `github_token`（`OVERRIDE_GITHUB_TOKEN`）があればそれを返す。OIDC は要求しない。
  OIDC を使うのは、入力が無いとき（App トークンへの交換）だけである。
- `base-action/src/workload-identity.ts`: OIDC を使うのは `ANTHROPIC_FEDERATION_RULE_ID` と `ANTHROPIC_ORGANIZATION_ID` を構成したときだけ。
  本ワークフローは構成しない。Bedrock / Vertex も使わない。
  → **`id-token: write` は要らない。**
- `action.yml`: `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` を CLI へ渡す。値はジョブ・段の env か、`allowed_non_write_users` を使うときの `1`。
  `docs/security.md` によると、`1` で CLI がサブプロセスの環境から Anthropic・クラウド・GitHub Actions の秘密を落とす（best-effort）。
  → 段の env で `1` を与える。
- `contents: write` は、実装の成果を届ける `git push`（checkout の資格）と `mcp__github__push_files` に要る。外すとワークフローの目的が成り立たない。
  → **残す。**

## 3. 変更

1. 起動の条件に、対象の書き手と出所の判定を足す（各句）:

   | 句 | 足す判定 |
   | --- | --- |
   | `issue_comment` | `github.event.issue.author_association`（issue / PR の書き手） |
   | `pull_request_review_comment`・`pull_request_review` | `github.event.pull_request.author_association`、`head.repo.full_name == github.repository` |
   | `issues` | 足さない（本文の書き手が対象の書き手である） |

2. `issue_comment` の対象が PR のとき、最初の段で `gh api repos/<repo>/pulls/<n>` から head のリポジトリを引き、同じリポジトリでなければ止める。引けなくても止める。値は env で受ける。
3. `id-token: write` を外す。
4. AI の段の env に `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB: "1"` を足す。
5. 試験（`scripts/scripts.repo.test.js`）で、句ごとの対象の判定・同じリポジトリの判定・PR の確かめの段・`id-token` が無いこと・秘密を落とす env を固定する。外す変異で赤になることも固定する。
6. ルールセットはリポジトリの外の設定なので変えない。手順書（develop のルールセット）に「所有者が設定すること」を足す。

## 4. 受け入れ基準

- 4 句がそれぞれ本文の書き手を、3 句が対象の書き手を判定する。PR の 2 句は同じリポジトリの判定も持つ。
- `issue_comment` の PR で、フォークの head は止まる。
- `id-token` が無い。AI の段が `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB: "1"` を持つ。
- 上のどれかを外す変異で、試験が赤になる。
- 手順書に、所有者が当てる設定の値と、当てた後の確かめ方がある。

## 5. 残る点

- 対象の書き手が信頼できても、同じスレッドに外の人が書いたコメントを AI が読むことはある（アクションが会話を集める）。
  ツールの許可は最小化してある（IADR-0625 決定 8）。
- 秘密を落とす仕組みは best-effort である。checkout が残す git の資格（`contents: write` のトークン）は、サブプロセスから読める。
  ジョブの間だけ有効で、起動は信頼できる書き手と同じリポジトリのブランチに限っている。
- `gh issue create` は `GH_TOKEN` を使う。秘密を落とす仕組みがこれを落とす場合、AI は GitHub MCP の道具で代わりに書く。
  効き方は実際の起動で確かめる（所有者の手順書）。
