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

# RSA 秘密鍵（$1）を検査する。不在なら 1 を返す（呼び出し側が起動を止める）。
# パーミッションが所有者読取専用でなければ警告する（Secret の defaultMode 誤設定の検知。#132）。
# 読み取り専用マウントのため chmod はできない。是正は k8s 側（defaultMode）で行う。
#
# #274: モードは **symlink を辿って実体**を見る（`stat -Lc`）。k8s の Secret ボリュームは実体を `..data/`
# 配下に置き、可視パスを symlink にする。symlink 自身のパーミッションは常に 777 のため、辿らない
# `stat -c`（lstat）では実体が正しく 0400 でも必ず誤警告になり、本物の誤設定を検知できなくなっていた。
# 直前の `-f`（test は symlink を辿る）を通過した時点で実体は存在するため、`stat -L` が失敗する経路は無い。
require_rsa_key_file() {
	local key_file="$1"
	if [ ! -f "${key_file}" ]; then
		echo "ERROR: OPEND_RSA_KEY_FILE=${key_file} が指定されていますが、ファイルがありません。" >&2
		echo "       Secret moomoo-rsa のマウントを確認してください（cross-network の trade は暗号化必須・#13）。" >&2
		return 1
	fi
	local mode
	# 直前の -f 通過により stat は失敗し得ないが、失敗時の扱いを呼び出し文脈へ依存させない
	# （set -e は `f || exit 1` の文脈では関数内でも抑止され、素の呼び出しでは即座に落ちる）。
	mode="$(stat -Lc '%a' "${key_file}")" || mode="unknown"
	if [ "${mode}" != "400" ] && [ "${mode}" != "440" ]; then
		echo "WARN: ${key_file} のパーミッションが ${mode} です（推奨 400 / 非 root 時 440）。" >&2
		echo "      chart の opend.rsaSecretDefaultMode を確認してください。" >&2
	fi
	return 0
}

# #722: OpenD の標準入力を FIFO 経由にして起動する。
#
# なぜ要るか: OpenD はログイン時の検証コード（SMS / 画像 CAPTCHA）を **PID 1 の標準入力**から読む。
# 素の構成では標準入力が Pod の tty（`/proc/1/fd/0 -> /dev/pts/0`）に直結しているため、
# **`kubectl exec` からは書けない**（exec は別プロセスを起こすだけである）。結果として入力手段は
# `kubectl attach` だけになり、手元に kubeconfig を持つ人しか再認証できない。
# 標準入力を FIFO にすると exec からも書けるようになり、**既に配備済みの Headlamp の Terminal が
# そのまま入力面になる**（認可は apiserver の RBAC がそのまま効く。新しい信頼の基点を作らない）。
#
# ［2026-09-09 追記 / #722 段 2］この FIFO には**サイドカー（opend-auth）も書く**ようになった。
# サイドカーが書けるのは検証コードの 3 コマンドだけである（allowlist。IADR-0320）。
# exec / attach の従来経路は意図的に残してある（サイドカーが落ちても再認証できるようにするため）。
#
# 🔴 3 つの細部が効く。どれを外しても壊れる。
#   1. **作る前に消す。** コンテナ再起動では前回の FIFO が残っており、`set -e` の下で
#      mkfifo が EEXIST になると OpenD が上がらない（CrashLoopBackOff になる）。
#   2. **保持用の書き手プロセスを置かず `0<>`（O_RDWR）で開く。** 書き手がゼロになると OpenD は
#      EOF を見て終了し、再起動 → SMS 再送になる。読み手自身が書き手なら EOF は来ない。
#   3. **fd 1 / 2 は触らない。** `tee` を挟むと C の stdio が行バッファから全バッファへ切り替わり、
#      `Command Tips` が 4KB バッファに埋もれて `kubectl logs` にも attach にも出なくなる。
#
# tty を FIFO へ流す背景プロセス（tty 転送）は **FIFO 直読み経路（start_opend_with_fifo）でだけ**張る。
# 🔴 console 経路（start_opend_with_console）では張らない —— [[IADR-0325]] / #727。理由は tty 転送の直下。
make_stdin_fifo() {
	fifo="$1"
	mkdir -p "$(dirname "$fifo")"
	# 細部 1。存在しないときの unlink 失敗で set -e に落とされないようにする。
	unlink "$fifo" 2>/dev/null || :
	mkfifo -m 600 "$fifo"
}

# コンテナ本来の標準入力（tty）を FIFO へ流す。attach で打った行はここを通る。
# FIFO の open(O_WRONLY) は読み手が現れるまで塞がるので、背景に置いて呼び出し側の exec で解く。
#
# 🔴 背景ジョブの標準入力を `<&0` で渡してはならない。ジョブ制御が無いシェルは背景ジョブの
# 標準入力を **/dev/null へ差し替えてから**リダイレクトを適用するため、`<&0` は
# 差し替え後の /dev/null を複製してしまう（attach で打った行が消える。実測で踏んだ）。先に別の fd へ退避する。
#
# 🔴 **この転送は `script`（console 経路）とは併用できない。** #727 で実測・再現した:
# tty（＝コンテナの標準入力）が **開いたまま塞がっている**とき（＝実 Pod の常態。attach していなくても
# kubelet が stdin を保持する）、この `cat` は生き続け、その状態だと **`script` が FIFO への外部書き込みを
# 子（OpenD）へ転送しなくなる**（検証コードが「入れたのに無反応」になる本症状）。tty を /dev/null にした
# 試験（stdin=/dev/null）では `cat` が即 EOF で消えるため転送が働き、**live だけで壊れて試験が素通り**した。
# したがって console 経路では tty 転送を張らず、入力は**サイドカー（画面）と `kubectl exec … > FIFO`** で行う
# （`0<>` の O_RDWR が EOF を抑えるので、`cat` が無くても OpenD は終了しない）。
start_tty_forwarder() {
	fifo="$1"
	exec 3<&0
	( exec cat > "$fifo" ) <&3 &
	exec 3<&-
}

# FIFO 直読み経路（console 複製を要しないとき）。ここは `script` を挟まないので tty 転送を併用できる
# （attach がそのまま効く）。
prepare_stdin_fifo() {
	fifo="$1"
	make_stdin_fifo "$fifo"
	start_tty_forwarder "$fifo"
}

start_opend_with_fifo() {
	fifo="$1"
	shift
	prepare_stdin_fifo "$fifo"
	# 細部 2。0<> は O_RDWR。
	exec "$@" 0<> "$fifo"
}

# #722 段 2: 標準入力は FIFO のまま、**コンソールの複製**を共有 emptyDir 上のファイルへ落として起動する。
#
# なぜ要るか: 検証コードの入力面を ai-stock-trading の画面にするには、画面側が
# 「いまどのプロンプトが出ているか」を読めなければならない。`kubectl logs` は人が読む面であって
# サイドカーが読む面ではない（コンテナのログは Pod の外＝ノードのログドライバが持つ）。
# 同一 Pod のサイドカーへ渡すには **ファイルとして共有 emptyDir に置く**のがいちばん素直である。
#
# 🔴 `tee` ではなく `script` を使う。 `tee` を挟むと OpenD の stdout がパイプになり、C の stdio が
# 行バッファから**全バッファ**へ切り替わる。`Command Tips` が 4KB のバッファに埋もれ、
# `kubectl logs` にも attach にも出なくなる（段 1 の細部 3 と同じ罠）。`script` は疑似端末（pty）を
# 与えるので、子から見た fd 1 は**依然として tty** であり行バッファのままである。
# 実 OpenD コンテナでの実測で次の 4 点を確認済み（#722 の作業仕様書）:
#   - FIFO からの入力が子プロセスへ届く
#   - コンテナの標準出力にも従来どおり出る（`kubectl logs` が壊れない）
#   - 複製ファイルにも同じ出力が入る
#   - 子から `[ -t 0 ]` が真＝本物の tty に見える
#
# 🔴 **tty 転送（start_tty_forwarder）を張らない。** #727 で、tty が開いたまま塞がる live 常態では
# tty 転送の `cat` が生き続け、その状態で `script` が FIFO への外部書き込みを子へ転送しなくなることを
# 再現した（詳細は start_tty_forwarder の直上）。console 経路の入力面はサイドカー（画面）と
# `kubectl exec … > FIFO` である。`0<>`（O_RDWR）が EOF を抑えるので tty 転送は不要。
#
# 🔴 `-a`（追記）を付ける。 これが**コンソールの上限**（cap_console_log）を成立させている。
# `-a` は複製ファイルを O_APPEND で開くため、外から `: > file` で切り詰めても
# 次の書き込みは**ファイルの現在の末尾＝先頭**へ行く。`-a` が無い（O_APPEND でない）場合、
# `script` は自分が数えているオフセットへ書き続けるので、切り詰めた直後のファイルが
# **NUL で埋まった穴あきファイル**になる（見かけのサイズが減らず、末尾を読むとゴミが混ざる）。
#
# `-e` は子の終了コードをそのまま返す（OpenD が落ちたときに Pod が Running のまま残らないようにする）。
start_opend_with_console() {
	fifo="$1"
	console="$2"
	shift 2
	mkdir -p "$(dirname "$console")"
	# 再起動を跨いで残った前回の複製は捨てる（emptyDir はコンテナ再起動で消えない）。
	: > "$console"
	# 🔴 make_stdin_fifo のみ。tty 転送（start_tty_forwarder）は張らない（#727・上のコメント）。
	make_stdin_fifo "$fifo"
	exec script -q -e -f -a -c "$*" "$console" 0<> "$fifo"
}

# #722 段 2: コンソール複製の上限。OpenD は週単位で常駐するため、放っておくと際限なく積む。
#
# 単発の判定と切り詰めだけを行う（ループは cap_console_log 側）。**分けてあるのは試験のためである**
# ——タイミングに依存せず「閾値を超えたら切り、超えていなければ触らない」を観測できる。
# 切り詰めは 1 回の truncate であり、`script -a`（O_APPEND）の書き込みと競合しても
# 行が壊れることはない（追記は常に現在の末尾へ行く）。
truncate_console_log_if_needed() {
	console="$1"
	max_bytes="$2"
	size="$(stat -c '%s' "$console" 2>/dev/null || echo 0)"
	[ "$size" -gt "$max_bytes" ] || return 1
	: > "$console"
	printf '==> console log truncated at %s bytes (cap=%s)\n' "$size" "$max_bytes" >> "$console"
	return 0
}

cap_console_log() {
	console="$1"
	max_bytes="$2"
	interval="$3"
	while :; do
		sleep "$interval"
		truncate_console_log_if_needed "$console" "$max_bytes" || :
	done
}

# #722 段 2: 画像 CAPTCHA の写しを共有 emptyDir へ置く。
#
# 🔴 **サイドカーに PVC をマウントさせない。** 画像の実体は PVC 上の
# `$HOME/.com.moomoo.OpenD/F3CNN/PicVerifyCode.png` にあり、そこには**デバイス信頼の実体**
# （`Device.dat`）と `OpenD.xml`（ログイン資格情報の MD5）が同居する。サイドカーへ PVC を
# 見せると、画像 1 枚のために口座の信頼状態そのものを晒すことになる。
# **複写するのは OpenD 本体（このコンテナ）の役目**であり、サイドカーは写しだけを読む。
#
# `$HOME` は chart の `opend.home` で可変である（非 root 化すると /home/opend になる）。**/root を焼き付けない。**
opend_captcha_source_path() {
	printf '%s/.com.moomoo.OpenD/F3CNN/PicVerifyCode.png' "${HOME:-/root}"
}

# 変化したときだけ複写する（複写したら 0、しなければ非 0）。
# 変化の判定は **mtime とサイズの両方**で行う —— OpenD は同じ秒内に画像を差し替えることがあり、
# mtime だけでは取りこぼす。
# 差し替えは同一ディレクトリ内の rename（不可分）なので、読み手が**半分書けた PNG** を読むことはない。
copy_captcha_if_changed() {
	src="$1"
	dst="$2"
	[ -f "$src" ] || return 1
	if [ -f "$dst" ] && [ ! "$src" -nt "$dst" ]; then
		src_size="$(stat -c '%s' "$src" 2>/dev/null || echo 0)"
		dst_size="$(stat -c '%s' "$dst" 2>/dev/null || echo -1)"
		[ "$src_size" != "$dst_size" ] || return 1
	fi
	cp "$src" "$dst.tmp" 2>/dev/null || return 1
	# 画像は秘密ではない（これから画面へ出すものである）。サイドカーが別 uid でも読めるようにしておく。
	chmod 0644 "$dst.tmp" 2>/dev/null || :
	mv "$dst.tmp" "$dst" 2>/dev/null || return 1
	return 0
}

watch_captcha() {
	src="$1"
	dst="$2"
	interval="$3"
	while :; do
		copy_captcha_if_changed "$src" "$dst" || :
		sleep "$interval"
	done
}

# deploy/opend/entrypoint.test.sh から関数だけを読み込むための入口（起動手順は実行しない）。
# scripts/k8s-local-deploy.sh の AST_DEPLOY_LIB と同じ idiom（#263 / IADR-0109）。
# コンテナ実行時には設定されない（Dockerfile / chart のいずれにも現れない）。
if [ "${AST_OPEND_LIB:-}" = "1" ]; then
	return 0 2>/dev/null || exit 0
fi

# #132 / IADR-0060 決定 3: 生成物を所有者のみに絞る。OpenD.xml は login_pwd_md5（＝ログイン資格情報）を含む。
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
# #132 / IADR-0060: 鍵が指定されているのに不在なら**起動時に落とす**。従来は黙って非暗号で起動していたが、
# それだと「接続はするが cross-network の trade だけ失敗する」形でしか表面化しない（Secret のマウント漏れ）。
# クライアント側（order-execution）の preflight と対称にする。
RSA_LINE=""
if [ -n "${OPEND_RSA_KEY_FILE:-}" ]; then
	require_rsa_key_file "${OPEND_RSA_KEY_FILE}" || exit 1
	RSA_LINE="<rsa_private_key>${OPEND_RSA_KEY_FILE}</rsa_private_key>"
	echo "==> RSA encryption enabled (key=${OPEND_RSA_KEY_FILE})"
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

# #722: 標準入力は FIFO 経由にする（画面から検証コードを入れられるようにするため）。
# 運用手順は deploy/opend/README.md を参照。`kubectl attach` の従来手順も引き続き使える。
OPEND_STDIN_FIFO="${OPEND_STDIN_FIFO:-/run/opend/stdin}"
# #722 段 2: コンソールの複製と画像 CAPTCHA の写しを、サイドカー（OpendAuthGateway）と共有する
# emptyDir へ置く。サイドカーはこの 3 つ（FIFO / console.log / captcha.png）しか見ない。
OPEND_CONSOLE_LOG="${OPEND_CONSOLE_LOG:-/run/opend/console.log}"
OPEND_CONSOLE_MAX_BYTES="${OPEND_CONSOLE_MAX_BYTES:-1048576}"
OPEND_CONSOLE_ROTATE_INTERVAL="${OPEND_CONSOLE_ROTATE_INTERVAL:-30}"
OPEND_CAPTCHA_SRC="${OPEND_CAPTCHA_SRC:-$(opend_captcha_source_path)}"
OPEND_CAPTCHA_DEST="${OPEND_CAPTCHA_DEST:-/run/opend/captcha.png}"
OPEND_CAPTCHA_POLL_INTERVAL="${OPEND_CAPTCHA_POLL_INTERVAL:-2}"

echo "==> stdin FIFO: ${OPEND_STDIN_FIFO}（exec / サイドカーから検証コードを流し込める）"
echo "==> console duplicate: ${OPEND_CONSOLE_LOG}（上限 ${OPEND_CONSOLE_MAX_BYTES} バイト）"
echo "==> captcha copy: ${OPEND_CAPTCHA_SRC} -> ${OPEND_CAPTCHA_DEST}"

mkdir -p "$(dirname "${OPEND_CONSOLE_LOG}")" "$(dirname "${OPEND_CAPTCHA_DEST}")"

# 背景の 2 本。どちらも**落ちても OpenD を巻き込まない**（複製が止まるだけで、
# `kubectl logs` と `kubectl attach` の従来経路は生きている）。
cap_console_log "${OPEND_CONSOLE_LOG}" "${OPEND_CONSOLE_MAX_BYTES}" "${OPEND_CONSOLE_ROTATE_INTERVAL}" &
watch_captcha "${OPEND_CAPTCHA_SRC}" "${OPEND_CAPTCHA_DEST}" "${OPEND_CAPTCHA_POLL_INTERVAL}" &

# #727: 標準入力の与え方を選べるようにする。**既定は console のまま**（#722 段 2 の画面経路）。
#
# 🔴 なぜ選択肢が要るか —— **`tty` は「実口座でログイン成功」を実際に確認できている唯一の構成**である
# （README の実績。OpenD の標準入力＝コンテナ本来の tty、`kubectl attach` で打つ）。#722 で標準入力を
# FIFO へ、段 2 で `script` の pty へ移したが、**稼働クラスタでは検証コードが OpenD に届かない**ことを
# #727 で実測した（画面・サイドカー・FIFO 直書きのいずれからも無反応。OpenD の 54 スレッドに端末を
# 読んでいるものが 1 つも無い）。原因は未特定であり、**特定できるまで実績構成へ戻せる逃げ道を残す**。
#
#   OPEND_STDIN_MODE=console（既定） … FIFO → script(pty) → OpenD。画面から入れられる（#722 段 2）
#   OPEND_STDIN_MODE=fifo            … FIFO → OpenD 直読み。console 複製は作らない（画面は使えない）
#   OPEND_STDIN_MODE=tty             … コンテナ本来の tty → OpenD。**実績構成**。`kubectl attach` で打つ
#
# `script` が無いイメージでは複製を諦めて fifo へ落とす（OpenD を上げないほうが害が大きい）。
OPEND_STDIN_MODE="${OPEND_STDIN_MODE:-console}"
if [ "$OPEND_STDIN_MODE" = "console" ] && ! command -v script >/dev/null 2>&1; then
	echo "WARN: script(1) が無いためコンソール複製を行いません（画面からの検証コード投入は使えません）。" >&2
	OPEND_STDIN_MODE=fifo
fi
echo "==> stdin mode: ${OPEND_STDIN_MODE}"
case "$OPEND_STDIN_MODE" in
	console) start_opend_with_console "${OPEND_STDIN_FIFO}" "${OPEND_CONSOLE_LOG}" ./OpenD ;;
	fifo)    start_opend_with_fifo "${OPEND_STDIN_FIFO}" ./OpenD ;;
	tty)
		# 実績構成。標準入力を一切すげ替えず、コンテナの tty のまま OpenD へ渡す。
		# 画面（サイドカー）は console 複製が無いので「供給なし」を宣言する＝入力は attach で行う。
		echo "==> 検証コードは kubectl attach -it deploy/opend から入れてください（実績構成）" >&2
		exec ./OpenD
		;;
	*) echo "ERROR: OPEND_STDIN_MODE は console | fifo | tty のいずれかです（受け取った値: ${OPEND_STDIN_MODE}）" >&2; exit 2 ;;
esac
