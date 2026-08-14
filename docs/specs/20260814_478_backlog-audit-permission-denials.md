---
title: 週次バックログ監査の権限拒否 75 件を分類し、allowedTools とプロンプトを是正する
type: spec
status: approved
related_ids: [NFR, IADR-0170, IADR-0145]
author: endazon (with Claude Code)
created: 2026-08-14
updated: 2026-08-14
---

# 仕様書: backlog-audit の権限拒否の是正（#478）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#478](https://github.com/endazon/ai-stock-trading/issues/478)
- 起点 ID: **NFR**（運用保守）。運用ガイド §4「レビュー・監査基盤の健全性を監視する」／§11「稼働確認」
- 関連 IADR: [IADR-0170](../adr/IADR-0170_backlog-audit-automation.md)（週次監査の新設）・[IADR-0145](../adr/IADR-0145_permission-denial-fixability-classification.md)（拒否は「許可リストで直せるか」で分類する）
- 環流元: パリティ点検 2026-08-14（計画リポ `draft/cross-project/20260814_ast-msp-parity-recheck.md`）

## 事象（実測）

初回スケジュール実行 [run 31348684057](https://github.com/endazon/ai-stock-trading/actions/runs/31348684057)（2026-08-10）が failure。`Run backlog audit` ステップ自体は success だが、**AI 監査中にツール権限拒否が 75 件**発生し（`check-permission-denials.js` がしきい値超過で fail）、**結果 issue は作成されなかった**。監査は静かに劣化するのではなく、設計どおり赤で止まった（IADR-0145 の fail 判定が機能した）。ただし止まっただけで、監査の産出は 0 のままである。

## 拒否 75 件の分類（check-permission-denials.js の出力・run ログより）

### (a) 許可リストで直るもの（34 件）

**前提となる制約**: ci.yml の `ai-workflow-config` は **STRICT**（`STRICT_AI_WORKFLOW_CONFIG=1`）であり、CI の allowedTools に **`.claude/settings.json` の allow に無いツールを足すと CI が落ちる**（3 系統同期の強制）。settings.json 自体は deny 規則により AI が編集できない（B-6。本作業でも編集は permission classifier に拒否された＝統制は実効している）。したがって (a) は**今回追加できたもの（a-1）**と**settings.json への人手適用待ち（a-2）**に分かれる。

#### (a-1) allowedTools へ追加した（settings.json の allow に既に在るツール）

| 拒否された形 | 件数 | 対処（allowedTools へ追加） | 安全性の根拠 |
| --- | ---: | --- | --- |
| `Bash(gh pr view …)` ほか PR 参照 | 7 の一部＋α | `Bash(gh pr view:*)`・`mcp__github__pull_request_read`・`mcp__github__get_pull_request`・`mcp__github__list_pull_requests` | 読み取りのみ。`gh pr list` の用途は MCP の `list_pull_requests` で代替 |
| `mcp__github__create_issue` / `update_issue` | 2 | 追加せず。**既存許可の `gh issue create/edit`（`--body-file`）と `issue_write` を使う**ようプロンプトで誘導 | 新規許可なし |
| `mcp__github__search_pull_requests` | 2 | `mcp__github__list_pull_requests`・`mcp__github__pull_request_read` で代替 | 読み取りのみ |
| `mcp__github__list_sub_issues` | 1 | 同名を追加（settings に在る） | 読み取りのみ（監査 4 点目のエピック集計が使う） |
| `Write` | 2 | `Write`（settings に在る） | 結果 issue の長い本文は `--body-file` で渡すのが唯一堅牢な形。本ジョブは `contents: read` のため**書いたファイルはどこへも永続しない**。「レビュー用は書き込み手段を持たない設計」（check-permission-denials.js の文言）はレビュー用ワークフローの設計であり、成果物が issue である本監査には当たらない |
| `Bash(git -C <絶対パス> log …)` | 1 | `Bash(git -C planning log:*)` `show` `diff`（**相対パス**）＋ `Bash(git submodule status:*)` | claude-code-review.yml と同じ流儀。絶対パス形は (b) でプロンプト側から禁止 |
| issue のタイムライン・コメント参照 | — | `mcp__github__get_issue`・`mcp__github__get_issue_comments` | 読み取りのみ（`gh api` の主用途の代替） |

あわせて `Bash(gh run list:*)` を追加した（監査が自分の前回 run の成否を確認する経路。claude-code-review.yml が同じ理由で許可済み）。

#### (a-2) settings.json への人手適用待ち（§残作業）

`Bash(gh api:*)`（5＋複合）・`Bash(gh pr list:*)`・`Bash(gh label list:*)`（3）・`Bash(gh search:*)`（1）・`Bash(wc:*)`（2）は settings.json の allow に無いため**今回は追加していない**。当面はプロンプト側で代替手段（MCP・node）へ誘導し、確認できないものは「未確認」と明記させる。人手適用後に allowedTools へ追加する後続 issue を起こせる（§残作業のスニペット参照）。

### (b) 許可リストでは原理的に直せない形（41 件）→ プロンプト側で回避

check-permission-denials.js の分類（B. 構文上ありえない形）そのまま:

| 形 | 件数 | プロンプトへ追記した指示 |
| --- | ---: | --- |
| リダイレクト（`>` 等） | 16 | 使わない。出力はそのまま読む。ファイルへ書くのは Write ツールだけ |
| シェルのループ・複合形（`for` / `&&` / xargs / python3 / mkdir / find） | 13 | 1 コマンド 1 実行。整形・件数勘定は node（許可済み）で行う。走査は Glob / Grep / rg |
| 引用符内の `\|`（`--jq '.[] \| .number'` / `grep -E "A\|B"`） | 11 | **`--jq` や `grep -E` の引用符の中にも `\|` を書かない**（許可判定は引用符内の `\|` もパイプとして分割する）。`--json` で受けて node で処理する |
| `git -C` の絶対パス | 1 | 相対パス `planning` のみ使う |

プロンプトには回避策だけでなく**理由**（承認する人間がいない・拒否は黙って起き調査が欠ける・初回実行の実害）を書いた。理由の無い禁止は次の書き換えで落ちる。

## 受け入れ基準との対応

| # | issue の基準 | 状態 |
| --- | --- | --- |
| 1 | 拒否ログを (a)(b) に分類する | 本仕様書の上表（75 件 = a 34 ＋ b 41） |
| 2 | allowedTools / プロンプトを是正する | `backlog-audit.yml` を修正（本 PR） |
| 3 | 手動トリガーで再実行し、結果 issue の作成を確認する（証跡: run URL・issue URL） | **本 PR では完了できない**。`workflow_dispatch` は default branch（develop）の定義で走るため、**再実行確認はマージ後**に行う（PR 本文と #478 に明記。したがって PR は `Closes` ではなく `Refs #478` とする） |

## 残作業（settings.json への人手適用・任意）

(a-2) の 5 ツールを監査でも使えるようにする場合、**利用者が** `.claude/settings.json` の `permissions.allow` へ次を追加し、その後の PR で `backlog-audit.yml` の allowedTools へ同じ 5 つを足す（順序が逆だと `ai-workflow-config`〔STRICT〕が落ちる）。

```json
"Bash(gh pr list:*)",
"Bash(gh label list:*)",
"Bash(gh search:*)",
"Bash(gh api:*)",
"Bash(wc:*)"
```

- `gh api` の注意: CI では書き込み面がジョブの `GITHUB_TOKEN`（`issues: write` のみ）で頭打ちになるが、**ローカルの allow に入れると利用者トークン（admin）で任意の API が確認なしに打てるようになる**。追加の要否は利用者の判断である（追加しなくても監査は動く。確認できない項目が「未確認」と報告されるだけである）。
- 適用しない判断も正当である。その場合は本節を「適用しない（日付）」へ更新して閉じる。

## やらないこと

- `check-permission-denials.js` のしきい値・分類ロジックの変更（検査は設計どおり機能した。直すのは監査側である）
- `.claude/settings.json` の編集（deny 規則により AI が編集できない。本作業でも permission classifier に拒否された＝統制の実効を再確認。B-6 の既知の残存）
- 監査の観点そのものの変更（観点 5 の追加は #473 / PR #479 の範囲）

## 再実行確認の手順（マージ後・人間または次セッション）

1. `gh workflow run backlog-audit.yml --repo endazon/ai-stock-trading`
2. run の成功と、`Check permission denials` が拒否 0〜4 件（しきい値内）で緑になることを確認する
3. issue `chore(NFR): バックログ定期監査の結果` が作成（または更新）されたことを確認し、run URL と issue URL を #478 へ記録してクローズする
