---
title: IADR-0625 週次のバックログ監査を生成 AI を使わない決定的なスクリプトへ置き換え、AI レビューを走らせる PR をジョブの中で判定し、必須チェックをブランチ名で外さず、全ワークフローの権限と版を固定し、検査器が git へ渡す値をシェルを通さずに渡す
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0170, IADR-0185, IADR-0504]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
---

# IADR-0625: CI のワークフローの権限・起動条件・入力の渡し方を締める（決定的なバックログ監査を含む）

- 状態: **Accepted**
- 日付: 2026-11-07
- 決定者: Claude Code（実装）／ オーケストレーター（CI と AI ワークフローの権限と起動条件を絞る指示 2026-11-07）

## 起点・関連

- 起点 ID: NFR（運用の安全）。
- 前提: IADR-0170（バックログ監査の自動化。自動クローズしない）・IADR-0185（blocked の再検証）・IADR-0504（計画 ID レンジのずれの専用 issue）。基盤（microservices-platform）の同型の決定（`scripts/backlog-audit.js` と、起動者の条件・SHA の固定）に揃える。
- 作業仕様書: [`.ai-context/specs/20261107_ci-workflow-hardening.md`](../specs/20261107_ci-workflow-hardening.md)

## コンテキストと課題

1. `backlog-audit.yml` は週次で生成 AI に監査させていた。AI は外の人も書ける issue の題名・本文を読み、issues: write と `id-token: write` を持つジョブで動く。数えられる面（Proposed の IADR・blocked の滞留・ci-failure 等）まで AI に任せていたため、結果は回ごとに揺れ、権限も広かった。
2. `claude-code-review.yml` は `pull_request` のたびに起動し、作成者や PR の出どころを見ていなかった（`claude-coding.yml` は本文の書き手を OWNER / MEMBER / COLLABORATOR に絞っている）。
3. 5 本のワークフロー（`backlog-audit` / `ci` / `claude-code-review` / `copilot-setup-steps` / `integration`）がワークフロー単位の `permissions:` を持たず、リポジトリの既定の権限で走っていた。`claude-coding.yml` 以外の外部の `uses:` はタグで参照していた。
4. 検査器の一部（`check-commit-messages.js` / `check-adr-index-sync.js` / `check-adr-index-addendum-loss.js` / `gen-changelog.js`）は、範囲（`--range` / `COMMIT_RANGE` / `GITHUB_BASE_REF` から組む）やタグ名を文字列へ連結して `execSync` に渡していた。git の参照名は `;` `$(...)` 等の文字を許すため、値がシェルで解釈される形だった（試験で実測した）。

## 検討した選択肢（バックログ監査）

| 案 | 採否 | 理由 |
| --- | --- | --- |
| AI のまま、許可ツールと権限だけ絞る | 不採用 | 外の人が書いた本文を AI が読む構造は残る。数えられる面を AI に任せる理由も無い |
| **決定的なスクリプトへ置き換え、判断が要る面は候補を並べて人に残す** | **採用** | 基盤の `scripts/backlog-audit.js` と同じ形。同じ入力から同じ報告になり、書き込みは issues だけで済む |
| 監査そのものをやめる | 不採用 | 滞留の検知を定期棚卸しに委ねる規約（CLAUDE.md）の実現手段が無くなる |

## 決定

1. **バックログ監査は `scripts/backlog-audit.js`（Node 標準のみ・GitHub API は fetch）で行う。** 見る面: Proposed の IADR／動いていない作業仕様書（draft / in progress / pending）と `docs/` の文書／`blocked*` ラベルの再検証（`再検証期限:` が過ぎた、または期限が無く最後のコメントから閾値日数超）／`blocked*` ラベルと `docs/blocked-tasks.md` の食い違い／正規化した題名が一致する open issue の組／エピック（既定 #344）の子 issue の進捗／`ci-failure`／古い PR／計画 ID レンジ（前段 `check-planning-adr-range.js` の JSON）。**状態は書き換えず、issue を自動でクローズしない**（IADR-0170 決定 5 を維持）。クローズ漏れ・分類の誤りの判定は機械で行わず、候補を見て人が判断する。
2. **issue の題名・本文はデータとしてのみ扱う。** シェル・式・プロンプトへは渡さない。報告へ載せる題名は無害化する（`<` `>`・バッククォート・角括弧を全角へ、`@` の直後にゼロ幅空白、改行を空白へ、120 文字で切る）。追跡 issue はラベル `backlog-audit` とマーカーで選び、無ければ旧題名の issue を信頼できる書き手（OWNER / MEMBER / COLLABORATOR・`github-actions[bot]`）のものに限って引き継ぐ。
3. **`backlog-audit.yml` の権限はワークフロー単位 `contents: read`、ジョブは `contents: read` / `issues: write` / `pull-requests: read` だけ。** AI のアクション・資格情報・`id-token` は持たない。checkout は資格情報を残さない。自己試験が通ったときだけ投稿し、本文を読み戻して run 固有のマーカーを確かめ、報告を step summary へ出し（空なら赤）、`check-backlog-audit-output.js` が別途 `updated_at` を確かめ、失敗は `ci-failure-issue.yml` が issue にする。
4. **AI レビューはジョブの中で判定する。** `claude-review` からジョブ単位の `if:` を外し（skipped は必須チェック上の合格になるため）、checkout の直後の step `Gate (trusted PR)` が `scripts/pr-trust-gate.js` を走らせる。判定には GitHub が認証した値（`head.repo.full_name`・作成者のログイン・`author_association`・変更ファイル）だけを使い、ブランチ名だけでは決めない。本リポジトリのブランチから出た PR で、作成者が OWNER / MEMBER / COLLABORATOR またはログインが `[bot]` で終わる（種別 `user.type` は見ない）なら AI を走らせる。同一リポジトリの dependabot と、`automation/` から出た信頼できる作成者（`github-actions[bot]` または OWNER / MEMBER / COLLABORATOR）の PR で変更が生成物（`CHANGELOG.md` / `docs/api/openapi.yaml`）だけのものは、AI を走らせずに合格にする。それ以外（フォーク・外部の作成者）は**赤**にする。後続の step はすべて `steps.gate.outputs.run == 'true'` で出し分ける。ワークフロー単位の `contents: read` を足し、外部の `uses:` を SHA で固定する。ツールの許可（`claude_args`）は変えない（`check-ai-workflow-config.js` の宣言の変更は要らない）。
5. **全ワークフローにワークフロー単位の `permissions:` を置き、外部の `uses:` を 40 桁の SHA ＋同じ行の `# vX.Y.Z` で固定する。** SHA は `git ls-remote` で版のタグと照合した。基盤が既に固定している版はその SHA に揃えた（`setup-dotnet` v6.1.0・`claude-code-action` v1.0.267・`codeql-action` v4.39.2 ほか）。Dependabot（github-actions）は SHA と注記を対で更新し、`codeql-action` のサブアクションは 1 本の PR にまとめる。
6. **検査器は git を引数の配列で起動する。** `scripts/lib/git-read.js` に `gitArgv`（配列以外は例外）と `isSafeRevArg`（`-` で始まる値・空・改行や NUL を拒む）を足し、上の 4 本と `.claude/hooks/` の 2 本を `execFileSync` へ移した。`-` で始まる範囲はオプションと取り違えるので、git へ渡さず読み取り失敗として扱う。
7. **必須チェックのジョブ（`ci.yml` の `lint` / `backend-test` / `build-and-test`。あわせて `frontend` / `frontend-e2e`）からブランチ名（`automation/`）によるジョブ単位の `if:` を外す。** ブランチ名は PR を出す人が自由に付けられ、フォークの PR でも skipped＝合格になっていた。生成物だけの自動 PR は、各ジョブの既存の Gate（`detect-changed-areas.js`。変更ファイルで判定する）が中身を skip する。ジョブ単位の `if:` を `github.head_ref` で決めないことを全ワークフローについて試験で固定する。

8. **`claude-coding.yml` の許可ツールを基盤の最小化に揃える。** 実装用から `find` と `sed` を外し、`node` は `node scripts/` の前置の形（リポジトリの `scripts/` 配下のスクリプトの実行）だけを許す。レビュー用との差は `check-ai-workflow-config.js` の宣言で表す（`CODING_NARROWED_TOOLCHAIN` に `node:*` ⇔ `node scripts/:*`、`REVIEW_ONLY_BASH` に `sed`。`find` は実装用の例外から外し、戻すと検出する）。`.claude/settings.json` との突き合わせは、狭めた形を広い形が覆っていれば警告しない。起動条件（4 句すべてで本文の書き手の `author_association` を OWNER / MEMBER / COLLABORATOR に絞る）とワークフロー単位の `contents: read` は維持し、試験で固定する。
9. **Dependabot の対象に使用スタックのエコシステムを足す。** nuget（`/`。ルートの `Directory.Packages.props`）と npm（`/frontend`）。既定は自動マージしない。

## 射程

- 対象: `.github/workflows/` の全 15 本・`.github/dependabot.yml`・ワークフローから呼ばれる検査器（上の 4 本）と `.claude/hooks/` の 2 本・`check-ai-workflow-config.js` の宣言。
- 対象外: ワークフローの起動イベントと必須チェックの名前（ブランチ保護が名前で見るため変えない）。`backlog-audit.yml` の有効化（利用者が行う。手順はワークフローの冒頭）。NuGet のロックファイル。

## 結果

- 監査は同じ入力から同じ報告になり、外の人が書いた本文がシェル・式・AI のいずれにも届かない。権限は issues: write だけになる。
- 外の作成者・フォークからの PR では AI レビューが走らず、`claude-review` は赤になる（その差分は人が読み、本リポジトリのブランチから出し直す）。ブランチ名だけを `automation/` に似せた PR は、AI レビューも必須チェックも素通りしない。
- 生成物だけの自動 PR でも `frontend` / `frontend-e2e` は走る（数分の所要が増える）。
- メタ文字を含む範囲・タグ名を与えても副作用が起きないことを、実バイナリの試験で固定した（先に赤を確かめた）。後戻り止めとして、`scripts/`（試験を除く）と `.claude/hooks/` に `execSync`・`shell: true` が無いことを静的に検査する。

## 残余

1. 外部の作成者の PR は `claude-review` が赤のまま残る。取り込み直す運用（人が差分を読む）に依存する。
2. `claude-coding.yml` / `claude-code-review.yml` の `id-token: write` は残した。アクションの認証に要らないことを確かめてから外す（所有者の判断）。
3. 許可ツールの最小化は多層防御である。実装用は `Write` と `node scripts/` を持つため、狭めた形だけでは任意の実行を原理的に止めない。一次の守りは起動条件（本文の書き手）である。`npm ci` / `npm run` / `npm --prefix` はフロントエンドの検証に要るため残した。
4. `changelog.yml` / `openapi.yml` などの checkout は資格情報を残したままである（push を create-pull-request に任せる形なので外せる見込み。基盤は外した）。
5. 題名の一致による重複の検出は、表記の違う重複を拾わない（候補を出すだけで、取りこぼしは人の棚卸しに残る）。
6. `Gate (trusted PR)` は PR の作業ツリーにある `scripts/pr-trust-gate.js` を走らせる。フォークの PR に対しては GitHub が秘密を渡さず書き込みの権限も与えないことが一次の守りで、Gate は必須チェックを skipped で合格にしないための判定である。
