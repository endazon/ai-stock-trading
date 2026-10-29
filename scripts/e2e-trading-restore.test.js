#!/usr/bin/env node
'use strict';
// NFR, #1418（E-60）, IADR-0565 決定 3, IADR-0599: scripts/e2e-trading-restore.js の本体の試験（偽の BFF の口）。
// scripts.repo.test.js から node の経路で起動する（本体が非同期のため別ファイルに置く）。
const assert = require('assert');
const core = require('./e2e-trading-restore.js');

const base = () => ({
  '/bff/risk-controls/settings': {
    guard: { enabledMarkets: [0] },
    limits: { maxOrderAmountRatio: 0.1, maxOpenPositions: 5 },
    stage: { current: 0 },
    brokerProvider: 1,
    stage1MinimumTradeCount: 100,
    stopLossMethod: 0,
    highVolatility: { categoryMaxOrderAmountRatio: 0.05, symbols: [] },
  },
  '/bff/risk-controls/settings/pre-close': {
    effectiveCutoff: { minutes: 60 }, effectiveFlatten: { enabled: false }, savedCutoff: null, cutoffSource: 0,
  },
  '/bff/risk-controls/settings/theme-concentration': { effective: [{ name: 'AI', symbols: ['NVDA'], maxPositions: 1 }], saved: null },
  '/bff/monitor/settings': { movementThresholdRatio: 0.03, cooldown: '00:30:00', monitoredSymbols: [] },
  '/bff/monitor/watchlist': [{ symbol: 'AAPL', market: 0 }, { symbol: 'MSFT', market: 0 }],
  '/bff/assumptions': { assumptions: { monthlyLlmCostCap: 10000 }, version: 3, isResolved: true },
});

/** 偽の BFF。書き込みは記録して、状態へ反映する（復元が効いたかを、もう一度の突合で見る）。 */
function fakeBff(state, { failGet = null, rejectWrites = false, failFirst = null } = {}) {
  const sent = [];
  let failedOnce = false;
  return {
    sent,
    state,
    async get(p) {
      if (p === failGet) return { status: 503, json: null };
      return { status: 200, json: JSON.parse(JSON.stringify(state[p])) };
    },
    async send(method, p, body) {
      sent.push({ method, path: p, body });
      if (rejectWrites) throw new Error(`${method} ${p} が 500`);
      if (failFirst === `${method} ${p}` && !failedOnce) {
        failedOnce = true;
        throw new Error(`${method} ${p} が 500`);
      }
      const s = state['/bff/risk-controls/settings'];
      if (p === '/bff/risk-controls/settings/limits') s.limits = body.limits;
      else if (p === '/bff/risk-controls/settings/stage1-minimum-trade-count') s.stage1MinimumTradeCount = body.minimumTradeCount;
      else if (p === '/bff/monitor/settings/movement-threshold') state['/bff/monitor/settings'].movementThresholdRatio = body.movementThresholdRatio;
      else if (p === '/bff/monitor/settings/cooldown') state['/bff/monitor/settings'].cooldown = body.cooldown;
      else if (p === '/bff/monitor/watchlist' && method === 'DELETE') {
        state[p] = state[p].filter((x) => !(x.symbol === body.symbol && x.market === body.market));
      } else if (p === '/bff/monitor/watchlist' && method === 'POST') state[p].push({ symbol: body.symbol, market: body.market });
      else throw new Error(`偽の BFF に無い書き込み: ${method} ${p}`);
    },
  };
}

const tests = [];
const test = (name, fn) => tests.push({ name, fn });

test('差分が無ければ何も送らず、残りは空', async () => {
  const bff = fakeBff(base());
  const snap = await core.takeSnapshot(bff, 'r1', new Date('2026-10-30T00:00:00Z'));
  assert.strictEqual(snap.runId, 'r1');
  assert.deepStrictEqual(Object.keys(snap.responses), core.SNAPSHOT_PATHS);
  const r = await core.reconcile(bff, snap);
  assert.deepStrictEqual([r.firstDiff, r.attempted, r.errors, r.remaining], [[], [], [], []]);
  assert.strictEqual(bff.sent.length, 0);
});

test('上限・Stage 1 件数・閾値・クールダウン・監視銘柄の差分は、理由 [e2e <runId> restore] で戻して残りが空になる', async () => {
  const bff = fakeBff(base());
  const snap = await core.takeSnapshot(bff, 'r2');
  const st = bff.state;
  st['/bff/risk-controls/settings'].limits.maxOpenPositions = 6;
  st['/bff/risk-controls/settings'].stage1MinimumTradeCount = 101;
  st['/bff/monitor/settings'].movementThresholdRatio = 0.04;
  st['/bff/monitor/settings'].cooldown = '00:31:00';
  st['/bff/monitor/watchlist'] = [{ symbol: 'AAPL', market: 0 }, { symbol: 'E2ETEST', market: 0 }];
  const r = await core.reconcile(bff, snap);
  assert.deepStrictEqual(r.firstDiff.map((d) => d.key).sort(), ['cooldown', 'limits', 'movementThresholdRatio', 'stage1MinimumTradeCount', 'watchlist']);
  assert.deepStrictEqual(r.remaining, []);
  assert.deepStrictEqual(r.errors, []);
  assert.ok(bff.sent.every((x) => x.body.reason === '[e2e r2 restore]'));
  assert.deepStrictEqual(
    bff.sent.filter((x) => x.path === '/bff/monitor/watchlist').map((x) => `${x.method} ${x.body.symbol}`),
    ['DELETE E2ETEST', 'POST MSFT'],
  );
});

test('🔴 発注先・損切り機構・全体前提条件などの差分は復元を送らず、残った差分として返す（赤）', async () => {
  const bff = fakeBff(base());
  const snap = await core.takeSnapshot(bff, 'r3');
  bff.state['/bff/risk-controls/settings'].brokerProvider = 2;
  bff.state['/bff/risk-controls/settings'].stopLossMethod = 1;
  bff.state['/bff/assumptions'].assumptions.monthlyLlmCostCap = 1;
  bff.state['/bff/risk-controls/settings/theme-concentration'].effective = [];
  const r = await core.reconcile(bff, snap);
  assert.strictEqual(bff.sent.length, 0, JSON.stringify(bff.sent));
  assert.deepStrictEqual(r.remaining.map((d) => d.key).sort(), ['assumptions', 'brokerProvider', 'stopLossMethod', 'themeConcentration']);
  assert.ok(r.remaining.every((d) => !d.restorable));
  assert.ok(!bff.sent.some((x) => /broker-provider|stop-loss-method|opend-auth/.test(x.path)));
});

test('同値の保存で動く欄（全体前提条件の版・閉場前の保存値と由来）は差分にしない', async () => {
  const bff = fakeBff(base());
  const snap = await core.takeSnapshot(bff, 'r4');
  bff.state['/bff/assumptions'].version = 4;
  bff.state['/bff/risk-controls/settings/pre-close'].savedCutoff = { minutes: 60 };
  bff.state['/bff/risk-controls/settings/pre-close'].cutoffSource = 1;
  bff.state['/bff/risk-controls/settings/theme-concentration'].saved = [{ name: 'AI' }];
  const r = await core.reconcile(bff, snap);
  assert.deepStrictEqual(r.firstDiff, []);
});

test('復元の要求が失敗したら errors に残り、差分も残る（赤）', async () => {
  const bff = fakeBff(base(), { rejectWrites: true });
  const snap = await core.takeSnapshot(bff, 'r5');
  bff.state['/bff/risk-controls/settings'].limits.maxOpenPositions = 6;
  const r = await core.reconcile(bff, snap);
  assert.strictEqual(r.errors.length, 1);
  assert.deepStrictEqual(r.remaining.map((d) => d.key), ['limits']);
});

test('監視銘柄の復元は 1 件目の DELETE が失敗しても残りを試し、失敗は errors に集め、差分は赤のまま残る', async () => {
  const bff = fakeBff(base(), { failFirst: 'DELETE /bff/monitor/watchlist' });
  const snap = await core.takeSnapshot(bff, 'r8');
  bff.state['/bff/monitor/watchlist'] = [
    { symbol: 'AAPL', market: 0 },
    { symbol: 'E2EA', market: 0 },
    { symbol: 'E2EB', market: 0 },
  ];
  const r = await core.reconcile(bff, snap);
  assert.deepStrictEqual(
    bff.sent.map((x) => `${x.method} ${x.body.symbol}`),
    ['DELETE E2EA', 'DELETE E2EB', 'POST MSFT'],
    '1 件目の失敗の後も、残りの DELETE と POST を送る',
  );
  assert.strictEqual(r.errors.length, 1);
  assert.ok(r.errors[0].startsWith('watchlist: DELETE 0:E2EA'), r.errors[0]);
  assert.deepStrictEqual(r.remaining.map((d) => d.key), ['watchlist'], '消せなかった銘柄の差分は残る（赤）');
});

test('dryRun は書き込まず、差分をそのまま残りとして返す', async () => {
  const bff = fakeBff(base());
  const snap = await core.takeSnapshot(bff, 'r6');
  bff.state['/bff/risk-controls/settings'].limits.maxOpenPositions = 6;
  const r = await core.reconcile(bff, snap, { dryRun: true });
  assert.strictEqual(bff.sent.length, 0);
  assert.deepStrictEqual(r.remaining.map((d) => d.key), ['limits']);
});

test('スナップショットの GET が 1 つでも 200 でなければ例外（不完全なスナップショットで始めない）', async () => {
  const bff = fakeBff(base(), { failGet: '/bff/assumptions' });
  await assert.rejects(core.takeSnapshot(bff, 'r7'), /GET \/bff\/assumptions が 503/);
});

test('要約は項目名だけで、値を出さない', async () => {
  const lines = core.summarize({
    firstDiff: [{ key: 'limits', before: { secret: 1 }, after: { secret: 2 }, restorable: true }],
    attempted: ['limits'],
    errors: [],
    remaining: [{ key: 'brokerProvider', before: 1, after: 2, restorable: false }],
  });
  assert.ok(lines.join('\n').includes('brokerProvider（復元しない項目）'));
  assert.ok(!lines.join('\n').includes('secret'));
});

test('CLI: --snapshot が無ければ exit 2、storageState が無ければ exit 2', async () => {
  const { spawnSync } = require('child_process');
  const run = (args, env = {}) => spawnSync(process.execPath, ['scripts/e2e-trading-restore.js', ...args], { encoding: 'utf8', env: { ...process.env, ...env } });
  assert.strictEqual(run([]).status, core.NOT_READY_EXIT_CODE);
  const fs = require('fs');
  const os = require('os');
  const path = require('path');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ast-e2e-restore-'));
  try {
    const snapFile = path.join(dir, 's.json');
    fs.writeFileSync(snapFile, '{}');
    const r = run(['--snapshot', snapFile], { E2E_AUTH_DIR: dir });
    assert.strictEqual(r.status, core.NOT_READY_EXIT_CODE, r.stderr);
    assert.ok(r.stderr.includes('storageState が無い'));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

(async () => {
  let failed = 0;
  for (const t of tests) {
    try {
      await t.fn();
      process.stdout.write(`  ok  ${t.name}\n`);
    } catch (e) {
      failed++;
      process.stdout.write(`  NG  ${t.name}\n${e && e.stack ? e.stack : e}\n`);
    }
  }
  process.stdout.write(`\n${tests.length - failed} passed, ${failed} failed\n`);
  process.exit(failed ? 1 : 0);
})();
