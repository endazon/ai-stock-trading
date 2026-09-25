---
title: CODEOWNERS の配置と develop ルールセットの利用者手順（#501・#644）
type: spec
status: accepted
related_ids: [NFR, IADR-0185, IADR-0190]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs: []
---

# CODEOWNERS の配置と develop ルールセットの利用者手順

## 背景

利用者の指示（2026-09-26「blocked:human となっているものもあなたが対応してください」）で、#501（CODEOWNERS）と
#644（ブランチ保護と必須チェック）を AI が進められるところまで進める。

🔴 **ハードリミット**: リポジトリのセキュリティ設定（ブランチ保護・ルールセット・必須チェック）は**変更しない**。
利用者が実行する正確なコマンドを用意するだけである（IADR-0185 決定 2 の「規則による禁止」）。

## 再測定（2026-09-26・読み取りのみ）

| 対象 | コマンド | 結果 |
| --- | --- | --- |
| 旧来の保護 | `gh api repos/endazon/ai-stock-trading/branches/develop/protection` | 404 `Branch not protected`（従来の記録どおり） |
| ルールセット | `gh api repos/endazon/ai-stock-trading/rulesets` | **`develop-rule`（18662050・active・2026-07-08 作成／07-16 更新）と `main-rule`（18662047）が存在** |
| develop に効くルール | `gh api repos/endazon/ai-stock-trading/rules/branches/develop` | PR 必須（承認 1・コードオーナー承認・last push 承認・会話解決）、必須チェック `pr-title` のみ、update/creation/deletion/non_fast_forward、直線履歴、署名、CodeQL、code_quality、Copilot レビュー |
| バイパス | 同上 `bypass_actors` | Admin ロール（id 5）**`exempt`**、Integration 29110（dependabot）/ 1236702（claude）/ 1143301（copilot-swe-agent）/ 946600（未特定）が `always` |
| check 名の実在 | PR #1014 の head `593136af` の check-runs | `build-and-test` `lint` `commit-messages` `pr-title` `Secret scan (gitleaks)` `Dependency review` `claude-review` すべて report（`Analyze (csharp)` `backend-test (1..4)` `static-checks` `scripts-tests` `frontend` `frontend-e2e` `pr-size` ほか） |
| bot PR | PR #715 の head の check-runs | 7 件は success または skipped（skipped は必須上合格） |
| 自動更新 PR（監査で是正） | `gh secret list`・PR #798 の head の check-runs | Secret は `CLAUDE_CODE_OAUTH_TOKEN` と `PLANNING_REPO_TOKEN` だけで `AUTOMATION_PR_TOKEN` は未登録。**#798 の check-run は 0 件**。初版の Runbook の「changelog は PAT で PR を作る」は誤りで、CHANGELOG も OpenAPI と同じく `--admin` が要る（#715 に check-run があった経緯は未調査） |
| 起動条件 | `ci.yml` / `security.yml` / `pr-title.yml` / `claude-code-review.yml` の `on:` | いずれも `paths:` 無し・`reopened` あり |
| GitHub Actions の App ID | `gh api apps/github-actions --jq .id` | 15368 |

**帰結**: 過去の再検証（#644・#501 の 2026-09-23 コメント、#483）は旧来の保護 API だけを見ていた。
#644 のコメントが挙げた `branches/develop/protection` への PUT を実行すると、**ルールセットと旧来の保護の二重管理**になる。
必須チェックはルールセットへ入れるのが正しい。

もう 1 点。PR は AI 作成分も利用者のアカウント（endazon）が作成者であり、**作成者は自分の PR を承認できない**。
「承認 1・コードオーナー承認」は単独アカウントではバイパスなしに満たせない。したがって、コードオーナーが AI に効くのは
AI に別アカウントを持たせたとき（Runbook 案 C）だけであり、単独アカウントのまま実効化できるのは「赤いマージを止める」まで（案 B）。

## 変更

1. `.github/CODEOWNERS.example` → `.github/CODEOWNERS`（`* @endazon`）。指名は #501 の再検証コメントの定め（本人で構わない）と
   利用者の引き取り指示による。現行ルールセットでは管理者が exempt のため、配置によって既存の運用が止まることは無い。
2. `docs/operations/branch-protection-runbook.md` を新設。実測・手順 0（退避）・手順 1（必須チェック 7 件をルールセットへ。
   ルール配列は取得値そのまま＋差し替え 1 要素）・手順 2（案 A/B/C と案 B の完全な本文）・確認・戻し方・限界。
3. `docs/ai-workflow.md` の「未配備」に日付つき訂正。`docs/blocked-tasks.md` B-1・B-2 に再測定を追記。
   `AI_SETUP.md` 共通セットアップ 4 に配置済みの注記。`docs/operations/operations.md` 関連文書に行を追加。

## 監査の指摘による是正（2026-09-26）

- 失敗時の分岐: 自動更新 PR は CHANGELOG・OpenAPI とも check が付かないため `--admin` が要る、と改めた（上表）。
- 手順 0 で取得値を更新 API が受け取る 6 項目に絞って保存し、そのまま戻し方の本文にする。管理者はバイパス設定と無関係にルールセットを編集・無効化できる（戻せなくなることは無い）ことを明記した。

## 母集合（規則 9）

誤りの側の文字列 `CODEOWNERS\.example` / `Branch not protected` / `ブランチ保護.{0,10}(未配備|未設定)` を
`.ai-context/` 以外の全ファイルで走査した: `AI_SETUP.md:63`・`docs/blocked-tasks.md:611`・`docs/ai-workflow.md:145` の 3 件。
いずれも本 PR で追随した。`.ai-context/` の凍結記録（IADR-0185 ほか）は書き換えない。`scripts/detect-changed-areas.js` は
`^\.github\/CODEOWNERS$` を既に SAFE として持つ（変更不要）。

## 受け入れ基準

- [x] `.github/CODEOWNERS` が存在し、全パスを `@endazon` に割り当てる
- [x] 必須チェック 7 件の名前が、実際に report された check-run 名と一致する（上表）
- [x] Runbook のルール配列が、取得したルールセットと `required_status_checks`（案 B では加えて `update`・`pull_request`）以外で一致する（scratchpad の生成スクリプトで取得値から組み立てた）
- [x] リポジトリ設定は一切変更していない（GET のみ）
- [ ] 利用者が手順 1・2 を実行する（#644・#501 の残件）

## 射程外

- `main-rule` の見直し。AI 用アカウントの作成（資格情報の新設は利用者の操作）。`--admin` を AI に禁じる規約の追加（案 B 採用後の判断）。
