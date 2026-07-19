# ArgoCD GitOps 配備（AST チャート）

> 起点: [ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md)（Hetzner・GitOps）/ [IADR-0094](../../docs/adr/IADR-0094_local-infra-observability-gitops.md)（#24）
> 受け入れ基準（Tier 3・本 PR では未充足）: ArgoCD 経由のデプロイが Git の状態と同期し、手動 kubectl 依存がない

Git を単一の真実源とし、ArgoCD が AST の Helm チャート（[`../helm/ai-stock-trading`](../helm/ai-stock-trading/README.md)）を
`ai-stock-trading` Namespace へ宣言的に同期する。**本ディレクトリのマニフェストは opt-in（宣言の骨子）** であり、
既定の経路B 起動（`kubectl apply`/`helm upgrade`）には影響しない。

## 構成

| ファイル | 種別 | 役割 |
| --- | --- | --- |
| `appproject.yaml` | `AppProject` | 許可するソース Git・配備先 Namespace・リソース種別を最小権限で制約 |
| `application.yaml` | `Application` | AST チャートを同期（`prune`/`selfHeal` 有効） |

> **`targetRevision: main` について**: `Application` は**安定版ブランチ `main`** を同期対象にしている（本 PR のベースは
> `develop`）。したがって `develop` へマージされた変更は、`main` へのリリースマージ後に初めて ArgoCD の同期対象になる。
> dev 環境で `develop` を回したい場合のみ、`targetRevision` を一時的に `develop` へ上書きする
> （`argocd app set ai-stock-trading --revision develop`）。

## Tier 境界（重要）

- **本 PR のスコープ**: 宣言マニフェストの**妥当性**（`kubectl apply --dry-run=client` / ArgoCD 描画）まで。
- **Tier 3（対象外・後続）**: Hetzner 実 k3s での**実同期**・実 egress・稼働率99%の実測。詳細は
  [`../../docs/infra/infra.md`](../../docs/infra/infra.md) を参照。
- **ローカル（経路B）で回す前提**: ArgoCD 本体の install（`argocd` Namespace）は **MSP 側の共有 stand-up**
  （別 PR・MSP IADR-0077）で行う。ここでは AST の `Application`/`AppProject` のみを提供する。

## 前提（段階順序）

- `external-secrets.yaml` は既定オフ。ArgoCD で同期する前に、`externalSecrets.enabled=true` にするなら
  **External Secrets Operator（`external-secrets.io` CRD）と Vault ストア**を先に導入しておくこと
  （手順は [`../../docs/operations/vault-secrets-runbook.md`](../../docs/operations/vault-secrets-runbook.md)）。
  CRD 未導入のクラスタで有効化すると `ExternalSecret` の適用が失敗する。

## 1. ArgoCD 導入（ブートストラップのみ kubectl）

ArgoCD 本体は MSP の共有 stand-up が入れる。未導入なら次で導入する（開発時の暫定）:

```sh
kubectl create namespace argocd
kubectl apply -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml
```

## 2. Application 登録（一度だけ kubectl・以降は Git 同期）

```sh
kubectl apply -f deploy/argocd/appproject.yaml
kubectl apply -f deploy/argocd/application.yaml
# 妥当性の事前確認（クラスタ非依存）:
kubectl apply --dry-run=client -f deploy/argocd/
```

以降のデプロイは Git 上の `deploy/helm/ai-stock-trading/values.yaml`（`services.<name>.tag` 等）を更新すると
ArgoCD が同期する。ロールバックは `argocd app rollback ai-stock-trading <revision>` もしくは Git revert。

## 3. 構成バージョンの GitOps 注入（#22 受け入れ基準③）

適用リビジョン（Git SHA）を自己申告 `configVersion` に反映するには、CD 側で供給する:

```sh
argocd app set ai-stock-trading --helm-set global.configVersion=$(git rev-parse HEAD)
```

空既定では自己申告は `null`（fail-safe）。手順は [`../../docs/operations/operations.md`](../../docs/operations/operations.md) を参照。
