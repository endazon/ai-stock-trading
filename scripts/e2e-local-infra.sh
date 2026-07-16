#!/usr/bin/env bash
# issue #82 / IADR-0049 補足: Docker API が無い環境（例: Rancher Desktop の containerd/nerdctl 構成）で
# 統合 E2E を実走するための実インフラ（実 PostgreSQL / 実 RabbitMQ / 実 Keycloak）を起動・破棄する。
#
# Testcontainers は Docker API（npipe/unix socket）必須のため containerd 系では
# "Failed to connect to Docker endpoint" となる。本スクリプトでコンテナを用意し、
# E2E_* 環境変数でエンドポイントを注入すると、テストは Testcontainers を使わず実インフラへ結線する
# （検証対象が実基盤である点は同じ。E2EInfrastructure.cs 参照）。
#
#   scripts/e2e-local-infra.sh up     # コンテナを作り直して起動し env を出力
#   scripts/e2e-local-infra.sh down   # 破棄
#   scripts/e2e-local-infra.sh env    # env だけ出力（既存インフラの使い回し）
#
#   # 実行例（テストの前に必ず up する）
#   scripts/e2e-local-infra.sh up
#   eval "$(scripts/e2e-local-infra.sh env)"
#   dotnet test backend/Tests/AiStockTrading.IntegrationTests/AiStockTrading.IntegrationTests.csproj
#
# ⚠️ **テスト実行のたびに `up` すること**（up は毎回コンテナを作り直す＝clean slate）。
#    Testcontainers は実行ごとに新しいコンテナを作るため状態が残らないが、外部インフラは**状態が残る**。
#    使い回すと前回の残留メッセージ/データで E2E が落ちる（実測: 使い回しで 3/4・up 後は 4/4）。
#
# ランタイムは nerdctl（Rancher Desktop）優先、無ければ docker。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

PG_PORT="${E2E_PG_PORT:-55432}"
MQ_PORT="${E2E_MQ_PORT:-55672}"
KC_PORT="${E2E_KC_PORT:-58080}"
REALM_NAME="ai-stock-trading"

# 在庫イメージを使う（docker.io の認証ヘルパ不調でも pull を避けられるよう、dev 環境と同じタグを既定にする）。
PG_IMAGE="${E2E_PG_IMAGE:-postgres:16-alpine}"
MQ_IMAGE="${E2E_MQ_IMAGE:-rabbitmq:3.13-management-alpine}"
KC_IMAGE="${E2E_KC_IMAGE:-quay.io/keycloak/keycloak:24.0}"

if command -v nerdctl >/dev/null 2>&1; then
  # Rancher Desktop の containerd。k8s.io namespace の在庫イメージを再利用する。
  RUN="nerdctl --namespace k8s.io"
elif command -v docker >/dev/null 2>&1 && docker version >/dev/null 2>&1; then
  RUN="docker"
else
  echo "ERROR: nerdctl（Rancher Desktop）か 稼働中の docker が必要です。" >&2
  exit 1
fi

emit_env() {
  echo "export E2E_POSTGRES_CONNECTION='Host=localhost;Port=${PG_PORT};Database=e2e;Username=e2e;Password=e2e'"
  echo "export E2E_RABBITMQ_CONNECTION='amqp://guest:guest@localhost:${MQ_PORT}'"
  echo "export E2E_KEYCLOAK_BASEURL='http://localhost:${KC_PORT}'"
}

wait_tcp() {
  for _ in $(seq 1 60); do
    (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null && return 0
    sleep 2
  done
  echo "ERROR: port $1 が開きませんでした（$2）" >&2
  return 1
}

case "${1:-up}" in
  up)
    echo "==> runtime: $RUN"
    $RUN rm -f e2e-postgres e2e-rabbitmq e2e-keycloak >/dev/null 2>&1 || true

    echo "==> PostgreSQL :$PG_PORT"
    $RUN run -d --name e2e-postgres -p "${PG_PORT}:5432" \
      -e POSTGRES_PASSWORD=e2e -e POSTGRES_USER=e2e -e POSTGRES_DB=e2e "$PG_IMAGE" >/dev/null

    echo "==> RabbitMQ :$MQ_PORT"
    $RUN run -d --name e2e-rabbitmq -p "${MQ_PORT}:5672" "$MQ_IMAGE" >/dev/null

    # dev realm（trading-owner/trading-service・direct access grants）を import する。
    echo "==> Keycloak :$KC_PORT（realm import）"
    REALM_FILE="$ROOT/infra/keycloak/realm-export.json"
    [ -f "$REALM_FILE" ] || { echo "ERROR: $REALM_FILE が見つかりません" >&2; exit 1; }
    if command -v cygpath >/dev/null 2>&1; then REALM_FILE="$(cygpath -w "$REALM_FILE")"; fi
    $RUN run -d --name e2e-keycloak -p "${KC_PORT}:8080" \
      -e KEYCLOAK_ADMIN=admin -e KEYCLOAK_ADMIN_PASSWORD=admin \
      -v "${REALM_FILE}:/opt/keycloak/data/import/realm-export.json" \
      "$KC_IMAGE" start-dev --import-realm >/dev/null

    wait_tcp "$PG_PORT" postgres
    wait_tcp "$MQ_PORT" rabbitmq
    echo "==> Keycloak realm の import 完了を待つ"
    for _ in $(seq 1 60); do
      code="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:${KC_PORT}/realms/${REALM_NAME}" || true)"
      [ "$code" = "200" ] && break
      sleep 2
    done
    [ "${code:-}" = "200" ] || { echo "ERROR: Keycloak realm ${REALM_NAME} が 200 になりません" >&2; exit 1; }

    echo "==> ready. 次を eval して dotnet test:"
    emit_env
    ;;
  env)
    emit_env
    ;;
  down)
    $RUN rm -f e2e-postgres e2e-rabbitmq e2e-keycloak >/dev/null 2>&1 || true
    echo "==> removed e2e containers"
    ;;
  *)
    echo "usage: $0 {up|down|env}" >&2
    exit 1
    ;;
esac
