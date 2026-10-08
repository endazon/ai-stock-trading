---
title: AST 専用レルムの写しのずれの検知を基盤側 CI と決めて記録し、レルム不整合による 401 の事故件数を数える（#1221）
type: spec
status: accepted
related_ids: [NFR-06, ADR-0038, ADR-0029, IADR-0324]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0038_linked-deploy-auth-realm-is-the-platform-realm.md (決定 3・フォローアップ 2・4・統制と現在の実現手段)
  - planning:projects/ai-stock-trading/07_adr/ADR-0029_impl-docs-restructure.md (決定 2: planning / 他リポジトリへの依存を持たない)
---

# 仕様書: レルムの写しのずれの検知の置き場所と、レルム不整合 401 の件数（#1221）

## 起点となる計画書（トレーサビリティ）

- 起点: NFR-06 / 計画 ADR-0038 フォローアップ 2（決定 3 の写しのずれを検知する手段）・フォローアップ 4（効果測定）
- 関連: IADR-0324（2026-09-11 追記 / #776 の 3.「突合の受け皿は基盤側」）・#776・#1204（第 4 回全体監査 B-18）
- 起票: #1221

## 目的・背景

IADR-0324 の #776 追記は「写しのずれを検知する手段は無い。突合の受け皿は基盤側」と書いたまま、
受け皿の所在を AST 側に記録していなかった。#1221 はその置き場所の決定と、401 事故件数の計数を求める。

## 調査（実測）

### 1. 基盤側に検知は既に在る

- MSP#1412（closed 2026-09-11）・MSP#1415 が `scripts/check-realm-copy-drift.js` と MSP の IADR-0434 を入れた。
  客体は接頭辞 `trading-`（realm ロール）／`ai-stock-trading-`（クライアント）で導出した**和集合**、
  比較はロールの存在・`composite`・`composites`・`attributes`・`description` の有無、クライアントの存在・4 フラグ・
  **service account の realm ロール付与**。片側だけに在る客体は理由つき宣言（宣言外は赤）。
  → 受け入れ基準 1 の「ロール名・付与先のクライアント・service account のロール」を覆う。
- 実走点は MSP `ci.yml` の `static-checks-units`（`src/*` submodule を取得するジョブ）。`static-checks` の同名ステップは
  submodule 未取得で skip 警告を出す設計（IADR-0434 決定 5）。トリガは `pull_request` と develop / main への push。
- MSP は AST を submodule `src/ai-stock-trading`（`.gitmodules`、`branch = develop`）として持つ。調査時の pin は
  `58fe8c24`（MSP develop `0172d9b7`）。**AST から MSP は読めないが、MSP から AST は読める** —— 両辺が揃うのは基盤側だけ。
- 手元実走（MSP の検査器・正本を scratch へ写し、AST develop `dec20e68` の `infra/keycloak/realm-export.json` を
  `src/ai-stock-trading/infra/keycloak/` に置いて実行。MSP の作業木は変更していない）:
  `OK: 正本と写しに差分はありません（突合: realm ロール 2 件 / クライアント 6 件、片側宣言 4 件）`・exit 0、
  `--self-test` 32 件 OK。AST develop の写しは pin `58fe8c24` の写しとバイト一致。
- AST 側の変更が検知に届く経路: MSP の submodule pin 更新 PR（MSP `static-checks-units` が走る）。
  2026-09-28〜10-05 に pin 更新 PR が 14 本（MSP#1698〜MSP#1744）あり、間隔は 0〜2 日。

### 2. 401 の事故件数

- 定義: **発行元レルムと検証側レルムの不一致（issuer 不一致）を原因とする 401** を 1 件と数える。
  Keycloak 未起動・再起動中のトークン取得失敗、および仕様どおりの拒否（無効化利用者など）は数えない。
- 期間: ADR-0038 の作成日 2026-09-11 〜 2026-10-08。
- 母集合: GitHub REST の issue 一覧（`state=all&since=2026-09-11`。PR を含む）を作成日 ≥ 2026-09-11 に絞った
  AST 512 件・MSP 423 件。抽出条件はタイトルに `401`、または本文に `issuer…invalid|不一致`・`invalid_token`・
  `レルム不整合` を含むこと。
- ヒット: #1134（全 Pod 同時再起動直後のトークン取得失敗）・#840（Keycloak Pod 起動中の一過性の未起動と本文が特定）・
  MSP#1399（Keycloak の OOMKilled による再起動）・MSP#1579（無効化利用者の 401。仕様どおり）・MSP#1415（検査器の導入）・
  #1221（本件）。**いずれもレルム不整合ではない → 0 件**（基準値 2 件＝#456 CronJob・#736 s2s 発信者は期間前）。
- 自動計数の手段: 無い。リポジトリ内に issuer 不一致の 401 を他の 401 と分けて数えるメトリクス・アラートは無い
  （`infra/` `deploy/` の yaml を `issuer` で走査してアラート・式のヒットなし）。暫定手段は**棚卸し時の上記 issue 走査**（手作業）。
- 0 件のため planning への環流は不要（受け入れ基準 3）。

- #1204 の台帳（`.ai-context/specs/20261007_1204_residual-ledger.md` 行 4）と #1221 本文は「基盤側にも AST 側にも
  追跡が無い」と書いたが、**基盤側の追跡（MSP#1412）は 2026-09-11 に起票・close 済みだった**。AST 側から参照されて
  いなかったのが実態である。台帳は凍結記録なので書き換えず、本仕様書と IADR-0324 の追記で正す。

## 決定（IADR-0324 への日付つき追記）

- 検知の置き場所は **基盤側の CI**（既設の MSP#1412 / MSP IADR-0434）。AST 側に検査器・定期ジョブは置かない。
- 新しい MSP issue は起票しない（同件が MSP#1412 で起票・実装・close 済み。重複起票になる）。#1221 には MSP#1412 を記す。
- 却下: AST 側の定期ジョブ（MSP を取得する＝ADR-0029 決定 2・受け入れ基準 4 に反する）、
  手順書の点検項目（機械で検知されない。既に機械の検知が在る）、AST 側に正本の写しの写しを置く（3 つ目の正本になる）。

## 変更

- `.ai-context/adr/IADR-0324_msp-linked-deploy-single-auth-realm.md`: ［2026-10-08 追記 / #1221］・`updated`。
- `.ai-context/adr/README.md`: IADR-0324 の索引行に追記ブロックを足す（原文は残す）。
- `infra/README.md`: 「突合の受け皿は基盤側」に実在の検査器と MSP#1412 を書き足す。
- `infra/keycloak/realm-export.json`: 読むだけ（`_sourceOfTruth` の「platform side が持つ」は事実と一致）。

## 受け入れ基準と検証

1. 置き場所が IADR-0324 の日付つき追記で決まり、基盤側の番号（MSP#1412）が #1221 に書かれる。
2. （検知を置く判断のため非該当。）
3. 件数（0 件）と数え方・出典 issue が IADR-0324 の追記に残る。
4. AST の CI へ MSP の取得を足していない（差分に `.github/` `scripts/` の変更が無い）。
- 検証: `node scripts/scripts.test.js`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-adr-index-sync`・
  `check-adr-index-addendum-loss`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-commit-messages`・
  `check-test-traceability`。C# は触らない。

## 母集合（規則 9・10）

- 誤りの側の文字列「写しのずれを検知する手段は無い」「突合の受け皿は基盤側」「Drift detection is owned」で
  `git grep` → IADR-0324（#776 追記）・`.ai-context/adr/README.md`（IADR-0324 行）・`infra/README.md`・
  `infra/keycloak/realm-export.json`。凍結記録（IADR-0324 本文・索引行の原文・過去の作業仕様書）は書き換えず追記で足す。
  realm-export.json は記述が事実と一致し、かつ varchar(255) 制約（#788）があるため変えない。

## 残余リスク

- AST 側だけで写しを変えた場合、赤になるのは AST の PR ではなく次の MSP pin 更新 PR である（検知の遅れ＝pin 更新の間隔）。
- 散文（`description` 本文）の陳腐化・`secret` の差は検知しない（MSP IADR-0434 決定 2 の設計どおり）。
- 401 の計数は手作業の issue 走査であり、issue にならなかった事故は数えられない。
