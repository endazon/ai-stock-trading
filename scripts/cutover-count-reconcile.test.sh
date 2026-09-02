#!/usr/bin/env bash
# #346 / IADR-0287: scripts/cutover-count-reconcile.sh（切替前後の件数突合）の挙動を固定する。
#
#   bash scripts/cutover-count-reconcile.test.sh
#
# 実 DB は要らない。PATH の先頭に `psql` スタブを置き、スタブが「DB に実在するテーブル」「各テーブルの計測値」
# を $AST_TEST_STATE から返す。対象スクリプトは AST_CUTOVER_LIB=1 で source すると関数定義だけを読み込む
# （scripts/k8s-local-deploy.test.sh / deploy/opend/entrypoint.test.sh と同じ idiom）。
#
# 固定する不変条件（Issue #346「退行防止」・NFR-09 / NFR-10）:
#   - snapshot は manifest（保全対象の全数表）と DB の実在テーブルを双方向に突き合わせ、片方にしか無ければ
#     部分出力をせずに exit 2 で止まる（数え落としを「全数」と読ませない）
#   - compare は 7 年保持対象の欠損ゼロ（件数・min/max・内容指紋の一致）を要求し、1 件でも違えば exit 1
#   - compare は未確定予約（Reserved）が 1 件でも減っていれば exit 1（無期限保持・自動削除禁止）
#   - after にだけあるテーブル・移行履歴の増加は NOTE であり失敗にしない（新スキーマ適用は正常）
#   - スクリプトは SELECT 以外の SQL を発行しない（スタブが全 SQL を記録し、書き込み語を検査する）
set -u

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
STATE="$(mktemp -d)"
STUB_BIN="$(mktemp -d)"
WORK="$(mktemp -d)"
trap 'rm -rf "$STATE" "$STUB_BIN" "$WORK"' EXIT

# ---- psql スタブ -----------------------------------------------------------
# $AST_TEST_STATE/tables.<db>        … pg_tables が返すテーブル名（1 行 1 件）
# $AST_TEST_STATE/rows.<db>.<table>  … count<TAB>min<TAB>max<TAB>pending<TAB>fingerprint（無ければ計測失敗）
# $AST_TEST_STATE/mig.<db>           … count<TAB>last_migration
# $AST_TEST_STATE/sql.log            … 受け取った SQL の全記録
cat > "$STUB_BIN/psql" <<'STUB'
#!/usr/bin/env bash
set -u
S="$AST_TEST_STATE"
db=""; sql=""; prev=""
for a in "$@"; do
  case "$prev" in
    -d) db="$a" ;;
    -c) sql="$a" ;;
  esac
  prev="$a"
done
printf '%s\t%s\n' "$db" "$sql" >> "$S/sql.log"
case "$sql" in
  *pg_tables*) cat "$S/tables.$db" 2>/dev/null; exit 0 ;;
  *__EFMigrationsHistory*) [ -f "$S/mig.$db" ] && cat "$S/mig.$db" || printf '1\t20260101000000_Init\n'; exit 0 ;;
esac
table="$(printf '%s' "$sql" | grep -o 'from "[A-Za-z0-9_]*"' | head -1 | cut -d'"' -f2)"
if [ -z "$table" ]; then
  # controls の照会（引用符無しのテーブル名）。$S/ctl.<db>.<table> があればそれを返し、
  # $S/fail.<db>.<table> があれば失敗を疑似する。どちらも無ければ空（行が無い）。
  table="$(printf '%s' "$sql" | grep -o -E 'from [a-z0-9_]+' | head -1 | cut -d' ' -f2)"
  [ -f "$S/fail.$db.$table" ] && { echo "ERROR: simulated failure on $table" >&2; exit 1; }
  [ -f "$S/ctl.$db.$table" ] && cat "$S/ctl.$db.$table"
  exit 0
fi
[ -f "$S/rows.$db.$table" ] || { echo "ERROR: relation \"$table\" does not exist" >&2; exit 1; }
cat "$S/rows.$db.$table"
STUB
chmod +x "$STUB_BIN/psql"

AST_CUTOVER_LIB=1
export AST_CUTOVER_LIB
# shellcheck source=scripts/cutover-count-reconcile.sh
. "$ROOT_DIR/scripts/cutover-count-reconcile.sh"

PATH="$STUB_BIN:$PATH"
export PATH
AST_TEST_STATE="$STATE"
export AST_TEST_STATE

pass=0; fail=0
ok() { pass=$((pass + 1)); echo "  ok   $1"; }
ng() { fail=$((fail + 1)); echo "  FAIL $1"; }
check() { if eval "$2"; then ok "$1"; else ng "$1"; fi; }

# manifest どおりの「整合した DB」を状態ディレクトリへ生成する（全テーブル 0 件・指紋 empty）。
reset_state() {
  rm -rf "$STATE"; mkdir -p "$STATE"
  printf '%s\n' "$AST_CUTOVER_MANIFEST" | awk -F '\t' -v S="$STATE" '
    { print $2 >> (S "/tables." $1); close(S "/tables." $1)
      pend = ($5 == "-") ? "-" : "0"
      printf "0\t-\t-\t%s\tempty\n", pend > (S "/rows." $1 "." $2); close(S "/rows." $1 "." $2) }'
}
set_rows() { printf '%s\n' "$4" > "$STATE/rows.$1.$2"; : "$3"; }
manifest_count() { printf '%s\n' "$AST_CUTOVER_MANIFEST" | grep -c .; }
manifest_db_count() { printf '%s\n' "$AST_CUTOVER_MANIFEST" | cut -f1 | sort -u | grep -c .; }

echo "== snapshot: manifest と DB が一致すれば全テーブル＋移行履歴を書く"
reset_state
set_rows audit_svc audit_events - "$(printf '334\t2026-08-31 11:40:16\t2026-09-02 22:21:58\t-\tabc123')"
set_rows order_execution_svc order_dispatch_reservations - "$(printf '3\t2026-09-01 00:00:00\t2026-09-02 00:00:00\t2\tfp-res')"
cmd_snapshot "$WORK/before.tsv" 2>"$WORK/err"; rc=$?
check "exit 0" "[ $rc -eq 0 ]"
n_data="$(grep -v '^#' "$WORK/before.tsv" | grep -vc 'migrations')"
check "データ行 = manifest 件数（$(manifest_count)）" "[ \"$n_data\" -eq \"$(manifest_count)\" ]"
n_mig="$(grep -c 'migrations' "$WORK/before.tsv")"
check "移行履歴行 = DB 数（$(manifest_db_count)）" "[ \"$n_mig\" -eq \"$(manifest_db_count)\" ]"
check "audit_events の件数・指紋が載る" "grep -q $'audit_svc\taudit_events\tledger\t334\t2026-08-31 11:40:16\t2026-09-02 22:21:58\t-\tabc123' \"$WORK/before.tsv\""
check "未確定予約の件数（pending=2）が載る" "grep -q $'order_dispatch_reservations\treserved\t3\t.*\t2\tfp-res' \"$WORK/before.tsv\""
check "発行 SQL に書き込み語が無い（SELECT のみ）" "! grep -i -E '\b(insert|update|delete|drop|alter|truncate|create)\b' \"$STATE/sql.log\""
check "SQL は Reserved を State=0 で数える" "grep -q 'filter (where \"State\" = 0)' \"$STATE/sql.log\""

echo "== snapshot: DB にあって manifest に無いテーブルは exit 2（数え落としを黙認しない）"
reset_state
printf 'mystery_table\n' >> "$STATE/tables.risk_management_svc"
cmd_snapshot "$WORK/x.tsv" 2>"$WORK/err"; rc=$?
check "exit 2" "[ $rc -eq 2 ]"
check "テーブル名を名指しする" "grep -q 'risk_management_svc.mystery_table' \"$WORK/err\""
check "部分出力を残さない" "[ ! -f \"$WORK/x.tsv\" ]"

echo "== snapshot: manifest にあって DB に無いテーブルは exit 2"
reset_state
grep -v '^cost_entries$' "$STATE/tables.cost_control_svc" > "$STATE/t" && mv "$STATE/t" "$STATE/tables.cost_control_svc"
cmd_snapshot "$WORK/x.tsv" 2>"$WORK/err"; rc=$?
check "exit 2" "[ $rc -eq 2 ]"
check "テーブル名を名指しする" "grep -q 'cost_control_svc.cost_entries' \"$WORK/err\""

echo "== snapshot: 計測に失敗したテーブルがあれば exit 2"
reset_state
rm -f "$STATE/rows.audit_svc.audit_events"
cmd_snapshot "$WORK/x.tsv" 2>"$WORK/err"; rc=$?
check "exit 2" "[ $rc -eq 2 ]"
check "部分出力を残さない" "[ ! -f \"$WORK/x.tsv\" ]"

echo "== compare: 同一スナップショットは exit 0"
cp "$WORK/before.tsv" "$WORK/after.tsv"
cmd_compare "$WORK/before.tsv" "$WORK/after.tsv" > "$WORK/out"; rc=$?
check "exit 0" "[ $rc -eq 0 ]"
check "FAIL 行が無い" "! grep -q '^FAIL' \"$WORK/out\""
check "SUMMARY に fail=0" "grep -q $'SUMMARY\tbefore=[0-9]*\tfail=0' \"$WORK/out\""

echo "== compare: 7 年保持台帳の件数減少は exit 1"
sed $'s/^audit_svc\taudit_events\tledger\t334\t/audit_svc\taudit_events\tledger\t333\t/' "$WORK/before.tsv" > "$WORK/after.tsv"
cmd_compare "$WORK/before.tsv" "$WORK/after.tsv" > "$WORK/out"; rc=$?
check "exit 1" "[ $rc -eq 1 ]"
check "audit_events を FAIL で名指しする" "grep -q $'^FAIL\taudit_svc.audit_events\t.*334 -> 333' \"$WORK/out\""

echo "== compare: 件数が同じでも内容指紋が違えば exit 1（改変検知）"
sed $'s/\tabc123$/\tzzz999/' "$WORK/before.tsv" > "$WORK/after.tsv"
cmd_compare "$WORK/before.tsv" "$WORK/after.tsv" > "$WORK/out"; rc=$?
check "exit 1" "[ $rc -eq 1 ]"
check "指紋の不一致を報告する" "grep -q $'^FAIL\taudit_svc.audit_events\t.*指紋' \"$WORK/out\""

echo "== compare: 未確定予約が減っていれば exit 1（テーブル件数が同じでも）"
sed $'s/\treserved\t3\t\\(.*\\)\t2\tfp-res$/\treserved\t3\t\\1\t1\tfp-res/' "$WORK/before.tsv" > "$WORK/after.tsv"
check "フィクスチャが pending=1 へ変わっている" "grep -q $'\treserved\t3\t.*\t1\tfp-res' \"$WORK/after.tsv\""
cmd_compare "$WORK/before.tsv" "$WORK/after.tsv" > "$WORK/out"; rc=$?
check "exit 1" "[ $rc -eq 1 ]"
check "未確定予約の減少を名指しする" "grep -q $'^FAIL\torder_execution_svc.order_dispatch_reservations\t未確定予約が減っている' \"$WORK/out\""

echo "== compare: before のテーブルが after に無ければ exit 1"
grep -v $'^cost_control_svc\tcost_entries\t' "$WORK/before.tsv" > "$WORK/after.tsv"
cmd_compare "$WORK/before.tsv" "$WORK/after.tsv" > "$WORK/out"; rc=$?
check "exit 1" "[ $rc -eq 1 ]"
check "欠損テーブルを名指しする" "grep -q $'^FAIL\tcost_control_svc.cost_entries\tafter に存在しない' \"$WORK/out\""

echo "== compare: after にだけあるテーブルと移行履歴の増加は NOTE（exit 0）"
{ cat "$WORK/before.tsv"; printf 'risk_management_svc\tnew_table\tledger\t0\t-\t-\t-\tempty\n'; } \
  | sed $'s/^audit_svc\t__EFMigrationsHistory\tmigrations\t1\t/audit_svc\t__EFMigrationsHistory\tmigrations\t2\t/' > "$WORK/after.tsv"
cmd_compare "$WORK/before.tsv" "$WORK/after.tsv" > "$WORK/out"; rc=$?
check "exit 0" "[ $rc -eq 0 ]"
check "新規テーブルは NOTE" "grep -q $'^NOTE\trisk_management_svc.new_table' \"$WORK/out\""
check "移行の増加は NOTE" "grep -q $'^NOTE\taudit_svc.__EFMigrationsHistory\t新スキーマ適用' \"$WORK/out\""

echo "== compare: 移行履歴が減っていれば exit 1"
sed $'s/^audit_svc\t__EFMigrationsHistory\tmigrations\t1\t/audit_svc\t__EFMigrationsHistory\tmigrations\t0\t/' "$WORK/before.tsv" > "$WORK/after.tsv"
cmd_compare "$WORK/before.tsv" "$WORK/after.tsv" > "$WORK/out"; rc=$?
check "exit 1" "[ $rc -eq 1 ]"

echo "== compare: 入力が無ければ exit 2"
cmd_compare "$WORK/before.tsv" "$WORK/nope.tsv" > /dev/null 2>&1; rc=$?
check "exit 2" "[ $rc -eq 2 ]"

echo "== manifest: 保全対象の全数表を出す"
n="$(cmd_manifest | grep -vc '^#')"
check "行数 = manifest 件数" "[ \"$n\" -eq \"$(manifest_count)\" ]"
other_classes="$(cmd_manifest | grep -v '^#' | cut -f3 | grep -v -x -E 'ledger|state|dedup|reserved' | grep -c .)"
check "dedup と reserved 以外は ledger / state（RetentionScope の閉世界の補集合）" "[ \"$other_classes\" -eq 0 ]"
special="$(cmd_manifest | grep -v '^#' | grep -E $'\t(dedup|reserved)\t' | cut -f2 | sort | tr '\n' ' ')"
check "dedup は processed_messages のみ、reserved は order_dispatch_reservations のみ" \
  "[ \"$special\" = 'order_dispatch_reservations processed_messages ' ]"

echo "== AST_PSQL: 複合コマンド（kubectl exec ... -- psql）へ差し替えられる"
reset_state
cat > "$STUB_BIN/kubectl" <<'STUB'
#!/usr/bin/env bash
# `kubectl -n ns exec -i deploy/postgres -- psql ...` の `--` 以降を psql スタブへ渡す
while [ $# -gt 0 ] && [ "$1" != "--" ]; do shift; done
shift
exec "$@"
STUB
chmod +x "$STUB_BIN/kubectl"
AST_PSQL="kubectl -n platform-infra exec -i deploy/postgres -- psql -U ai" cmd_snapshot "$WORK/k.tsv" 2>"$WORK/err"; rc=$?
check "exit 0" "[ $rc -eq 0 ]"
check "ヘッダに psql コマンドを記録する" "grep -q 'psql=kubectl -n platform-infra exec -i deploy/postgres -- psql -U ai' \"$WORK/k.tsv\""

echo "== AST_DB_PREFIX: リハーサル用コピー（cutover_rehearsal_<db>）を同じ manifest で測り、TSV には論理名を書く"
reset_state
for f in "$STATE"/tables.* "$STATE"/rows.*; do
  b="$(basename "$f")"
  cp "$f" "$STATE/${b%%.*}.cutover_rehearsal_${b#*.}"
done
AST_DB_PREFIX="cutover_rehearsal_" cmd_snapshot "$WORK/reh.tsv" 2>"$WORK/err"; rc=$?
check "exit 0" "[ $rc -eq 0 ]"
check "接続先はコピー DB" "grep -q $'^cutover_rehearsal_audit_svc\t' \"$STATE/sql.log\""
check "TSV の db 列は論理名（compare で本番の TSV と突き合わせられる）" "grep -q $'^audit_svc\taudit_events\t' \"$WORK/reh.tsv\" && ! grep -q $'^cutover_rehearsal_' \"$WORK/reh.tsv\""
check "ヘッダに db_prefix を記録する" "grep -q 'db_prefix=cutover_rehearsal_' \"$WORK/reh.tsv\""

echo "== controls: 統制状態・切替前チェックの現在値を key/value で出す（読み取りのみ）"
reset_state
printf '1\n' > "$STATE/ctl.risk_management_svc.risk_settings"
printf '0\n' > "$STATE/ctl.order_execution_svc.executed_orders"
cmd_controls > "$WORK/ctl" 2>"$WORK/err"; rc=$?
check "exit 0" "[ $rc -eq 0 ]"
check "risk_settings の版を出す" "grep -q $'^risk_settings.version\t1$' \"$WORK/ctl\""
check "行が無い統制は <none>（空欄で「無い」と読ませない）" "grep -q $'^kill_switch.engaged\t<none>$' \"$WORK/ctl\""
check "未約定注文の件数（切替前チェック）を出す" "grep -q $'^executed_orders.non_terminal\t0$' \"$WORK/ctl\""
check "発行 SQL に書き込み語が無い（SELECT のみ）" "! grep -i -E '\b(insert|update|delete|drop|alter|truncate|create)\b' \"$STATE/sql.log\""
: > "$STATE/fail.risk_management_svc.kill_switch"
cmd_controls > "$WORK/ctl" 2>"$WORK/err"; rc=$?
check "1 項目でも読めなければ exit 2" "[ $rc -eq 2 ]"
check "読めなかった項目を名指しする" "grep -q 'kill_switch.engaged' \"$WORK/err\""

echo
echo "passed=$pass failed=$fail"
[ "$fail" -eq 0 ]
