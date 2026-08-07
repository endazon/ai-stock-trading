import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import type { Page, Route } from '@playwright/test';
import type {
  RiskManagementSettings,
  RiskStatusView,
  SettingsChangeEntry,
  ShortSellingStatusView,
  StageGateStatus,
} from '../src/features/risk/contracts';
import type {
  MarketMonitorSettings,
  MonitorSettingsChangeEntry,
} from '../src/features/monitor/contracts';

// SC-01/02/03, IADR-0087: E2E の BFF モック定義（test-only）。
// 画面が叩く BFF パス/メソッドを Playwright の page.route で横取りし、決定的な応答を返す（実 API に依存しない）。
// キーは `"<METHOD> <path>"`（path は /bff を除いた画面側の呼び出しパス）。値は応答（status/body）または応答を返す関数。

export type BffResponse = { status: number; body?: unknown };
export type BffHandler = BffResponse | ((route: Route) => BffResponse | Promise<BffResponse>);
export type BffConfig = Record<string, BffHandler>;

// FR-10, FR-20, SC-02, SC-03, #389, IADR-0146: リスク系のモック本文は**バックエンドの実 HTTP 応答**
// （契約フィクスチャ）を土台にする。手書きの literal で組むと、フロントが「こう返ってくるはずだ」と
// 思っている形を E2E でも検証することになり、バックエンドの改名（#329 / #333）を素通りさせる。
// フィクスチャ本体はバックエンドの xUnit（FrontendContractFixtureTests）が生成・突合しており、
// 型としての突合は src 側（contractFixtures.ts）がコンパイル時に行う。
const CONTRACT_FIXTURE_DIR = fileURLToPath(
  new URL('../src/features/risk/contract-fixtures/', import.meta.url),
);
// SC-01 §2, #340: 監視設定（MarketMonitorService）の契約フィクスチャ。置き場は領域ごとに分かれる。
const MONITOR_CONTRACT_FIXTURE_DIR = fileURLToPath(
  new URL('../src/features/monitor/contract-fixtures/', import.meta.url),
);

function loadContract<T>(fileName: string): T {
  return JSON.parse(readFileSync(CONTRACT_FIXTURE_DIR + fileName, 'utf8')) as T;
}

function loadMonitorContract<T>(fileName: string): T {
  return JSON.parse(readFileSync(MONITOR_CONTRACT_FIXTURE_DIR + fileName, 'utf8')) as T;
}

// ---- 既定のサンプル応答（受け入れ基準の主要フロー用） ----

export const ASSUMPTIONS = {
  assumptions: {
    capitalGainsTaxRate: 0.20315,
    japanCommission: { rate: 0.00055, minimum: 0, cap: 1100 },
    unitedStatesCommission: { rate: 0.00495, minimum: 0, cap: 22 },
    fxSpreadRatio: 0.0025,
    minimumExpectedProfitMultiple: 1.5,
    costLimits: { total: 50000, llm: 30000, infrastructure: 15000, data: 5000 },
  },
  version: 3,
  isResolved: true,
};

export const ASSUMPTIONS_HISTORY = [
  { actor: 'owner', reason: '税率見直し', changedAt: '2026-07-17T00:00:00Z', version: 3 },
  { actor: 'owner', reason: '初期値', changedAt: '2026-07-01T00:00:00Z', version: 1 },
];

const CONTRACT_RISK_SETTINGS = loadContract<RiskManagementSettings>('risk-controls.settings.json');
const CONTRACT_RISK_STATUS = loadContract<RiskStatusView>('risk-controls.status.json');
const CONTRACT_STAGE_GATE = loadContract<StageGateStatus>('risk-controls.stage-gate.json');
const CONTRACT_SETTINGS_HISTORY = loadContract<SettingsChangeEntry[]>(
  'risk-controls.settings-history.json',
);
// FR-10, SC-03, #340, IADR-0154: 空売りの現況。**既定では維持率・借株料の累計・自動縮小の発動履歴が
// いずれも未供給（MetricAvailability.NotSupplied = 1）である**——これが現在の事実であり、
// E2E は「未供給を未供給として描く」ことを実応答の形で検証する。
export const SHORT_SELLING: ShortSellingStatusView = loadContract<ShortSellingStatusView>(
  'risk-controls.short-selling.json',
);
// SC-01 §2, FR-13, #340: 収集パラメータ（変動閾値・クールダウン）と監視設定の変更履歴。
export const MONITOR_SETTINGS: MarketMonitorSettings =
  loadMonitorContract<MarketMonitorSettings>('monitor.settings.json');
export const MONITOR_SETTINGS_HISTORY: MonitorSettingsChangeEntry[] =
  loadMonitorContract<MonitorSettingsChangeEntry[]>('monitor.settings-history.json');

export const RISK_SETTINGS: RiskManagementSettings = {
  ...CONTRACT_RISK_SETTINGS,
  limits: { ...CONTRACT_RISK_SETTINGS.limits, maxOpenPositions: 5, losingStreakThreshold: 3 },
  // FR-20, #334: 段階（Stage 1）と発注先（現在は moomoo SIMULATE）は独立した 2 軸。
  // stage.mode は**段階が定める既定の発注先**（BrokerProvider 数値・2=moomoo SIMULATE）。
  stage: { ...CONTRACT_RISK_SETTINGS.stage, stage: 1, mode: 2 },
  brokerProvider: 2,
};

export const RISK_SETTINGS_HISTORY: SettingsChangeEntry[] = [
  { ...CONTRACT_SETTINGS_HISTORY[1], actor: 'owner', changeType: 1, reason: '上限調整' },
];

export const RISK_STATUS: RiskStatusView = {
  ...CONTRACT_RISK_STATUS,
  stage: 1,
  brokerProvider: 2, // BrokerProvider.MoomooSimulate（段階とは別の軸）
  dailyRealizedPnl: 1000,
  unrealizedPnl: -500,
  dailyPnl: 500,
  capital: 1000000,
  dailyOrderedAmount: 200000,
  // equity から解決済みの**実額**（設定側の比率とは別物・#389）。
  maxOrderAmount: 250000,
  maxDailyOrderAmount: 500000,
  drawdownRatio: 0.05,
  maxDrawdownRatio: 0.2,
  openPositionCount: 2,
  maxOpenPositions: 5,
};

// SC-02 監視銘柄（watchlist）セクション（#196・別サービス MarketMonitor）。market は数値 enum（0=日本/1=米国）。
export const WATCHLIST = [
  { symbol: '7203', market: 0 },
  { symbol: 'AAPL', market: 1 },
];

export const WATCHLIST_HISTORY = [
  { actor: 'owner', changeType: 0, reason: '監視追加', changedAt: '2026-07-17T00:00:00Z' },
];

export const STAGE_GATE: StageGateStatus = {
  ...CONTRACT_STAGE_GATE,
  currentStage: 1,
  currentSettings: { ...CONTRACT_STAGE_GATE.currentSettings, stage: 1, mode: 2 },
  history: [{ ...CONTRACT_STAGE_GATE.history[0], sequence: 1, approvedBy: 'owner', reason: '昇格' }],
  promotion: { targetStage: 2, eligible: false, unmetCriteria: [0] },
  // FR-20, #334, IADR-0142: Stage 1 の進捗は moomoo SIMULATE の実績のみ。paper 稼働日は除外して別掲する。
  stage1Progress: { qualifiedTradingDays: 42, tradeCount: 70, excludedInternalPaperDays: 3 },
};

// FR-20, #334: 内蔵 paper で稼働している状態（警告バナー・paper ラベルの検証用）。
export const RISK_SETTINGS_PAPER = { ...RISK_SETTINGS, brokerProvider: 0 };
export const RISK_STATUS_PAPER = { ...RISK_STATUS, brokerProvider: 0 };

// 既定の全ハンドラ（各画面の正常系）。個々のテストは spread で必要なキーだけ上書きする。
export function defaultBff(): BffConfig {
  return {
    'GET /assumptions': { status: 200, body: ASSUMPTIONS },
    'GET /assumptions/history': { status: 200, body: ASSUMPTIONS_HISTORY },
    'PUT /assumptions': { status: 200, body: ASSUMPTIONS },
    'GET /risk-controls/settings': { status: 200, body: RISK_SETTINGS },
    'GET /risk-controls/settings/history': { status: 200, body: RISK_SETTINGS_HISTORY },
    'PUT /risk-controls/settings/limits': { status: 200, body: RISK_SETTINGS },
    // FR-20, #334: 発注先の変更（SC-02 のみが持つ操作）。
    'PUT /risk-controls/settings/broker-provider': { status: 200, body: { settings: RISK_SETTINGS } },
    'GET /risk-controls/status': { status: 200, body: RISK_STATUS },
    'GET /risk-controls/stage-gate': { status: 200, body: STAGE_GATE },
    // FR-10, SC-03, #340: 空売りの現況（維持率・空売り比率・建玉方向・借株料累計）。
    'GET /risk-controls/short-selling': { status: 200, body: SHORT_SELLING },
    // 監視銘柄（#196・MarketMonitor `/monitor/watchlist`）。POST/DELETE は更新後の一覧を返す（成功時に再取得される）。
    'GET /monitor/watchlist': { status: 200, body: WATCHLIST },
    'GET /monitor/watchlist/history': { status: 200, body: WATCHLIST_HISTORY },
    'POST /monitor/watchlist': { status: 200, body: WATCHLIST },
    'DELETE /monitor/watchlist': { status: 200, body: WATCHLIST },
    // SC-02, FR-03, FR-13, #340, #423: 市場監視パラメータ（変動閾値・クールダウン）。
    // **2026-08-07 の裁定で SC-01 §2 から SC-02 へ移管された**（権威は MarketMonitorService）。
    // PUT は更新後の設定を返す（成功時に再取得される）。
    'GET /monitor/settings': { status: 200, body: MONITOR_SETTINGS },
    'GET /monitor/settings/history': { status: 200, body: MONITOR_SETTINGS_HISTORY },
    'PUT /monitor/settings/movement-threshold': { status: 200, body: MONITOR_SETTINGS },
    'PUT /monitor/settings/cooldown': { status: 200, body: MONITOR_SETTINGS },
    // FR-20, SC-02, #423: Stage 1 の最小取引件数（既定 100・値域 1〜1000）。
    'PUT /risk-controls/settings/stage1-minimum-trade-count': { status: 200, body: RISK_SETTINGS },
  };
}

// page.route を /bff/** に 1 本張り、method+path で config を引く。未定義パスは 404（存在秘匿と整合）。
export async function installBff(page: Page, config: BffConfig): Promise<void> {
  await page.route('**/bff/**', async (route) => {
    const req = route.request();
    const url = new URL(req.url());
    const path = url.pathname.replace(/^\/bff/, '');
    const key = `${req.method()} ${path}`;
    const handler = config[key];
    if (handler === undefined) {
      await route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
      return;
    }
    const resp = typeof handler === 'function' ? await handler(route) : handler;
    await route.fulfill({
      status: resp.status,
      contentType: 'application/json',
      body: resp.body === undefined ? '' : JSON.stringify(resp.body),
    });
  });
}

// 認証ロールを URL クエリで表現する遷移先ヘルパ。既定は非利用者（空ロール＝存在秘匿）。
export function pathWithRoles(path: string, roles: string[] = []): string {
  const q = roles.length > 0 ? `?roles=${encodeURIComponent(roles.join(','))}` : '';
  return `${path}${q}`;
}
