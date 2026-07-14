#!/usr/bin/env bash
# #124 / IADR-0053: FutuOpenD ヘッドレス起動。資格情報は env（k8s Secret）から受け取り、
# コマンドライン引数として渡す。パスワードは MD5 で渡す想定（OpenD の login_pwd_md5）。
#
# 必須 env（Secret 由来）:
#   OPEND_LOGIN_ACCOUNT   moomoo ログイン account（電話/UID 等）
#   OPEND_LOGIN_PWD_MD5   ログインパスワードの MD5（小文字 hex）。平文は扱わない。
# 任意 env:
#   OPEND_API_PORT        API ポート（既定 11111）
#   OPEND_LOG_LEVEL       ログレベル（既定 no_output/info 等・版依存）
#
# ⚠️ 版により実行ファイル名/フラグが異なる（要調整）。実バイナリでの検証はあなたの環境で行う。
# ⚠️ 新規環境/IP では moomoo のデバイス認証が要求され得る。無人運用の成立性は #124 PoC で検証する
#    （デバイス情報の永続化＝PVC マウントで再起動時の再認証を回避できるか）。SIMULATE 前提・実弾は撃たない。
set -euo pipefail

: "${OPEND_LOGIN_ACCOUNT:?OPEND_LOGIN_ACCOUNT（Secret）が未設定です}"
: "${OPEND_LOGIN_PWD_MD5:?OPEND_LOGIN_PWD_MD5（Secret）が未設定です}"
API_PORT="${OPEND_API_PORT:-11111}"
LOG_LEVEL="${OPEND_LOG_LEVEL:-no_output}"

# 実行ファイルを探す（版によりファイル名が異なるため候補から解決）。
BIN=""
for candidate in ./FutuOpenD ./FutuOpenD.sh ./OpenD ./moomooOpenD; do
  if [ -x "$candidate" ]; then BIN="$candidate"; break; fi
done
[ -n "$BIN" ] || { echo "ERROR: OpenD 実行ファイルが見つかりません（/opt/opend 配下を確認）。" >&2; ls -la /opt/opend >&2; exit 1; }

echo "==> starting OpenD ($BIN) api_port=${API_PORT} (SIMULATE 前提・実弾なし)"
# ip=0.0.0.0 でコンテナネットワークから到達可能にする。フラグ名は版により要調整。
exec "$BIN" \
  -login_account="${OPEND_LOGIN_ACCOUNT}" \
  -login_pwd_md5="${OPEND_LOGIN_PWD_MD5}" \
  -ip=0.0.0.0 \
  -api_port="${API_PORT}" \
  -log_level="${LOG_LEVEL}"
