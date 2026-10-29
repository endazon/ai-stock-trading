import { expect, test } from '@playwright/test';
import {
  IN_FLIGHT_PATH,
  inFlightProblems,
  inFlightProblemsFrom,
  restoreCore,
  runIdOf,
  tradingGateProblems,
  type TradingClient,
} from './live/support/snapshot';
import { restoreCommandError, verdictOf } from './live/support/restore';
import { testSymbolOf } from './live/support/symbols';

// NFR / IADR-0565 決定 2・3 / IADR-0599（#1418 / E-60）: live の trading 層の**門**と**突合の判定**を L1 で固定する。
// 稼働の環境には触れない（時刻と in-flight の判定を差し替えて、門の関数だけを呼ぶ）。市場の規則そのものの境界は
// `scripts/scripts.repo.test.js`（MarketSessionsTests と同じ 5 ケース）が、突合・復元の本体は `scripts/e2e-trading-restore.test.js` が持つ。

const CLOSED = new Date('2026-10-29T20:00:00Z'); // 木曜 16:00 EDT（閉場の直後）
const OPEN = new Date('2026-10-29T14:00:00Z'); // 木曜 10:00 EDT（場中）

// NFR / IADR-0600（#1502）: in-flight の門は BFF の `/bff/risk-controls/in-flight-orders` が空のときだけ通す。照会の失敗も不成立。
const reply = (status: number, json: unknown): TradingClient => ({
  get: async (path) => {
    expect(path).toBe(IN_FLIGHT_PATH);
    return { status, json };
  },
  send: async () => {
    throw new Error('門は書き込まない');
  },
});

test('in-flight の門: 一覧が空なら通し、1 件でもあれば銘柄・種別・状態を理由に並べて不成立', async () => {
  expect(await inFlightProblems(reply(200, []))).toEqual([]);
  const problems = await inFlightProblems(
    reply(200, [
      { symbol: 'HOOD', market: 0, kind: 2, state: 1, approvedAt: '2026-10-29T19:08:20Z' },
      { symbol: 'NVDA', market: 0, kind: 1, state: 2, approvedAt: '2026-10-29T19:00:00Z' },
    ]),
  );
  expect(problems).toHaveLength(1);
  expect(problems[0]).toContain('2 件');
  expect(problems[0]).toContain('HOOD（決済・承認済み・未終端');
  expect(problems[0]).toContain('NVDA（新規建て・予約');
});

test('🔴 in-flight の門: 照会の失敗（200 でない・配列でない・届かない）は不成立（判断できないものを「無い」と読まない）', async () => {
  expect(inFlightProblemsFrom({ status: 403, json: null })).toEqual([expect.stringContaining('403')]);
  expect(inFlightProblemsFrom({ status: 502, json: null })).toEqual([expect.stringContaining('502')]);
  expect(inFlightProblemsFrom({ status: 200, json: { items: [] } })).toEqual([
    expect.stringContaining('配列でない'),
  ]);
  const unreachable: TradingClient = {
    get: async () => {
      throw new Error('connect ECONNREFUSED\n詳細');
    },
    send: async () => undefined,
  };
  expect(await inFlightProblems(unreachable)).toEqual([expect.stringContaining('届かない（connect ECONNREFUSED。')]);
});

test('門: 閉場で in-flight が無いときだけ通し、開場中・まもなく開場・in-flight ありは不成立（両方の理由を並べる）', async () => {
  const none = async () => [];
  const some = async () => ['in-flight の注文が 1 件ある'];
  expect(await tradingGateProblems(CLOSED, none)).toEqual([]);
  expect(await tradingGateProblems(CLOSED, some)).toEqual(['in-flight の注文が 1 件ある']);
  expect(await tradingGateProblems(OPEN, none)).toEqual([expect.stringContaining('開場中')]);
  // 開場の 30 分前（9:00 EDT）。
  expect(await tradingGateProblems(new Date('2026-10-29T13:00:00Z'), none)).toEqual([
    expect.stringContaining('30 分後に開場'),
  ]);
  expect(await tradingGateProblems(OPEN, some)).toHaveLength(2);
});

test('runId: E2E_RUN_ID を英数字とハイフンで固定でき、無ければ時刻から作る', () => {
  expect(runIdOf({ E2E_RUN_ID: 'abc-123' })).toBe('abc-123');
  expect(() => runIdOf({ E2E_RUN_ID: '../x' })).toThrow(/E2E_RUN_ID/);
  expect(runIdOf({}, new Date('2026-10-30T01:02:03.456Z'))).toBe('20261030-010203');
});

test('突合の判定: 差分か復元の失敗が残れば赤。要約は項目名だけ', () => {
  const clean = { firstDiff: [], attempted: [], errors: [], remaining: [] };
  expect(verdictOf(clean).failed).toBe(false);
  const left = {
    firstDiff: [{ key: 'brokerProvider', before: 1, after: 2, restorable: false }],
    attempted: [],
    errors: [],
    remaining: [{ key: 'brokerProvider', before: 1, after: 2, restorable: false }],
  };
  expect(verdictOf(left).failed).toBe(true);
  expect(verdictOf({ ...clean, errors: ['limits: PUT が 500'] }).failed).toBe(true);
  expect(verdictOf(left).lines.join('\n')).toContain('brokerProvider（復元しない項目）');
});

test('スナップショットは取引に影響する設定の GET を全て読む（MSP#2013 コメント 3 §5.3）', () => {
  expect(restoreCore.SNAPSHOT_PATHS).toEqual([
    '/bff/risk-controls/settings',
    '/bff/risk-controls/settings/pre-close',
    '/bff/risk-controls/settings/theme-concentration',
    '/bff/monitor/settings',
    '/bff/monitor/watchlist',
    '/bff/assumptions',
  ]);
});

test('突合が例外で落ちたら、スナップショットの場所と単独実行の復元コマンドを ::error:: で出す', () => {
  const line = restoreCommandError('/x/test-results/trading-snapshot-r1.json', new Error('GET /bff/assumptions が 503\n詳細'));
  expect(line).toMatch(/^::error::/);
  expect(line).toContain('GET /bff/assumptions が 503');
  expect(line).not.toContain('詳細');
  expect(line).toContain(
    'node scripts/e2e-trading-restore.js --snapshot /x/test-results/trading-snapshot-r1.json --live',
  );
});

// NFR / IADR-0601（#1419 / A-047）: 試験銘柄のコードは常に `E2E` ＋ 数字 6 桁（runId に数字が足りなければ時刻の数字で補う）。
test('A-047 の試験銘柄のコードは、runId の数字が足りなくても常に E2E ＋ 6 桁になる', () => {
  const now = new Date(1_700_000_123_456);
  expect(testSymbolOf('20261030-010203', now)).toBe('E2E010203');
  expect(testSymbolOf('abc', now)).toBe('E2E123456');
  expect(testSymbolOf('run-42', now)).toBe('E2E345642'); // 時刻の数字（…123456）の後に runId の数字（42）を置いた下 6 桁
  for (const id of ['', 'x', '1', 'abc-def', '20261030-010203']) expect(testSymbolOf(id, now)).toMatch(/^E2E\d{6}$/);
});
