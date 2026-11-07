---
title: 作業仕様書 — 週次のバックログ監査を決定的なスクリプトへ置き換え、AI レビューの起動条件・実装用の許可ツール・全ワークフローの権限と版・検査器の git の起動・Dependabot の対象を締める
type: spec
status: in progress
related_ids: [NFR, IADR-0170, IADR-0185, IADR-0504, IADR-0625]
author: claude
created: 2026-11-07
updated: 2026-11-07
---

# 作業仕様書 — CI のワークフローの点検（IADR-0625）

> 本仕様書は実装着手前に作成した（着手 2026-11-07。develop `7d485f688`）。以前に develop `18524551b` 基準で用意した是正の内容を、今の develop へ移植した。秘密の検出のフックの規則（Anthropic の API キーの形）は #1540 で既に develop に入っていたため、本作業の差分には含まない。

## 目的

- `backlog-audit.yml` を、生成 AI を使わない決定的なスクリプト（`scripts/backlog-audit.js`）で動かし、書き込みを issues だけにする。
- `claude-code-review.yml` の自動起動を、本リポジトリのブランチから出た信頼できる作成者の PR に絞る。
- 全ワークフローにワークフロー単位の `permissions:` を置き、外部の `uses:` を SHA ＋版の注記で固定する。
- `claude-coding.yml` の起動者の条件（本文の書き手の `author_association`）を、コメント・issue で起動する全ワークフローについて確かめる。
- ワークフローから呼ばれる検査器が、外から与えられる値を文字列連結で `execSync` に渡している箇所をすべて直す。
- `claude-coding.yml` の許可ツールを基盤の最小化に揃え（`find` / `sed` を外し、`node` は `node scripts/` の前置だけ）、`check-ai-workflow-config.js` の宣言を同時に直す。
- Dependabot の対象に nuget（`/`）と npm（`/frontend`）を足す。

## 母集合（規則 9・10。着手前に引いた）

- コメント・issue・レビューで起動するワークフロー（`issue_comment` / `issues` / `pull_request_review*` / `discussion*` / `pull_request_target` / `workflow_run`）: `claude-coding.yml` の 1 本だけ。4 句とも書き手の判定を持つ。
- ジョブ単位の `if:` をブランチ名（`github.head_ref` の `automation/`）で決めているもの: `ci.yml` の `lint` / `backend-test` / `build-and-test` / `frontend` / `frontend-e2e` と `claude-code-review.yml` の `claude-review`（前 3 つと `claude-review` は必須チェック）。
- AI のアクションを使うワークフロー: `claude-coding.yml` / `claude-code-review.yml` / `backlog-audit.yml`。
- ワークフロー単位の `permissions:` が無い: `backlog-audit` / `ci` / `claude-code-review` / `copilot-setup-steps` / `integration`。
- 外部 `uses:`（2026-11-07 の develop で再走査）: `claude-coding.yml` 以外はタグ参照。固定後は 16 種（サブアクション別）で、`git ls-remote` で全件を公式のタグと照合した（不一致 0）。基盤が固定している版（setup-dotnet v6.1.0・claude-code-action v1.0.267・codeql-action v4.39.2 ほか）は同じ SHA に揃えた。コメント中の `uses:` 2 行（`ci.yml` の例示）は対象外。
- 実装用の許可ツールで任意の実行に当たる・基盤が外したもの: `Bash(find:*)`・`Bash(sed:*)`・`Bash(node:*)`。`npm ci` / `npm run` / `npm --prefix` はフロントエンドの検証に要るため残した（IADR-0625 残余 3）。
- Dependabot の対象: github-actions（`/`）・docker（`/backend`）だけで、nuget / npm が無い。`scripts/` と `.claude/hooks/` の package.json は依存を持たないため対象外。
- `child_process` を使う `scripts/`（試験を除く）と `.claude/hooks/`: 17 ファイル。うち `execSync` で文字列を組むもの: `check-commit-messages.js`・`check-adr-index-sync.js`・`check-adr-index-addendum-loss.js`・`gen-changelog.js`（外から与えられる値を含む）、`.claude/hooks/check-impl.js`・`.claude/hooks/guard-bash.js`（定数だけだが同じ形へ揃えた）。他の 11 ファイルは `execFileSync` / `spawnSync` / `spawn` を引数の配列で使っている。
- 旧版の監査の配線を前提にした試験: `scripts.repo.test.js` の #711 / #717 / #1208 の 4 件（AI のステップ名・プロンプトの文言を見ていた）→ 新しい配線に合わせて書き直した。

## 受け入れ基準と試験（`scripts/scripts.repo.test.js`）

1. `check-commit-messages.js` / `check-adr-index-sync.js` / `check-adr-index-addendum-loss.js` に `COMMIT_RANGE`・`GITHUB_BASE_REF` としてメタ文字を含む値を与えても、副作用のファイルが作られない（実バイナリ）。
2. `gen-changelog.js` はメタ文字を含むタグ名でも副作用が起きず、CHANGELOG を生成する（使い捨てのリポジトリで実測）。
3. `gitArgv` は配列以外を拒み、`isSafeRevArg` は `-` で始まる値・空・改行を拒む。
4. `scripts/`（試験を除く）と `.claude/hooks/` に `execSync(`・`exec('…`・`shell: true` が無い（変異試験あり。正規表現の `.exec` は誤検出しない）。
5. 全ワークフローがワークフロー単位の `permissions:` を持ち、外部の `uses:` がすべて SHA ＋ `# vX.Y.Z`。
6. `claude-review` の `if:` が「同一リポジトリ」AND（「OWNER / MEMBER / COLLABORATOR」OR「bot」）、ワークフロー単位 `contents: read`、`pull_request_target` を使わない（変異試験あり）。
7. `backlog-audit.yml` が AI のアクション・資格情報・`id-token` を持たず、ジョブの権限が `contents: read` / `issues: write` / `pull-requests: read` だけで、`node scripts/backlog-audit.js --post` を呼び、checkout が資格情報を残さず、イベントの値を式で展開しない（変異試験あり）。
8. `backlog-audit.js --self-test`（14 件）が通り、題名の無害化（マーカー・メンション・コード・改行）が効く。
9. `claude-coding.yml` の許可ツールが `find` / `sed` / `awk` / `node:*` を持たず、`node scripts/:*` を持つ。`parityWarnings` は狭めた形を広い形で覆われたと見なし、覆われなければ警告する。`check-ai-workflow-config.js --self-test` が狭めた対・逆向き・`find` / `sed` の戻しを検出する。
10. `dependabot.yml` が github-actions（`/`）・nuget（`/`）・npm（`/frontend`）・docker（`/backend`）を対象にする。

- **追加（同日・監査の追加指摘）**: `claude-review` はジョブ単位の `if:` を持たず、checkout の直後の `Gate (trusted PR)`（`scripts/pr-trust-gate.js`）が判定し、後続の step はすべて `steps.gate.outputs.run == 'true'` で出し分ける（変異試験: ジョブ単位の if を戻す・Gate を外す・1 step の出し分けを外すと落ちる）。`pr-trust-gate.js` はフォーク・外部の作成者・生成物以外を含む `automation/` の PR を合格（skip）にしない（ブランチ名だけを偽った場合に赤）。どのワークフローもジョブ単位の `if:` を `github.head_ref` で決めない（変異試験あり）。

先に試験（`scripts/scripts.repo.test.js`）だけを develop `7d485f688` の作業ツリーへ当て、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` で新しい試験 23 件が赤になることを確かめてから実装を入れた。もう 1 件の赤（`過去の索引追随漏れ 2 件を検出できる`）は本作業と無関係で、develop に無いコミットが手元のクローンにだけ残っているため走った（develop だけを持つクローンでは CI と同じく skip の扱い）。

［2026-11-07 追記 / AI レビューの指摘への対応］

- `pr-trust-gate.js` の `changedFiles()` は、`git cat-file -e <sha>^{commit}` で両 SHA が手元にあるかを確かめ、無いときだけ `--depth` を付けずに fetch する（従来は `--depth=1` の fetch を無条件に呼んでいた。checkout は `fetch-depth: 0` であり、浅い fetch は手元の履歴を浅くして、この diff と後続の step の merge base の解決を壊し得る）。差分が取れなければ従来どおり null（信頼できる作成者なら run に倒れる）。自己試験を 1 件足した（使い捨てのリポジトリで、両 SHA があれば fetch を呼ばず `.git/shallow` が作られず差分が取れること、無い SHA では `--depth` なしの fetch を試みて null になること）。自己試験は 9 件。
- `backlog-audit.js` の `renderReport` は、blocked の台帳にも `SECTION_LIMIT` を当て、超えた件数を「ほか N 件」と書く（台帳だけが上限の対象外だった）。自己試験を 1 件足し、15 件になった（受け入れ基準 8 の「14 件」は当時の値）。

## 再開の手順（利用者）

`backlog-audit.yml` の冒頭の注記に書いた（develop に入ったことを確かめ、ワークフローを有効にし、手動実行で追跡 issue と Summary を確かめる）。

## 対象外

- `id-token: write` の撤去、`changelog.yml` / `openapi.yml` の checkout の資格情報、NuGet のロックファイル（IADR-0625 残余）。
- `backlog-audit.yml` の有効化（利用者が行う）。
