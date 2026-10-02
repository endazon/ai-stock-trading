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
#   - 段 2（IADR-0462）の 2 種（照会の状態の変化・LLM を呼ぶ前の見送り）も数える（T-10-1775）
#   - §12 は LlmCostIncurred の円を数え、§13 は上限を configuration_svc から読んで cost_control_svc の照会へ渡す（#1140。T-10-2024〜T-10-2026）
#   - §14 は取引判断の最中の例外の最終の失敗（TradeDecisionFailed）を起点 × 型名で数え、メッセージの欄を読まない（#1111。T-10-2185）
set -u

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$ROOT_DIR/scripts/nightly-ledger-summary.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# psql の記録スタブ: 接続先の DB（-d）ごとに、引数を args.<db>.log へ、標準入力（SQL）を sql.<db>.log へ書く。
# 設定サービス（configuration_svc）への照会には STUB_LIMIT（既定 12345）を 1 行返す（#1140 の §13 の上限）。
cat > "$WORK/psql-stub" <<'STUB'
#!/usr/bin/env bash
db=""; prev=""
for a in "$@"; do [ "$prev" = "-d" ] && db="$a"; prev="$a"; done
printf '%s\n' "$@" > "$STUB_DIR/args.$db.log"
cat > "$STUB_DIR/sql.$db.log"
if [ "$db" = "configuration_svc" ]; then printf '%s\n' "${STUB_LIMIT-12345}"; fi
STUB
chmod +x "$WORK/psql-stub"

pass=0
fail=0
ok() { pass=$((pass + 1)); printf '  ok  %s\n' "$1"; }
ng() { fail=$((fail + 1)); printf '  NG  %s\n' "$1" >&2; }

run() {
  rm -f "$WORK"/args.*.log "$WORK"/sql.*.log
  STUB_DIR="$WORK" AST_PSQL="$WORK/psql-stub" bash "$SCRIPT" "$@" >"$WORK/out" 2>"$WORK/err"
}

has_arg() { grep -qxF -- "$1" "$WORK/args.${2:-audit_svc}.log" 2>/dev/null; }
no_psql() { ! compgen -G "$WORK/args.*.log" >/dev/null; }

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
  if [ "$rc" -eq 2 ] && no_psql; then ok "時差の無い・形の違う時刻は psql を呼ばずに exit 2（${bad}）"
  else ng "不正な時刻を通した（${bad}・rc=${rc}）"; fi
done

run 2026-09-30T05:00+09:00 2026-09-29T22:30+09:00
if [ $? -eq 2 ] && no_psql; then ok 'from が to 以降なら exit 2'; else ng 'from >= to を通した'; fi

run 2026-09-29T22:30+09:00 2026-09-29T22:30+09:00
if [ $? -eq 2 ]; then ok '空の窓（from = to）も exit 2'; else ng '空の窓を通した'; fi

for bad in '--night' '--night 2026-13-01' '--night 2026-09-29 extra'; do
  # shellcheck disable=SC2086
  run $bad
  if [ $? -eq 2 ] && no_psql; then ok "--night の誤りは exit 2（${bad}）"; else ng "--night の誤りを通した（${bad}）"; fi
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
sql="$(cat "$WORK/sql.audit_svc.log")"
if grep -q '^BEGIN TRANSACTION READ ONLY;' <<<"$sql" && [ "$(tail -n 1 <<<"$sql")" = 'ROLLBACK;' ]; then
  ok 'SQL は読み取り専用のトランザクションで走り、ROLLBACK で終わる'
else ng '読み取り専用のトランザクションになっていない'; fi
if grep -Eiq '\b(insert|update|delete|create|drop|alter|truncate|grant|revoke|copy|vacuum)\b' <<<"$sql"; then
  ng 'SQL に書き込み・DDL の語がある'
else ok 'SQL に書き込み・DDL の語が無い'; fi
if grep -q 'ON_ERROR_STOP 1' <<<"$sql"; then ok '途中の失敗で止まる（ON_ERROR_STOP）'; else ng 'ON_ERROR_STOP が無い'; fi
for t in TradeDecisionMade TradeDecisionHeld TradeDecisionSkipped OrderApproved OrderRejected OrderDispatchForgone \
  OrderExecuted SoftwareStopArmed StopLossTriggered SoftwareStopExecuted BrokerPositionsObserved BrokerAvailabilityObserved \
  PositionQueryStatusChanged TradeDecisionForgoneBeforeLlm; do
  if grep -q "'$t'" <<<"$sql"; then :; else ng "SQL が ${t} を数えていない"; fi
done
# T-10-1775, NFR, #1092 段 2, IADR-0462: 照会の失敗の区間（§10）と LLM を呼ぶ前の見送り（§11）を数える。
ok '台帳の 14 種類のイベントを数える（段 2 の照会の状態の変化・LLM を呼ぶ前の見送りを含む）'
# T-10-1796, FR-10, #1113, IADR-0463 決定 5: §5 と §11 に計器の移動（審査の拒否 → LLM を呼ぶ前の見送り）の注記がある。
if grep -q "^\\\\echo '-- EntryBlockedByRiskControls は" <<<"$sql" && grep -q "^\\\\echo '-- 保有 0・未約定なしで新規建てが必ず拒否される銘柄は" <<<"$sql"; then
  ok '§5・§11 に計器の移動（EntryBlockedByRiskControls）の注記がある'
else ng '§5・§11 に計器の移動の注記が無い'; fi

# --- §14: 取引判断の最中の例外（NFR, #1111, IADR-0483 決定 5） --------------------------------
# T-10-2185: TradeDecisionFailed を起点 × 型名で数える。台帳に無い欄（メッセージ・スタック）を読まない。
if grep -q "^\\\\echo '== 14. 取引判断の最中の例外" <<<"$sql" && grep -q "\"EventType\" = 'TradeDecisionFailed'" <<<"$sql" \
  && grep -q '"Detail"->>'"'"'ExceptionType'"'" <<<"$sql" && grep -q '"Detail"->>'"'"'CycleTrigger'"'" <<<"$sql"; then
  ok '§14 は TradeDecisionFailed を起点 × 例外の型名で数える'
else ng '§14（取引判断の最中の例外）が無い'; fi
if grep -Eq "'(Message|StackTrace|InnerException)'" <<<"$sql"; then ng 'SQL が例外のメッセージ・スタックの欄を読む'
else ok 'SQL は例外のメッセージ・スタックの欄を読まない'; fi

# --- §12・§13: LLM の費用（NFR, #1140, IADR-0478 決定 2） ------------------------------------
# T-10-2024: 窓の中の LlmCostIncurred の円を数える（§12）。
if grep -q "'LlmCostIncurred'" <<<"$sql" && grep -q "^\\\\echo '== 12. LLM の費用" <<<"$sql" && grep -q '"Detail"->>'"'"'Amount'"'" <<<"$sql"; then
  ok '§12 は窓の中の LlmCostIncurred の円（Amount）を合計する'
else ng '§12（LLM の費用の円）が無い'; fi
# T-10-2025: 上限は設定サービスの前提条件から 1 行で読み（-A -t）、その値を §13 の照会へ llm_limit として渡す。値を複写しない。
run --night 2026-09-29
if has_arg '-A' configuration_svc && has_arg '-t' configuration_svc && has_arg '-X' configuration_svc \
  && grep -q "\"Json\"->'costLimits'->>'llm' FROM assumptions" "$WORK/sql.configuration_svc.log"; then
  ok '月次上限は configuration_svc の assumptions（costLimits.llm）から読む'
else ng '月次上限の読み先が違う'; fi
if has_arg 'llm_limit=12345' cost_control_svc && has_arg 'to=2026-09-30T08:00+09:00' cost_control_svc && has_arg '-X' cost_control_svc; then
  ok '§13 は cost_control_svc へ、読んだ上限（llm_limit）と窓の終端（to）を渡す'
else ng '§13 へ上限・窓の終端が渡らない'; fi
for db in configuration_svc cost_control_svc; do
  q="$(cat "$WORK/sql.$db.log")"
  if grep -q '^BEGIN TRANSACTION READ ONLY;' <<<"$q" && [ "$(tail -n 1 <<<"$q")" = 'ROLLBACK;' ] \
    && ! grep -Eiq '\b(insert|update|delete|create|drop|alter|truncate|grant|revoke|copy|vacuum)\b' <<<"$q"; then
    ok "${db} の SQL も読み取り専用のトランザクションで ROLLBACK で終わり、書き込みの語が無い"
  else ng "${db} の SQL が読み取り専用になっていない"; fi
done
if grep -Eq '15[,_]?000' "$SCRIPT"; then
  ng '要約のスクリプトに月次上限の値（15,000）を複写している'
else ok '要約のスクリプトに月次上限の値を複写していない'; fi
# T-10-2026: 上限を読めない（行が無い・数値でない）なら空で渡し（§13 は「不明」）、理由を標準エラーへ出して続ける。
for bad in '' 'oops'; do
  rm -f "$WORK"/args.*.log "$WORK"/sql.*.log
  STUB_DIR="$WORK" AST_PSQL="$WORK/psql-stub" STUB_LIMIT="$bad" bash "$SCRIPT" --night 2026-09-29 >"$WORK/out" 2>"$WORK/err"
  rc=$?
  if [ "$rc" -eq 0 ] && has_arg 'llm_limit=' cost_control_svc && grep -q '月次 LLM 費用上限を設定サービス' "$WORK/err"; then
    ok "上限を読めない（'${bad}'）なら llm_limit は空・警告を出して続ける"
  else ng "上限を読めない（'${bad}'）ときの扱い（rc=${rc}）"; fi
done

# --- 関数だけの読み込み ---------------------------------------------------------------
if AST_NIGHTLY_LIB=1 bash -c ". '$SCRIPT'; declare -F nightly_window nightly_sql nightly_cost_limit_sql nightly_cost_sql nightly_main >/dev/null"; then
  ok 'AST_NIGHTLY_LIB=1 で関数だけを読み込める（実行しない）'
else ng 'AST_NIGHTLY_LIB=1 の読み込み'; fi

echo
if [ "$fail" -ne 0 ]; then
  echo "✗ ${fail} failed / ${pass} passed" >&2
  exit 1
fi
echo "✓ ${pass} tests passed"
