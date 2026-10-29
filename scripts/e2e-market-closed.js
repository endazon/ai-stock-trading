#!/usr/bin/env node
'use strict';
/*
 * e2e-market-closed.js — NFR, #1418（E-60）, IADR-0565 決定 2, IADR-0599:
 * live の取引影響層（Playwright の `trading` project・L3-T）の**市場の閉場の門**。
 *
 * 「全対象市場が閉場しており、いまから次の開場まで minMinutes 分（既定 60）以上ある」ときだけ通す。
 * 通らなければ exit 2（前提未整備。skip の緑とも、製品の不具合の赤とも区別する。MSP:IADR-0569 決定 4）。
 *
 * 規則は AST の backend と同じものを Node で持つ（2 か所に置くので、境界の試験を同じ値で固定する）:
 *   - 取引時間: `backend/Shared/AiStockTrading.Shared.Kernel/Trading/MarketSessions.cs`
 *     米国 9:30–16:00 ET（半日取引日は 13:00 まで）。東証 前場 9:00–11:30・後場 12:30–15:30 JST。開始は包含・終了は排他。
 *   - 休場日・半日取引日: 同 `MarketHolidays.cs`（米国の 10 日を規則で計算する。東証は週末だけ＝C# と同じ射程）。
 *   - 現地時刻への写像はタイムゾーン（America/New_York・Asia/Tokyo）で行う。固定のオフセットで換算しない（夏時間）。
 *
 * 判定は「いま〜いま＋minMinutes 分の各分が、どの対象市場でも場中でない」ことで行う（次の開場の時刻を逆算しない。
 * タイムゾーンの逆変換を持たずに済み、夏時間の切替の日も同じ関数で正しい）。
 *
 * 使い方:
 *   node scripts/e2e-market-closed.js [--now <ISO 8601>] [--markets UnitedStates,Japan] [--min-minutes 60]
 *   既定の対象市場は UnitedStates（PoC が取引しているのは米国市場。MSP#2013 コメント 3 §5.2）。
 */

const NOT_READY_EXIT_CODE = 2;
const DEFAULT_MIN_MINUTES = 60;
const MARKETS = {
  UnitedStates: { timeZone: 'America/New_York' },
  Japan: { timeZone: 'Asia/Tokyo' },
};
const DEFAULT_MARKETS = ['UnitedStates'];

// ---- 取引時間（MarketSessions.cs の写し。分で持つ）----
const hm = (h, m) => h * 60 + m;
const US_OPEN = hm(9, 30);
const US_CLOSE = hm(16, 0);
const US_HALF_DAY_CLOSE = hm(13, 0);
const JP_MORNING = [hm(9, 0), hm(11, 30)];
const JP_AFTERNOON = [hm(12, 30), hm(15, 30)];

/** 現地時刻（0 時からの分）が取引時間内か。曜日・休場日は見ない（MarketSessions.IsWithinSession と同じ）。 */
function isWithinSession(market, localMinutes, isHalfDay) {
  if (market === 'UnitedStates') {
    return localMinutes >= US_OPEN && localMinutes < (isHalfDay ? US_HALF_DAY_CLOSE : US_CLOSE);
  }
  if (market === 'Japan') {
    // 東証に半日取引日は無い（半日フラグを無視する）。昼休み 11:30–12:30 は場中に含めない。
    return (
      (localMinutes >= JP_MORNING[0] && localMinutes < JP_MORNING[1]) ||
      (localMinutes >= JP_AFTERNOON[0] && localMinutes < JP_AFTERNOON[1])
    );
  }
  return false; // 未知の市場は安全側（場外）。ただし門は未知の市場の指定そのものを拒否する（parseArgs）。
}

// ---- 休場日・半日取引日（MarketHolidays.cs の写し。日付は UTC の Date を暦日の入れ物にする）----
const d = (y, m, day) => new Date(Date.UTC(y, m - 1, day));
const sameDay = (a, b) => a.getTime() === b.getTime();
const addDays = (date, n) => new Date(date.getTime() + n * 86_400_000);
const dow = (date) => date.getUTCDay(); // 0=日 … 6=土

function observed(date, backwardOnSaturday = true) {
  if (dow(date) === 6) return backwardOnSaturday ? addDays(date, -1) : date;
  if (dow(date) === 0) return addDays(date, 1);
  return date;
}
function nthWeekday(year, month, weekday, n) {
  const first = d(year, month, 1);
  const shift = (weekday - dow(first) + 7) % 7;
  return addDays(first, shift + (n - 1) * 7);
}
function lastWeekday(year, month, weekday) {
  const last = d(year, month + 1, 0);
  return addDays(last, -((dow(last) - weekday + 7) % 7));
}
function goodFriday(year) {
  const a = year % 19;
  const b = Math.floor(year / 100);
  const c = year % 100;
  const dd = Math.floor(b / 4);
  const e = b % 4;
  const f = Math.floor((b + 8) / 25);
  const g = Math.floor((b - f + 1) / 3);
  const h = (19 * a + b - dd - g + 15) % 30;
  const i = Math.floor(c / 4);
  const k = c % 4;
  const l = (32 + 2 * e + 2 * i - h - k) % 7;
  const m = Math.floor((a + 11 * h + 22 * l) / 451);
  const month = Math.floor((h + l - 7 * m + 114) / 31);
  const day = ((h + l - 7 * m + 114) % 31) + 1;
  return addDays(d(year, month, day), -2);
}

function isUnitedStatesHoliday(date) {
  const y = date.getUTCFullYear();
  if (sameDay(observed(d(y, 1, 1), false), date)) return true; // 元日（土曜は振替しない）
  if (sameDay(nthWeekday(y, 1, 1, 3), date) || sameDay(nthWeekday(y, 2, 1, 3), date)) return true;
  if (sameDay(goodFriday(y), date)) return true;
  if (
    sameDay(lastWeekday(y, 5, 1), date) ||
    sameDay(nthWeekday(y, 9, 1, 1), date) ||
    sameDay(nthWeekday(y, 11, 4, 4), date)
  ) {
    return true;
  }
  if (y >= 2022 && sameDay(observed(d(y, 6, 19)), date)) return true;
  return sameDay(observed(d(y, 7, 4)), date) || sameDay(observed(d(y, 12, 25)), date);
}

function isUnitedStatesHalfDay(date) {
  const w = dow(date);
  if (w === 0 || w === 6 || isUnitedStatesHoliday(date)) return false;
  const y = date.getUTCFullYear();
  if (sameDay(addDays(nthWeekday(y, 11, 4, 4), 1), date)) return true; // 感謝祭翌日
  if (sameDay(d(y, 7, 3), date)) return true; // 独立記念日前日
  return sameDay(d(y, 12, 24), date); // クリスマスイブ
}

const isHoliday = (market, date) => market === 'UnitedStates' && isUnitedStatesHoliday(date);
const isHalfDay = (market, date) => market === 'UnitedStates' && isUnitedStatesHalfDay(date);

// ---- 現地時刻への写像 ----
const formatters = new Map();
function localParts(instant, timeZone) {
  let f = formatters.get(timeZone);
  if (!f) {
    f = new Intl.DateTimeFormat('en-US', {
      timeZone,
      hourCycle: 'h23',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    });
    formatters.set(timeZone, f);
  }
  const p = Object.fromEntries(f.formatToParts(instant).map((x) => [x.type, x.value]));
  return { date: d(Number(p.year), Number(p.month), Number(p.day)), minutes: hm(Number(p.hour), Number(p.minute)) };
}

/** その瞬間に市場が場中か（曜日・休場日・半日取引日を含む）。 */
function isOpenAt(market, instant) {
  const { date, minutes } = localParts(instant, MARKETS[market].timeZone);
  const w = dow(date);
  if (w === 0 || w === 6 || isHoliday(market, date)) return false;
  return isWithinSession(market, minutes, isHalfDay(market, date));
}

/**
 * 門の判定（純関数）。不成立の理由を並べて返す。空なら通す。
 * いま場中の市場があれば「開場中」、いま〜いま＋minMinutes 分のうちに開く市場があれば「まもなく開場」。
 */
function closedGateProblems(now, { markets = DEFAULT_MARKETS, minMinutes = DEFAULT_MIN_MINUTES } = {}) {
  const problems = [];
  for (const market of markets) {
    if (!MARKETS[market]) {
      problems.push(`未知の市場: ${market}（${Object.keys(MARKETS).join(' / ')}）`);
      continue;
    }
    if (isOpenAt(market, now)) {
      problems.push(`${market} は開場中である（${now.toISOString()}）`);
      continue;
    }
    // m 分後に開くなら、次の開場まで m 分。m < minMinutes の間に開くものだけを不成立にする（ちょうど minMinutes 分は通す）。
    for (let m = 1; m < minMinutes; m++) {
      const t = new Date(now.getTime() + m * 60_000);
      if (isOpenAt(market, t)) {
        problems.push(`${market} は ${m} 分後に開場する（次の開場まで ${minMinutes} 分以上が要る）`);
        break;
      }
    }
  }
  return problems;
}

function parseArgs(argv) {
  const out = { now: new Date(), markets: DEFAULT_MARKETS, minMinutes: DEFAULT_MIN_MINUTES };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => {
      const v = argv[++i];
      if (v === undefined) throw new Error(`${a} に値が無い`);
      return v;
    };
    if (a === '--now') {
      const v = next();
      out.now = new Date(v);
      if (Number.isNaN(out.now.getTime())) throw new Error(`--now が日時ではない: ${v}`);
    } else if (a === '--markets') {
      out.markets = next().split(',').map((s) => s.trim()).filter(Boolean);
      for (const m of out.markets) if (!MARKETS[m]) throw new Error(`--markets の値域外: ${m}`);
      if (out.markets.length === 0) throw new Error('--markets が空である');
    } else if (a === '--min-minutes') {
      const v = next();
      out.minMinutes = Number(v);
      if (!Number.isInteger(out.minMinutes) || out.minMinutes < 0) throw new Error(`--min-minutes が 0 以上の整数ではない: ${v}`);
    } else {
      throw new Error(`未知の引数: ${a}`);
    }
  }
  return out;
}

module.exports = {
  NOT_READY_EXIT_CODE,
  DEFAULT_MIN_MINUTES,
  DEFAULT_MARKETS,
  isWithinSession,
  isUnitedStatesHoliday,
  isUnitedStatesHalfDay,
  isOpenAt,
  closedGateProblems,
  parseArgs,
};

if (require.main === module) {
  let args;
  try {
    args = parseArgs(process.argv.slice(2));
  } catch (e) {
    process.stderr.write(`[e2e-market-closed] ${e.message}\n`);
    process.exit(NOT_READY_EXIT_CODE);
  }
  const problems = closedGateProblems(args.now, args);
  if (problems.length) {
    process.stderr.write(
      `[e2e-market-closed] 前提未整備（exit ${NOT_READY_EXIT_CODE}）:\n  - ${problems.join('\n  - ')}\n`,
    );
    process.exit(NOT_READY_EXIT_CODE);
  }
  process.stdout.write(
    `[e2e-market-closed] 閉場を確かめた: ${args.markets.join(', ')}（${args.now.toISOString()}・次の開場まで ${args.minMinutes} 分以上）\n`,
  );
}
