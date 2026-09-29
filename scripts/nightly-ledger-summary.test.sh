#!/usr/bin/env bash
# NFR, #1092: scripts/nightly-ledger-summary.sh（夜間の台帳の要約）の挙動を固定する。
#
#   bash scripts/nightly-ledger-summary.test.sh
#
# 実 DB は要らない。AST_PSQL に記録スタブを与え、渡された引数と SQL を検査する
# （scripts/cutover-count-reconcile.test.sh と同じ idiom）。SQL を実 Postgres で流した検証は作業仕様書に残す。
#
# 固定する不変条件:
#   - 窓の時刻は時差つきの ISO 8601 だけを受け付け、誤りは psql を呼ぶ前に exit 2 で止まる（窓のずれを黙って通さない）
#   - --night <日付> は その日 20:00 JST 〜 翌日 08:00 JST（米国市場の夜を夏冬とも覆う）
#   - 接続先の DB は audit_svc で、SQL は読み取り専用のトランザクションの中で走り、最後に ROLLBACK する
#   - SQL に書き込み・DDL の語が無い
set -u

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$ROOT_DIR/scripts/nightly-ledger-summary.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# psql の記録スタブ: 引数を args.log へ、標準入力（SQL）を sql.log へ書く。
cat > "$WORK/psql-stub" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$STUB_DIR/args.log"
cat > "$STUB_DIR/sql.log"
STUB
chmod +x "$WORK/psql-stub"

pass=0
fail=0
ok() { pass=$((pass + 1)); printf '  ok  %s\n' "$1"; }
ng() { fail=$((fail + 1)); printf '  NG  %s\n' "$1" >&2; }

run() {
  rm -f "$WORK/args.log" "$WORK/sql.log"
  STUB_DIR="$WORK" AST_PSQL="$WORK/psql-stub" bash "$SCRIPT" "$@" >"$WORK/out" 2>"$WORK/err"
}

has_arg() { grep -qxF -- "$1" "$WORK/args.log" 2>/dev/null; }

# --- 窓 ------------------------------------------------------------------------
run --night 2026-09-29
if [ $? -eq 0 ] && has_arg 'from=2026-09-29T20:00+09:00' && has_arg 'to=2026-09-30T08:00+09:00'; then
  ok '--night はその日 20:00 JST 〜 翌日 08:00 JST の窓を渡す'
else ng '--night の窓が違う'; fi

run --night 2026-12-31
if [ $? -eq 0 ] && has_arg 'to=2027-01-01T08:00+09:00'; then ok '--night は年をまたいでも翌日を正しく引く'; else ng '--night の年またぎ'; fi

run 2026-09-29T22:30+09:00 2026-09-30T05:00:00Z
if [ $? -eq 0 ] && has_arg 'from=2026-09-29T22:30+09:00' && has_arg 'to=2026-09-30T05:00:00Z'; then
  ok '時差つきの ISO 8601（+09:00 / Z）はそのまま渡す'
else ng '明示の窓がそのまま渡らない'; fi

for bad in '2026-09-29T22:30 2026-09-30T05:00+09:00' '2026-09-29 2026-09-30' 'yesterday today'; do
  # shellcheck disable=SC2086  # 2 引数へ分ける
  run $bad
  rc=$?
  if [ "$rc" -eq 2 ] && [ ! -e "$WORK/args.log" ]; then ok "時差の無い・形の違う時刻は psql を呼ばずに exit 2（${bad}）"
  else ng "不正な時刻を通した（${bad}・rc=${rc}）"; fi
done

run 2026-09-30T05:00+09:00 2026-09-29T22:30+09:00
if [ $? -eq 2 ] && [ ! -e "$WORK/args.log" ]; then ok 'from が to 以降なら exit 2'; else ng 'from >= to を通した'; fi

run 2026-09-29T22:30+09:00 2026-09-29T22:30+09:00
if [ $? -eq 2 ]; then ok '空の窓（from = to）も exit 2'; else ng '空の窓を通した'; fi

for bad in '--night' '--night 2026-13-01' '--night 2026-09-29 extra'; do
  # shellcheck disable=SC2086
  run $bad
  if [ $? -eq 2 ] && [ ! -e "$WORK/args.log" ]; then ok "--night の誤りは exit 2（${bad}）"; else ng "--night の誤りを通した（${bad}）"; fi
done

run
if [ $? -eq 2 ] && grep -q '使い方' "$WORK/err"; then ok '引数なしは使い方を出して exit 2'; else ng '引数なしの扱い'; fi

# --- 接続と SQL -------------------------------------------------------------------
run --night 2026-09-29
if has_arg '-d' && has_arg 'audit_svc' && has_arg '-X'; then ok '接続先の DB は audit_svc（psqlrc を読まない）'; else ng '接続先が違う'; fi
if has_arg 'positions_gap=20 minutes' && has_arg 'availability_gap=10 minutes'; then
  ok '観測の欠けのしきい値の既定は建玉 20 分・稼働 10 分'
else ng 'しきい値の既定が違う'; fi

STUB_DIR="$WORK" AST_PSQL="$WORK/psql-stub" NIGHTLY_POSITIONS_GAP='30 minutes' bash "$SCRIPT" --night 2026-09-29 >/dev/null 2>&1
if has_arg 'positions_gap=30 minutes'; then ok 'しきい値は環境変数で変えられる'; else ng 'しきい値の上書きが効かない'; fi

run --night 2026-09-29
sql="$(cat "$WORK/sql.log")"
if grep -q '^BEGIN TRANSACTION READ ONLY;' <<<"$sql" && [ "$(tail -n 1 <<<"$sql")" = 'ROLLBACK;' ]; then
  ok 'SQL は読み取り専用のトランザクションで走り、ROLLBACK で終わる'
else ng '読み取り専用のトランザクションになっていない'; fi
if grep -Eiq '\b(insert|update|delete|create|drop|alter|truncate|grant|revoke|copy|vacuum)\b' <<<"$sql"; then
  ng 'SQL に書き込み・DDL の語がある'
else ok 'SQL に書き込み・DDL の語が無い'; fi
if grep -q 'ON_ERROR_STOP 1' <<<"$sql"; then ok '途中の失敗で止まる（ON_ERROR_STOP）'; else ng 'ON_ERROR_STOP が無い'; fi
for t in TradeDecisionMade TradeDecisionHeld TradeDecisionSkipped OrderApproved OrderRejected OrderDispatchForgone \
  OrderExecuted SoftwareStopArmed StopLossTriggered SoftwareStopExecuted BrokerPositionsObserved BrokerAvailabilityObserved; do
  if grep -q "'$t'" <<<"$sql"; then :; else ng "SQL が ${t} を数えていない"; fi
done
ok '台帳の 12 種類のイベントを数える'

# --- 関数だけの読み込み ---------------------------------------------------------------
if AST_NIGHTLY_LIB=1 bash -c ". '$SCRIPT'; declare -F nightly_window nightly_sql nightly_main >/dev/null"; then
  ok 'AST_NIGHTLY_LIB=1 で関数だけを読み込める（実行しない）'
else ng 'AST_NIGHTLY_LIB=1 の読み込み'; fi

echo
if [ "$fail" -ne 0 ]; then
  echo "✗ ${fail} failed / ${pass} passed" >&2
  exit 1
fi
echo "✓ ${pass} tests passed"
