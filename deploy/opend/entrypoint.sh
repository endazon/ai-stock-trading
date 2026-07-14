#!/usr/bin/env bash
# #124 / IADR-0053: コマンドライン moomoo OpenD をヘッドレス起動する。
# 資格情報は env（k8s Secret）から受け取り、OpenD.xml を生成して ./OpenD を起動する
# （OpenD はカレントの OpenD.xml を読む）。パスワードは MD5（小文字 hex）で扱い、平文は保持しない。
#
# 必須 env（Secret 由来）:
#   OPEND_LOGIN_ACCOUNT   moomoo ログイン account（ユーザーID / 手機号 "+81 90..." / メール）
#   OPEND_LOGIN_PWD_MD5   ログインパスワードの MD5（小文字 hex 32桁）
# 任意 env:
#   OPEND_API_PORT        API ポート（既定 11111）
#   OPEND_LANG            言語（en / chs。既定 en）
#   OPEND_LOG_LEVEL       no/debug/info/warning/error/fatal（既定 info）
#
# ⚠️ SIMULATE 前提・実弾は撃たない（取引環境はクライアント側で TrdEnv.SIMULATE を選ぶ）。
# ⚠️ 新規環境/IP では moomoo のデバイス認証が要求され得る。無人運用の成立性は #124 PoC で検証する
#    （デバイス情報の永続化＝PVC マウントで再起動時の再認証を回避できるか）。
set -euo pipefail

: "${OPEND_LOGIN_ACCOUNT:?OPEND_LOGIN_ACCOUNT（Secret）が未設定です}"
: "${OPEND_LOGIN_PWD_MD5:?OPEND_LOGIN_PWD_MD5（Secret）が未設定です}"
API_PORT="${OPEND_API_PORT:-11111}"
LANG_="${OPEND_LANG:-en}"
LOG_LEVEL="${OPEND_LOG_LEVEL:-info}"

cd /opt/opend

# OpenD.xml を env から生成する（ip=0.0.0.0 でコンテナネットワークから到達可能に）。
cat > /opt/opend/OpenD.xml <<EOF
<moomoo_opend>
	<ip>0.0.0.0</ip>
	<api_port>${API_PORT}</api_port>
	<login_account>${OPEND_LOGIN_ACCOUNT}</login_account>
	<login_pwd_md5>${OPEND_LOGIN_PWD_MD5}</login_pwd_md5>
	<lang>${LANG_}</lang>
	<log_level>${LOG_LEVEL}</log_level>
</moomoo_opend>
EOF

echo "==> starting moomoo OpenD (headless) api_port=${API_PORT} ip=0.0.0.0 (SIMULATE 前提・実弾なし)"
[ -x ./OpenD ] || { echo "ERROR: /opt/opend/OpenD が見つかりません。" >&2; ls -la /opt/opend >&2; exit 1; }
exec ./OpenD
