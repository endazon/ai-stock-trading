// NFR / IADR-0565 決定 3 / IADR-0599（#1418 / E-60）: live の取引影響層（`trading` project・L3-T）の**突合と復元**。
// `E2E_LAYER` に `trading` があるときだけ、取引オーナーの失効（teardown.ts）より**先に**走る（playwright.live.config.ts の配列の順）。
//
// スナップショット（snapshot.ts が取った）と現在値を突合し、差分があれば理由 `[e2e <runId> restore]` で戻し、もう一度突合する。
// 🔴 **それでも差分が残れば赤**（例外を投げる＝Playwright の実行を失敗にする）。項目名を `::error::` で出し、差分の中身は成果物
//    `test-results/trading-restore-<runId>.json` に残す（オーナーが SC-01/02 の画面で戻す）。
// 🔴 process.exit しない —— 後ろの teardown.ts（取引オーナーの失効）を必ず走らせる。Playwright は globalTeardown の例外を記録して
//    残りの teardown を続け、実行の終了コードを 1 にする。
// 中断（Playwright の kill）で本 teardown が走らなかったときは、`node scripts/e2e-trading-restore.js --snapshot <file> --live` で同じ処理を単独で流す。
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { request } from '@playwright/test';
import { parseLiveEnv, stateFile } from './env';
import { restoreCore, type ReconcileResult, type TradingSnapshot } from './snapshot';

/** 突合の結果から、赤にすべきかと人へ出す行を決める（純関数）。 */
export function verdictOf(result: ReconcileResult): { failed: boolean; lines: string[] } {
  return {
    failed: result.remaining.length > 0 || result.errors.length > 0,
    lines: restoreCore.summarize(result),
  };
}

export default async function tradingGlobalTeardown(): Promise<void> {
  const cfg = parseLiveEnv(process.env);
  if (!cfg.live || !cfg.layers.includes('trading')) return;
  const file = process.env.E2E_TRADING_SNAPSHOT;
  // 門で落ちた実行はここへ来ない（globalSetup の exit 2）。スナップショットが無いのは書き込みを始めていないということ。
  if (!file || !existsSync(file)) {
    process.stdout.write('[e2e-live] trading のスナップショットが無い（書き込みを始めていない）。突合しない\n');
    return;
  }
  const snapshot = JSON.parse(readFileSync(file, 'utf8')) as TradingSnapshot;
  const ctx = await request.newContext({
    baseURL: cfg.edgeUrl,
    storageState: stateFile(cfg.authDir, 'trading-owner'),
    ignoreHTTPSErrors: cfg.insecureTls,
  });
  let result: ReconcileResult;
  try {
    result = await restoreCore.reconcile(restoreCore.wrapPlaywrightRequest(ctx), snapshot);
  } finally {
    await ctx.dispose();
  }
  const { failed, lines } = verdictOf(result);
  const report = file.replace(/trading-snapshot-/, 'trading-restore-');
  writeFileSync(report, JSON.stringify(result, null, 2));
  process.stdout.write(`[e2e-live] trading の突合（run ${snapshot.runId}）:\n  - ${lines.join('\n  - ')}\n`);
  if (failed) {
    process.stdout.write(
      `::error::[e2e-live] trading の復元の後も差分が残った（${result.remaining.map((d) => d.key).join(', ') || '復元の失敗'}）。SC-01/02 の画面で戻す。差分: ${report}\n`,
    );
    throw new Error(`trading の復元の後も差分が残った: ${report}`);
  }
}
