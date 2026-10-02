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
$PSQL -d postgres -q -c 'CREATE DATABASE audit_svc' -c 'CREATE DATABASE cost_control_svc' -c 'CREATE DATABASE configuration_svc' || exit 1

# #1140, IADR-0478 決定 2: §13 が読む 2 つの表（移行 20260710160156_InitialCreate / 20260710105651_InitialCreate と同じ形）。
# 窓（--night 2026-09-29）の終端は 2026-09-29T23:00Z ＝ UTC の 2026-09。上限は前提条件の JSON（Web 既定＝camelCase）の costLimits.llm。
$PSQL -d cost_control_svc -q -v ON_ERROR_STOP=1 -o /dev/null <<'SQL' || exit 1
CREATE TABLE cost_entries ("Id" uuid PRIMARY KEY, "Month" varchar(7) NOT NULL, "Category" integer NOT NULL,
  "Amount" numeric NOT NULL, "RecordedAt" timestamptz NOT NULL);
-- 上限の対象（Llm=0）: 月の頭と窓の中の 2 件（1,000 + 2,000）を数える。窓の終端ちょうど・前の月は数えない。
INSERT INTO cost_entries VALUES (gen_random_uuid(), '2026-09', 0, 1000, '2026-09-01T00:00:00Z');
INSERT INTO cost_entries VALUES (gen_random_uuid(), '2026-09', 0, 2000, '2026-09-29T22:00:00Z');
INSERT INTO cost_entries VALUES (gen_random_uuid(), '2026-09', 0, 500, '2026-09-29T23:00:00Z');
INSERT INTO cost_entries VALUES (gen_random_uuid(), '2026-08', 0, 9999, '2026-08-31T23:59:00Z');
-- 対象外（LlmUncapped=3）は別の列。インフラ（Infrastructure=1）は数えない。
INSERT INTO cost_entries VALUES (gen_random_uuid(), '2026-09', 3, 700, '2026-09-15T00:00:00Z');
INSERT INTO cost_entries VALUES (gen_random_uuid(), '2026-09', 1, 4000, '2026-09-15T00:00:00Z');
SQL
$PSQL -d configuration_svc -q -v ON_ERROR_STOP=1 -o /dev/null <<'SQL' || exit 1
CREATE TABLE assumptions ("Id" integer PRIMARY KEY, "Json" jsonb NOT NULL, "Version" integer NOT NULL, "UpdatedAt" timestamptz NOT NULL);
INSERT INTO assumptions VALUES (1, '{"costLimits":{"total":20000,"llm":12000,"infrastructure":5000,"data":0}}', 3, now());
SQL

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
-- T-10-1775, #1092 段 2, IADR-0462: 照会の状態の変化。
-- ガード: 窓の前（19:00）に失敗が始まり、20:30 に回復（150 回）。01:00 に失敗、01:20 に再起動の後の最初の成功。
-- スナップショット: 03:00 に失敗し、窓の終端まで回復しない。窓の尻ちょうど（08:00）の記録は数えない。
-- 保有照会: 窓の前に成功（区間を出さない）。
SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL,
  '{"Source":"ProtectiveStopGuard","Status":"Failing","PreviousStatus":"Healthy","FailureKind":null,"FailedQueries":1}',
  '2026-09-29T19:00:00+09');
SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL,
  '{"Source":"ProtectiveStopGuard","Status":"Healthy","PreviousStatus":"Failing","FailureKind":null,"FailedQueries":150}',
  '2026-09-29T20:30:00+09');
SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL,
  '{"Source":"ProtectiveStopGuard","Status":"Failing","PreviousStatus":"Healthy","FailureKind":"Transient","FailedQueries":1}',
  '2026-09-30T01:00:00+09');
SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL,
  '{"Source":"ProtectiveStopGuard","Status":"Healthy","PreviousStatus":"Unknown","FailureKind":null,"FailedQueries":0}',
  '2026-09-30T01:20:00+09');
SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL,
  '{"Source":"BrokerPositionSnapshot","Status":"Failing","PreviousStatus":"Healthy","FailureKind":null,"FailedQueries":1}',
  '2026-09-30T03:00:00+09');
SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL,
  '{"Source":"BrokerPositionSnapshot","Status":"Healthy","PreviousStatus":"Failing","FailureKind":null,"FailedQueries":30}',
  '2026-09-30T08:00:00+09');
SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL,
  '{"Source":"TradeDecisionHoldings","Status":"Healthy","PreviousStatus":"Unknown","FailureKind":null,"FailedQueries":0}',
  '2026-09-29T18:00:00+09');
-- 稼働 probe: 窓の前（19:00）は成功、窓の頭ちょうど（20:00）に失敗、20:10 に回復（60 回）。PR #1110 の監査 N4(a):
-- 窓の頭ちょうどの記録は「窓の中」の側だけで数える（窓の前の最後の記録として二重に拾わない）。
SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL,
  '{"Source":"BrokerAvailabilityProbe","Status":"Healthy","PreviousStatus":"Unknown","FailureKind":null,"FailedQueries":0}',
  '2026-09-29T19:00:00+09');
SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL,
  '{"Source":"BrokerAvailabilityProbe","Status":"Failing","PreviousStatus":"Healthy","FailureKind":"Other","FailedQueries":1}',
  '2026-09-29T20:00:00+09');
SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL,
  '{"Source":"BrokerAvailabilityProbe","Status":"Healthy","PreviousStatus":"Failing","FailureKind":null,"FailedQueries":60}',
  '2026-09-29T20:10:00+09');
-- #1140: LLM の費用（窓の中の 3 件・窓の尻ちょうどの 1 件は数えない。用途の無い従来の形は (不明)）。
SELECT ev('LlmCostIncurred', gen_random_uuid(), NULL,
  '{"Amount":10.25,"At":"2026-09-29T12:00:00Z","Purpose":"trade-decision","Model":"claude-sonnet-5"}', '2026-09-29T21:00:00+09');
SELECT ev('LlmCostIncurred', gen_random_uuid(), NULL,
  '{"Amount":20.5,"At":"2026-09-29T13:00:00Z","Purpose":"trade-decision","Model":"claude-sonnet-5"}', '2026-09-29T22:00:00+09');
SELECT ev('LlmCostIncurred', gen_random_uuid(), NULL,
  '{"Amount":3,"At":"2026-09-29T14:00:00Z","Purpose":null,"Model":null}', '2026-09-29T23:00:00+09');
SELECT ev('LlmCostIncurred', gen_random_uuid(), NULL,
  '{"Amount":99,"At":"2026-09-29T23:00:00Z","Purpose":"trade-decision","Model":"claude-sonnet-5"}', '2026-09-30T08:00:00+09');
-- LLM を呼ぶ前の見送り（窓の尻ちょうどは数えない）。
SELECT ev('TradeDecisionForgoneBeforeLlm', gen_random_uuid(), 'NVDA',
  '{"Reason":"DailyPolicyUnconfirmed","CycleTrigger":"scheduled"}', '2026-09-29T22:30:00+09');
SELECT ev('TradeDecisionForgoneBeforeLlm', gen_random_uuid(), 'TSLA',
  '{"Reason":"DailyPolicyUnconfirmed","CycleTrigger":"scheduled"}', '2026-09-29T22:30:00+09');
SELECT ev('TradeDecisionForgoneBeforeLlm', gen_random_uuid(), 'AAPL',
  '{"Reason":"CurrentPriceUnavailable","CycleTrigger":"price-movement"}', '2026-09-30T02:00:00+09');
SELECT ev('TradeDecisionForgoneBeforeLlm', gen_random_uuid(), 'AAPL',
  '{"Reason":"FxRateUnresolved","CycleTrigger":"scheduled"}', '2026-09-30T08:00:00+09');
SELECT ev('TradeDecisionForgoneBeforeLlm', gen_random_uuid(), 'META',
  '{"Reason":"EntryBlockedByRiskControls","CycleTrigger":"scheduled"}', '2026-09-29T23:00:00+09');
SELECT ev('TradeDecisionForgoneBeforeLlm', gen_random_uuid(), 'AMZN',
  '{"Reason":"EntryBlockedByRiskControls","CycleTrigger":"scheduled"}', '2026-09-30T01:00:00+09');
-- T-10-2185, #1111, IADR-0483: 取引判断の最中の例外の最終の失敗。窓の頭ちょうど（含む）・窓の尻ちょうど（含まない）・窓の前（含まない）。
SELECT ev('TradeDecisionFailed', gen_random_uuid(), 'META',
  '{"CycleTrigger":"scheduled","ExceptionType":"System.InvalidOperationException"}', '2026-09-29T20:00:00+09');
SELECT ev('TradeDecisionFailed', gen_random_uuid(), 'NVDA',
  '{"CycleTrigger":"scheduled","ExceptionType":"System.InvalidOperationException"}', '2026-09-29T22:00:00+09');
SELECT ev('TradeDecisionFailed', gen_random_uuid(), 'NVDA',
  '{"CycleTrigger":"scheduled","ExceptionType":"System.InvalidOperationException"}', '2026-09-29T23:00:00+09');
SELECT ev('TradeDecisionFailed', gen_random_uuid(), 'AAPL',
  '{"CycleTrigger":"price-movement","ExceptionType":"System.InvalidOperationException"}', '2026-09-30T02:00:00+09');
SELECT ev('TradeDecisionFailed', gen_random_uuid(), 'AMZN',
  '{"CycleTrigger":"price-movement","ExceptionType":"System.TimeoutException"}', '2026-09-30T08:00:00+09');
SELECT ev('TradeDecisionFailed', gen_random_uuid(), 'TSLA',
  '{"CycleTrigger":"scheduled","ExceptionType":"System.Collections.Generic.KeyNotFoundException"}', '2026-09-29T19:59:59+09');
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
# T-10-1775, #1092 段 2, IADR-0462: 照会の失敗の区間と LLM を呼ぶ前の見送り。
has '照会の失敗: 窓の前から続いた失敗は窓の頭から回復まで（回数つき）' \
  'ProtectiveStopGuard|2026-09-29 20:00:00+09|2026-09-29 20:30:00+09|回復（失敗 150 回）|-|窓の前から'
has '照会の失敗: 再起動の後の最初の成功で閉じる（回復の時刻は不明と書く）' \
  'ProtectiveStopGuard|2026-09-30 01:00:00+09|2026-09-30 01:20:00+09|再起動の後に成功（回復の時刻は不明）|Transient|'
# 終端は窓の終端（過ぎた窓）か現在時刻（窓の途中で走らせた場合）なので、時刻の列は見ない。
if grep -qE '^BrokerPositionSnapshot\|2026-09-30 03:00:00\+09\|[^|]+\|窓の終端まで続いた（回復の記録なし）\|-\|$' <<<"$OUT"; then
  pass=$((pass + 1)); echo '  ok  照会の失敗: 窓の尻ちょうどの回復は数えず、窓の終端まで続いたと書く'
else fail=$((fail + 1)); echo '  NG  照会の失敗: 窓の終端まで続いた区間が無い' >&2; grep '^BrokerPositionSnapshot|' <<<"$OUT" >&2; fi
hasnt '照会の失敗: 窓の前の成功は区間を出さない' 'TradeDecisionHoldings|'
# PR #1110 の監査 N4(a): 窓の頭ちょうどに始まった失敗は 1 区間だけ（窓の前の最後の記録として二重に拾うと 2 行になる）。
has '照会の失敗: 窓の頭ちょうどに始まった失敗は窓の中の区間として出す' \
  'BrokerAvailabilityProbe|2026-09-29 20:00:00+09|2026-09-29 20:10:00+09|回復（失敗 60 回）|Other|'
n_head="$(grep -c '^BrokerAvailabilityProbe|2026-09-29 20:00:00+09|' <<<"$OUT")"
if [ "$n_head" = "1" ]; then pass=$((pass + 1)); echo '  ok  照会の失敗: 窓の頭ちょうどの記録を二重に数えない（1 区間）'
else fail=$((fail + 1)); echo "  NG  照会の失敗: 窓の頭ちょうどの区間が ${n_head} 行（1 行のはず）" >&2
  grep '^BrokerAvailabilityProbe|' <<<"$OUT" >&2; fi
has '状態の変化の件数: 再起動の後の最初の観測を数える' 'ProtectiveStopGuard|Unknown→Healthy|1'
has '状態の変化の件数: 窓の前の記録は数えない（窓の中の失敗→回復は 1 件）' 'ProtectiveStopGuard|Failing→Healthy|1'
has 'LLM を呼ぶ前の見送り: 理由 × 起点で数える' 'DailyPolicyUnconfirmed|scheduled|2|NVDA,TSLA'
has 'LLM を呼ぶ前の見送り: 別の理由' 'CurrentPriceUnavailable|price-movement|1|AAPL'
hasnt 'LLM を呼ぶ前の見送り: 窓の尻ちょうどは数えない' 'FxRateUnresolved|'
# T-10-1796, #1113, IADR-0463: 新規建てが塞がっている銘柄の見送りも理由 × 起点で数える（審査の拒否から移った分）。
has 'LLM を呼ぶ前の見送り: 新規建てが塞がっている銘柄' 'EntryBlockedByRiskControls|scheduled|2|AMZN,META'
has '§11 に計器の移動の注記' '-- EntryBlockedByRiskControls は新規建てが審査で必ず拒否される銘柄（kill switch・一時停止・当日の損切り・建玉数の上限等）の見送り。§5 の拒否から移った分'
# T-10-2024〜T-10-2026, #1140, IADR-0478 決定 2: LLM の費用の円（§12）と、当月の累計・月次上限に対する使用率（§13）。
has '§12: 用途 × モデル別の件数と円（窓の尻ちょうどは数えない）' 'trade-decision|claude-sonnet-5|2|30.75'
has '§12: 用途の無い従来の形は (不明)' '(不明)|(不明)|1|3.00'
has '§12: 最後の行が窓の合計' '合計|-|3|33.75'
has '§13: 当月（UTC の 2026-09）の対象の累計・上限（設定サービスの値）・使用率・対象外の累計' '2026-09|3000.00|12000|25.0%|700.00'
hasnt '§13: 前の月・窓の終端ちょうど・インフラの計上を数えない' '2026-09|12999'
# T-10-2185, #1111, IADR-0483 決定 5: 取引判断の最中の例外の最終の失敗を起点 × 型名で数える（1 行＝1 回の最終の失敗）。
has '§14: 起点 × 型名で数え、窓の頭ちょうどは含む（同じ銘柄の 2 回は 2 件）' 'scheduled|System.InvalidOperationException|3|META,NVDA'
has '§14: 起点が違えば別の行' 'price-movement|System.InvalidOperationException|1|AAPL'
hasnt '§14: 窓の尻ちょうどは数えない' 'System.TimeoutException'
hasnt '§14: 窓の前は数えない' 'KeyNotFoundException'
has '§1: 種類別の件数に TradeDecisionFailed が出る' 'TradeDecisionFailed|4'
$PSQL -d configuration_svc -q -o /dev/null -c 'DELETE FROM assumptions' || exit 1
OUT_NOLIM="$(AST_PSQL="$PSQL -A -F|" bash "$SCRIPT" --night 2026-09-29 2>&1)"
rc_nolim=$?
if [ "$rc_nolim" -eq 0 ] && grep -qxF '2026-09|3000.00||不明（上限を読めない）|700.00' <<<"$OUT_NOLIM" \
  && grep -q '^WARN: 月次 LLM 費用上限を設定サービス' <<<"$OUT_NOLIM"; then
  pass=$((pass + 1)); echo '  ok  §13: 前提条件の行が無ければ使用率は「不明」と出し、累計は出して exit 0'
else fail=$((fail + 1)); echo "  NG  §13: 上限を読めないときの扱い（rc=${rc_nolim}）" >&2; grep '^2026-09|\|^WARN' <<<"$OUT_NOLIM" >&2; fi
$PSQL -d configuration_svc -q -o /dev/null \
  -c "INSERT INTO assumptions VALUES (1, '{\"costLimits\":{\"llm\":0}}', 4, now())" || exit 1
OUT_ZERO="$(AST_PSQL="$PSQL -A -F|" bash "$SCRIPT" --night 2026-09-29 2>&1)"
if grep -qxF '2026-09|3000.00|0|上限 0 以下（費用統制は統制しない）|700.00' <<<"$OUT_ZERO"; then
  pass=$((pass + 1)); echo '  ok  §13: 上限 0 は割り算せず「統制しない」と出す'
else fail=$((fail + 1)); echo '  NG  §13: 上限 0 の扱い' >&2; grep '^2026-09|' <<<"$OUT_ZERO" >&2; fi
# T-10-2025（月の境界）, #1140: 月は JST ではなく UTC の暦月で、窓 [from, to) の最後の瞬間（to の 1 マイクロ秒前）の月。
# 上の行に 9 月の 2 件（09-30T22:59Z・09-30T23:59:59Z）と、UTC で 10 月に計上された 10 月分 1 件（10-01T00:00Z ちょうど）を足す。
$PSQL -d configuration_svc -q -o /dev/null -c 'DELETE FROM assumptions' \
  -c "INSERT INTO assumptions VALUES (1, '{\"costLimits\":{\"llm\":12000}}', 5, now())" || exit 1
$PSQL -d cost_control_svc -q -v ON_ERROR_STOP=1 -o /dev/null <<'SQL' || exit 1
INSERT INTO cost_entries VALUES (gen_random_uuid(), '2026-09', 0, 400, '2026-09-30T22:59:00Z');
INSERT INTO cost_entries VALUES (gen_random_uuid(), '2026-09', 0, 100, '2026-09-30T23:59:59Z');
INSERT INTO cost_entries VALUES (gen_random_uuid(), '2026-10', 0, 8000, '2026-10-01T00:00:00Z');
SQL
# JST の月初の夜（--night 2026-09-30）: 終端は 10-01 08:00 JST だが UTC では 09-30T23:00Z ＝ まだ 9 月。
# 9 月の 1,000 + 2,000 + 500 + 400 を数え（23:59:59Z は終端より後）、10 月分は含めない。JST で切ると 2026-10 になって赤。
OUT_JST="$(AST_PSQL="$PSQL -A -F|" bash "$SCRIPT" --night 2026-09-30 2>&1)"
if grep -qxF '2026-09|3900.00|12000|32.5%|700.00' <<<"$OUT_JST" && ! grep -q '^2026-10|' <<<"$OUT_JST"; then
  pass=$((pass + 1)); echo '  ok  §13: JST の月初の夜も UTC の暦月（9 月）で累計し、10 月分を含めない'
else fail=$((fail + 1)); echo '  NG  §13: JST の月初の夜の月の取り違え' >&2; grep '^2026-' <<<"$OUT_JST" >&2; fi
# 窓の終端がちょうど月初 00:00Z: 窓は半開区間なので終端の月（10 月）ではなく前の月（9 月）として扱う。
# 9 月の 5 件（4,000）を数え、終端ちょうどに計上された 10 月分は含めない。1 マイクロ秒を引かないと 2026-10 になって赤。
OUT_EDGE="$(AST_PSQL="$PSQL -A -F|" bash "$SCRIPT" 2026-09-30T20:00+00:00 2026-10-01T00:00+00:00 2>&1)"
if grep -qxF '2026-09|4000.00|12000|33.3%|700.00' <<<"$OUT_EDGE" && ! grep -q '^2026-10|' <<<"$OUT_EDGE"; then
  pass=$((pass + 1)); echo '  ok  §13: 窓の終端がちょうど月初 00:00Z なら前の月（9 月）として扱う'
else fail=$((fail + 1)); echo '  NG  §13: 窓の終端がちょうど月初のときの月' >&2; grep '^2026-' <<<"$OUT_EDGE" >&2; fi

# 窓の途中で走らせる（場中の確かめ）: 終端は現在時刻で切り、まだ来ていない時間を欠けとして出さない。
# 観測は 5 分ごとに現在時刻の 30 分前まで。欠けは「最後の観測 → 現在時刻」の約 30 分であり、「→ 窓の終端」の約 10 時間ではない。
MID_FROM="$(date -u -d '-2 hours' +%Y-%m-%dT%H:%M:00+00:00)"
MID_TO="$(date -u -d '+10 hours' +%Y-%m-%dT%H:%M:00+00:00)"
$PSQL -d audit_svc -q -v ON_ERROR_STOP=1 -o /dev/null -c "DELETE FROM audit_events WHERE \"EventType\" = 'BrokerAvailabilityObserved';" \
  -c "SELECT ev('BrokerAvailabilityObserved', gen_random_uuid(), NULL, '{}', t) FROM generate_series('$MID_FROM'::timestamptz, now() - interval '30 minutes', '5 minutes') t;" \
  -c "SELECT ev('PositionQueryStatusChanged', gen_random_uuid(), NULL, '{\"Source\":\"SoftwareStopClose\",\"Status\":\"Failing\",\"PreviousStatus\":\"Healthy\",\"FailureKind\":null,\"FailedQueries\":1}', now() - interval '1 hour');" \
  || exit 1
OUT="$(AST_PSQL="$PSQL -A -F|" bash "$SCRIPT" "$MID_FROM" "$MID_TO" 2>&1)"
rc=$?
if [ "$rc" -eq 0 ]; then pass=$((pass + 1)); echo '  ok  窓の途中の実行も exit 0'
else fail=$((fail + 1)); echo "  NG  窓の途中の実行が exit $rc" >&2; echo "$OUT" >&2; fi
if grep -q '^注意: 窓の終端が現在時刻より後' <<<"$OUT"; then pass=$((pass + 1)); echo '  ok  窓の途中なら注意を 1 行出す'
else fail=$((fail + 1)); echo '  NG  窓の途中なのに注意が無い' >&2; fi
LAST_GAP="$(grep '^BrokerAvailabilityObserved|' <<<"$OUT" | tail -1)"
case "$LAST_GAP" in
  *'|00:3'[0-9]':'*) pass=$((pass + 1)); echo "  ok  最後の欠けは現在時刻まで（${LAST_GAP##*|}）" ;;
  *) fail=$((fail + 1)); echo "  NG  最後の欠けが現在時刻で切られていない: ${LAST_GAP:-（行なし）}" >&2 ;;
esac
# PR #1110 の監査 N4(b): 窓の途中なら、回復の記録が無い照会の失敗の区間は**現在時刻**で切る（窓の終端〔約 10 時間後〕まで伸ばさない）。
MID_Q="$(grep '^SoftwareStopClose|' <<<"$OUT" | head -1)"
MID_Q_TO="$(cut -d'|' -f3 <<<"$MID_Q")"
if [ -n "$MID_Q_TO" ] && q_to=$(date -d "$MID_Q_TO" +%s 2>/dev/null) && [ $(( q_to - $(date +%s) )) -le 300 ] \
  && [ $(( $(date +%s) - q_to )) -le 300 ] && grep -qF '|窓の終端まで続いた（回復の記録なし）|-|' <<<"$MID_Q"; then
  pass=$((pass + 1)); echo "  ok  窓の途中なら未回復の照会の失敗は現在時刻で切る（${MID_Q_TO}）"
else fail=$((fail + 1)); echo "  NG  窓の途中の未回復の照会の失敗が現在時刻で切られていない: ${MID_Q:-（行なし）}" >&2; fi
OUT_DONE="$(AST_PSQL="$PSQL -A -F|" bash "$SCRIPT" --night 2026-09-20 2>&1)"
if grep -q '^注意: 窓の終端が現在時刻より後' <<<"$OUT_DONE"; then fail=$((fail + 1)); echo '  NG  過ぎた窓なのに注意を出す' >&2
else pass=$((pass + 1)); echo '  ok  過ぎた窓では注意を出さない'; fi
# 過ぎた窓（観測なし）: 欠けは窓の頭から窓の終端まで（現在時刻まで伸ばさない）。
if grep -qxF 'BrokerAvailabilityObserved|2026-09-20 20:00:00+09|2026-09-21 08:00:00+09|12:00:00' <<<"$OUT_DONE"; then
  pass=$((pass + 1)); echo '  ok  過ぎた窓の欠けは窓の終端で切る'
else fail=$((fail + 1)); echo '  NG  過ぎた窓の欠けが窓の終端で切られていない' >&2; grep '^Broker' <<<"$OUT_DONE" >&2; fi
if grep -q '^Broker.* days\?' <<<"$OUT_DONE"; then fail=$((fail + 1)); echo '  NG  過ぎた窓に日をまたぐ欠けが出る（現在時刻まで伸ばした）' >&2
else pass=$((pass + 1)); echo '  ok  過ぎた窓に日をまたぐ欠けは出ない'; fi
# 窓が丸ごと未来: 欠けは 0 行（窓の頭より前へ戻らない）。
FUT_FROM="$(date -u -d '+5 days' +%Y-%m-%dT%H:%M:00+00:00)"
FUT_TO="$(date -u -d '+6 days' +%Y-%m-%dT%H:%M:00+00:00)"
OUT_FUT="$(AST_PSQL="$PSQL -A -F|" bash "$SCRIPT" "$FUT_FROM" "$FUT_TO" 2>&1)"
if grep -q '^Broker' <<<"$OUT_FUT"; then fail=$((fail + 1)); echo '  NG  窓が丸ごと未来なのに欠けが出る' >&2; grep '^Broker' <<<"$OUT_FUT" >&2
else pass=$((pass + 1)); echo '  ok  窓が丸ごと未来なら欠けは 0 行'; fi

echo
if [ "$fail" -ne 0 ]; then
  echo "✗ ${fail} failed / ${pass} passed" >&2
  exit 1
fi
echo "✓ ${pass} tests passed"
