#!/usr/bin/env bash
# #124 / IADR-0053: moomoo OpenD イメージをローカルビルドし k8s へ供給する。
# tar.gz（~440MB・非コミット）を参照ディレクトリからビルドコンテキストへ一時コピーしてビルドする。
# ランタイム自動判定（Rancher Desktop nerdctl / docker+k3d）。
#
#   scripts/opend-build.sh [tarball-path]
#     tarball-path 既定: OPEND_TARBALL_PATH env、無ければ リポジトリ隣接 ../references の moomoo_OpenD_*.tar.gz
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OPEND_DIR="$ROOT/deploy/opend"
IMAGE="k3d-local/ai-stock-trading/opend:latest"
CLUSTER="${K8S_LOCAL_CLUSTER:-msp-ast-dev}"

# tar.gz の場所を解決。
SRC="${1:-${OPEND_TARBALL_PATH:-}}"
if [ -z "$SRC" ]; then
  # 開発者固有の絶対パスに依存せず、リポジトリ隣接の references/ を既定探索先とする（#150）。
  SRC="$(ls -1 "$ROOT/../references/"moomoo_OpenD_*.tar.gz 2>/dev/null | head -1 || true)"
fi
[ -n "$SRC" ] && [ -f "$SRC" ] || { echo "ERROR: OpenD tar.gz が見つかりません。引数か OPEND_TARBALL_PATH で指定してください。" >&2; exit 1; }
TARBALL="$(basename "$SRC")"

echo "==> コンテキストへ配置: $SRC -> $OPEND_DIR/$TARBALL"
cp -f "$SRC" "$OPEND_DIR/$TARBALL"
# 失敗時も後片付けする（~440MB を残さない）。
trap 'rm -f "$OPEND_DIR/$TARBALL"' EXIT

RUNTIME="${K8S_LOCAL_RUNTIME:-auto}"
if [ "$RUNTIME" = "auto" ]; then
  if command -v nerdctl >/dev/null 2>&1; then RUNTIME="rancher";
  elif command -v k3d >/dev/null 2>&1 && command -v docker >/dev/null 2>&1; then RUNTIME="k3d";
  else echo "ERROR: nerdctl（Rancher Desktop）か docker+k3d が必要です。" >&2; exit 1; fi
fi
echo "==> runtime: $RUNTIME / build $IMAGE (tarball=$TARBALL)"

if [ "$RUNTIME" = "rancher" ]; then
  nerdctl --namespace k8s.io build -t "$IMAGE" --build-arg "OPEND_TARBALL=$TARBALL" "$OPEND_DIR"
else
  docker build -t "$IMAGE" --build-arg "OPEND_TARBALL=$TARBALL" "$OPEND_DIR"
  k3d image import "$IMAGE" -c "$CLUSTER"
fi
echo "done. 次: kubectl -n ai-stock-trading apply -f deploy/opend/k8s/{pvc,opend}.yaml（Secret 作成後）"
