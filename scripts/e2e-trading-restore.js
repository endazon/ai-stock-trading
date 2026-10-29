#!/usr/bin/env node
'use strict';
/*
 * e2e-trading-restore.js — NFR, #1418（E-60）, IADR-0565 決定 3, IADR-0599:
 * live の取引影響層（`trading` project・L3-T）の**事前スナップショットと、突合・復元**の本体。
 *
 * - 本体（純関数＋要求の口を差し替えられる関数）は、Playwright の globalSetup / globalTeardown
 *   （`frontend/e2e/live/support/snapshot.ts` / `restore.ts`）と、本ファイルの単独実行の両方が使う（規則を 2 か所に置かない）。
 * - 単独実行は、Playwright が kill されて globalTeardown が走らなかったときの復元の口である:
 *     node scripts/e2e-trading-restore.js --snapshot <file> [--live]
 *   `--live` が無ければ突合して差分を出すだけ（書き込まない）。`--live` のときだけ復元の PUT / POST / DELETE を送る。
 *   到達先と storageState は live の config と同じ環境変数（E2E_EDGE_URL・E2E_AUTH_DIR・E2E_INSECURE_TLS）から読む。
 * - 終了コード: 0＝差分なし（復元できた）／1＝差分が残った（オーナーが画面 SC-01/02 で戻す）／2＝前提未整備。
 *
 * 🔴 送らないもの（IADR-0565 決定 1）: `PUT …/settings/broker-provider`・`POST /opend-auth/*`・`PUT …/settings/stop-loss-method`。
 *    発注先・損切り機構に差分があっても**復元しない**（差分として残し、赤にする）。
 * 🔴 復元できる項目は、要求の形が単純で、戻すことが取引を新たに動かさない項目に限る（下の ITEMS の restore）。
 *    それ以外の差分は、復元を試みずに「残った差分」として報告する（推測で全置換の PUT を組み立てない）。
 */

const fs = require('fs');
const path = require('path');

const NOT_READY_EXIT_CODE = 2;
const RESTORE_FAILED_EXIT_CODE = 1;
const CSRF_HEADER = { 'X-MSP-CSRF': '1' }; // MSP の BFF の 2 枚目の壁（値に意味は無い。SPA の apiClient と同じ）

/** スナップショットで読む GET（MSP#2013 コメント 3 §5.3 の 1）。 */
const SNAPSHOT_PATHS = [
  '/bff/risk-controls/settings',
  '/bff/risk-controls/settings/pre-close',
  '/bff/risk-controls/settings/theme-concentration',
  '/bff/monitor/settings',
  '/bff/monitor/watchlist',
  '/bff/assumptions',
];

const sortWatchlist = (list) =>
  [...(list || [])]
    .map((s) => ({ symbol: s.symbol, market: s.market }))
    .sort((a, b) => a.market - b.market || (a.symbol < b.symbol ? -1 : a.symbol > b.symbol ? 1 : 0));

const keyOf = (s) => `${s.market}:${s.symbol}`;

/**
 * 突合する項目。`pick` は GET の応答から比べる値を取り出す（版・保存の有無など、同値の書き込みでも動く欄は比べない）。
 * `restore` があるものだけ復元する。無いものは差分が残れば赤（画面で戻す）。
 */
const ITEMS = [
  {
    key: 'limits',
    from: '/bff/risk-controls/settings',
    pick: (v) => v.limits,
    restore: (client, before, _after, reason) =>
      client.send('PUT', '/bff/risk-controls/settings/limits', { limits: before, reason }),
  },
  { key: 'guard', from: '/bff/risk-controls/settings', pick: (v) => v.guard },
  { key: 'stage', from: '/bff/risk-controls/settings', pick: (v) => v.stage },
  // 🔴 発注先と損切り機構は復元しない（送らない。IADR-0565 決定 1）。差分は赤として人へ届ける。
  { key: 'brokerProvider', from: '/bff/risk-controls/settings', pick: (v) => v.brokerProvider },
  { key: 'stopLossMethod', from: '/bff/risk-controls/settings', pick: (v) => v.stopLossMethod },
  {
    key: 'stage1MinimumTradeCount',
    from: '/bff/risk-controls/settings',
    pick: (v) => v.stage1MinimumTradeCount,
    restore: (client, before, _after, reason) =>
      client.send('PUT', '/bff/risk-controls/settings/stage1-minimum-trade-count', {
        minimumTradeCount: before,
        reason,
      }),
  },
  { key: 'highVolatility', from: '/bff/risk-controls/settings', pick: (v) => v.highVolatility },
  // 閉場前の統制・テーマは**効いている値**だけを比べる（同値の保存で「保存値の有無・由来」は変わるが、効く値は変わらない）。
  {
    key: 'preClose',
    from: '/bff/risk-controls/settings/pre-close',
    pick: (v) => ({ cutoff: v.effectiveCutoff, flatten: v.effectiveFlatten }),
  },
  { key: 'themeConcentration', from: '/bff/risk-controls/settings/theme-concentration', pick: (v) => v.effective },
  {
    key: 'movementThresholdRatio',
    from: '/bff/monitor/settings',
    pick: (v) => v.movementThresholdRatio,
    restore: (client, before, _after, reason) =>
      client.send('PUT', '/bff/monitor/settings/movement-threshold', { movementThresholdRatio: before, reason }),
  },
  {
    key: 'cooldown',
    from: '/bff/monitor/settings',
    pick: (v) => v.cooldown,
    restore: (client, before, _after, reason) =>
      client.send('PUT', '/bff/monitor/settings/cooldown', { cooldown: before, reason }),
  },
  {
    // 監視銘柄は個別操作（全置換しない）。増えた銘柄を削除し、消えた銘柄を足し戻す。
    key: 'watchlist',
    from: '/bff/monitor/watchlist',
    pick: (v) => sortWatchlist(v),
    restore: async (client, before, after, reason) => {
      const want = new Set(before.map(keyOf));
      const have = new Set(after.map(keyOf));
      for (const s of after) if (!want.has(keyOf(s))) await client.send('DELETE', '/bff/monitor/watchlist', { ...s, reason });
      for (const s of before) if (!have.has(keyOf(s))) await client.send('POST', '/bff/monitor/watchlist', { ...s, reason });
    },
  },
  // 全体前提条件は値だけを比べる（版は同値の保存でも進む）。全置換の PUT は版の扱いを伴うので復元しない（画面で戻す）。
  { key: 'assumptions', from: '/bff/assumptions', pick: (v) => v.assumptions },
];

/** 順序に依らない比較のため、オブジェクトのキーを並べ替えて直列化する。 */
function canonical(value) {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (value && typeof value === 'object') {
    return `{${Object.keys(value)
      .sort()
      .map((k) => `${JSON.stringify(k)}:${canonical(value[k])}`)
      .join(',')}}`;
  }
  return JSON.stringify(value === undefined ? null : value);
}

/** GET の応答の集合（path → JSON）から、項目ごとの比べる値を取り出す。 */
function projectItems(responses) {
  const out = {};
  for (const item of ITEMS) {
    const raw = responses[item.from];
    if (raw === undefined) throw new Error(`スナップショットに ${item.from} が無い`);
    out[item.key] = item.pick(raw);
  }
  return out;
}

/** 差分のある項目（純関数）。 */
function diffItems(before, after) {
  return ITEMS.filter((item) => canonical(before[item.key]) !== canonical(after[item.key])).map((item) => ({
    key: item.key,
    before: before[item.key],
    after: after[item.key],
    restorable: typeof item.restore === 'function',
  }));
}

/** スナップショットで読む GET を全て読む。1 つでも 200 でなければ例外（不完全なスナップショットで書き込みを始めない）。 */
async function readAll(client) {
  const responses = {};
  for (const p of SNAPSHOT_PATHS) {
    const res = await client.get(p);
    if (res.status !== 200) throw new Error(`GET ${p} が ${res.status}（スナップショットを取れない）`);
    responses[p] = res.json;
  }
  return responses;
}

async function takeSnapshot(client, runId, now = new Date()) {
  const responses = await readAll(client);
  projectItems(responses); // 形の検査（欄が無ければここで落とす）
  return { runId, takenAt: now.toISOString(), responses };
}

/**
 * 突合して、差分があれば復元し、もう一度突合する。
 * 返り値の remaining が空なら成功。dryRun のときは書き込まない（remaining は差分そのもの）。
 */
async function reconcile(client, snapshot, { dryRun = false } = {}) {
  const before = projectItems(snapshot.responses);
  const firstDiff = diffItems(before, projectItems(await readAll(client)));
  const reason = `[e2e ${snapshot.runId} restore]`;
  const attempted = [];
  const errors = [];
  if (!dryRun) {
    for (const d of firstDiff) {
      if (!d.restorable) continue;
      const item = ITEMS.find((i) => i.key === d.key);
      attempted.push(d.key);
      try {
        await item.restore(client, d.before, d.after, reason);
      } catch (e) {
        errors.push(`${d.key}: ${e instanceof Error ? e.message : String(e)}`);
      }
    }
  }
  const remaining = dryRun ? firstDiff : diffItems(before, projectItems(await readAll(client)));
  return { firstDiff, attempted, errors, remaining };
}

/** Playwright の APIRequestContext を、本体が使う最小の口（get / send）へ包む。 */
function wrapPlaywrightRequest(ctx) {
  return {
    async get(p) {
      const res = await ctx.get(p, { failOnStatusCode: false, timeout: 30_000 });
      const status = res.status();
      return { status, json: status === 200 ? await res.json() : null };
    },
    async send(method, p, body) {
      const res = await ctx.fetch(p, {
        method,
        data: body,
        headers: { ...CSRF_HEADER, 'Content-Type': 'application/json' },
        failOnStatusCode: false,
        timeout: 30_000,
      });
      if (res.status() >= 300) throw new Error(`${method} ${p} が ${res.status()}`);
    },
  };
}

/** 結果の要約（値は出さない。項目名だけ）。 */
function summarize(result) {
  const names = (list) => list.map((d) => `${d.key}${d.restorable ? '' : '（復元しない項目）'}`).join(', ');
  return [
    `突合の最初の差分: ${result.firstDiff.length ? names(result.firstDiff) : 'なし'}`,
    `復元を試みた項目: ${result.attempted.length ? result.attempted.join(', ') : 'なし'}`,
    ...result.errors.map((e) => `復元の失敗: ${e}`),
    `残った差分: ${result.remaining.length ? names(result.remaining) : 'なし'}`,
  ];
}

function parseArgs(argv) {
  const out = { snapshot: '', live: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--snapshot') {
      out.snapshot = argv[++i] || '';
    } else if (a === '--live') {
      out.live = true;
    } else {
      throw new Error(`未知の引数: ${a}`);
    }
  }
  if (!out.snapshot) throw new Error('--snapshot <file> が要る');
  return out;
}

module.exports = {
  NOT_READY_EXIT_CODE,
  RESTORE_FAILED_EXIT_CODE,
  SNAPSHOT_PATHS,
  ITEMS,
  canonical,
  projectItems,
  diffItems,
  takeSnapshot,
  reconcile,
  wrapPlaywrightRequest,
  summarize,
  parseArgs,
};

async function main() {
  let args;
  try {
    args = parseArgs(process.argv.slice(2));
  } catch (e) {
    process.stderr.write(`[e2e-trading-restore] ${e.message}\n`);
    return NOT_READY_EXIT_CODE;
  }
  const authDir = process.env.E2E_AUTH_DIR || '';
  const edgeUrl = process.env.E2E_EDGE_URL || 'https://localhost';
  const stateFile = path.join(authDir, 'trading-owner.json');
  if (!fs.existsSync(args.snapshot)) {
    process.stderr.write(`[e2e-trading-restore] スナップショットが無い: ${args.snapshot}\n`);
    return NOT_READY_EXIT_CODE;
  }
  if (!authDir || !fs.existsSync(stateFile)) {
    process.stderr.write(
      `[e2e-trading-restore] 取引オーナーの storageState が無い: ${stateFile}（E2E_AUTH_DIR。MSP の live の setup を E2E_KEEP_SESSIONS=trading-owner で流す）\n`,
    );
    return NOT_READY_EXIT_CODE;
  }
  // Playwright は frontend の依存から読む（scripts は依存を持たない）。
  const { createRequire } = require('module');
  const frontendRequire = createRequire(path.join(__dirname, '..', 'frontend', 'package.json'));
  const { request } = frontendRequire('@playwright/test');
  const snapshot = JSON.parse(fs.readFileSync(args.snapshot, 'utf8'));
  const ctx = await request.newContext({
    baseURL: edgeUrl,
    storageState: stateFile,
    ignoreHTTPSErrors: process.env.E2E_INSECURE_TLS === '1',
  });
  try {
    const result = await reconcile(wrapPlaywrightRequest(ctx), snapshot, { dryRun: !args.live });
    const lines = summarize(result);
    process.stdout.write(`[e2e-trading-restore] ${args.live ? '復元' : '突合のみ（--live なし）'}: run ${snapshot.runId}\n  - ${lines.join('\n  - ')}\n`);
    if (result.remaining.length || result.errors.length) {
      process.stdout.write(`::error::[e2e-trading-restore] 差分が残った。SC-01/02 の画面で戻す（項目は上の「残った差分」）\n`);
      return RESTORE_FAILED_EXIT_CODE;
    }
    return 0;
  } catch (e) {
    process.stderr.write(`[e2e-trading-restore] 突合できない: ${e instanceof Error ? e.message : String(e)}\n`);
    return NOT_READY_EXIT_CODE;
  } finally {
    await ctx.dispose();
  }
}

if (require.main === module) {
  main().then((code) => process.exit(code));
}
