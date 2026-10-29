import { expect, test } from '@playwright/test';
import {
  IN_FLIGHT_SOURCE_MISSING,
  inFlightProblems,
  restoreCore,
  runIdOf,
  tradingGateProblems,
} from './live/support/snapshot';
import { restoreCommandError, verdictOf } from './live/support/restore';

// NFR / IADR-0565 決定 2・3 / IADR-0599（#1418 / E-60）: live の trading 層の**門**と**突合の判定**を L1 で固定する。
// 稼働の環境には触れない（時刻と in-flight の判定を差し替えて、門の関数だけを呼ぶ）。市場の規則そのものの境界は
// `scripts/scripts.repo.test.js`（MarketSessionsTests と同じ 5 ケース）が、突合・復元の本体は `scripts/e2e-trading-restore.test.js` が持つ。

const CLOSED = new Date('2026-10-29T20:00:00Z'); // 木曜 16:00 EDT（閉場の直後）
const OPEN = new Date('2026-10-29T14:00:00Z'); // 木曜 10:00 EDT（場中）

test('🔴 in-flight の門は、情報源（#1502）が入るまで常に不成立で、閉場中でも trading 層は流れない', () => {
  expect(inFlightProblems()).toEqual([IN_FLIGHT_SOURCE_MISSING]);
  expect(tradingGateProblems(CLOSED)).toEqual([IN_FLIGHT_SOURCE_MISSING]);
  expect(IN_FLIGHT_SOURCE_MISSING).toContain('#1502');
});

test('市場の門: 開場中は不成立、閉場で次の開場まで 60 分以上あれば成立（in-flight の判定を差し替えたとき）', () => {
  const none = () => [];
  expect(tradingGateProblems(CLOSED, none)).toEqual([]);
  expect(tradingGateProblems(OPEN, none)).toEqual([expect.stringContaining('開場中')]);
  // 開場の 30 分前（9:00 EDT）。
  expect(tradingGateProblems(new Date('2026-10-29T13:00:00Z'), none)).toEqual([
    expect.stringContaining('30 分後に開場'),
  ]);
  // 両方の門の理由を並べる（片方で止めて、もう片方を隠さない）。
  expect(tradingGateProblems(OPEN, inFlightProblems)).toHaveLength(2);
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
