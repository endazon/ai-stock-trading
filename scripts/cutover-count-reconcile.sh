#!/usr/bin/env bash
# FR-11 / NFR-09 / NFR-10, #346, IADR-0287: 再実装版への切替（cutover）の前後で、保全必須データが
# 1 行も欠けていないことを機械で突き合わせる。
#
#   bash scripts/cutover-count-reconcile.sh snapshot <out.tsv>          # 現状を採る（読み取りのみ）
#   bash scripts/cutover-count-reconcile.sh compare  <before.tsv> <after.tsv>
#   bash scripts/cutover-count-reconcile.sh manifest                    # 保全対象の全数表を出す
#
# 接続は AST_PSQL（psql の呼び出しコマンド。既定 `psql`）で差し替える。ローカル k3s の platform-infra なら:
#   AST_PSQL="kubectl -n platform-infra exec -i deploy/postgres -- psql -U ai"
# 本スクリプトは SELECT しか発行しない（書き込み・DDL・パージは一切行わない）。
#
# ■ 何を数えるか（IADR-0287 決定 1・決定 2）
#   - 母集合は 7 サービス DB の全ユーザテーブル（`__EFMigrationsHistory` を除く）。DB 側から pg_tables で
#     実在テーブルを発見し、**manifest に無いテーブルが在れば exit 2 で止まる**（黙って数え落とさない＝fail-loud）。
#     逆に manifest にあって DB に無いテーブルも exit 2。
#   - 各テーブルで `件数 / 時刻列の min / max / 未確定件数 / 内容指紋` を採る。指紋は各行の md5 を昇順に連結した
#     md5（行順に依存しない）であり、**件数が同じでも 1 行でも改変されれば変わる**（欠損ゼロだけでなく改変ゼロ）。
#   - `order_dispatch_reservations` の未確定件数（State=0＝Reserved）は NFR-09 の不変条件（無期限保持・自動削除禁止）
#     の対象であり、compare は before と after で**1 件も減っていない**ことを要求する。
#
# ■ compare の判定（純関数。2 つの TSV 以外を読まない）
#   FAIL: before にあるテーブルが after に無い／件数・min・max・指紋・未確定件数のいずれかが違う
#   NOTE: after にだけあるテーブル（新スキーマの新規テーブルは正常）／`__EFMigrationsHistory` の増加（新スキーマ適用の証跡）
#   exit 0 = FAIL 0 件、exit 1 = FAIL あり、exit 2 = 使い方・入力の誤り
#
# ■ テスト: scripts/cutover-count-reconcile.test.sh（psql スタブ・実 DB 不要）。AST_CUTOVER_LIB=1 で source すると
#   関数定義だけを読み込む（#263 / IADR-0109、#274 の idiom）。manifest と EF ModelSnapshot の突合は
#   scripts/scripts.repo.test.js（テーブルの増減で manifest が腐るのを CI で止める）。
set -u

TAB=$'\t'

# 保全対象の全数表（manifest）。列: db / table / class / 時刻列 / 未確定条件（無ければ -）
#   class: ledger   … 業務台帳・監査証跡（7 年保持・自動パージ対象外。NFR-10）
#          state    … 統制状態・現在値（引き継ぎ必須。履歴ではない。自動パージ対象外）
#          reserved … 未確定予約を含む冪等化ストア（Reserved は無期限保持・自動削除禁止。NFR-09）
#          dedup    … 重複排除メタデータ（運用中は保持期間パージ可。NFR-08。**切替では保全する**）
#   自動パージの可否は RetentionScope（backend/Shared/AiStockTrading.Shared.Contracts/Operations/RetentionScope.cs）
#   の閉世界が正本であり、dedup と reserved 以外はすべて「消してはならない」側である。本表はその補集合を
#   明示列挙し、scripts.repo.test.js が EF ModelSnapshot と突き合わせて腐りを止める。
AST_CUTOVER_MANIFEST="\
audit_svc	audit_events	ledger	RecordedAt	-
configuration_svc	assumptions	state	UpdatedAt	-
configuration_svc	assumptions_change_log	ledger	ChangedAt	-
cost_control_svc	cost_entries	ledger	RecordedAt	-
cost_control_svc	processed_messages	dedup	ProcessedAt	-
market_monitor_svc	cooldown	state	LastTriggeredAt	-
market_monitor_svc	monitor_settings	state	UpdatedAt	-
market_monitor_svc	monitor_settings_change	ledger	ChangedAt	-
market_monitor_svc	price_baseline	state	UpdatedAt	-
order_execution_svc	executed_orders	ledger	ExecutedAt	-
order_execution_svc	order_dispatch_reservations	reserved	ReservedAt	\"State\" = 0
order_execution_svc	order_lifecycle_events	ledger	OccurredAt	-
order_execution_svc	protective_stop_orders	ledger	UpdatedAt	-
report_svc	reports	ledger	ConfirmedAt	-
risk_management_svc	approved_orders	ledger	ApprovedAt	-
risk_management_svc	borrow_fee_accruals	ledger	AccruedAtUtc	-
risk_management_svc	borrow_fee_unavailable_days	ledger	ObservedAtUtc	-
risk_management_svc	buy_in_inferences	ledger	InferredAtUtc	-
risk_management_svc	good_faith_violation_clearances	ledger	ClearedAtUtc	-
risk_management_svc	good_faith_violations	ledger	RecordedAtUtc	-
risk_management_svc	kill_switch	state	ChangedAt	-
risk_management_svc	lockout	state	EngagedAt	-
risk_management_svc	order_activity	ledger	PlacedAt	-
risk_management_svc	order_screening_observations	ledger	ObservedAtUtc	-
risk_management_svc	pause	state	ChangedAt	-
risk_management_svc	position_drift_state	state	UpdatedAt	-
risk_management_svc	position_observation_days	ledger	UpdatedAt	-
risk_management_svc	risk_settings	state	UpdatedAt	-
risk_management_svc	settings_change_log	ledger	ChangedAt	-
risk_management_svc	stage1_fill_observations	ledger	ObservedAtUtc	-
risk_management_svc	stage1_session_uptime	ledger	UpdatedAtUtc	-
risk_management_svc	stage_performance	state	UpdatedAt	-
risk_management_svc	stage_transitions	ledger	OccurredAtUtc	-
risk_management_svc	trade_fills	ledger	ExecutedAt	-
risk_management_svc	withdrawal_notification	state	UpdatedAt	-"

MIGRATIONS_TABLE="__EFMigrationsHistory"

log() { printf '%s\n' "$*" >&2; }

# psql を 1 回呼ぶ。出力は TAB 区切り・ヘッダ無し（-A -t -F）。失敗は呼び出し側へ返す。
# AST_PSQL は語分割して使う（`kubectl -n ns exec -i deploy/postgres -- psql -U ai` のような複合コマンドを許す）。
run_sql() {
  local db="$1" sql="$2"
  # stdin は必ず /dev/null にする —— `kubectl exec -i` 越しの psql は呼び出し元ループの stdin を
  # 飲み込み、母集合の残りを黙って読み飛ばす（テーブルが「無かった」ことになる）。
  # shellcheck disable=SC2086
  ${AST_PSQL:-psql} -X -v ON_ERROR_STOP=1 -A -t -F "$TAB" -d "${AST_DB_PREFIX:-}$db" -c "$sql" < /dev/null
}
# AST_DB_PREFIX: 接続先 DB 名の前置（リハーサル用のコピー `cutover_rehearsal_<db>` を同じ manifest で測る）。
# スナップショットには論理名（manifest の db）を書くので、本番と コピーの TSV を compare でそのまま突き合わせられる。

manifest_dbs() {
  printf '%s\n' "$AST_CUTOVER_MANIFEST" | cut -f1 | awk '!seen[$0]++'
}

manifest_rows_for_db() {
  local db="$1"
  printf '%s\n' "$AST_CUTOVER_MANIFEST" | awk -F '\t' -v db="$db" '$1 == db'
}

# DB 側の実在テーブル（public スキーマ・移行履歴を除く）
discover_tables() {
  local db="$1"
  run_sql "$db" "select tablename from pg_tables where schemaname = 'public' and tablename <> '$MIGRATIONS_TABLE' order by 1"
}

# 1 テーブルの計測。出力: count<TAB>min<TAB>max<TAB>pending<TAB>fingerprint
measure_table() {
  local db="$1" table="$2" ts="$3" cond="$4"
  local pending_expr="'-'"
  if [ "$cond" != "-" ]; then
    pending_expr="count(*) filter (where $cond)::text"
  fi
  run_sql "$db" "select count(*)::text,
       coalesce((min(\"$ts\") at time zone 'UTC')::text, '-'),
       coalesce((max(\"$ts\") at time zone 'UTC')::text, '-'),
       $pending_expr,
       coalesce((select md5(string_agg(h, '' order by h)) from (select md5(t::text) as h from \"$table\" t) s), 'empty')
     from \"$table\""
}

measure_migrations() {
  local db="$1"
  run_sql "$db" "select count(*)::text, coalesce(max(\"MigrationId\"), '-') from \"$MIGRATIONS_TABLE\""
}

# snapshot <out.tsv>: 列 = db / table / class / count / min / max / pending / fingerprint
cmd_snapshot() {
  local out="${1:-}"
  if [ -z "$out" ]; then log "usage: snapshot <out.tsv>"; return 2; fi
  local tmp; tmp="$(mktemp)"
  local failures=0 n_tables=0
  printf '# cutover snapshot\tgenerated=%s\tpsql=%s\tdb_prefix=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${AST_PSQL:-psql}" "${AST_DB_PREFIX:-}" > "$tmp"
  printf '#db\ttable\tclass\tcount\tmin\tmax\tpending\tfingerprint\n' >> "$tmp"
  local db
  while IFS= read -r db; do
    local actual expected
    actual="$(discover_tables "$db")" || { log "ERROR: $db: テーブル一覧を取得できない"; failures=$((failures + 1)); continue; }
    expected="$(manifest_rows_for_db "$db" | cut -f2)"
    # DB にあって manifest に無い → 数え落とし（fail-loud）
    local t
    while IFS= read -r t; do
      [ -n "$t" ] || continue
      if ! printf '%s\n' "$expected" | grep -qx -- "$t"; then
        log "ERROR: $db.$t は manifest に無い（保全対象から漏れる。manifest へ追加してから再実行する）"
        failures=$((failures + 1))
      fi
    done <<< "$actual"
    # manifest にあって DB に無い → manifest の腐り
    while IFS= read -r t; do
      [ -n "$t" ] || continue
      if ! printf '%s\n' "$actual" | grep -qx -- "$t"; then
        log "ERROR: $db.$t は manifest にあるが DB に存在しない"
        failures=$((failures + 1))
      fi
    done <<< "$expected"

    local row
    while IFS= read -r row; do
      [ -n "$row" ] || continue
      local table class ts cond
      table="$(printf '%s' "$row" | cut -f2)"
      class="$(printf '%s' "$row" | cut -f3)"
      ts="$(printf '%s' "$row" | cut -f4)"
      cond="$(printf '%s' "$row" | cut -f5)"
      printf '%s\n' "$actual" | grep -qx -- "$table" || continue
      local m
      if ! m="$(measure_table "$db" "$table" "$ts" "$cond")"; then
        log "ERROR: $db.$table の計測に失敗した"; failures=$((failures + 1)); continue
      fi
      printf '%s\t%s\t%s\t%s\n' "$db" "$table" "$class" "$m" >> "$tmp"
      n_tables=$((n_tables + 1))
    done < <(manifest_rows_for_db "$db")

    local mig
    if mig="$(measure_migrations "$db")"; then
      printf '%s\t%s\tmigrations\t%s\t-\t-\t-\t%s\n' "$db" "$MIGRATIONS_TABLE" "$(printf '%s' "$mig" | cut -f1)" "$(printf '%s' "$mig" | cut -f2)" >> "$tmp"
    else
      log "ERROR: $db: $MIGRATIONS_TABLE を読めない"; failures=$((failures + 1))
    fi
  done < <(manifest_dbs)

  if [ "$failures" -ne 0 ]; then
    rm -f "$tmp"
    log "snapshot: 失敗 $failures 件。出力しない（部分的なスナップショットは「全数」と読み違えられる）"
    return 2
  fi
  if [ "$n_tables" -eq 0 ]; then
    rm -f "$tmp"; log "snapshot: 計測できたテーブルが 0 件。出力しない"; return 2
  fi
  mv "$tmp" "$out"
  log "snapshot: $n_tables テーブルを $out へ書いた"
  return 0
}

# compare <before.tsv> <after.tsv>: 純関数（2 つの TSV だけを読む）。
cmd_compare() {
  local before="${1:-}" after="${2:-}"
  if [ -z "$before" ] || [ -z "$after" ] || [ ! -r "$before" ] || [ ! -r "$after" ]; then
    log "usage: compare <before.tsv> <after.tsv>"; return 2
  fi
  awk -F '\t' '
    /^#/ { next }
    NF < 8 { next }
    FNR == NR {
      k = $1 "." $2
      b_class[k] = $3; b_count[k] = $4; b_min[k] = $5; b_max[k] = $6; b_pending[k] = $7; b_fp[k] = $8
      order[++nb] = k
      next
    }
    {
      k = $1 "." $2
      a_seen[k] = 1; a_class[k] = $3; a_count[k] = $4; a_min[k] = $5; a_max[k] = $6; a_pending[k] = $7; a_fp[k] = $8
      if (!(k in b_class)) note("NOTE", k, "after にだけ存在する（新スキーマの新規テーブルなら正常。before の母集合では数えていない）")
    }
    function note(level, key, msg) {
      if (level == "FAIL") fails++
      printf "%s\t%s\t%s\n", level, key, msg
    }
    END {
      for (i = 1; i <= nb; i++) {
        k = order[i]
        if (!(k in a_seen)) { note("FAIL", k, "after に存在しない（テーブルごと欠損）"); continue }
        if (b_class[k] == "migrations") {
          if (a_count[k] + 0 < b_count[k] + 0) note("FAIL", k, "移行履歴が減っている（" b_count[k] " -> " a_count[k] "）")
          else if (a_count[k] + 0 > b_count[k] + 0) note("NOTE", k, "新スキーマ適用: 移行 " b_count[k] " -> " a_count[k] "（最終 " a_fp[k] "）")
          else note("OK", k, "移行 " a_count[k] " 件（最終 " a_fp[k] "）")
          continue
        }
        bad = 0
        if (a_count[k] != b_count[k]) { note("FAIL", k, "件数が違う（" b_count[k] " -> " a_count[k] "）"); bad = 1 }
        if (a_min[k] != b_min[k]) { note("FAIL", k, "時刻列の min が違う（" b_min[k] " -> " a_min[k] "）"); bad = 1 }
        if (a_max[k] != b_max[k]) { note("FAIL", k, "時刻列の max が違う（" b_max[k] " -> " a_max[k] "）"); bad = 1 }
        if (b_pending[k] != "-" && a_pending[k] + 0 < b_pending[k] + 0) { note("FAIL", k, "未確定予約が減っている（" b_pending[k] " -> " a_pending[k] "。NFR-09: 無期限保持・自動削除禁止）"); bad = 1 }
        else if (b_pending[k] != "-" && a_pending[k] != b_pending[k]) { note("FAIL", k, "未確定予約の件数が変わっている（" b_pending[k] " -> " a_pending[k] "。凍結中に状態が動いた）"); bad = 1 }
        if (a_fp[k] != b_fp[k]) { note("FAIL", k, "内容指紋が違う（件数は同じでも行が改変・入れ替えされている）"); bad = 1 }
        if (!bad) note("OK", k, b_class[k] " " b_count[k] " 件" (b_pending[k] != "-" ? "（未確定 " b_pending[k] "）" : ""))
      }
      printf "SUMMARY\tbefore=%d\tfail=%d\n", nb, fails
      exit (fails > 0 ? 1 : 0)
    }
  ' "$before" "$after"
}

cmd_manifest() {
  printf '#db\ttable\tclass\tts_column\tpending_condition\n'
  printf '%s\n' "$AST_CUTOVER_MANIFEST"
}

# controls: 統制状態の引き継ぎ確認と切替前チェックに使う現在値を key<TAB>value で出す（読み取りのみ）。
# 値の意味づけ（TradingDefaults との一致・未約定ゼロ など）は docs/migration/ の移行仕様書が定める。
# 1 項目でも読めなければ exit 2（「読めなかった」を空欄で「無かった」と読ませない）。
cmd_controls() {
  local failures=0
  emit() {
    local key="$1" db="$2" sql="$3" v
    if v="$(run_sql "$db" "$sql")"; then
      printf '%s\t%s\n' "$key" "${v:-<none>}"
    else
      log "ERROR: $key を読めない（$db）"; failures=$((failures + 1))
    fi
  }
  # リスク統制設定（単一行）。JSON は jsonb の正規化表現（キー順序に依存しない）で出す。
  emit risk_settings.version risk_management_svc "select \"Version\"::text from risk_settings"
  emit risk_settings.updated_at risk_management_svc "select (\"UpdatedAt\" at time zone 'UTC')::text from risk_settings"
  emit risk_settings.limits risk_management_svc "select (\"Json\"::jsonb -> 'limits')::text from risk_settings"
  emit risk_settings.guard.enabled_product_types risk_management_svc "select (\"Json\"::jsonb -> 'guard' -> 'enabledProductTypes')::text from risk_settings"
  emit risk_settings.guard.enabled_markets risk_management_svc "select (\"Json\"::jsonb -> 'guard' -> 'enabledMarkets')::text from risk_settings"
  emit risk_settings.guard.banned_symbols risk_management_svc "select string_agg(b ->> 'symbol' || '@' || (b ->> 'market'), ',' order by b ->> 'symbol') from risk_settings, jsonb_array_elements(\"Json\"::jsonb -> 'guard' -> 'bannedSymbols') b"
  emit risk_settings.guard.prevent_same_day_reentry risk_management_svc "select (\"Json\"::jsonb -> 'guard' ->> 'preventSameDayReentry') from risk_settings"
  emit risk_settings.guard.configured_account_type risk_management_svc "select (\"Json\"::jsonb -> 'guard' ->> 'configuredAccountType') from risk_settings"
  emit risk_settings.stage risk_management_svc "select (\"Json\"::jsonb -> 'stage')::text from risk_settings"
  emit risk_settings.broker_provider risk_management_svc "select (\"Json\"::jsonb ->> 'brokerProvider') from risk_settings"
  emit risk_settings.short_sell.limits risk_management_svc "select (\"Json\"::jsonb -> 'shortSell' -> 'limits')::text from risk_settings"
  emit risk_settings.stage1_minimum_trade_count risk_management_svc "select (\"Json\"::jsonb ->> 'stage1MinimumTradeCount') from risk_settings"
  # 停止系 3 統制（kill switch ＞ 日次損失ロックアウト ＞ 一時停止）
  emit kill_switch.engaged risk_management_svc "select \"Engaged\"::text from kill_switch"
  emit pause.paused risk_management_svc "select \"Paused\"::text from pause"
  emit lockout.release_on risk_management_svc "select \"ReleaseOn\"::text from lockout"
  # 段階ゲート: 現在段階は risk_settings.stage、進捗は観測ログの件数（供給元はこれらの表だけ）
  emit stage_transitions.count risk_management_svc "select count(*)::text from stage_transitions"
  emit stage_transitions.last risk_management_svc "select \"FromStage\"::text || '->' || \"ToStage\"::text || ' ' || \"Kind\"::text || ' ' || (\"OccurredAtUtc\" at time zone 'UTC')::text from stage_transitions order by \"Sequence\" desc limit 1"
  emit stage_performance.row risk_management_svc "select \"BacktestPassed\"::text || ' dd_bt=' || \"BacktestMaxDrawdownRatio\"::text || ' dd_obs=' || \"ObservedMaxDrawdownRatio\"::text from stage_performance"
  emit stage1_session_uptime.days risk_management_svc "select count(*)::text from stage1_session_uptime"
  emit stage1_fill_observations.count risk_management_svc "select count(*)::text from stage1_fill_observations"
  emit order_screening_observations.count risk_management_svc "select count(*)::text from order_screening_observations"
  emit settings_change_log.count risk_management_svc "select count(*)::text from settings_change_log"
  # 全体前提条件（FR-17）の版
  emit assumptions.version configuration_svc "select \"Version\"::text from assumptions"
  emit assumptions_change_log.count configuration_svc "select count(*)::text from assumptions_change_log"
  # 切替前チェック: 未約定注文・有効な逆指値・未確定予約（いずれも 0 か、逆指値は「維持」を確認する）
  emit executed_orders.non_terminal order_execution_svc "select count(*)::text from executed_orders where \"Status\" in (0, 1)"
  emit protective_stop_orders.active order_execution_svc "select count(*)::text from protective_stop_orders where \"State\" = 0"
  emit order_dispatch_reservations.reserved order_execution_svc "select count(*)::text from order_dispatch_reservations where \"State\" = 0"
  emit trade_fills.count risk_management_svc "select count(*)::text from trade_fills"
  emit position_drift_state.row risk_management_svc "select (\"UpdatedAt\" at time zone 'UTC')::text from position_drift_state"
  [ "$failures" -eq 0 ] || { log "controls: 失敗 $failures 件"; return 2; }
  return 0
}

main() {
  local cmd="${1:-}"
  shift || true
  case "$cmd" in
    snapshot) cmd_snapshot "$@" ;;
    compare) cmd_compare "$@" ;;
    manifest) cmd_manifest ;;
    controls) cmd_controls ;;
    *) log "usage: $0 {snapshot <out.tsv> | compare <before.tsv> <after.tsv> | manifest | controls}"; return 2 ;;
  esac
}

# テストから関数だけを読み込むための入口（#263 / IADR-0109 の idiom）。
if [ "${AST_CUTOVER_LIB:-}" = "1" ]; then
  return 0 2>/dev/null || exit 0
fi

main "$@"
