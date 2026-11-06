---
title: IADR-0624 公開のファイルから非公開の指摘の識別子を除き（凍結記録も本文を置換する例外）、advisory の識別子は公開の第三者の allowlist だけを許す検査器で再混入を止める
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0098, IADR-0314, IADR-0566, IADR-0567, IADR-0568, IADR-0570]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# IADR-0624: 非公開の指摘の識別子の除去と、advisory の識別子の allowlist 検査

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-07
- 決定者: 利用者裁定（2026-11-06）／ Claude Code（起案・実装）

## 起点・関連

- issue: #1566。非機能 `NFR`（メタ作業。計画の非機能要件表に当たる番号は無い）。
- 実装仕様書: [`.ai-context/specs/20261107_1566_private-advisory-id-scrub.md`](../specs/20261107_1566_private-advisory-id-scrub.md)

## コンテキストと課題

- 本リポジトリは公開である。非公開の security advisory の識別子が、IADR・作業仕様書・chart・起動器・CI のコメント、テスト ID、作業仕様書のファイル名に残っていた。
- 非公開の advisory の識別子を公開の場に置くと、未公開の指摘の存在と対象を外部へ示すことになる。
- 一方、依存パッケージの既知脆弱性を指す**公開の第三者 advisory** の識別子は、根拠の記録として残す価値がある。
- `.ai-context/` は凍結記録であり、本文を後から書き換えない規律がある。

## 検討した選択肢

1. **凍結記録も含めて本文を中立表記へ置換し、記録ごとに日付つきの追記を 1 行だけ足す（採用）** — 公開のツリーから識別子が消える。凍結を破った事実は追記と本 IADR に残る。
2. 凍結記録は残し、生きた文書だけを置換する — 公開のツリーに識別子が残り、裁定の目的を果たさない。採らない。
3. 履歴まで書き換える（force-push） — 本リポジトリは破壊的な git 操作をしない。過去の版に残るものは利用者が残余として受容した。採らない。

再混入の検査器の形:

- **(a) allowlist だけ（採用）** — `GHSA-` で始まる識別子は公開の第三者の allowlist にあるものだけを許す。検査器は非公開の識別子を持たない。
- (b) 非公開の識別子のハッシュを照合する — 前置きなしの断片も捕まえられるが、検査器に識別子の派生物を置くことになる。利用者裁定で入れない。

## 決定

1. **凍結の例外の射程。** 利用者裁定により、`.ai-context/` の IADR と作業仕様書も、**識別子の置換だけ**を目的に本文を書き換える。**決定の内容は変えない。** 触った記録ごとに日付つきの追記を 1 行だけ足し、`updated:` を進める。追記・本 IADR・コミット・PR には、識別子・旧ファイル名・旧→新の対応表を書かない。
2. **置換の規則。** 単独の参照は帰属に従って「非公開の指摘（AST）」または「非公開の指摘（MSP）」。テスト ID は `T-devsec-NN` と `T-rmqcred-NN`（番号は変えない）。作業仕様書の名前は `YYYYMMDD_` の直後の識別子の部分を除く（`git mv` と参照の追随を同じコミットに置く）。公開の第三者 advisory の識別子は残す。
3. **触ったファイル**（新しい名前で書く）:
   - IADR: IADR-0098・IADR-0314・IADR-0566・IADR-0567・IADR-0568・IADR-0570、索引 `.ai-context/adr/README.md`。
   - 作業仕様書: `20261021_order-dispatch-final-halt-check`・`20261022_dev-client-secrets-fail-closed`・`20261022_opend-ingress-and-caller-secret`・`20261022_rabbitmq-per-service-credentials`・`20261023_mesh-injection-handover`。
   - chart: `deploy/helm/ai-stock-trading/` の `README.md`・`values.yaml`・`templates/deployment.yaml`・`templates/external-secrets.yaml`・`templates/opend.yaml`。
   - 起動器と試験: `scripts/k8s-local-deploy.sh`・`scripts/k8s-local-deploy.test.sh`。
   - CI: `.github/workflows/helm.yml`。
   - 文書の frontmatter: `docs/infra/infra.md`・`docs/operations/operations.md`。
4. **再混入を止める検査器 `scripts/check-advisory-ids.js`。** 追跡下の全ファイルの本文で `GHSA-` で始まる識別子を拾い（大文字小文字を区別しない）、公開の第三者 advisory 12 件の allowlist にあるものだけを許す。完全形は allowlist と完全一致、短縮形（先頭の区画だけ）は allowlist の先頭の区画と一致するときだけ許す。1 件でも外れれば exit 1。`git ls-files` が失敗したら exit 1（黙って 0 件検査にしない）。CI は `ci.yml` の `static-checks` ジョブ、試験は `scripts/scripts.repo.test.js`（陰性対照つき）。
5. **allowlist に足す規則。** 公開済みの第三者 advisory（依存パッケージの既知脆弱性）を新たに引くときだけ、理由を添えて足す。自リポジトリ・基盤の非公開の指摘は足さない（中立表記で書く）。

## 理由

- 公開のツリーから識別子を除くことが裁定の目的であり、凍結記録だけを残すと目的を果たさない。
- allowlist の形は、検査器そのものが漏えいにならない。新しい非公開の指摘が増えても検査器を変えずに止められる。

## 結果

- 良い影響: 公開のツリーに非公開の識別子が 0 件になる。新たに前置きつきで書かれれば CI が止める。
- 悪い影響・トレードオフ:
  - **前置きなしの断片（識別子の一部を含むテスト ID・ファイル名など）は検査器で捕まらない**（残余。書き手とレビューで守る）。
  - 過去のコミット本文・過去の版・PR の Commits タブ・本文の編集履歴には残る（利用者が残余として受容済み）。
  - 凍結記録の本文が裁定の時点と字面で食い違う。日付つきの追記で読み分ける。
- フォローアップ: なし。

## 関連

- Supersedes: なし
- Superseded by: なし
