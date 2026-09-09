#!/usr/bin/env bash
# #274 / IADR-0060 決定 3: deploy/opend/entrypoint.sh の RSA 秘密鍵検査（require_rsa_key_file）の挙動を固定する。
#
#   bash deploy/opend/entrypoint.test.sh
#
# 実コンテナ・実 OpenD・追加パッケージは要らない。対象スクリプトは AST_OPEND_LIB=1 で source すると
# 関数定義だけを読み込み、起動手順（OpenD.xml 生成 / exec ./OpenD）は実行しない。
#
# 検証する不変条件（Issue #274 の受け入れ基準）:
#   - k8s Secret ボリュームと同じ構成（実体は ..data/ 配下・可視パスは symlink）で、実体が 0400/0440 なら
#     警告を出さない（symlink 自身の 777 を見て誤警告しない＝本 Issue の回帰テスト）
#   - 実体のパーミッションが誤っていれば従来どおり警告し、実体のモードを表示する
#   - 鍵ファイル不在なら起動を止める（非ゼロ終了）
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ---- 対象の読み込み（関数のみ） -------------------------------------------
AST_OPEND_LIB=1
export AST_OPEND_LIB
# shellcheck source=./entrypoint.sh
. "$HERE/entrypoint.sh"
set +e +o pipefail   # 対象が有効化した set -e を戻し、失敗ケースを観測できるようにする

# ---- テストハーネス -------------------------------------------------------
PASSED=0
FAILED=0
SKIPPED=0
ok() { PASSED=$((PASSED + 1)); printf '  ok    %s\n' "$1"; }
ng() { FAILED=$((FAILED + 1)); printf '  NG    %s\n        %s\n' "$1" "$2"; }
skip() { SKIPPED=$((SKIPPED + 1)); printf '  skip  %s\n        %s\n' "$1" "$2"; }
assert_contains() { case "$2" in *"$3"*) ok "$1" ;; *) ng "$1" "expected to contain: $3" ;; esac; }
assert_missing() { case "$2" in *"$3"*) ng "$1" "expected NOT to contain: $3" ;; *) ok "$1" ;; esac; }
assert_eq() { [ "$2" = "$3" ] && ok "$1" || ng "$1" "expected [$3] but was [$2]"; }

# require_rsa_key_file をサブシェルで実行し、RC / OUT / ERR を埋める。
run_check() {
  ( require_rsa_key_file "$1" ) > "$WORK/out" 2> "$WORK/err"
  RC=$?
  OUT="$(cat "$WORK/out")"
  ERR="$(cat "$WORK/err")"
}

# 秘密鍵の中身は検査対象ではない（本物の PEM は置かない・秘密情報を一切含めない）。
write_dummy_key() { printf 'dummy-key-material-not-a-real-key\n' > "$1"; }

# k8s の Secret ボリュームと同じ構成を作り、可視パス（symlink）を返す。
#   <dir>/..2026_07_29/opend_rsa.pem  … 実体（モードは $1）
#   <dir>/..data      -> ..2026_07_29 … symlink（k8s が atomic に差し替える層）
#   <dir>/opend_rsa.pem -> ..data/opend_rsa.pem … symlink（Pod から見える可視パス・常に 777）
given_k8s_secret_mount() {
  local mode="$1" dir="$WORK/rsa"
  rm -rf "$dir"
  mkdir -p "$dir/..2026_07_29"
  write_dummy_key "$dir/..2026_07_29/opend_rsa.pem"
  chmod "$mode" "$dir/..2026_07_29/opend_rsa.pem"
  ( cd "$dir" && ln -s "..2026_07_29" "..data" && ln -s "..data/opend_rsa.pem" "opend_rsa.pem" )
  printf '%s' "$dir/opend_rsa.pem"
}

# symlink を介さない実ファイル（従来経路・docker run -v 直マウント相当）。
given_plain_file() {
  local mode="$1" path="$WORK/plain_rsa.pem"
  write_dummy_key "$path"
  chmod "$mode" "$path"
  printf '%s' "$path"
}

# ---- 実行環境の能力チェック -----------------------------------------------
# Windows（Git Bash）等では symlink を作れない／chmod が反映されないため、依存するケースは skip する
# （実行環境依存で偽陰性・偽陽性を出さない）。CI は ubuntu-latest なので本体は必ず実行される。
MODE_OK=1
printf 'probe\n' > "$WORK/probe"
chmod 400 "$WORK/probe"
[ "$(stat -Lc '%a' "$WORK/probe" 2>/dev/null)" = "400" ] || MODE_OK=0

SYMLINK_OK=1
( cd "$WORK" && ln -s "probe" "probe.link" ) 2>/dev/null || SYMLINK_OK=0
[ -L "$WORK/probe.link" ] || SYMLINK_OK=0

printf 'entrypoint.sh: RSA 秘密鍵の検査（#274 / IADR-0060 決定3）\n'

# ---- T-274-01/02/03: k8s Secret ボリューム（symlink 越し） -----------------
if [ "$MODE_OK" = "1" ] && [ "$SYMLINK_OK" = "1" ]; then
  # T-274-01: 実体 0400（chart の既定 rsaSecretDefaultMode: 0400）→ 警告なし
  KEY="$(given_k8s_secret_mount 400)"
  run_check "$KEY"
  assert_eq       'T-274-01 symlink/0400: 正常終了する' "$RC" "0"
  assert_missing  'T-274-01 symlink/0400: 警告を出さない' "$ERR" 'WARN'
  assert_missing  'T-274-01 symlink/0400: symlink 自身の 777 を読まない' "$ERR" '777'

  # T-274-02: 実体 0440（非 root 実行時の fsGroup 併用）→ 警告なし
  KEY="$(given_k8s_secret_mount 440)"
  run_check "$KEY"
  assert_eq       'T-274-02 symlink/0440: 正常終了する' "$RC" "0"
  assert_missing  'T-274-02 symlink/0440: 警告を出さない' "$ERR" 'WARN'

  # T-274-03: 実体 0644（defaultMode 誤設定）→ 実体のモードを添えて警告する
  KEY="$(given_k8s_secret_mount 644)"
  run_check "$KEY"
  assert_eq       'T-274-03 symlink/0644: 起動は止めない（警告のみ）' "$RC" "0"
  assert_contains 'T-274-03 symlink/0644: 警告を出す' "$ERR" 'WARN'
  assert_contains 'T-274-03 symlink/0644: 実体のモードを表示する' "$ERR" '644'
  assert_contains 'T-274-07 警告に是正先を示す' "$ERR" 'opend.rsaSecretDefaultMode'
else
  skip 'T-274-01/02/03/07 symlink 越しの検査' \
    "この環境では symlink 作成またはモード設定が反映されない（symlink=$SYMLINK_OK mode=$MODE_OK）"
fi

# ---- T-274-04/05: symlink を介さない実ファイル（既存挙動の据置） -----------
if [ "$MODE_OK" = "1" ]; then
  # T-274-04: 0400 → 警告なし
  KEY="$(given_plain_file 400)"
  run_check "$KEY"
  assert_eq      'T-274-04 実ファイル/0400: 正常終了する' "$RC" "0"
  assert_missing 'T-274-04 実ファイル/0400: 警告を出さない' "$ERR" 'WARN'

  # T-274-05: 0644 → 警告する
  KEY="$(given_plain_file 644)"
  run_check "$KEY"
  assert_eq       'T-274-05 実ファイル/0644: 起動は止めない（警告のみ）' "$RC" "0"
  assert_contains 'T-274-05 実ファイル/0644: 警告を出す' "$ERR" 'WARN'
  assert_contains 'T-274-05 実ファイル/0644: モードを表示する' "$ERR" '644'
else
  skip 'T-274-04/05 実ファイルの検査' "この環境では chmod が反映されない"
fi

# ---- T-274-06: 鍵ファイル不在（Secret のマウント漏れ）→ 起動を止める -------
run_check "$WORK/does-not-exist.pem"
assert_eq       'T-274-06 不在: 非ゼロ終了する（起動を止める）' "$RC" "1"
assert_contains 'T-274-06 不在: ERROR にパスを示す' "$ERR" 'does-not-exist.pem'
assert_contains 'T-274-06 不在: Secret のマウント確認を促す' "$ERR" 'moomoo-rsa'

# ---- T-722: 標準入力の FIFO 経路（#722） -----------------------------------
# 実 OpenD は要らない。`start_opend_with_fifo` の最後は `exec "$@"` なので、OpenD の代わりに
# `cat` を渡せば「標準入力へ届いたもの」がそのまま標準出力に出る＝届いたかどうかを観測できる。

# FIFO への書き込みは読み手が居なければ塞がる。**試験が固まらないよう必ず時間を切る。**
#
# 読み手（起動中の child）が FIFO を開くまでの間は塞がるので、数回やり直す。
# **1 回で諦めると、FIFO が既に在る場合（＝再起動を模したケース）に競走で落ちる** ——
# `wait_for_fifo` は「在ること」しか見られず、「読み手が開いたこと」は見られない。
write_line() {
  local i=0
  while [ "$i" -lt 4 ]; do
    timeout 3 bash -c 'printf "%s\n" "$2" > "$1"' _ "$1" "$2" && return 0
    i=$((i + 1))
  done
  return 1
}

# FIFO が現れるまで待つ（起動は背景で走るため）。
wait_for_fifo() {
  local f="$1" i=0
  while [ "$i" -lt 50 ]; do
    [ -p "$f" ] && return 0
    sleep 0.1
    i=$((i + 1))
  done
  return 1
}

# 背景で起動し、child の PID を CHILD へ、標準出力を $2 へ入れる。$3 は child の標準入力にするファイル。
start_fake_opend() {
  local fifo="$1" out="$2" stdin_src="$3"
  : > "$out"
  ( start_opend_with_fifo "$fifo" cat > "$out" 2>&1 ) < "$stdin_src" &
  CHILD=$!
  # 片付けの kill -9 でシェルが「Killed」を出すのを抑える（試験の出力を読みにくくするだけのため）。
  disown "$CHILD" 2>/dev/null || :
}

stop_fake_opend() {
  # child は 0<> で開いているので EOF では終わらない。必ず落とす（wait では止まらない）。
  kill -9 "$CHILD" 2>/dev/null || :
  CHILD=""
}

# この環境で **この機序そのもの**が成立するかを測る。
#
# 🔴 `mkfifo` の成否では足りず、読み書きの往復でも足りない。Git Bash（MSYS）は mkfifo にも
# 素朴な往復にも成功しながら、**`0<>`（O_RDWR）で開いた FIFO 経由では届かない**（実測）。
# したがって探針は**本番と同じ関数を同じ形で 1 回走らせて**判定する。
# 実機（Linux コンテナ）での実測は #722 の作業仕様書に残してある。
fifo_mechanism_works() {
  command -v timeout >/dev/null 2>&1 || return 1
  # 既存の試験が $WORK/probe を**通常ファイル**として使っている。名前を分ける（衝突すると mkdir が失敗する）。
  local probe="$WORK/fifo-probe/stdin"
  start_fake_opend "$probe" "$WORK/fifo-probe.out" /dev/null
  local ok=1
  if wait_for_fifo "$probe" && write_line "$probe" 'ping'; then
    sleep 0.5
    grep -q 'ping' "$WORK/fifo-probe.out" || ok=0
    # **`0<>` が効いているかまで見る。** 書き手がゼロになった後も読み手が生きていること。
    # MSYS の FIFO は往復だけなら通ることがあるが、ここは通らない（実測。ここを見ないと群が不安定に緑/赤へ振れる）。
    kill -0 "$CHILD" 2>/dev/null || ok=0
  else
    ok=0
  fi
  stop_fake_opend
  [ "$ok" = "1" ]
}

FIFO_OK=0
fifo_mechanism_works && FIFO_OK=1

if [ "$FIFO_OK" = "1" ]; then
  # T-722-01: FIFO へ書いた 1 行が OpenD の標準入力へ届く（＝exec から検証コードを入れられる）
  F1="$WORK/run1/stdin"
  start_fake_opend "$F1" "$WORK/out1" /dev/null
  if wait_for_fifo "$F1"; then
    write_line "$F1" 'input_phone_verify_code -code=123456'
    sleep 0.5
    assert_contains 'T-722-01 FIFO へ書いた行が標準入力へ届く' "$(cat "$WORK/out1")" 'input_phone_verify_code -code=123456'
  else
    ng 'T-722-01 FIFO へ書いた行が標準入力へ届く' 'FIFO が作られなかった'
  fi

  # T-722-02: 書き手がゼロになっても child は終わらない（0<> ＝ O_RDWR。EOF を見ない）
  # 上の書き込みで開いた fd は既に閉じており、tty 側の copier も /dev/null で EOF 済みである。
  # それでも child が生きていること＝保持用プロセス無しで EOF を防げていること。
  if kill -0 "$CHILD" 2>/dev/null; then
    ok 'T-722-02 書き手がゼロでも終了しない（EOF を見ない）'
  else
    ng 'T-722-02 書き手がゼロでも終了しない（EOF を見ない）' 'child が終了していた（再起動 → SMS 再送につながる）'
  fi
  stop_fake_opend

  # T-722-03: tty（コンテナ本来の標準入力）から打った行も同じ標準入力へ届く
  #           ＝ `kubectl attach` の既存手順を壊していない
  printf 'input_pic_verify_code -code=ab12\n' > "$WORK/tty-input"
  F3="$WORK/run3/stdin"
  start_fake_opend "$F3" "$WORK/out3" "$WORK/tty-input"
  if wait_for_fifo "$F3"; then
    sleep 0.5
    assert_contains 'T-722-03 tty から打った行も届く（attach を壊さない）' "$(cat "$WORK/out3")" 'input_pic_verify_code -code=ab12'
  else
    ng 'T-722-03 tty から打った行も届く（attach を壊さない）' 'FIFO が作られなかった'
  fi
  stop_fake_opend

  # T-722-04: 前回の FIFO が残っていても EEXIST で落ちない（コンテナ再起動の形）
  #           これを外すと OpenD が CrashLoopBackOff になる。
  F4="$WORK/run4/stdin"
  mkdir -p "$(dirname "$F4")"
  mkfifo "$F4"
  start_fake_opend "$F4" "$WORK/out4" /dev/null
  if wait_for_fifo "$F4"; then
    write_line "$F4" 'req_phone_verify_code'
    sleep 0.5
    assert_contains 'T-722-04 残存 FIFO があっても起動する（再起動の形）' "$(cat "$WORK/out4")" 'req_phone_verify_code'
    assert_missing  'T-722-04 EEXIST を出さない' "$(cat "$WORK/out4")" 'File exists'
  else
    ng 'T-722-04 残存 FIFO があっても起動する（再起動の形）' 'FIFO が作られなかった（EEXIST で落ちた可能性）'
  fi
  stop_fake_opend
else
  skip 'T-722-01/02/03/04 標準入力の FIFO 経路' \
    'この環境では FIFO の機序が成立しない（MSYS は mkfifo に成功しても 0<> 経由で届かない）。Linux で走らせること'
fi

printf '\n%d passed, %d failed, %d skipped\n' "$PASSED" "$FAILED" "$SKIPPED"
[ "$FAILED" -eq 0 ] || exit 1
