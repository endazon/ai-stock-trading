---
title: 公開のファイルから非公開の指摘の識別子を除き、advisory の識別子の再混入を allowlist の検査器で止める
type: spec
status: done
related_ids: [NFR, IADR-0624, IADR-0098, IADR-0314, IADR-0566, IADR-0567, IADR-0568, IADR-0570]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 仕様書: 非公開の識別子の除去（#1566）

> 本仕様書は実装着手前に作成する。本作業はメタ作業（文書・検査器の統制）であり、起点は非機能 `NFR` である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（計画 ADR に影響しない。実装の判断は IADR-0624）
- 起点: #1566。利用者の裁定（2026-11-06）。非機能 `NFR`。

## 目的・背景

- 本リポジトリは公開である。非公開の security advisory の識別子が、IADR・作業仕様書・chart・起動器・CI のコメントとファイル名に残っていた。
- 利用者は 2026-11-06 に、公開の場からこれらを除くと裁定した。依存パッケージの既知脆弱性を指す**公開の第三者 advisory の識別子は残す**。
- 除いた後に再び入らないよう、検査器で止める。

## 対象範囲

- 対象:
  - 追跡下のファイルの本文に出る非公開の識別子（前置き `GHSA-` つきの完全形・短縮形）。
  - 前置きなしの断片（識別子の一部を含むテスト ID）。
  - 識別子を含むファイル名（作業仕様書 4 件）と、その参照。
  - 凍結記録（`.ai-context/` の IADR・作業仕様書）の本文。利用者裁定による凍結の例外である（IADR-0624）。
- 対象外:
  - 公開の第三者 advisory の識別子 12 件（残す）。
  - git の履歴（過去のコミット本文・過去の版・PR の Commits タブ・編集履歴）。force-push・rebase・amend をしない。利用者が残余として受容済み。
  - issue・PR の本文とコメント（別の段で扱った。本 PR の範囲外）。

## 設計

### 1. 置換の規則

| 現在の形 | 置き換え |
| --- | --- |
| 単独の参照（`Refs …` を含む） | 「非公開の指摘（AST）」または「非公開の指摘（MSP）」（識別子の帰属に従う） |
| テスト ID（前置きなしの断片を含む 2 系統） | `T-devsec-NN` と `T-rmqcred-NN`（番号は変えない） |
| 作業仕様書の名前 | `YYYYMMDD_` の直後にある識別子の部分を除く |

- ファイル名の変更は `git mv` で行い、参照の追随と同じコミットに置く。改名後の名前の衝突は無い（実測）。
- 凍結記録は、触った記録ごとに日付つきの追記を 1 行だけ足し、`updated:` を進める。追記には識別子・旧名・置換表を書かない。
- `.ai-context/adr/README.md` の索引行は本文に合わせて置換する。IADR-0314 の行の日付つき追記の印の文言が変わるため、`check-adr-index-addendum-loss.js` には、コミット本文で当該行の撤去を宣言する（旧い文言を書かない形 `IADR-0314 *` を使う）。

### 2. 再混入を止める検査器 `scripts/check-advisory-ids.js`

- 追跡下の全ファイル（`git ls-files`）の本文で、`GHSA-` で始まる識別子を拾う（大文字小文字を区別しない）。
- **allowlist だけの形にする。** 許すのは公開の第三者 advisory 12 件だけ。完全形は allowlist と完全一致、2 区画の形（先頭の 2 区画だけ）は allowlist の完全形の先頭 2 区画と一致するとき、短縮形（先頭 4 文字だけ）は allowlist の先頭の区画と一致するときだけ許す。それ以外は 1 件でも exit 1。
- 非公開の識別子は検査器にも試験にも書かない。ハッシュ照合は入れない（利用者裁定）。
- 前置きなしの断片はこの形では捕まらない（残余。IADR-0624）。
- バイナリ（NUL を含むファイル）は飛ばす。`git ls-files` が失敗したら exit 1（黙って 0 件検査にしない）。
- CI: `ci.yml` の `static-checks` ジョブ（依存ゼロの文書・コードの検査を束ねたジョブ）に 1 ステップ足す。試験は `scripts/scripts.repo.test.js`（`scripts-tests` ジョブが `REQUIRE_REPO_TESTS=1` で走らせる）。`scripts/README.md` の表に足す。

## 母集合（規則 9・10）

- 走査は最新の develop（e262a0adc）で、非公開の識別子の一覧（利用者の手元の非公開の資料）を使い、3 つの形で引いた。結果は件数だけをここに書く。
  - 前置きつき: 52 行・53 箇所・22 ファイル（`.ai-context/adr` 7、`.ai-context/specs` 5、`deploy/helm` 5、`scripts` 2、`.github/workflows` 1、`docs` 2）。
  - 前置きなしの断片: テスト ID 60 箇所（`scripts/k8s-local-deploy.test.sh` と IADR-0098 の参照）。ほかに前置きなしの断片は 0 件。
  - ファイル名: 4 件（作業仕様書）。参照は 6 ファイル・8 箇所。MSP からの参照は無い。
- 除外: ファイル名の走査で 1 件の一致があったが、無関係な語（`Grpc…` の綴りの一部）であり、識別子ではない。
- 公開の第三者 advisory の識別子は develop のツリーに 2 件（`security.yml` のコメント・`Directory.Packages.props` のコメント）。どちらも allowlist に入るので残す。
- 規則 10: 置換後のツリー全体を同じ一覧で引き直し、3 つの形とも 0 件であることを確かめる。改名で切れるリンクは `check-doc-links`・`gen-knowledge-graph --check` で引き直す。

## 受け入れ基準

- [x] 置換後のツリー全体で、非公開の識別子が前置きつき・前置きなしの断片・ファイル名のいずれでも 0 件である。
- [x] 公開の第三者 advisory の識別子は残っている。
- [x] 作業仕様書 4 件の改名と参照の追随が同じコミットにあり、リンク切れが無い（`check-doc-links`・`gen-knowledge-graph --check`）。
- [x] 触った凍結記録ごとに日付つきの追記が 1 行あり、識別子・旧名・置換表を含まない。
- [x] IADR-0624 があり、識別子・旧ファイル名・対応表を含まない。
- [x] `check-advisory-ids.js` が実ツリーで緑、allowlist に無い架空の識別子で赤になる（陰性対照）。allowlist は 12 件ちょうど。
- [x] `static-checks` ジョブで検査器が走る。
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`bash scripts/k8s-local-deploy.test.sh`・文書系の検査器一式が緑。

## テスト方針

- `scripts/scripts.repo.test.js` に次を置く。
  - allowlist が 12 件ちょうどで、すべて GHSA の形であること。
  - allowlist の完全形・2 区画の形・短縮形を許すこと。
  - allowlist に無い架空の識別子（同じ文字の繰り返し。実在しない形が明らかなもの）を完全形・短縮形とも検出すること（陰性対照）。先頭の区画が allowlist と同じで 2 区画目が違う 2 区画の形も検出すること（陰性対照。先頭の区画は公開の allowlist から取る）。架空の識別子は試験の中で組み立て、ファイルの字面には置かない（実ツリーの検査を自分で赤にしない）。
  - 小文字の前置きも検出すること。
  - `GHSA-...` のような識別子でない例示は検出しないこと。
  - 実ツリーで 0 件であること。
- テスト ID の改名は `bash scripts/k8s-local-deploy.test.sh` で確かめる（名前だけの変更で、件数は変わらない）。
- C# のコード・コメント・テスト名には触れない（前置きつき・断片とも `backend/` に 0 件）ため、`dotnet build` / `dotnet test` は回帰の確認として走らせる。

## 計画書との差異

- 差異: なし（計画書に影響しない）。

## 未決事項

- なし。

## 検証（実施記録・2026-11-07）

- 置換後のツリー（コミット済みの HEAD）を非公開の識別子の一覧で引き直した: 前置きつき 0 件・前置きなしの断片 0 件・識別子の後ろの区画 0 件・ファイル名 0 件。部分文字列の緩い走査で残る一致はすべて無関係な語（`Grpc…` の綴りの一部）で、識別子ではない。
- 公開の第三者 advisory の識別子は 12 種がそのまま残る（すべて allowlist にある）。
- `node scripts/check-advisory-ids.js`（OK）、`check-doc-links`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-test-traceability`・`check-adr-index-addendum-loss`（宣言 1 件）・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-commit-messages`・`check-reading-budget`・`check-workflow-job-refs`・`check-action-versions` が緑。
- AI レビューの指摘で、2 区画の形を先頭の区画だけで照合していた穴を塞いだ（陰性対照を追加。旧い照合では赤になることを確かめた）。
- `check-adr-index-sync`: IADR-0567 は本文の識別子を置換したが、索引行には識別子が無く要約も変わらないため、索引行を変えない（本記録のコミットで宣言する）。
- `bash scripts/k8s-local-deploy.test.sh`: 310 passed / 0 failed。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 新設の試験は全件緑。手元では既存の 1 件（過去の索引追随漏れの実コミットでの回帰）が落ちるが、develop の素の作業ツリーでも同じく落ち、develop の CI（`scripts-tests`）では緑である。本作業と無関係（手元の環境差）。
- `dotnet build backend/backend.slnx`（警告 0・エラー 0）と `dotnet test backend/backend.slnx`（22 アセンブリ・失敗 0）。C# は無改修で、回帰の確認として走らせた。
