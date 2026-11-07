---
title: クラウドの DB の資格をサービスごとに分ける（受け手の段。chart と values-cloud）
type: spec
status: done
related_ids: [NFR, NFR-05, NFR-18, ADR-0001, ADR-0080, IADR-0630, IADR-0608, IADR-0623, MSP:IADR-0695]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0080_cloud-poc-gate-in-cluster-credentials-and-pre-secret-reachability.md
---

# 作業仕様書 — クラウドの DB の資格をサービスごとに分ける（受け手の段）

> 着手 2026-11-07（develop `7b237f8fe`）。判断の記録は IADR-0630。基盤の側は MSP:IADR-0695（同名の作業仕様書）。稼働中のクラスタには何もしない。

## 起点

- 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」）: DB の利用者をサービスごとに分け、最小権限にする（ブローカの IADR-0568 の DB 版）。
- 基盤の段 ①（MSP）が `cloud/shared/ast-db-<サービス>`（7 つ）と Postgres の利用者を作る。本件は段 ②（chart・values-cloud）。

## 現状（develop `7b237f8fe` で読んだ）

| 場所 | 現状 |
| --- | --- |
| `templates/deployment.yaml` | `global.db.usernameSecret` / `passwordSecret` を DB を持つ全サービスへ同じ値で描く |
| `values-cloud.yaml` | `global.db` の 2 つとも `ast-db-credentials`。`externalSecrets.dbCredentials` が `shared/ast-db` から写す |
| `templates/external-secrets.yaml` | DB の資格の ExternalSecret は 1 つ（`dbCredentials`） |
| `.github/workflows/helm.yml` | 「DB_PASSWORD が Secret ast-db-credentials から読まれる」を固定している（変える） |

## 母集合（規則 9）

- `ast-db-credentials` / `usernameSecret` / `passwordSecret` / `dbCredentials` / `shared/ast-db` を `git grep` で走査した（`.ai-context/specs` を除く）。
  - 変える: `templates/deployment.yaml`・`templates/external-secrets.yaml`・`values.yaml`（注記と既定の無効）・`values-cloud.yaml`・`helm.yml`（values-cloud の段の 1 行と新しい段）・
    chart の `README.md`・`docs/operations/cloud-single-node-migration-runbook.md`（§5.1・§5.4）。
  - 変えない: 既定・ローカルの `global.db`（ai のまま）・`externalSecrets.dbCredentials`（共有の 1 組。最終の段の後の PR で外す）。
- DB を持つサービス: `values.yaml` の `services.<名前>.db` を持つ 7 つ（基盤の Postgres への入りの 7 つと同じ）。

## 方式

1. chart: `services.<名前>.dbCredentials.{usernameSecret,passwordSecret}`。与えたサービスだけ上書き、与えなければ `global.db`。誤り（片方だけ・DB 無し・別の Secret）は描画で止める。
2. ExternalSecret: `externalSecrets.serviceDbCredentials.{enabled,remoteKeyPrefix}`。dbCredentials を持つサービスごとに 1 つ。
3. values-cloud: 7 つに `ast-db-<サービス>-credentials`、`serviceDbCredentials` は `shared/ast-db-`。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | values-cloud で 7 つのサービスが互いに違う Secret（`ast-db-<サービス>-credentials`）から利用者名・パスワードを読み、共有の Secret を読むサービスが無い | helm.yml「Assert per-service DB credentials (IADR-0630)」・「Assert values-cloud」 |
| AC2 | 各 Secret は ExternalSecret が `cloud/shared/ast-db-<サービス>` から写す（Retain） | 同上 |
| AC3 | 既定・ローカルの描画は変わらない（バイト等価・部品が現れない） | 同上（既定・ローカルの検査）＋手元の比較 |
| AC4 | 誤り（片方だけ・DB を持たないサービス・別の Secret・接頭辞が空）は描画で止まる | 同上（must_fail） |
| AC5 | 修正を外すと AC1 の段が赤になる | 手元の実測（下） |

## 記録

- 既定と values-local の描画を develop と比べてバイト等価（`cmp` で一致）。
- helm.yml の run の段を手元で全部流した（54 段。失敗 0）。
- 赤の確かめ（手元）: (a) values-cloud の report の dbCredentials を外す、(b) deployment.yaml の上書きを外す（global だけを読む）、(c) chart の変更を全部外す —
  どれも「Assert values-cloud」と「Assert per-service DB credentials」の両方が失敗した。
- 実装の途中で見つけたこと: 片方だけ与えたとき、ExternalSecret の側が先に nil を読んで理由の無いエラーになった。ExternalSecret の側にも同じ検めを置いた。
