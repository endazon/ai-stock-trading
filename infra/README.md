# infra/ — ローカル実行の補助アセット（dev 専用）

`docker-compose.yml` が参照する開発用インフラ設定。**すべて dev 専用**であり、本番へは import/流用しない。
本番の資格情報・構成は Vault/Secrets（[ADR-0006](../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md) / #24）で管理する。起動手順は [docs/how-to/local-run.md](../docs/how-to/local-run.md)。

## postgres/init/01-create-databases.sql

`postgres` コンテナ初回起動時に実行され、ADR-0001（Database per Service）に沿って 7 つの専有 DB を作成する。

## keycloak/realm-export.json

`--import-realm` で取り込む dev レルム `ai-stock-trading`。OwnerOnly 認可（ADR-0003 / ADR-0007 / ADR-0008 / IADR-0011）で
参照するレルムロール `trading-owner` と、動作確認用ユーザー `dev-owner` を含む。

クライアントは 3 つ: `ai-stock-trading-dev`（public・利用者ログイン）、`ai-stock-trading-svc`（confidential・
`trading-service`＝s2s read 系・IADR-0051）、`ai-stock-trading-owner`（confidential・service-account に
`trading-owner`＝Discord Bot 制御コマンド `/pause`・`/resume`・`/killswitch`・`/stage` の OwnerAuth・#226 / IADR-0098）。
owner クライアントの dev secret は `dev-only-owner-secret`（`scripts/k8s-local-deploy.sh` の ast-secrets 既定と一致）。

> **⚠️ dev 専用・本番へ import しない。** `dev-owner` のパスワードや client secret はローカル検証用の使い捨て値であり、
> 他の dev ダミー資格情報（`.env.example` の `POSTGRES_PASSWORD` 等）と同じ位置づけ。JSON 直書きなのは
> Keycloak の realm import が静的 JSON を要求するため（環境変数補間に非対応）。実 OwnerOnly 疎通の検証は #82。

> **ローカル反映（realm 再インポート）**: Keycloak の realm import は初回起動のみ有効。owner クライアント追加を
> 反映するには **realm を再インポート**する。docker-compose は Keycloak のボリュームを破棄して再作成
> （`docker compose rm -sfv keycloak` → `docker compose up -d keycloak` で `--import-realm` を再実行）。
> k8s-local（MSP 連結・realm を ConfigMap で配る構成）は **ConfigMap 再作成 → keycloak Pod restart**（`kubectl rollout
> restart`）後、`scripts/k8s-local-deploy.sh` を再実行して ast-secrets（owner-auth dev 既定）を更新する。

## otel/otel-collector-config.yaml

OTLP（gRPC :4317 / HTTP :4318）を受け、dev では debug エクスポータで標準出力するのみ（外部送信なし）。
実バックエンド（Tempo/Loki/Prometheus 等・ADR-0006）連携は #82 以降で追加する。
