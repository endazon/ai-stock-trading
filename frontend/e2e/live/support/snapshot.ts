// NFR / IADR-0565 決定 2・3 / IADR-0599（#1418 / E-60）: live の取引影響層（`trading` project・L3-T）の**門と事前スナップショット**。
// `E2E_LAYER` に `trading` があるときだけ、readiness（取引オーナーの state の門）の後に 1 回走る（playwright.live.config.ts）。
//
// 門（どれか 1 つでも不成立なら **exit 2（前提未整備）**で、spec を 1 本も流さない）:
//   1. 市場の閉場: 全対象市場が閉場しており、次の開場まで 60 分以上ある（`scripts/e2e-market-closed.js`。AST の MarketSessions / MarketHolidays と同じ規則）。
//   2. in-flight（約定待ち・手仕舞い中）が無い: `GET /bff/risk-controls/in-flight-orders`（risk-management の OwnerOnly の口。
//      承認済みで終端していない新規建て・決済・予約。IADR-0600）が空であること。🔴 照会の失敗（200 でない・配列でない・届かない）も
//      不成立にする（判断できないものを「無い」と読んで書き込みを始めない。IADR-0599 決定 2）。
// 門を通ったら、取引に影響する設定を GET で全て読み、`test-results/trading-snapshot-<runId>.json` に保存する（秘密は含まない）。
// runId とスナップショットの場所は環境変数（E2E_RUN_ID・E2E_TRADING_SNAPSHOT）で、spec の worker と globalTeardown（restore.ts）へ渡す。
import { mkdirSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, request } from '@playwright/test';
import { parseLiveEnv, stateFile, type LiveEnv } from './env';
import { NOT_READY_EXIT_CODE } from './readiness';
import { revokeOwnerSession } from './teardown';

const require = createRequire(import.meta.url);
const SCRIPTS = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '..', 'scripts');

interface MarketGate {
  closedGateProblems(now: Date, opts?: { markets?: string[]; minMinutes?: number }): string[];
}

/** 復元の本体（`scripts/e2e-trading-restore.js`。単独実行の口と同じものを使う）。 */
export interface TradingClient {
  get(path: string): Promise<{ status: number; json: unknown }>;
  send(method: string, path: string, body: unknown): Promise<void>;
}
export interface DiffItem {
  key: string;
  before: unknown;
  after: unknown;
  restorable: boolean;
}
export interface TradingSnapshot {
  runId: string;
  takenAt: string;
  responses: Record<string, unknown>;
}
export interface ReconcileResult {
  firstDiff: DiffItem[];
  attempted: string[];
  errors: string[];
  remaining: DiffItem[];
}
export interface RestoreCore {
  SNAPSHOT_PATHS: string[];
  takeSnapshot(client: TradingClient, runId: string, now?: Date): Promise<TradingSnapshot>;
  reconcile(client: TradingClient, snapshot: TradingSnapshot, opts?: { dryRun?: boolean }): Promise<ReconcileResult>;
  wrapPlaywrightRequest(ctx: unknown): TradingClient;
  summarize(result: ReconcileResult): string[];
}

export const marketGate = require(join(SCRIPTS, 'e2e-market-closed.js')) as MarketGate;
export const restoreCore = require(join(SCRIPTS, 'e2e-trading-restore.js')) as RestoreCore;

/** in-flight の照会の口（BFF）。 */
export const IN_FLIGHT_PATH = '/bff/risk-controls/in-flight-orders';

/** risk-management の未終端の注文の 1 行（IADR-0600。銘柄・市場・種別・状態・承認時刻だけ）。 */
interface InFlightOrderRow {
  symbol?: unknown;
  market?: unknown;
  kind?: unknown;
  state?: unknown;
  approvedAt?: unknown;
}

const KIND_LABELS: Record<number, string> = { 1: '新規建て', 2: '決済' };
const STATE_LABELS: Record<number, string> = { 1: '承認済み・未終端', 2: '予約' };

/** in-flight の照会の結果から、門の不成立の理由を決める（純関数）。空なら通す。 */
export function inFlightProblemsFrom(res: { status: number; json: unknown }): string[] {
  if (res.status !== 200) return [`in-flight の照会（${IN_FLIGHT_PATH}）が ${res.status}（判断できないので流さない）`];
  if (!Array.isArray(res.json)) return [`in-flight の照会の応答が配列でない（判断できないので流さない）`];
  const rows = res.json as InFlightOrderRow[];
  if (rows.length === 0) return [];
  const items = rows.map((r) => {
    const kind = KIND_LABELS[Number(r.kind)] ?? `種別 ${String(r.kind)}`;
    const state = STATE_LABELS[Number(r.state)] ?? `状態 ${String(r.state)}`;
    return `${String(r.symbol)}（${kind}・${state}・承認 ${String(r.approvedAt)}）`;
  });
  return [`in-flight の注文が ${rows.length} 件ある: ${items.join(', ')}`];
}

/** in-flight の門（BFF へ照会する）。届かないときも不成立。 */
export async function inFlightProblems(client: TradingClient): Promise<string[]> {
  try {
    return inFlightProblemsFrom(await client.get(IN_FLIGHT_PATH));
  } catch (e) {
    return [`in-flight の照会が届かない（${e instanceof Error ? e.message.split('\n')[0] : String(e)}。判断できないので流さない）`];
  }
}

/** trading の門の判定。時刻と in-flight の判定を差し替えられる。両方の門の理由を並べる（片方で止めて、もう片方を隠さない）。 */
export async function tradingGateProblems(
  now: Date,
  inFlight: () => Promise<string[]>,
  gate: MarketGate = marketGate,
): Promise<string[]> {
  return [...gate.closedGateProblems(now), ...(await inFlight())];
}

export function runIdOf(env: Record<string, string | undefined>, now: Date = new Date()): string {
  const fixed = env.E2E_RUN_ID;
  if (fixed !== undefined && fixed !== '') {
    if (!/^[A-Za-z0-9-]{1,40}$/.test(fixed)) throw new Error(`E2E_RUN_ID は英数字とハイフン 40 字以内: ${fixed}`);
    return fixed;
  }
  return now.toISOString().replace(/[-:]/g, '').replace(/\..*$/, '').replace('T', '-');
}

export function snapshotFile(runId: string): string {
  return join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'test-results', `trading-snapshot-${runId}.json`);
}

async function revokeBeforeExit(cfg: LiveEnv): Promise<void> {
  const browser = await chromium.launch();
  try {
    const result = await revokeOwnerSession(cfg, browser);
    if (result.failure)
      process.stderr.write(
        `[e2e-live] 門で落ちる前に取引オーナーを失効させられなかった（state は消した）: ${result.failure}\n`,
      );
  } finally {
    await browser.close();
  }
}

export default async function tradingGlobalSetup(): Promise<void> {
  const cfg = parseLiveEnv(process.env);
  if (!cfg.live || !cfg.layers.includes('trading')) return;
  const ctx = await request.newContext({
    baseURL: cfg.edgeUrl,
    storageState: stateFile(cfg.authDir, 'trading-owner'),
    ignoreHTTPSErrors: cfg.insecureTls,
  });
  const client = restoreCore.wrapPlaywrightRequest(ctx);
  const failNotReady = async (message: string): Promise<never> => {
    await ctx.dispose();
    // 門で exit 2 に抜けると globalTeardown は走らないので、取引オーナーの失効を先に済ませる（readiness と同じ）。
    await revokeBeforeExit(cfg);
    process.stderr.write(message);
    process.exit(NOT_READY_EXIT_CODE);
  };
  const problems = await tradingGateProblems(new Date(), () => inFlightProblems(client));
  if (problems.length) {
    await failNotReady(
      `[e2e-live] trading の前提未整備（exit ${NOT_READY_EXIT_CODE}）。test は 1 件も流していない:\n  - ${problems.join('\n  - ')}\n`,
    );
  }
  const runId = runIdOf(process.env);
  let snapshot: TradingSnapshot | undefined;
  try {
    snapshot = await restoreCore.takeSnapshot(client, runId);
  } catch (e) {
    await failNotReady(
      `[e2e-live] trading のスナップショットを取れない（exit ${NOT_READY_EXIT_CODE}）: ${e instanceof Error ? e.message : String(e)}\n`,
    );
  }
  await ctx.dispose();
  const file = snapshotFile(runId);
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, JSON.stringify(snapshot, null, 2));
  process.env.E2E_RUN_ID = runId;
  process.env.E2E_TRADING_SNAPSHOT = file;
  process.stdout.write(`[e2e-live] trading の門を通り、スナップショットを取った: ${file}\n`);
}
