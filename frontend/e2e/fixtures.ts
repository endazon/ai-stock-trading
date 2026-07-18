import type { Page, Route } from '@playwright/test';

// SC-01/02/03, IADR-0087: E2E の BFF モック定義（test-only）。
// 画面が叩く BFF パス/メソッドを Playwright の page.route で横取りし、決定的な応答を返す（実 API に依存しない）。
// キーは `"<METHOD> <path>"`（path は /bff を除いた画面側の呼び出しパス）。値は応答（status/body）または応答を返す関数。

export type BffResponse = { status: number; body?: unknown };
export type BffHandler = BffResponse | ((route: Route) => BffResponse | Promise<BffResponse>);
export type BffConfig = Record<string, BffHandler>;

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

export const RISK_SETTINGS = {
  guard: {
    enabledProductTypes: [0],
    enabledMarkets: [0, 1],
    bannedSymbols: [],
    preventSameDayReentry: true,
    prohibitManipulativeOrderPatterns: true,
  },
  limits: {
    maxOrderAmount: 300000,
    maxDailyOrderAmount: 500000,
    maxOpenPositions: 5,
    dailyLossLimitRatio: 0.05,
    perTradeRiskRatio: 0.01,
    maxDrawdownRatio: 0.2,
    losingStreakThreshold: 3,
    losingStreakSizeFactor: 0.5,
  },
  stage: { stage: 1, mode: 0, capitalCap: 1000000 },
};

export const RISK_SETTINGS_HISTORY = [
  { actor: 'owner', changeType: 1, reason: '上限調整', changedAt: '2026-07-17T00:00:00Z' },
];

export const RISK_STATUS = {
  killSwitchEngaged: false,
  dailyLossLockoutActive: false,
  lockoutReleaseOn: null,
  tradingPaused: false,
  activeControl: 0,
  newEntriesBlocked: false,
  stage: 1,
  dailyRealizedPnl: 1000,
  unrealizedPnl: -500,
  dailyPnl: 500,
  capital: 1000000,
  dailyOrderedAmount: 200000,
  maxDailyOrderAmount: 500000,
  drawdownRatio: 0.05,
  maxDrawdownRatio: 0.2,
  openPositionCount: 2,
  maxOpenPositions: 5,
};

export const STAGE_GATE = {
  currentStage: 1,
  currentSettings: { stage: 1, mode: 0, capitalCap: 1000000 },
  history: [
    {
      sequence: 1,
      fromStage: 0,
      toStage: 1,
      kind: 0,
      approvedBy: 'owner',
      occurredAtUtc: '2026-07-10T00:00:00Z',
      reason: '昇格',
    },
  ],
  promotion: { targetStage: 2, eligible: false, unmetCriteria: [0] },
  withdrawal: { triggered: false, reason: null, haltNewEntries: false, proposedStage: null },
};

// 既定の全ハンドラ（各画面の正常系）。個々のテストは spread で必要なキーだけ上書きする。
export function defaultBff(): BffConfig {
  return {
    'GET /assumptions': { status: 200, body: ASSUMPTIONS },
    'GET /assumptions/history': { status: 200, body: ASSUMPTIONS_HISTORY },
    'PUT /assumptions': { status: 200, body: ASSUMPTIONS },
    'GET /risk-controls/settings': { status: 200, body: RISK_SETTINGS },
    'GET /risk-controls/settings/history': { status: 200, body: RISK_SETTINGS_HISTORY },
    'PUT /risk-controls/settings/limits': { status: 200, body: RISK_SETTINGS },
    'GET /risk-controls/status': { status: 200, body: RISK_STATUS },
    'GET /risk-controls/stage-gate': { status: 200, body: STAGE_GATE },
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
