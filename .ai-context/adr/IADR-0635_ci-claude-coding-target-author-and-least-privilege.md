---
title: IADR-0635 実装用の AI ワークフロー（claude-coding.yml）は、対象の issue / PR の書き手と PR の出所（同じリポジトリのブランチ）も確かめてから起動し、id-token を外し、AI のサブプロセスから秘密を落とす
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0625]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# IADR-0635: claude-coding.yml の起動条件と権限を絞る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」）／ Claude Code（起案・実装）

## 起点・関連

- 非機能 `NFR`（供給網と CI の堅牢化）。IADR-0625 決定 8（許可ツールの最小化・本文の書き手の判定）と §6（`id-token: write` は確かめてから外す）を受ける。
- 実装仕様書: [`.ai-context/specs/20261107_ci-claude-coding-author-check.md`](../specs/20261107_ci-claude-coding-author-check.md)

## コンテキストと課題

- 公開リポジトリの実装用の AI は、書き込みのトークンで動き、ビルド・テストを実行できる。
- 起動の条件は、`@claude` を書いた人の `author_association` だけを見ていた。コラボレーターが外の人の issue・PR で呼ぶと、次のことが起こり得た。
  - AI が外の人の本文を読む。
  - フォークの PR では、外の人のコードがビルド・テストで動く。
- `id-token: write` は、要るかどうかを確かめないまま残っていた。

## 決定

1. **起動の条件**に、対象の issue / PR の書き手（`issue.author_association`・`pull_request.author_association`）の判定を足す。値は OWNER / MEMBER / COLLABORATOR。
   - PR の句は、head が同じリポジトリであること（`pull_request.head.repo.full_name == github.repository`）も求める。
   - `issues` の句は、本文の書き手が対象の書き手なので変えない。
2. **`issue_comment` の対象が PR のとき**は、イベントに head が無い。最初の段で API から head のリポジトリを引き、同じリポジトリでなければ止める。引けないときも止める（閉じる向き）。
3. **`id-token: write` を外す。** アクション（固定の版 v1.0.267）の原文で、次を確かめた。
   - `github_token` を渡すと、その値を使い OIDC を要求しない（`setupGitHubToken`）。
   - OIDC を使うのは workload identity の構成（`ANTHROPIC_FEDERATION_RULE_ID`・`ANTHROPIC_ORGANIZATION_ID`）のときだけである。本ワークフローは構成しない。
   - Bedrock / Vertex も使わない。
4. **`contents: write` は残す。** 実装の成果を届ける `git push` と `push_files` に要る。外すと、ワークフローの目的（実装して PR を出す）が成り立たない。
5. **AI のサブプロセスから秘密を落とす。** AI の段の env に `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB: "1"` を与える。
   - アクションの `action.yml` がこの値を CLI へ渡す。
   - CLI は、AI が起動するビルド・テストなどの環境から、Anthropic・クラウド・GitHub Actions の秘密を落とす（best-effort）。
6. 上の 1〜5 は `scripts/scripts.repo.test.js` が句・段・env を読んで固定する。外す変異で赤になる。
7. **ルールセット（管理者の除外・必須チェック）は GitHub の設定で、所有者の作業**とする。リポジトリでは変えない。当てる値と確かめ方を develop のルールセットの手順書に書く。

## 結果

- 外の書き手の issue・PR、フォークの PR では、AI は起動しない。
- ジョブは OIDC のトークンを要求できない。
- AI のビルド・テストのサブプロセスは、Anthropic の資格と段の `GH_TOKEN` を（best-effort で）持たない。

## 残余・フォローアップ

1. 同じスレッドに外の人が書いたコメントを、AI が読むことはある（アクションが会話を集める）。ツールの許可は最小化してある（IADR-0625 決定 8）。
2. 秘密を落とす仕組みは best-effort である。checkout が残す git の資格（`contents: write` のトークン）は、サブプロセスから読める。
   - このトークンはジョブの間だけ有効で、起動は信頼できる書き手と同じリポジトリのブランチに限っている。
3. `gh issue create` が `GH_TOKEN` を失うかどうかは、実際の起動で確かめる。失うなら、AI は GitHub MCP の道具で代わりに書く。
   - 戻すときは、段の env の 1 行を外す（試験も同時に直す）。
4. `claude-code-review.yml` の `id-token: write` は本 IADR の対象外である。レビュー用の構成（`github_token` の有無）を確かめてから、同じ形で外す。
