#!/usr/bin/env bash
# NFR, #1092: scripts/nightly-ledger-summary.sh の SQL を**実 PostgreSQL** で流して固定する。
#
#   bash scripts/nightly-ledger-summary.pg.test.sh
#
# psql スタブの試験（nightly-ledger-summary.test.sh）は、窓の端（`<` と `<=`）や「注文ごとの最新の 1 行」を
# 検出できない（SQL を実行しないため。PR の監査で変異が残った）。ここでは一時クラスタに、移行と同じ形の
# audit_events を作り、境界・同順位・帰属の取り違えを狙った行を入れて、出力を突き合わせる。
#
# 前提: PostgreSQL のサーバの実行ファイル（/usr/lib/postgresql/<版>/bin/initdb）。GitHub の ubuntu-latest には在る。
#   無ければ、CI（CI=true）では失敗し、手元では飛ばす。root で動かすときは postgres 利用者でサーバを立てる
#   （initdb は root を拒む）。一時クラスタは unix ソケットだけで待ち受け、終了時に止めて消す。
set -u

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$ROOT_DIR/scripts/nightly-ledger-summary.sh"

PG_BIN="$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -n 1)"
if [ -z "$PG_BIN" ] || [ ! -x "$PG_BIN/initdb" ]; then
  if [ "${CI:-}" = "true" ]; then
    echo "✗ PostgreSQL のサーバの実行ファイルが見つかりません（CI では飛ばさない）" >&2
    exit 1
  fi
  echo "- skip: PostgreSQL のサーバの実行ファイルが無い（手元のみ飛ばす）"
  exit 0
fi

DIR="$(mktemp -d /tmp/nightly-pg.XXXXXX)"
PORT=$(( 54000 + RANDOM % 900 ))
as_pg() {
  if [ "$(id -u)" = "0" ]; then su postgres -s /bin/bash -c "$1"; else bash -c "$1"; fi
}
cleanup() {
  as_pg "'$PG_BIN/pg_ctl' -D '$DIR/data' stop -m fast" >/dev/null 2>&1 || true
  rm -rf "$DIR"
}
trap cleanup EXIT
[ "$(id -u)" = "0" ] && chown postgres "$DIR"

as_pg "'$PG_BIN/initdb' -D '$DIR/data' -U ai --auth=trust -E UTF8" >/dev/null || { echo "✗ initdb に失敗" >&2; exit 1; }
as_pg "'$PG_BIN/pg_ctl' -D '$DIR/data' -o \"-k '$DIR' -p $PORT -c listen_addresses=''\" -l '$DIR/log' -w start" >/dev/null \
  || { echo "✗ サーバの起動に失敗" >&2; cat "$DIR/log" >&2; exit 1; }
PSQL="psql -h $DIR -p $PORT -U ai"
$PSQL -d postgres -q -c 'CREATE DATABASE audit_svc' || exit 1

# 移行（20260710095747_InitialCreate）と同じ形の表。
$PSQL -d audit_svc -q -v ON_ERROR_STOP=1 -o /dev/null <<'SQL' || exit 1
CREATE TABLE audit_events ("Id" uuid PRIMARY KEY, "EventType" varchar(64) NOT NULL, "CorrelationId" uuid NOT NULL,
  "Symbol" varchar(32), "Summary" varchar(512) NOT NULL, "Detail" jsonb NOT NULL,
  "OccurredAt" timestamptz NOT NULL, "RecordedAt" timestamptz NOT NULL);
CREATE FUNCTION ev(t text, c uuid, sym text, d jsonb, at timestamptz, rec timestamptz DEFAULT NULL) RETURNS void AS $$
  INSERT INTO audit_events VALUES (gen_random_uuid(), t, c, sym, 's', d, at, COALESCE(rec, at));
$$ LANGUAGE sql;
-- 窓は 2026-09-29T20:00+09:00 〜 2026-09-30T08:00+09:00（--night 2026-09-29）。
-- 判断: 窓の頭ちょうど（含む）・窓の尻ちょうど（含まない）・窓の前（含まない）
SELECT ev('TradeDecisionMade', '10000000-0000-0000-0000-000000000001', 'NVDA',
  '{"Intent":{"Side":"Buy","PositionEffect":"Open"}}', '2026-09-29T20:00:00+09');
SELECT ev('TradeDecisionMade', '10000000-0000-0000-0000-000000000002', 'TSLA',
  '{"Intent":{"Side":"Buy","PositionEffect":"Open"}}', '2026-09-30T08:00:00+09');
SELECT ev('TradeDecisionMade', '10000000-0000-0000-0000-000000000003', 'AAPL',
  '{"Intent":{"Side":"Sell","PositionEffect":"Close"}}', '2026-09-29T19:00:00+09');
-- 注文 O1: 受付 → 約定（最新は Filled）。O2: 同じ時刻で RecordedAt の遅い方が PartiallyFilled。
SELECT ev('OrderExecuted', '10000000-0000-0000-0000-000000000001', NULL,
  '{"OrderId":"O1","Status":"Accepted","FilledQuantity":0,"AveragePrice":0}', '2026-09-30T02:00:00+09');
SELECT ev('OrderExecuted', '10000000-0000-0000-0000-000000000001', NULL,
  '{"OrderId":"O1","Status":"Filled","FilledQuantity":1049,"AveragePrice":230.82}', '2026-09-30T02:01:00+09');
SELECT ev('OrderExecuted', '10000000-0000-0000-0000-000000000003', NULL,
  '{"OrderId":"O2","Status":"Accepted","FilledQuantity":0,"AveragePrice":0}', '2026-09-29T22:40:00+09', '2026-09-29T22:40:00+09');
SELECT ev('OrderExecuted', '10000000-0000-0000-0000-000000000003', NULL,
  '{"OrderId":"O2","Status":"PartiallyFilled","FilledQuantity":5,"AveragePrice":180}', '2026-09-29T22:40:00+09', '2026-09-29T22:41:00+09');
-- 窓の尻ちょうどの約定（含まない）
SELECT ev('OrderExecuted', '10000000-0000-0000-0000-000000000002', NULL,
  '{"OrderId":"O5","Status":"Filled","FilledQuantity":1,"AveragePrice":1}', '2026-09-30T08:00:00+09');
-- S1 の決済 O3: 同じ CloseDecisionId に CloseRejected（CloseIntent は null）と ClosePlaced がある。
SELECT ev('SoftwareStopExecuted', '10000000-0000-0000-0000-000000000001', 'NVDA',
  '{"Outcome":"ClosePlaced","CloseDecisionId":"20000000-0000-0000-0000-000000000003","CloseIntent":{"Side":"Sell","PositionEffect":"Close"}}',
  '2026-09-30T04:00:00+09');
SELECT ev('SoftwareStopExecuted', '10000000-0000-0000-0000-000000000001', 'NVDA',
  '{"Outcome":"CloseRejected","CloseDecisionId":"20000000-0000-0000-0000-000000000003","CloseIntent":null}',
  '2026-09-30T04:05:00+09');
SELECT ev('OrderExecuted', '20000000-0000-0000-0000-000000000003', NULL,
  '{"OrderId":"O3","Status":"Filled","FilledQuantity":1049,"AveragePrice":226.40}', '2026-09-30T04:00:30+09');
-- 判断を経ない注文 O4（利用者の手仕舞い）: 承認の記録だけがある。
SELECT ev('OrderApproved', '30000000-0000-0000-0000-000000000004', 'MSFT',
  '{"Intent":{"Side":"Sell","PositionEffect":"Close"}}', '2026-09-30T03:00:00+09');
SELECT ev('OrderExecuted', '30000000-0000-0000-0000-000000000004', NULL,
  '{"OrderId":"O4","Status":"Filled","FilledQuantity":3,"AveragePrice":410}', '2026-09-30T03:00:10+09');
-- 建玉の観測: 20:00 から 10 分ごと。21:00→21:20 はちょうど 20 分（出さない）、22:00→22:30 は 30 分（出す）。
SELECT ev('BrokerPositionsObserved', gen_random_uuid(), NULL, '{}', t)
FROM generate_series('2026-09-29T20:00+09'::timestamptz, '2026-09-30T08:00+09', '10 minutes') t
WHERE t NOT IN ('2026-09-29T21:10+09', '2026-09-29T22:10+09', '2026-09-29T22:20+09')
  AND t < '2026-09-30T08:00+09';
SELECT ev('BrokerAvailabilityObserved', gen_random_uuid(), NULL, '{}', t)
FROM generate_series('2026-09-29T20:00+09'::timestamptz, '2026-09-30T07:55+09', '5 minutes') t;
SQL

OUT="$(AST_PSQL="$PSQL -A -F|" bash "$SCRIPT" --night 2026-09-29 2>&1)"
rc=$?

pass=0
fail=0
has() {
  if grep -qxF -- "$2" <<<"$OUT"; then pass=$((pass + 1)); printf '  ok  %s\n' "$1"
  else fail=$((fail + 1)); printf '  NG  %s（行 %s が無い）\n' "$1" "$2" >&2; fi
}
hasnt() {
  if grep -qF -- "$2" <<<"$OUT"; then fail=$((fail + 1)); printf '  NG  %s（%s が在る）\n' "$1" "$2" >&2
  else pass=$((pass + 1)); printf '  ok  %s\n' "$1"; fi
}

if [ "$rc" -eq 0 ]; then pass=$((pass + 1)); echo '  ok  実 PostgreSQL で exit 0'
else fail=$((fail + 1)); echo "  NG  exit $rc" >&2; echo "$OUT" >&2; fi

has '窓は --night の 20:00〜08:00 JST' '2026-09-29 20:00:00+09|2026-09-30 08:00:00+09'
has '窓の頭ちょうどの判断は含み、尻ちょうど・窓の前は含まない' 'TradeDecisionMade|1'
has '判断の内訳（窓の中の 1 件だけ）' 'Buy|Open|1|NVDA'
has '窓の尻ちょうどの約定は数えない（O1・O2・O3・O4 の 4 注文・7 行のうち窓の中は 6 行）' 'OrderExecuted|6'
has '注文ごとの最新の状態: 約定 3 件（O1・O3・O4）' 'Filled|3'
has '同じ時刻の行は記録の遅い方を最新とする（O2 は PartiallyFilled）' 'PartiallyFilled|1'
hasnt '受付の行を最新として数えない' 'Accepted|'
has '窓の外の判断から銘柄を引く（O2 は AAPL）' '2026-09-29 22:40:00+09|AAPL|Sell|Close|decision|PartiallyFilled|5|180|O2'
has 'S1 の決済は決済の意図を持つ行から引く（CloseRejected の null を採らない）' \
  '2026-09-30 04:00:30+09|NVDA|Sell|Close|S1|Filled|1049|226.40|O3'
has '判断を経ない注文は承認の記録から引く' '2026-09-30 03:00:10+09|MSFT|Sell|Close|approved|Filled|3|410|O4'
hasnt '帰属の分からない注文が無い' '|?|'
has 'S1 の結果は理由別に数える' 'SoftwareStopExecuted|ClosePlaced|1|NVDA'
has '観測の欠け: 30 分は出す' 'BrokerPositionsObserved|2026-09-29 22:00:00+09|2026-09-29 22:30:00+09|00:30:00'
hasnt '観測の欠け: ちょうど 20 分は出さない（しきい値は「超える」）' '2026-09-29 21:00:00+09|2026-09-29 21:20:00+09'

# 窓の途中で走らせる（場中の確かめ）: 終端は現在時刻で切り、まだ来ていない時間を欠けとして出さない。
# 観測は 5 分ごとに現在時刻の 30 分前まで。欠けは「最後の観測 → 現在時刻」の約 30 分であり、「→ 窓の終端」の約 10 時間ではない。
MID_FROM="$(date -u -d '-2 hours' +%Y-%m-%dT%H:%M:00+00:00)"
MID_TO="$(date -u -d '+10 hours' +%Y-%m-%dT%H:%M:00+00:00)"
$PSQL -d audit_svc -q -v ON_ERROR_STOP=1 -o /dev/null -c "DELETE FROM audit_events WHERE \"EventType\" = 'BrokerAvailabilityObserved';" \
  -c "SELECT ev('BrokerAvailabilityObserved', gen_random_uuid(), NULL, '{}', t) FROM generate_series('$MID_FROM'::timestamptz, now() - interval '30 minutes', '5 minutes') t;" || exit 1
OUT="$(AST_PSQL="$PSQL -A -F|" bash "$SCRIPT" "$MID_FROM" "$MID_TO" 2>&1)"
rc=$?
if [ "$rc" -eq 0 ]; then pass=$((pass + 1)); echo '  ok  窓の途中の実行も exit 0'
else fail=$((fail + 1)); echo "  NG  窓の途中の実行が exit $rc" >&2; echo "$OUT" >&2; fi
if grep -q '^注意: 窓の終端が現在時刻より後' <<<"$OUT"; then pass=$((pass + 1)); echo '  ok  窓の途中なら注意を 1 行出す'
else fail=$((fail + 1)); echo '  NG  窓の途中なのに注意が無い' >&2; fi
LAST_GAP="$(grep '^BrokerAvailabilityObserved|' <<<"$OUT" | tail -1)"
case "$LAST_GAP" in
  *'|00:3'[0-9]':'*|*'|00:29:'*) pass=$((pass + 1)); echo "  ok  最後の欠けは現在時刻まで（${LAST_GAP##*|}）" ;;
  *) fail=$((fail + 1)); echo "  NG  最後の欠けが現在時刻で切られていない: ${LAST_GAP:-（行なし）}" >&2 ;;
esac
OUT_DONE="$(AST_PSQL="$PSQL -A -F|" bash "$SCRIPT" --night 2026-09-20 2>&1)"
if grep -q '^注意: 窓の終端が現在時刻より後' <<<"$OUT_DONE"; then fail=$((fail + 1)); echo '  NG  過ぎた窓なのに注意を出す' >&2
else pass=$((pass + 1)); echo '  ok  過ぎた窓では注意を出さない'; fi

echo
if [ "$fail" -ne 0 ]; then
  echo "✗ ${fail} failed / ${pass} passed" >&2
  exit 1
fi
echo "✓ ${pass} tests passed"
