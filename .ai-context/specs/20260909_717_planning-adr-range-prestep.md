---
title: 週次バックログ監査の項目 6（計画 ADR レンジ鮮度）を Claude ステップ前の決定的ステップで解決する
issue: "#717"
plan_refs:
  - NFR
adr_refs: []
status: done
created: 2026-09-09
---

# 作業仕様書: 週次バックログ監査の項目 6（計画 ADR レンジ鮮度）を Claude ステップ前の決定的ステップで解決する（#717）

## 起点

- issue #717（NFR・無採番）。関連: #710（レンジ宣言の更新漏れ）・#711（産出検証）・#496（`PLANNING_REPO_TOKEN` が
  PR CI から使える実測）・`docs/blocked-tasks.md` B-3（secret の存否は AI から読めない）。
- 実測（2026-09-09・run 34303693213 の #483 §6）: PR #713 が追加した監査項目 6 は、AI が
  `mcp__github__get_file_contents` で `endazon/project-planning` を読む設計だったが、`secrets.GITHUB_TOKEN` は
  本リポジトリしか読めず **404**。隣接クローンもランナーに無い。項目 6 は恒久的に「未確認」で終わる。
  プロンプトの「許可済み・1 回の呼び出しで済みます」は誤りだった。

## 設計

AI に cross-repo の資格情報を持たせず、**Claude ステップの前に決定的なスクリプトが突き合わせを済ませ、
AI は結果ファイルを読んで報告するだけ**にする。

| 要素 | 内容 |
| --- | --- |
| `scripts/check-planning-adr-range.js`（新設） | 宣言 `ADR-0001..NNNN`（`lib/plan-ranges.js` の `readPlanAdrRange()`）と、`gh api repos/endazon/project-planning/contents/projects/ai-stock-trading/07_adr` の一覧から得た実在最大番号を比較し、`--out` の JSON へ `status`（`ok` / `behind` / `ahead` / `unverified`）・`declaredMax`・`planningMax`・`reason`・`checkedAt`・`source` を書く。token は env `PLANNING_REPO_TOKEN` を子プロセスの `GH_TOKEN` へ写す |
| fail-open | **常に exit 0**。secret 不在・API 失敗（404 含む）・宣言不読はいずれも `unverified` と理由。項目 6 の検証不能で監査の他 5 項目を巻き込まない（産出そのものは `check-backlog-audit-output.js` が守る）。`behind` / `ahead` / `unverified` は CI アノテーション（warning）にも出す |
| `backlog-audit.yml` | `Resolve planning ADR range` ステップ（setup-node 直後・Claude ステップ前。env `PLANNING_REPO_TOKEN: ${{ secrets.PLANNING_REPO_TOKEN }}`。出力 `.backlog-audit/planning-adr-range.json`＝ワークスペース内で AI の Read が届く範囲。`contents: read` のため永続しない） |
| プロンプト項目 6 | 前段の JSON を Read し `status` に従って報告する形へ書き換え。「`endazon/project-planning` を自分で読みに行かない（404 になる）」を明記。誤った「許可済み」の記述を削除 |
| `.gitignore` | `.backlog-audit/` を追加（ローカル実行時の混入防止） |
| 回帰テスト | `scripts.repo.test.js` に 3 件（自己試験・secret 不在で exit 0 かつ `unverified`〔実バイナリ〕・ワークフロー配線〔前段ステップの位置・secret・出力先とプロンプト参照先の一致・誤記の不在〕） |

### 採らなかった案

- `with.github_token` を PAT にする: MCP 経由の issue 書き込みが bot ではなく個人名義になり、IADR-0170 の前提と変わる。
- AI に `gh api` を許可して PAT を env で渡す: cross-repo の資格情報が AI のツール実行に露出する。許可リストの拡張も要る。

## 検証

- `node scripts/check-planning-adr-range.js --self-test` → 9 件 pass。
- `node scripts/check-planning-adr-range.js --out <tmp>`（secret 無し）→ exit 0・`status: unverified`・
  `declaredMax: 35`・`reason: 計画側を取得できない: PLANNING_REPO_TOKEN が渡されていない（secret 不在。B-3）`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 338 件 pass。`check-ai-workflow-config.js` 問題なし。
  `check-doc-links.js` OK。YAML 構文（ステップ順: Checkout → Record run start time → setup-node →
  Resolve planning ADR range → Run backlog audit → Upload audit transcript → Check permission denials →
  Verify backlog audit output）。
- 実走: 本ブランチを `workflow_dispatch` し、#483 §6 が `ok`（一致・根拠 `declaredMax`）か `unverified（secret 不在）`
  のどちらで出るかを確認する。**`unverified` なら `PLANNING_REPO_TOKEN` の登録が利用者作業として残る**
  （受け入れ基準 2 の形で success すること自体は本 PR で確認できる）。

## 変更しないこと

- `--allowedTools` は変えない（AI に cross-repo の手段を足さない）。
- 突き合わせの結果でジョブを落とさない（`behind` は監査の指摘として issue 本文に載せ、判断は人間／後続 PR に残す）。
