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
#   OPEND_API_IP          listen アドレス（既定 0.0.0.0＝コンテナ網から到達可能）
#   OPEND_RSA_KEY_FILE    RSA 秘密鍵（PKCS#1 PEM）のパス。設定時は暗号化通信を有効化する（#13）。
#                         指定したのにファイルが無い場合は起動時に停止する（#132・非暗号への暗黙フォールバックを断つ）。
#   OPEND_LANG            言語（en / chs。既定 en）
#   OPEND_LOG_LEVEL       no/debug/info/warning/error/fatal（既定 info）
#
# ⚠️ SIMULATE 前提・実弾は撃たない（取引環境はクライアント側で TrdEnv.SIMULATE を選ぶ）。
# ⚠️ 暗号化（#13 で確定）: moomoo は cross-network（別 Pod 間）の trade 接続に RSA 暗号化を要求する。
#    in-cluster（worker→opend）では OPEND_RSA_KEY_FILE を設定し、クライアントも同一鍵で暗号化接続する。
#    非暗号は OpenD が 127.0.0.1 listen のとき（同一 Pod/loopback）のみ許可される。
set -euo pipefail

# #132 / IADR-0059 決定 3: 生成物を所有者のみに絞る。OpenD.xml は login_pwd_md5（＝ログイン資格情報）を含む。
# 同一ユーザーが読み書きするだけなので挙動は変わらない（純粋なハードニング）。
umask 077

: "${OPEND_LOGIN_ACCOUNT:?OPEND_LOGIN_ACCOUNT（Secret）が未設定です}"
: "${OPEND_LOGIN_PWD_MD5:?OPEND_LOGIN_PWD_MD5（Secret）が未設定です}"
API_PORT="${OPEND_API_PORT:-11111}"
API_IP="${OPEND_API_IP:-0.0.0.0}"
LANG_="${OPEND_LANG:-en}"
LOG_LEVEL="${OPEND_LOG_LEVEL:-info}"

cd /opt/opend

# 暗号化: RSA 秘密鍵ファイルが指定されていれば OpenD.xml に <rsa_private_key> を追加する（#13）。
# #132 / IADR-0059: 鍵が指定されているのに不在なら**起動時に落とす**。従来は黙って非暗号で起動していたが、
# それだと「接続はするが cross-network の trade だけ失敗する」形でしか表面化しない（Secret のマウント漏れ）。
# クライアント側（order-execution）の preflight と対称にする。
RSA_LINE=""
if [ -n "${OPEND_RSA_KEY_FILE:-}" ]; then
	if [ ! -f "${OPEND_RSA_KEY_FILE}" ]; then
		echo "ERROR: OPEND_RSA_KEY_FILE=${OPEND_RSA_KEY_FILE} が指定されていますが、ファイルがありません。" >&2
		echo "       Secret moomoo-rsa のマウントを確認してください（cross-network の trade は暗号化必須・#13）。" >&2
		exit 1
	fi
	RSA_LINE="<rsa_private_key>${OPEND_RSA_KEY_FILE}</rsa_private_key>"
	echo "==> RSA encryption enabled (key=${OPEND_RSA_KEY_FILE})"
	# 秘密鍵が所有者以外に読める状態なら警告する（Secret の defaultMode 誤設定の検知。#132）。
	# 読み取り専用マウントのため chmod はできない。是正は k8s 側（defaultMode）で行う。
	RSA_MODE="$(stat -c '%a' "${OPEND_RSA_KEY_FILE}")"
	if [ "${RSA_MODE}" != "400" ] && [ "${RSA_MODE}" != "440" ]; then
		echo "WARN: ${OPEND_RSA_KEY_FILE} のパーミッションが ${RSA_MODE} です（推奨 400 / 非 root 時 440）。" >&2
		echo "      chart の opend.rsaSecretDefaultMode を確認してください。" >&2
	fi
fi

# OpenD.xml を env から生成する（既定 ip=0.0.0.0 でコンテナネットワークから到達可能に）。
cat > /opt/opend/OpenD.xml <<EOF
<moomoo_opend>
	<ip>${API_IP}</ip>
	<api_port>${API_PORT}</api_port>
	<login_account>${OPEND_LOGIN_ACCOUNT}</login_account>
	<login_pwd_md5>${OPEND_LOGIN_PWD_MD5}</login_pwd_md5>
	<lang>${LANG_}</lang>
	<log_level>${LOG_LEVEL}</log_level>
	${RSA_LINE}
</moomoo_opend>
EOF
# umask 077 で 600 になるが、既存ファイル（再起動・PVC 由来）を上書きした場合に備えて明示する（#132）。
chmod 600 /opt/opend/OpenD.xml

echo "==> starting moomoo OpenD (headless) api_port=${API_PORT} ip=${API_IP} (SIMULATE 前提・実弾なし)"
[ -x ./OpenD ] || { echo "ERROR: /opt/opend/OpenD が見つかりません。" >&2; ls -la /opt/opend >&2; exit 1; }
exec ./OpenD
