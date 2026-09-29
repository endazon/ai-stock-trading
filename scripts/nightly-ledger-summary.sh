#!/usr/bin/env bash
# NFR, #1092: 夜間（任意の時間窓）の判断・審査・発注・約定・S1・ブローカ観測の欠けを、**監査台帳だけから**要約する。
# ログ・メトリクスは Pod の再起動で消える（稼働クラスタに Loki / Prometheus は無い）ため、翌朝の振り返りは台帳を正とする。
#
#   bash scripts/nightly-ledger-summary.sh <from> <to>        # 時刻は ISO 8601 の時差つき（例 2026-09-29T22:30+09:00）
#   bash scripts/nightly-ledger-summary.sh --night <YYYY-MM-DD>  # その日 20:00 JST 〜 翌日 08:00 JST（米国市場の夜を夏冬とも覆う）
#
# 接続は AST_PSQL（psql の呼び出しコマンド。既定 `psql`）で差し替える。ローカル k3s の platform-infra なら:
#   AST_PSQL="kubectl -n platform-infra exec -i deploy/postgres -- psql -U ai"
# DB は audit_svc（監査台帳 audit_events）。
# 窓の検査は GNU date（`date -d`）を使う（Linux・WSL・CI の ubuntu-latest。BSD / macOS の date では動かない）。
# 🔴 **読み取りだけ**を行う。SQL は `BEGIN TRANSACTION READ ONLY` の中で走らせ、最後に ROLLBACK する
#    （書き込み・DDL・一時オブジェクトの作成もしない）。
#
# ■ 何が分かり、何が分からないか（#1092）
#   分かる: 判断（発注意図あり）・判断後の見送り（理由別）・モデル利用不能の見送り・審査・発注前の見送り（理由別）・
#          注文ごとの最新の状態（約定・拒否・取消）・S1 の武装と発動の結果・損切りライン到達・
#          ブローカの建玉観測（10 分ごと）と稼働観測（5 分ごと）の欠け（＝照会できなかった時間帯の推定）。
#   段 2（IADR-0462）で足したもの: 建玉照会・保有照会の失敗の区間（発生源別。PositionQueryStatusChanged）・
#          LLM を呼ぶ前の見送り（理由別。TradeDecisionForgoneBeforeLlm）。段 2 の配備より前の夜は 0 行になる（9 の推定を使う）。
#   分からない（台帳に記録が無い）: 判断中の例外（段 2 でも入れていない。作業仕様書 20260930_1092_ledger-gap-events）。
#
# ■ テスト: scripts/nightly-ledger-summary.test.sh（psql スタブ・実 DB 不要）。AST_NIGHTLY_LIB=1 で source すると
#   関数定義だけを読み込む（scripts/cutover-count-reconcile.sh と同じ idiom）。
set -u

# ISO 8601・時差つき（Z または ±HH:MM）。時差の無い時刻は受け付けない —— DB と端末の時刻帯の食い違いで窓がずれるため。
NIGHTLY_TS_RE='^[0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]+)?)?(Z|[+-][0-9]{2}:?[0-9]{2})$'
# 観測の欠けとみなす間隔（既定は各観測の間隔の 2 倍。建玉 600 秒・稼働 300 秒）。
NIGHTLY_POSITIONS_GAP="${NIGHTLY_POSITIONS_GAP:-20 minutes}"
NIGHTLY_AVAILABILITY_GAP="${NIGHTLY_AVAILABILITY_GAP:-10 minutes}"

nightly_usage() {
  cat >&2 <<'USAGE'
使い方:
  bash scripts/nightly-ledger-summary.sh <from> <to>
  bash scripts/nightly-ledger-summary.sh --night <YYYY-MM-DD>
時刻は時差つきの ISO 8601（例 2026-09-29T22:30+09:00）。窓は [from, to) の半開区間。
接続: AST_PSQL="kubectl -n platform-infra exec -i deploy/postgres -- psql -U ai"
USAGE
}

# nightly_window <args...>: 窓を検査して「from<TAB>to」を 1 行出す。誤りは exit 2 相当（戻り値 2）。
nightly_window() {
  local from to day next
  if [ "${1:-}" = "--night" ]; then
    day="${2:-}"
    if [ $# -ne 2 ] || [[ ! "$day" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] || ! date -d "$day" +%F >/dev/null 2>&1; then
      echo "ERROR: --night には日付（YYYY-MM-DD）を 1 つ与えてください: '${day}'" >&2
      return 2
    fi
    next="$(date -d "$day +1 day" +%F)"
    printf '%s\t%s\n' "${day}T20:00+09:00" "${next}T08:00+09:00"
    return 0
  fi
  if [ $# -ne 2 ]; then
    return 2
  fi
  from="$1"; to="$2"
  for ts in "$from" "$to"; do
    if [[ ! "$ts" =~ $NIGHTLY_TS_RE ]]; then
      echo "ERROR: 時刻は時差つきの ISO 8601 で与えてください（例 2026-09-29T22:30+09:00）: '${ts}'" >&2
      return 2
    fi
  done
  if [ "$(date -d "$from" +%s 2>/dev/null || echo x)" = x ] || [ "$(date -d "$to" +%s 2>/dev/null || echo x)" = x ]; then
    echo "ERROR: 時刻を解釈できません: '${from}' / '${to}'" >&2
    return 2
  fi
  if [ "$(date -d "$from" +%s)" -ge "$(date -d "$to" +%s)" ]; then
    echo "ERROR: from は to より前にしてください（窓は [from, to)）: '${from}' / '${to}'" >&2
    return 2
  fi
  printf '%s\t%s\n' "$from" "$to"
}

# nightly_sql: 要約の SQL（psql 変数 from / to / positions_gap / availability_gap を使う）。
nightly_sql() {
  cat <<'SQL'
\set ON_ERROR_STOP 1
\pset footer off
BEGIN TRANSACTION READ ONLY;
SET LOCAL TIME ZONE 'Asia/Tokyo';

\echo '== 1. 窓（JST）と、種類別の件数'
SELECT :'from'::timestamptz AS from_jst, :'to'::timestamptz AS to_jst;
SELECT "EventType" AS event_type, count(*) AS n
FROM audit_events
WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
GROUP BY 1 ORDER BY 2 DESC, 1;

\echo '== 2. 判断（発注意図あり: TradeDecisionMade）'
SELECT "Detail"->'Intent'->>'Side' AS side, "Detail"->'Intent'->>'PositionEffect' AS effect,
       count(*) AS n, string_agg(DISTINCT "Symbol", ',' ORDER BY "Symbol") AS symbols
FROM audit_events
WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
  AND "EventType" = 'TradeDecisionMade'
GROUP BY 1, 2 ORDER BY 1, 2;

\echo '== 3. 判断後の見送り（TradeDecisionHeld・理由別）'
SELECT "Detail"->>'Reason' AS reason, count(*) AS n, string_agg(DISTINCT "Symbol", ',' ORDER BY "Symbol") AS symbols
FROM audit_events
WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
  AND "EventType" = 'TradeDecisionHeld'
GROUP BY 1 ORDER BY 2 DESC, 1;

\echo '== 4. モデル利用不能による見送り（TradeDecisionSkipped）'
SELECT "Detail"->>'Purpose' AS purpose, "Detail"->>'Reason' AS reason, count(*) AS n
FROM audit_events
WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
  AND "EventType" = 'TradeDecisionSkipped'
GROUP BY 1, 2 ORDER BY 3 DESC, 1, 2;

\echo '== 5. 審査（承認の件数と、拒否の理由別の件数。1 件の拒否が複数の理由を持つことがある）'
SELECT 'OrderApproved' AS kind, '-' AS reason, count(*) AS n
FROM audit_events
WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
  AND "EventType" = 'OrderApproved'
UNION ALL
SELECT 'OrderRejected', r.reason, count(*)
FROM audit_events e, jsonb_array_elements_text(e."Detail"->'Reasons') AS r(reason)
WHERE e."OccurredAt" >= :'from'::timestamptz AND e."OccurredAt" < :'to'::timestamptz
  AND e."EventType" = 'OrderRejected'
GROUP BY r.reason
ORDER BY 1, 3 DESC;

\echo '== 6. 発注前の見送り（OrderDispatchForgone・理由別）'
SELECT "Detail"->>'Reason' AS reason, count(*) AS n, string_agg(DISTINCT "Symbol", ',' ORDER BY "Symbol") AS symbols
FROM audit_events
WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
  AND "EventType" = 'OrderDispatchForgone'
GROUP BY 1 ORDER BY 2 DESC, 1;

\echo '== 7. 注文（注文ごとの最新の状態。OrderExecuted は同じ注文で複数行になり得る）'
WITH last AS (
  SELECT DISTINCT ON ("Detail"->>'OrderId')
         "Detail"->>'OrderId' AS order_id, "CorrelationId" AS decision_id, "Detail"->>'Status' AS status,
         ("Detail"->>'FilledQuantity')::int AS filled, ("Detail"->>'AveragePrice')::numeric AS avg_price,
         "OccurredAt" AS at
  FROM audit_events
  WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
    AND "EventType" = 'OrderExecuted'
  ORDER BY "Detail"->>'OrderId', "OccurredAt" DESC, "RecordedAt" DESC
)
SELECT status, count(*) AS n FROM last GROUP BY 1 ORDER BY 2 DESC, 1;

\echo '-- 7a. 注文の一覧（銘柄は判断 → S1 → 承認の順に引く。窓の外の記録も引く）'
WITH last AS (
  SELECT DISTINCT ON ("Detail"->>'OrderId')
         "Detail"->>'OrderId' AS order_id, "CorrelationId" AS decision_id, "Detail"->>'Status' AS status,
         ("Detail"->>'FilledQuantity')::int AS filled, ("Detail"->>'AveragePrice')::numeric AS avg_price,
         "OccurredAt" AS at
  FROM audit_events
  WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
    AND "EventType" = 'OrderExecuted'
  ORDER BY "Detail"->>'OrderId', "OccurredAt" DESC, "RecordedAt" DESC
)
SELECT l.at, COALESCE(d."Symbol", s."Symbol", a."Symbol") AS symbol,
       COALESCE(d."Detail"->'Intent'->>'Side', s."Detail"->'CloseIntent'->>'Side', a."Detail"->'Intent'->>'Side') AS side,
       COALESCE(d."Detail"->'Intent'->>'PositionEffect', s."Detail"->'CloseIntent'->>'PositionEffect',
                a."Detail"->'Intent'->>'PositionEffect') AS effect,
       CASE WHEN d."Id" IS NOT NULL THEN 'decision' WHEN s."Id" IS NOT NULL THEN 'S1'
            WHEN a."Id" IS NOT NULL THEN 'approved' ELSE '?' END AS origin,
       l.status, l.filled, l.avg_price, l.order_id
FROM last l
LEFT JOIN LATERAL (
  SELECT "Id", "Symbol", "Detail" FROM audit_events
  WHERE "CorrelationId" = l.decision_id AND "EventType" = 'TradeDecisionMade' LIMIT 1
) d ON true
-- S1 の決済は同じ CloseDecisionId の記録が複数あり得る（CloseRejected / CloseUnfilled は CloseIntent が null）。
-- 決済の意図を持つ行を先に採る。
LEFT JOIN LATERAL (
  SELECT "Id", "Symbol", "Detail" FROM audit_events
  WHERE "EventType" = 'SoftwareStopExecuted' AND "Detail"->>'CloseDecisionId' = l.decision_id::text
  ORDER BY jsonb_typeof("Detail"->'CloseIntent') = 'object' DESC, "OccurredAt" DESC
  LIMIT 1
) s ON true
-- 判断を経ない注文（利用者の手仕舞い・維持証拠金の自動縮小・照合後の保護レグ）は承認の記録から引く。
LEFT JOIN LATERAL (
  SELECT "Id", "Symbol", "Detail" FROM audit_events
  WHERE "CorrelationId" = l.decision_id AND "EventType" = 'OrderApproved' LIMIT 1
) a ON true
ORDER BY l.at;

\echo '== 8. S1（武装・損切りライン到達・発動の結果）'
SELECT "EventType" AS event_type, COALESCE("Detail"->>'Outcome', '-') AS outcome, count(*) AS n,
       string_agg(DISTINCT "Symbol", ',' ORDER BY "Symbol") AS symbols
FROM audit_events
WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
  AND "EventType" IN ('SoftwareStopArmed', 'StopLossTriggered', 'SoftwareStopExecuted')
GROUP BY 1, 2 ORDER BY 1, 3 DESC;

\echo '== 9. ブローカの観測の欠け（照会できなかった時間帯の推定。窓の両端も境界として数える）'
-- 場中など窓の途中で走らせたときは、終端を現在時刻で切る（まだ来ていない時間を欠けとして出さない）。
SELECT '注意: 窓の終端が現在時刻より後。欠けは現在時刻（' || to_char(now(), 'YYYY-MM-DD HH24:MI') || ' JST）までで数える' AS note
WHERE :'to'::timestamptz > now();
WITH obs AS (
  SELECT "EventType" AS event_type, "OccurredAt" AS at
  FROM audit_events
  WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
    AND "EventType" IN ('BrokerPositionsObserved', 'BrokerAvailabilityObserved')
), bounded AS (
  SELECT event_type, at FROM obs
  UNION ALL SELECT t, :'from'::timestamptz FROM (VALUES ('BrokerPositionsObserved'), ('BrokerAvailabilityObserved')) v(t)
  UNION ALL SELECT t, GREATEST(:'from'::timestamptz, LEAST(:'to'::timestamptz, now()))
    FROM (VALUES ('BrokerPositionsObserved'), ('BrokerAvailabilityObserved')) v(t)
), gaps AS (
  SELECT event_type, lag(at) OVER (PARTITION BY event_type ORDER BY at) AS gap_from, at AS gap_to
  FROM bounded
)
SELECT event_type, gap_from, gap_to, gap_to - gap_from AS gap
FROM gaps
WHERE gap_from IS NOT NULL
  AND gap_to - gap_from > CASE event_type
        WHEN 'BrokerPositionsObserved' THEN :'positions_gap'::interval
        ELSE :'availability_gap'::interval END
ORDER BY event_type, gap_from;

\echo '== 10. 建玉照会・保有照会の失敗の区間（PositionQueryStatusChanged・発生源別。窓の前から続く失敗も出す）'
-- 状態が変わったときだけ記録される（成功⇄失敗）。区間の終わりは同じ発生源の次の記録で、前の状態が Unknown の記録は
-- 再起動の後の最初の観測である（回復の時刻は「再起動の後の最初の成功」までしか分からない）。
WITH ev AS (
  SELECT "Detail"->>'Source' AS source, "Detail"->>'Status' AS status, "Detail"->>'PreviousStatus' AS prev,
         "Detail"->>'FailureKind' AS kind, ("Detail"->>'FailedQueries')::int AS failed,
         "OccurredAt" AS at, "RecordedAt" AS rec
  FROM audit_events
  WHERE "EventType" = 'PositionQueryStatusChanged' AND "OccurredAt" < :'to'::timestamptz
), before AS (
  SELECT DISTINCT ON (source) * FROM ev WHERE at < :'from'::timestamptz ORDER BY source, at DESC, rec DESC
), seq AS (
  SELECT x.*, lead(at) OVER w AS next_at, lead(status) OVER w AS next_status, lead(prev) OVER w AS next_prev,
         lead(failed) OVER w AS next_failed
  FROM (SELECT * FROM before UNION ALL SELECT * FROM ev WHERE at >= :'from'::timestamptz) x
  WINDOW w AS (PARTITION BY source ORDER BY at, rec)
)
SELECT source, GREATEST(at, :'from'::timestamptz) AS failing_from,
       COALESCE(next_at, GREATEST(at, :'from'::timestamptz, LEAST(:'to'::timestamptz, now()))) AS failing_to,
       CASE WHEN next_at IS NULL THEN '窓の終端まで続いた（回復の記録なし）'
            WHEN next_prev = 'Unknown' AND next_status = 'Healthy' THEN '再起動の後に成功（回復の時刻は不明）'
            WHEN next_prev = 'Unknown' THEN '再起動の後も失敗'
            ELSE '回復（失敗 ' || next_failed || ' 回）' END AS ended,
       COALESCE(kind, '-') AS kind,
       CASE WHEN at < :'from'::timestamptz THEN '窓の前から' ELSE '' END AS note
FROM seq
WHERE status = 'Failing'
ORDER BY source, at;

\echo '-- 10a. 状態の変化の件数（発生源 × 前→後。Unknown→ は再起動の後の最初の観測）'
SELECT "Detail"->>'Source' AS source, ("Detail"->>'PreviousStatus') || '→' || ("Detail"->>'Status') AS change, count(*) AS n
FROM audit_events
WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
  AND "EventType" = 'PositionQueryStatusChanged'
GROUP BY 1, 2 ORDER BY 1, 2;

\echo '== 11. LLM を呼ぶ前の見送り（TradeDecisionForgoneBeforeLlm・理由 × 起点別）'
SELECT "Detail"->>'Reason' AS reason, COALESCE("Detail"->>'CycleTrigger', '-') AS cycle_trigger, count(*) AS n,
       string_agg(DISTINCT "Symbol", ',' ORDER BY "Symbol") AS symbols
FROM audit_events
WHERE "OccurredAt" >= :'from'::timestamptz AND "OccurredAt" < :'to'::timestamptz
  AND "EventType" = 'TradeDecisionForgoneBeforeLlm'
GROUP BY 1, 2 ORDER BY 3 DESC, 1, 2;

ROLLBACK;
SQL
}

nightly_main() {
  local window from to
  if ! window="$(nightly_window "$@")"; then
    nightly_usage
    return 2
  fi
  from="${window%%$'\t'*}"; to="${window#*$'\t'}"
  # shellcheck disable=SC2086  # AST_PSQL は意図的に単語分割する（kubectl exec … psql の形を与えるため）
  nightly_sql | ${AST_PSQL:-psql} -d audit_svc -X -q \
    -v from="$from" -v to="$to" \
    -v positions_gap="$NIGHTLY_POSITIONS_GAP" -v availability_gap="$NIGHTLY_AVAILABILITY_GAP" \
    -f -
}

if [ "${AST_NIGHTLY_LIB:-}" != "1" ]; then
  nightly_main "$@"
  exit $?
fi
