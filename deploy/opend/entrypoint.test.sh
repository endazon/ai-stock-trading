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

printf '\n%d passed, %d failed, %d skipped\n' "$PASSED" "$FAILED" "$SKIPPED"
[ "$FAILED" -eq 0 ] || exit 1
