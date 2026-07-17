# ローカル実行手順（docker compose） — issue #107 / IADR-0048

ai-stock-trading を単独で起動して疎通を確認するための手順。設計判断は
[IADR-0048](../adr/IADR-0048_runtime-scaffold.md)、作業仕様は
[作業仕様書](../specs/20260712_107_runtime-scaffold.md) を参照。

> **fail-safe 既定**: 既定では実 LLM を呼ばず・実市場データに接続せず・実発注せず・外部送信しない。
> 実接続は `.env` の明示設定時のみ有効化される（#13/#79/#81/#15/#76）。実基盤を起動しての
> 疎通・E2E は #82 で扱う。

## 前提

- Docker / Docker Compose v2（`docker compose version` で確認）
- 単独リポでも submodule 配置（`src/ai-stock-trading/`）でも、`docker-compose.yml` を置いた
  ディレクトリで実行すれば相対パスで解決される。

## 起動

```sh
# 1) 環境変数テンプレートをコピーして編集する（機密は空既定のまま = fail-safe）
cp .env.example .env

# 2a) infra + 10 サービスをまとめて起動（初回はイメージビルドが走る）
docker compose up -d

# 2b) もしくは infra だけ起動（アプリはホストから dotnet run したいとき等）
docker compose up -d postgres rabbitmq keycloak otel-collector
```

`.env` はコミットされない（`.gitignore` 済み）。`docker compose` はコマンドを実行した
ディレクトリの `.env` を自動読込して `${VAR}` を補間する。`.env` を用意しない場合でも、
compose 内の既定値（dev ダミー）で `docker compose config` / 起動は成立する。

## エンドポイント（ホスト公開）

| コンポーネント | URL / ポート | 備考 |
| --- | --- | --- |
| PostgreSQL | `localhost:5432` | user/pass は `.env`（既定 `ai`/`ai`）。init が 7 専有 DB を作成 |
| RabbitMQ 管理 UI | http://localhost:15672 | 既定 `guest`/`guest` |
| Keycloak 管理コンソール | http://localhost:8081 | 既定 admin `admin`/`admin`。realm `ai-stock-trading` を import |
| OTel Collector | （内部）`otel-collector:4317` | dev は debug エクスポータで標準出力 |

AST の 10 サービスはホストへポート公開せず、compose ネットワーク内で相互通信する。個別の
HTTP 疎通（ヘルスチェック `/health/live`・`/health/ready`、同期照会 API）を外から叩く構成は
#82 で必要に応じて追加する。

## 機密情報の設定（ユーザー作業）

実値・実シークレットはコミットしない。用途に応じて次のいずれかに設定する。

- **compose で使う**: `.env` に記入（`.env.example` のキーを埋める）。例:
  - `LLM_GATEWAY_BASEURL`（実 LLM を使う場合 / #11）。**鍵ではなく MSP の LLM ゲートウェイ URL** を入れる。
    LLM プロバイダ鍵は AST では扱わず MSP 側が保持する（ADR-0010 / IADR-0061 決定6）。
  - `COLLECTION_SOURCE_PROVIDER` + `COLLECTION_FINNHUB_API_KEY`（実市場情報 / #81）
  - `NOTIFICATIONS_PROVIDER=discord-webhook` + `NOTIFICATIONS_DISCORD_WEBHOOK_URL`（実通知 / #15）
  - `MOOMOO_API_KEY` / `MOOMOO_API_SECRET`（実発注 / #13。既定は実弾防止ゲートで無効）
- **ホストで `dotnet run` する**: `dotnet user-secrets`（Worker プロジェクトごと）に設定する。例:

  ```sh
  cd backend/Services/TradeDecisionService/src/TradeDecisionService.Worker
  dotnet user-secrets init
  dotnet user-secrets set "Anthropic:ApiKey" "<your-key>"
  ```

本番の DB/MQ/Keycloak・証券会社資格情報は Vault/Secrets（[ADR-0006] / #24）で管理し、本手順の
dev ダミーは用いない。

## 停止 / クリーンアップ

```sh
docker compose down            # コンテナ停止・削除（ボリュームは残す）
docker compose down -v         # DB ボリュームも削除（初期化し直す）
```

## トラブルシュート

- **構成の静的確認**: `docker compose config` で変数解決・構文を検証できる（起動不要）。
- **サービスが DB へ繋がらない**: `postgres` の healthcheck 完了を待って app が起動する。
  ログは `docker compose logs -f <service>`。EF Migration は各 Worker 起動時に適用される。
- **実基盤ありの E2E**: 実 migration 適用・キュー疎通・OwnerOnly 認可の通し検証は #82 で行う。

[ADR-0006]: ../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
