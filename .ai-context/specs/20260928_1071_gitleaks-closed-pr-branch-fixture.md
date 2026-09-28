---
title: 週次 Security の gitleaks が閉じた PR のブランチに残る fixture の偽値で落ちる（#1071）
type: spec
status: accepted
related_ids: [NFR, IADR-0439]
author: claude (Claude Code)
created: 2026-09-28
updated: 2026-09-28
plan_refs: []
---

# 週次 Security の gitleaks が閉じた PR のブランチに残る fixture の偽値で落ちる

## 背景

#1071（自動起票）: Security（`schedule`・develop `1465f3e6`・実行 36403687073）のジョブ
`Secret scan (gitleaks)` が失敗した。NFR（セキュリティ・CI 運用）の無採番メタ作業である。

## 調査（読み取りのみ・値は書き写さない）

| 項目 | 結果 |
| --- | --- |
| ルール | `kubernetes-secret-yaml`（既定ルール） |
| ファイル・行 | `scripts/fixtures/helm-release-drift/live.yaml` 12 行目（Secret の `data:` の値） |
| コミット | `13fcb635`（2026-09-26。#1042 の第 1 コミット） |
| 値の正体 | 自己試験用の**見張り用の偽値**（`FIXTURE-SECRET-SENTINEL` の base64。先頭 4 文字 `RklY`）。ファイル冒頭のコメントどおり実在の資格情報ではない（デコードして前方一致だけを確認し、値そのものは表示していない） |
| develop に含まれるか | **含まれない**（`git merge-base --is-ancestor` が偽）。`git branch -r --contains` は `origin/chore/NFR-1022-helm-release-drift` だけ |
| MSP の pin を進めた範囲 `7a7a8a1..1465f3e`（24 コミット） | **含まれない** |
| なぜ develop の実行で出るか | `actions/checkout`（`fetch-depth: 0`）が全ブランチを取得し、gitleaks-action は schedule では `gitleaks detect`（＝`git log --all`）を走らせる。閉じた PR #1042 のブランチが削除されずに残っているため、その履歴も走査対象になる |
| 経緯 | #1042 は PR 範囲の gitleaks が同じ理由で赤になり、force push 禁止のため #1043（fixture を `stringData` に改め 1 コミットに集約・squash `f23f2fa3`）で置き換えて閉じた。ブランチは残った |

**判定: 偽陽性**（テスト用の見張り偽値。develop の現行ツリー・履歴には無い）。

## 変更

`.gitleaksignore` に当該 finding の fingerprint（コミット＋パス＋ルール＋行）を 1 行追加する。
既存の運用（「単発・履歴上の誤検知は fingerprint で無効化・履歴は書き換えない」。`security.yml` の注記）に従う。

### 採らなかった案

- **`.gitleaks.toml` の `[allowlist]` にパスまたは値形を足す**: 今回は単発で、同型の再発（新しいコミットで同じ値形が出続ける）ではない。
  fixture ディレクトリ全体を許可すると、将来同ディレクトリへ本物が混入しても見逃すため広すぎる。
- **workflow を develop / main だけの走査に絞る**: 閉じた PR のブランチの走査を捨てることになり、検査範囲を狭める変更である。
  本 issue の範囲を越えるため採らない（利用者の判断に委ねる）。
- **残ったブランチを削除する**: 最も素直に解消するが、リモートの破壊的操作であり利用者が判断する。
  削除されても本 fingerprint は何にも一致しなくなるだけで無害である。

## 母集合（規則 9）

- CI の全履歴走査（604 コミット）で finding は **この 1 件だけ**（ログ `leaks found: 1`）。
- 同じ fixture の別行・`rendered.yaml`・develop 上の `f23f2fa3` は finding なし（develop のみの走査 `--log-opts=origin/develop` で `no leaks found`）。
- `gitleaks dir`（作業ツリー走査）では `.github/workflows/helm.yml` の既知のダミー Discord ID が出るが、CI は `detect`（履歴走査）であり、
  当該行は既存の fingerprint（`c255bfeb…:discord-client-id:624`）で管理済み。本件の対象外。

## 受け入れ基準

1. CI と同じ gitleaks 8.24.3 の `detect --redact --exit-code=2`（全ブランチ）が `no leaks found` になる。
2. 追加は fingerprint 1 行のみ（パス・ルール・行・コミットに限定）で、`.gitleaks.toml` の allowlist は広げない。
3. `node scripts/check-commit-messages.js` が通る。

## 検証

- 修正前: 手元の gitleaks 8.24.3 で CI と同じ fingerprint の finding を再現（`leaks found: 1`）。
- 修正後: `gitleaks detect --redact --exit-code=2` → `no leaks found`（exit 0）。
- 手元のクローンは shallow（`git rev-parse --is-shallow-repository` = true）。最終確認は PR の Security（PR 範囲）と、マージ後の push・次回 schedule で行う。
