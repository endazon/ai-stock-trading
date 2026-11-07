---
title: IADR-0630 クラウドの DB の資格をサービスごとに分ける受け手の段。chart は services.<名前>.dbCredentials で DB の Secret をサービスごとに上書きでき（既定は global.db のまま）、values-cloud は DB を持つ 7 つのサービスがそれぞれ自分の Secret を読む。共有の 1 組は基盤の最終の段まで残す
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-05, NFR-18, ADR-0001, ADR-0080, IADR-0608, IADR-0623, IADR-0568, MSP:IADR-0695]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0080_cloud-poc-gate-in-cluster-credentials-and-pre-secret-reachability.md
---

# IADR-0630: クラウドの DB の資格をサービスごとに分ける（取引ユニットの受け手の段）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」）・claude（方式）

## 起点・関連

- 要求 **NFR-05**（秘匿は OpenBao）・**NFR-18**（統制）。計画 ADR-0001（DB per service）・ADR-0080（クラスタの中の資格情報）。
- 基盤の MSP:IADR-0695（サービスごとの DB の利用者。値は `cloud/shared/ast-db-<サービス>`、各利用者が自分の DB だけを OWNER で持ち、他の DB へは CONNECT できない）の段 ②。
- 前提: IADR-0608 決定 3（DB のパスワードは Secret から読み、接続文字列は env の参照で組む）、IADR-0623 決定 2（利用者名も Secret から読む・ESO で写す）。
  ブローカは既にサービスごとのユーザー（IADR-0568）。
- 作業仕様書: `.ai-context/specs/20261107_cloud-db-per-service-users.md`。

## コンテキストと課題

- chart は `global.db.usernameSecret` / `passwordSecret` を、DB を持つ全サービスへ同じ値で描く。values-cloud は 7 つのサービスが同じ Secret
  `ast-db-credentials`（`cloud/shared/ast-db`）を読む。基盤の側でサービスごとの利用者ができても、chart がサービスごとに別の Secret を描けなければ使えない。

## 決定

### 決定 1 — chart に `services.<名前>.dbCredentials.usernameSecret` / `passwordSecret` を足す。与えなければ `global.db` の値

- 与えたサービスだけ、そのサービスの `DB_USERNAME`・`DB_PASSWORD` をその Secret から読む（接続文字列の組み方は IADR-0608 決定 3 のまま）。
- 描画で止める誤り: 片方だけ（利用者名とパスワードを別の資格から組まない）・DB を持たないサービス（`services.<名前>.db` が無い）・2 つが別の Secret
  （ESO は 1 つの置き場から 1 つの Secret へ写す）。どれも Deployment の側（`deployment.yaml`）で止め、ESO が無効でも効く。ExternalSecret の側にも同じ検めを置く。
- 既定（`values.yaml`）とローカル（`values-local.yaml`）は与えない。描画はバイト等価（実測。helm.yml が既定・ローカルにサービスごとの部品が現れないことを固定する）。

### 決定 2 — ExternalSecret は `externalSecrets.serviceDbCredentials`（`enabled`・`remoteKeyPrefix`）で、dbCredentials を持つサービスごとに 1 つ描く

- 同期先は dbCredentials の Secret、置き場は `<remoteKeyPrefix><サービスの名前>`（property は `username`・`password`）。`deletionPolicy` は既存と同じ（values-cloud は Retain）。
- 有効なのに dbCredentials を持つサービスが 1 つも無い・接頭辞が空なら描画で止める（空の key で同期させない）。
- サービスの一覧を values に 2 度書かない（Deployment と ExternalSecret が同じ `services.<名前>.dbCredentials` から描く）。

### 決定 3 — values-cloud は DB を持つ 7 つのサービスに `ast-db-<サービス>-credentials` を与える。共有の 1 組（`ast-db-credentials`）は移行の間だけ残す

- 7 つ（audit・configuration・cost-control・market-monitor・order-execution・report・risk-management）が互いに違う Secret を読み、共有の Secret を読むサービスは無い（helm.yml が固定する）。
- `global.db.passwordSecret` / `usernameSecret` と `externalSecrets.dbCredentials`（共有の 1 組）は、基盤が共有の利用者を消す最終の段（MSP:IADR-0695 決定 4）の後の PR で外す。
  それまでは、DB を持つサービスを足して dbCredentials を書き忘れると共有の資格へ戻る（helm.yml の「DB の資格を読む env が 14」と「共有の Secret を読むサービスが無い」が止める）。
- 採らなかった: `global.db` をサービスごとの表（サービス → Secret）にする — 既存のキーの意味を変え、既定・ローカルの描画に触れる。サービスの側に置けば、既定は何も変わらない。

## 結果

- 良い影響: クラウドで 1 つのサービスの Pod の資格が漏れても、他のサービスの DB に入れない（基盤の側の CONNECT の剥奪と合わせて）。
- 悪い影響: values-cloud に 7 組の宣言が増える。DB を持つサービスを足すときは、基盤（利用者・値・policy）と values-cloud を対で足す。

## 残余

1. 適用の順は基盤の段 ① → この chart → 基盤の段 ③（手順書）。基盤の段 ① の前にこの chart を当てると、7 つの Secret が同期されず新しい Pod は起動しない（旧の Pod が残る。平文や共有の資格へは倒れない）。
2. クラスタでしか確かめられないこと: ESO が 7 つの Secret を書くこと・各サービスが自分の資格で起動しマイグレーションを流せること。
