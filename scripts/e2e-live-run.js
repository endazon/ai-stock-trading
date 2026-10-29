#!/usr/bin/env node
'use strict';
/*
 * e2e-live-run.js — NFR, #1420（E-62）, IADR-0602: live の E2E（`frontend/playwright.live.config.ts`）を**手元で**流す口。
 *
 * CI のジョブで流す実行場所は未決（#1506。public リポジトリに self-hosted のランナーを置く危険があるため、オーナーの判断事項）。
 * それまでは、取引オーナーの storageState を持つ手元の端末から、この口で流す。
 *
 *   cd frontend && E2E_AUTH_DIR=<MSP の live の setup が state を書いた置き場> npm run e2e:live -- --layer trading
 *
 * すること:
 *   1. 層に trading があれば、**最初に**市場の閉場の門（scripts/e2e-market-closed.js）を確かめる。不成立なら Playwright を起動しない。
 *   2. runId を決めて（E2E_RUN_ID・無ければ時刻）、E2E_LIVE=1・E2E_LAYER を与えて Playwright を起動する。
 *   3. 終了コードを読む: 0＝成功 / 2＝前提未整備（門）/ それ以外＝失敗。
 *      🔴 **前提未整備は赤にしない**（この口は exit 0 で終える。skip の緑とも、製品の不具合の赤とも区別して「前提未整備」と書く）。
 *   4. trading 層なら、未復元一覧（frontend/test-results/trading-restore-<runId>.json の残った差分と復元の失敗）を出す。
 *   5. docs/tests/E2E_LIVE.md の実行記録の表へ貼る 1 行を出す（flaky とイメージの作成日時は人が書く）。
 *
 * 終了コード: 0＝成功か前提未整備 / 1＝失敗（Playwright の失敗・未復元が残った）/ 64＝使い方の誤り。
 */

const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');
const { closedGateProblems } = require('./e2e-market-closed.js');

const FRONTEND = path.join(__dirname, '..', 'frontend');
const LAYERS = ['ro', 'trading'];
const NOT_READY = 2;
const USAGE = 64;

function parseArgs(argv) {
  let layer = 'ro';
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--layer') {
      layer = argv[++i] || '';
    } else {
      throw new Error(`未知の引数: ${argv[i]}`);
    }
  }
  const layers = layer.split(',').map((s) => s.trim()).filter(Boolean);
  if (layers.length === 0) throw new Error('--layer が空である（ro / trading のカンマ区切り）');
  for (const l of layers) if (!LAYERS.includes(l)) throw new Error(`--layer の値域外: ${l}（ro / trading のカンマ区切り）`);
  return { layers: [...new Set(layers)] };
}

/** runId（frontend/e2e/live/support/snapshot.ts の runIdOf と同じ形）。 */
function runIdOf(env, now = new Date()) {
  const fixed = env.E2E_RUN_ID;
  if (fixed) {
    if (!/^[A-Za-z0-9-]{1,40}$/.test(fixed)) throw new Error(`E2E_RUN_ID は英数字とハイフン 40 字以内: ${fixed}`);
    return fixed;
  }
  return now.toISOString().replace(/[-:]/g, '').replace(/\..*$/, '').replace('T', '-');
}

/** 門（globalSetup）が exit 2 で抜けるときに必ず出す目印（readiness.ts・snapshot.ts の行）。 */
const NOT_READY_MARKER = '前提未整備（exit 2）';

/**
 * Playwright の終了コードと出力を、記録の結果と、この口の終了コードへ写す（純関数）。
 * 🔴 「前提未整備」は、終了コードが 2 **かつ**門の目印の行が出力にあるときだけ。目印の無い 2 は失敗として扱う
 *    （Playwright や Node が別の理由で 2 を返すのを、門で止まったと取り違えない）。
 */
function verdictOf(playwrightStatus, output = '') {
  if (playwrightStatus === 0) return { result: '成功', exit: 0 };
  if (playwrightStatus === NOT_READY && output.includes(NOT_READY_MARKER)) return { result: '前提未整備', exit: 0 }; // 🔴 赤にしない
  return { result: '失敗', exit: 1 };
}

/**
 * 未復元一覧（項目名と、復元の失敗の行）。ファイルが無いか、**この起動より前**に書かれたもの（前回の実行の残り）なら
 * null（trading 層が今回は書き込みを始めていない）。
 */
function remainingOf(
  reportFile,
  startedAtMs = 0,
  read = (f) => fs.readFileSync(f, 'utf8'),
  mtimeOf = (f) => (fs.existsSync(f) ? fs.statSync(f).mtimeMs : null),
) {
  const mtime = mtimeOf(reportFile);
  if (mtime === null || mtime < startedAtMs) return null;
  const r = JSON.parse(read(reportFile));
  return [...(r.remaining || []).map((d) => d.key), ...(r.errors || []).map((e) => `復元の失敗: ${e}`)];
}

/** docs/tests/E2E_LIVE.md の実行記録の表へ貼る 1 行（純関数）。 */
function recordRow({ date, runId, layers, result, remaining }) {
  const left = remaining === null ? '—（書き込みなし）' : remaining.length ? remaining.join('、') : 'なし';
  return `| ${date} | ${runId} | ${layers.join(',')} | ${result} | ${left} | （書く） | （書く） |`;
}

/** Playwright を起動し、出力をそのまま流しつつ溜める（目印の判定に使う）。 */
function runPlaywright(env) {
  const cli = require.resolve('@playwright/test/cli', { paths: [FRONTEND] });
  return new Promise((resolve) => {
    let output = '';
    const child = spawn(process.execPath, [cli, 'test', '--config', 'playwright.live.config.ts'], {
      cwd: FRONTEND,
      stdio: ['inherit', 'pipe', 'pipe'],
      env,
    });
    child.stdout.on('data', (b) => {
      process.stdout.write(b);
      output += b.toString();
    });
    child.stderr.on('data', (b) => {
      process.stderr.write(b);
      output += b.toString();
    });
    child.on('close', (status) => resolve({ status, output }));
  });
}

async function main() {
  let args;
  try {
    args = parseArgs(process.argv.slice(2));
  } catch (e) {
    process.stderr.write(`[e2e-live-run] ${e.message}\n`);
    return USAGE;
  }
  const now = new Date();
  const date = now.toISOString().slice(0, 10);
  let runId;
  try {
    runId = runIdOf(process.env, now);
  } catch (e) {
    process.stderr.write(`[e2e-live-run] ${e.message}\n`);
    return USAGE;
  }

  // 1. trading 層は、Playwright を起動する前に市場の閉場の門を確かめる（門は globalSetup でもう一度確かめる）。
  if (args.layers.includes('trading')) {
    const problems = closedGateProblems(now);
    if (problems.length) {
      process.stdout.write(`[e2e-live-run] 前提未整備（赤にしない）。Playwright を起動していない:\n  - ${problems.join('\n  - ')}\n`);
      process.stdout.write(`${recordRow({ date, runId, layers: args.layers, result: '前提未整備', remaining: null })}\n`);
      return 0;
    }
  }

  // 2. Playwright を起動する（frontend の依存から）。🔴 同じ runId の前回の復元レポートは先に消す（前回の未復元を今回の結果に混ぜない）。
  const reportFile = path.join(FRONTEND, 'test-results', `trading-restore-${runId}.json`);
  fs.rmSync(reportFile, { force: true });
  const startedAtMs = Date.now();
  const r = await runPlaywright({ ...process.env, E2E_LIVE: '1', E2E_LAYER: args.layers.join(','), E2E_RUN_ID: runId });
  const v = verdictOf(r.status, r.output);

  // 4. 未復元一覧（この起動の後に書かれたものだけ）。
  let remaining = null;
  if (args.layers.includes('trading')) {
    remaining = remainingOf(reportFile, startedAtMs);
    if (remaining && remaining.length) {
      process.stdout.write(`::error::[e2e-live-run] 未復元: ${remaining.join('、')}（SC-01/02 の画面で戻す）\n`);
    }
  }
  const exit = remaining && remaining.length ? 1 : v.exit;
  const result = remaining && remaining.length && v.result === '成功' ? '失敗' : v.result;

  // 5. 実行記録の 1 行。
  process.stdout.write(`[e2e-live-run] ${result}（Playwright の終了コード ${r.status}）。docs/tests/E2E_LIVE.md の表へ貼る行:\n`);
  process.stdout.write(`${recordRow({ date, runId, layers: args.layers, result, remaining })}\n`);
  return exit;
}

module.exports = { parseArgs, runIdOf, verdictOf, remainingOf, recordRow, NOT_READY, NOT_READY_MARKER, USAGE };

if (require.main === module) main().then((code) => process.exit(code));
