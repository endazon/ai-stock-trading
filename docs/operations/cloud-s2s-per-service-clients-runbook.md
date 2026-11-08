---
title: 運用 Runbook — クラウドのサービス間の認証をサービスごとの機密クライアントへ移す（適用の順序・確かめ・旧の共有クライアントを外す時期・戻し方）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-08
---
<!-- trace:
ids: [NFR, NFR-05, NFR-06]
adrs: [ADR-0047, ADR-0080]
iadrs: [IADR-0645, IADR-0637, IADR-0626, IADR-0611, IADR-0623, MSP:IADR-0689, MSP:IADR-0700]
specs: [20261108_origin-alerts-replay-store-bot-health, 20261107_s2s-per-service-clients]
issues: []
-->

# 運用 Runbook: サービス間の認証をサービスごとの機密クライアントへ移す

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。** クラウドへの移行の全体は[クラウド移行 Runbook](cloud-single-node-migration-runbook.md)にある。
>
> 🔴 **本書の手順はすべてオーナーが行う。** 配備・クラスタの操作は、AI（コーディングエージェント）は行わない。秘密の値は手元へ出さない。
>
> **状態を `draft` に留めている理由**: クラウドのノードで移行をまだ流していない。

## 何が変わるか

| 項目 | 旧 | 新（`values-cloud.yaml`） |
| --- | --- | --- |
| サービスの身元 | 7 つのサービスが 1 つの機密クライアント `ai-stock-trading-svc` を共有（Secret `ast-secrets`） | サービスごとの機密クライアント `ai-stock-trading-svc-<サービス>`（Secret `ast-service-auth-<サービス>`。ESO が基盤の `cloud/shared/ast-client-secrets` の同じ clientId のプロパティだけを写す） |
| 受け側の宛先（aud） | 見ない | `ai-stock-trading-api` でなければ 401（サービス・所有者のトークンとも） |
| 受け側の呼び手（azp） | サービスのロールなら通す | 口ごとの一覧（既定はイメージの中。構成 `Auth:ServiceCallers` で置き換えられる）に在るクライアントだけ。無ければ 403 |
| 第 2 要素の確かめの口 | どのサービスでも呼べる | 報告書のクライアントだけ |
| 段 | — | `global.serviceTokenBinding.mode=Enforce`、移行の段は `legacyClients: [ai-stock-trading-svc]` |

- 既定（`values.yaml`）と経路 B（`values-local.yaml`）は変わらない（段を描かない＝Off）。
- 7 つのサービス: backtest・cost-control・information-collection・market-monitor・order-execution・report・trade-decision。

## 適用の順序

| 段 | 誰が | 何をするか |
| --- | --- | --- |
| 1 | 基盤 | realm にサービスごとの機密クライアントを足し（稼働中の realm へは基盤の手順の Job）、秘密を OpenBao の中で作る（在る値は上書きしない） |
| **2** | **取引ユニット** | **本書の §段 2**（image と chart。移行の段） |
| **3** | **取引ユニット** | **本書の §段 3**（旧の共有クライアントを外す） |
| 4 | 基盤 | realm から旧の共有クライアントを外す（取引ユニットの段 3 の後） |

### 段 2: サービスごとのクライアントで動かす（移行の段）

前提（基盤の段 1 が済んでいる）:

- `kubectl -n ai-stock-trading get externalsecret` の `ast-service-auth-<サービス>` の 7 つが同期する（chart を当てた後に確かめる。前提は基盤の OpenBao の置き場に 7 つの clientId のキーが在ること）。
- 基盤の Keycloak の realm に 7 つのクライアントが在る（基盤の手順の確かめ）。

1. 新しい image と chart で配備する（digest のファイルは従来どおり）。

   ```sh
   helm upgrade --install ast deploy/helm/ai-stock-trading \
     -f deploy/helm/ai-stock-trading/values-cloud.yaml -f <digest のファイル>
   ```

   - 受け側（認証を持つ 8 サービス: audit・configuration・cost-control・information-collection・market-monitor・order-execution・report・risk-management。
     `helm template` で `Auth__Authority` を持つ Deployment の数。backtest・notification・trade-decision・opend は受け側でない）に `Auth__ServiceToken__Mode=Enforce` と `Auth__ServiceToken__LegacyClients__0=ai-stock-trading-svc` が描かれる。
   - 入れ替えの途中で旧の Pod（共有の資格）が呼んでも、移行の段なので通る（受けた回は「移行の段の共有クライアント」の Warning で残る）。
2. 確かめ:
   - 11 サービスが Ready。`ast-service-auth-<サービス>` が無いと、その Pod は起動しない（`CreateContainerConfigError`）。
   - 受け側のログに「この口を呼んでよいサービスでないため拒否します」が出ない。出たら、ログの `azp` と `口` の組を本書の §戻し方で止めてから一覧を直す。
   - 受け側のログの「移行の段の共有クライアントのトークンを通します」が、入れ替えが済んだ後に出なくなる（旧の Pod が残っていない）。
     同じ回はメトリクス `ast_auth_legacy_service_client_uses_total`（口ごとの `endpoint`）にも数えられ、警報 `AstLegacyServiceClientUsed` が最初の 1 件で鳴る
     （入れ替えの途中の旧の Pod の分で鳴るのは想定どおり。入れ替えが済んだ後に鳴ったら、旧の資格で動いている呼び手を探す）。
   - 取引の判断の 1 巡回・報告書の確定（第 2 要素つき）・Discord の操作が 401 / 403 にならない。

### 段 3: 旧の共有クライアントを外す

**いつ**: 段 2 から**1 日**、受け側のログに「移行の段の共有クライアント」が出ず、`ast_auth_legacy_service_client_uses_total` が増えていないことを確かめた後。**本番の取引（実資金）へ進む前に必ず済ませる。**

🔴 **段 3 までは、第 2 要素の確かめの口を呼べるのが報告書だけにならない。** 移行の段の旧の共有クライアントは、口の名前と一覧を見ずに通る。その資格は 7 つのサービスが
持っていたので、どのサービスの旧い資格でも確かめの口（危険な操作の第 2 要素）を呼べる。段 2 と段 3 の間を長く置かない。目安は上の 1 日である。

1. `values-cloud.yaml` の `global.serviceTokenBinding.legacyClients` を空にし、`externalSecrets.appSecrets` の `service-auth-client-*`（旧の共有クライアントの id と secret）を外す変更を入れ、配備する。
2. 確かめ: 段 2 の確かめと同じ。加えて、旧の共有クライアントのトークンは 401 になる（宛先を持たない）。
3. 基盤の段 4（realm から旧の共有クライアントを外す）へ渡す。

## 稼働中への影響

- 段 2 の配備は、7 つのサービスの Pod を作り直す（新しい Secret を読む）。受け側も作り直す（段の env が増える）。取引ユニットは入れ替えの間だけ照会が遅れ得る（既存の fail-safe の範囲）。
- 一覧の漏れがあると、その口はサービスから 403 になる（閉じる向き。取引の判断は「分からない」として見送る）。

## 戻し方

- **呼び手の拒否が出た（一覧の漏れ）**: 段を Report に下げる（記録だけで通す）。

  ```sh
  helm upgrade ast deploy/helm/ai-stock-trading -f deploy/helm/ai-stock-trading/values-cloud.yaml -f <digest のファイル> \
    --set-string global.serviceTokenBinding.mode=Report
  ```

  一覧を直したイメージを入れてから Enforce へ戻す。急ぐときは env で一覧を足せる（`Auth__ServiceCallers__<口の名前>__<n>=ai-stock-trading-svc-<サービス>`）。
- **サービスごとのクライアントでトークンが取れない（基盤の段 1 が未完）**: 前の版の chart（共有の資格を読む）を当てる。基盤の realm と OpenBao は旧の共有クライアントを残しているので、そのまま動く。
- 段 3 の後に戻すときは、段 3 の変更を戻す（`legacyClients` と `service-auth-client-*` を戻す）。基盤の段 4 の後は、基盤の realm も戻す。
