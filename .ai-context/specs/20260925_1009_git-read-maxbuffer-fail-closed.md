---
title: git の出力を既定 maxBuffer で読み、読めなかったことを skip／成功へ倒す検査器を是正する
type: spec
status: accepted
related_ids: [NFR, IADR-0363, IADR-0375, IADR-0400]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
---

# 仕様書: 索引 README の 1 MiB 超えで止まった追記消失検査と、同型の読み取り失敗の是正（#1009）

## 起点

- #1009（fix）。`.ai-context/adr/README.md` が **1,048,651 バイト**（909241f5 / #1001）になり、Node の
  `execSync` の既定 `maxBuffer`（1 MiB = 1,048,576 バイト）を超えた。
- `scripts/check-adr-index-addendum-loss.js` の `sh()` は `git show <rev>:README` を既定の上限で読むため
  **`ENOBUFS`** で例外になり、`main()` の `catch` が「版を取得できなかったため skip した」を `warn` して
  **exit 0** で返していた。
- **証跡**（着手時に自分で取り直した）:

| 観測 | 結果 |
| --- | --- |
| `git cat-file -s <c>:.ai-context/adr/README.md` | 471cbf31 = 1,044,312 / d15ec5ff = 1,046,497 / **909241f5 = 1,048,651** |
| 手元 `node scripts/check-adr-index-addendum-loss.js --range=d15ec5ff...909241f5` | `warn … skip した … spawnSync C:\WINDOWS\system32\cmd.exe ENOBUFS`、**exit=0** |
| CI run 36141345761（develop・909241f5 の push）`static-checks` / `Check ADR index addendum loss` | `##[warning][check-adr-index-addendum-loss] 版を取得できなかったため skip した（origin/develop...HEAD）: spawnSync /bin/sh ENOBUFS`（ジョブは緑） |
| CI run 36136589820（d15ec5ff） | `OK: … 追記ブロック 149 件は、すべて残っています。` |
| `git rev-parse --is-shallow-repository`（作業ツリー） | `false`（上の実測は浅いクローンの打ち切りではない） |

**909241f5 以降、develop と、それを基点にする全 PR で本検査は実行されていなかった**（緑のまま）。

## 目的・受け入れ基準

1. git の出力を読む exec に十分大きな `maxBuffer`（256 MiB）を渡す。
2. **読めなかったら赤にする。** skip してよいのは既存の設計どおり「浅いクローンで範囲・版を辿れない」場合だけ。
   `ENOBUFS` は浅いクローンでも常に赤（浅さと無関係な「読めなかった」。未知 ≠ 無し）。
3. 同じ欠陥の形を `scripts/` 全体から引き直して併せて直す（下の母集合）。
4. 回帰試験: 1 MiB 超の読み取り（実 git の一時リポジトリ）と ENOBUFS の注入で、非ゼロ終了／正しい判定になる。
5. 是正後の検査器が develop の範囲で**実際に走って**緑になる（追記がすべて残っている）。

## 設計

- 共通部品 `scripts/lib/git-read.js`: `GIT_MAX_BUFFER`（256 MiB）と `isShallowSkip(err, { cwd, shallow })`
  （`ENOBUFS` → false、`git rev-parse --is-shallow-repository` が `true` → true、判定できなければ false）。
  4 スクリプトで同じ判定を書くと片方だけ緩む非対称が生まれるため 1 か所へ寄せた。ENOBUFS はテストでも
  #569 で起きており（`scripts.repo.test.js` の `MAX_BUFFER_TB`）、本件が 2 回目。
- **浅いクローンの skip を残す根拠**: ci.yml の static-checks のコメント（「浅いクローンでは版を取れず skip する
  （その旨を warn で出す）」「浅いクローンでは範囲を決められず skip する」）と、各検査器の
  `resolveRange` → null の分岐（「浅いクローン等」）が設計として明示している。範囲が決まらない（null）分岐は
  **一切変えていない**。変えたのは「範囲は決まったのに読めなかった」`catch` の分岐だけで、ここで skip して
  よいのを浅いクローン（3 ドットの merge-base がたどれない・古い版のオブジェクトが無い）に限った。
  浅いクローンでないのに読めない正当な経路は無い（自動解決の範囲は `revExists` で実在を確かめてから組む。
  明示した範囲が読めないのは入力の誤りであり、赤で知らせるべきもの）。

## 母集合（着手時に自分で引いた。規則 1〜6）

**軸 1（呼び出し側から）**: `git grep -n "child_process" -- . ':!**/node_modules/**' ':!.ai-context/**'`（拡張子で絞らない）。

| ファイル | 読むもの | 出力の規模（実測） | 失敗時 | 扱い |
| --- | --- | --- | --- | --- |
| `scripts/check-adr-index-addendum-loss.js` | `git show <rev>:README`・`git merge-base`・`git log --format=%B` | **1,048,651 B（超過中）** | warn・exit 0 | **是正**（上限＋浅いクローン以外は赤） |
| `scripts/check-adr-index-sync.js` | `git diff --name-only`・`git diff -U0 <range> -- README`・`git log --format=%B` | 索引行は 1 行が数千文字。行を大量に書き換える PR で 1 MiB を超えうる | warn・exit 0 | **是正**（同上）＋ `revExists` の `^{commit}` を引用符で囲む（Windows の cmd.exe が `^` を食い、手元では常に範囲が決まらず skip していた。姉妹検査器は既に囲んでいた） |
| `scripts/check-commit-messages.js` | `git log <range> --pretty=…%B` | 全履歴フォールバック（`HEAD`）で 313,087 B | stderr・exit 0 | **是正**（同上） |
| `scripts/gen-changelog.js` | `git log <range> --pretty=%h%x1f%s`（全履歴） | 76,631 B（伸び続ける） | **`[]` を返し CHANGELOG の節を空にする**（changelog.yml が自動コミットしうる） | **是正**（上限＋読めなければ例外） |
| `scripts/check-cross-repo-refs.js` | `git ls-files` | 248,409 B | null → fail-open | 除外: 既に `maxBuffer: 64 MiB`。fail-open は「git を使えない環境」の設計（README 記載） |
| `scripts/check-plan-id-qualification.js` | `git ls-files`（×2） | 同上 / 1 行 | 同上 | 除外: 既に 64 MiB。2 本目は自ファイル 1 行 |
| `scripts/check-test-traceability.js` | `git ls-files` 等 | 同上 | 例外 | 除外: 既に 64 MiB |
| `scripts/check-action-versions.js` | `git ls-tree` / `git show <ref>:.github/workflows/<f>` | 最大のワークフロー 78,997 B | null | 除外: 13 倍以上の余裕。対象は少数の YAML で 1 MiB へ届く経路が無い |
| `scripts/detect-changed-areas.js` | `git diff --name-only HEAD^1 HEAD` | ファイル名の一覧 | `[]` → **走らせる側へ倒す** | 除外: 失敗は fail-safe（backend を走らせる）であって成功の偽装ではない |
| `scripts/check-backlog-audit-output.js` / `scripts/check-planning-adr-range.js` | `gh issue view --json` / `gh api`（JSON） | 数 KB | 例外（呼び出し側が不在／unverified） | 除外: git 出力ではなく小さな JSON。失敗は既に可視化される |
| `.claude/hooks/check-impl.js` / `.claude/hooks/guard-bash.js` | `git diff --name-only` 等 / `git symbolic-ref` | 名前の一覧 / 1 行 | null（警告のみの hook） | 除外: 検査器ではなく助言 hook（「誤検知より見逃しを選ぶ」と明記）。timeout 800ms で縛る |
| `scripts/scripts.test.js` / `scripts/scripts.repo.test.js` | 検査器の子プロセス出力・固定 SHA の `git show` | 子の stdout は小さい。固定 SHA は将来も伸びない | 例外 → テストが赤 | 除外: 読めなければテストが落ちる（成功へ倒れない）。1 MiB 超の知識グラフは #569 で既に `MAX_BUFFER_TB` |

**軸 2（誤りの側＝ skip の文言から）**: `git grep -n -E "skip した|スキップする|取得できなかった|できなかったため" -- scripts`。
上の 4 件の他に出たのは `check-ci-latency.js`（GitHub API。fail-open は決定）・`check-ai-workflow-config.js`
（ワークフローが無い環境）・`check-cross-repo-refs.js` / `check-plan-id-qualification.js`（git が無い環境）・
`detect-changed-areas.js`（fail-safe）で、**いずれも git 出力の読み取り失敗を成功へ倒す形ではない**。

**軸 3（大きいファイルから）**: `git ls-tree -r -l HEAD | sort -k4 -n -r`。1 MiB を超えるのは索引 README だけ。
次点 `docs/tests/FR-10_risk-controls-tests.md` は 467,918 B で、これを git 経由で読むスクリプトは無い（`fs` で読む）。
`CHANGELOG.md` は 57,250 B（`fs`）。

**追随する文書（規則 9。誤りの側の文言で走査）**: `git grep -n -E "版を取得できなかった|差分を取得できなかった|版を取れず|範囲を決められず|git log できなかった|検査範囲を特定できなかった|maxBuffer|ENOBUFS"`
（`.ai-context/specs/` は凍結記録のため除外）→ `.github/workflows/ci.yml` のコメント 2 か所・`scripts/README.md`
（行の説明・lib 表・検査器を書くときの規約）を更新した。

## テスト（受け入れ基準 → テスト）

| 基準 | テスト |
| --- | --- |
| 1・4 | `scripts.repo.test.js` `check-adr-index-addendum-loss[#1009 e2e]`（1.1 MiB の索引を持つ一時リポジトリで、無消失は `OK:`・消失は赤で行を名指し）、`check-commit-messages[#1009 e2e]`（本文 1.2 MiB のコミットを検査し、件名違反なら赤）、`gen-changelog[#1009 e2e]`（件名 1.1 MiB のコミットが CHANGELOG に載る） |
| 2 | 自己試験: addendum-loss 7 件・adr-index-sync 5 件（ENOBUFS → 赤〔浅いクローンでも〕・浅いクローンでない失敗 → 赤・浅いクローン → skip〔否定形〕）。`scripts.repo.test.js` の `collectCommits` 3 件・`isShallowSkip` 1 件 |
| 4（効きの確認） | `GIT_MAX_BUFFER` を 1 MiB へ戻す変異で e2e 3 件＋上限の試験 1 件が赤、検査器の本走は ENOBUFS で **exit 1**（旧: exit 0） |
| 5 | `node scripts/check-adr-index-addendum-loss.js --range=d15ec5ff...909241f5` → `OK: … 149 件は、すべて残っています`（exit 0）。既定範囲でも同じ |

## 別の問い（本 PR では扱わない）

索引 README（1 MiB 超・1 行が数千文字）そのものを分割すべきか。IADR-0363 決定 5 は「1 IADR = 1 行の要約」案を
並走 PR との衝突を理由に見送っている。本 PR は検査器側の上限と fail-closed だけで、索引の形には触れない。
