import { expect, test, type Page } from '@playwright/test';
import { OWNER_ROLE, parseLiveEnv, stateFile } from './support/env';
import { dangerousOf, installWriteGuard, type WriteRecord } from './support/guard';

// NFR-06 / FR-09 / FR-12 / FR-17 / FR-20 / SC-01 / SC-02 / SC-03 / SC-04 / ADR-0038 / IADR-0589（#1417 / E-43）: AST の live-ro の**4 画面の到達・
// バナー・REAL モーダルの無効送信**。網羅表（#1410）の A-003・A-004・A-007・A-025・A-042・A-043・A-057・A-058・A-066（A-002 は MSP の shell.ro）。
//
// 🔴 読み取りだけ。本番の取引・OpenD を動かす要求（`PUT …/broker-provider`・`POST /bff/opend-auth/code|resend`）は、各 test の前に
// `support/guard.ts` が `page.route` で abort する保険を張り、test の後に**送ろうとした記録が 0 件**であることを見る（`/bff/` への GET 以外の
// 要求も同じく 0 件）。REAL のモーダルは**チェックだけ**を入れて送信が無効のままであることを見て閉じる（文字入力をしない＝送信の条件を揃えない）。
// 🔴 AST の画面は MSP の共通シェルの直下（`/settings`・`/settings/risk`・`/controls`・`/opend-auth`）に載る。`/trading` の接頭辞は無い
// （`/settings`・`/settings/risk`・`/controls` は MSP の `router.test.ts` 189〜191 行、`/opend-auth` は
// `src/features/sc04-opend-auth/routes/sc04OpendAuthRoute.tsx` 30 行。網羅表の `/trading/*` は誤り）。

const cfg = parseLiveEnv(process.env);

/** 4 画面（パス・見出し・ナビの表示名。表示名は各画面の routes の `label`）。 */
const SCREENS = [
  { id: 'SC-01', path: '/settings', heading: '設定', nav: '設定' },
  { id: 'SC-02', path: '/settings/risk', heading: 'リスク設定', nav: 'リスク設定' },
  { id: 'SC-03', path: '/controls', heading: '統制状態', nav: '統制状態' },
  { id: 'SC-04', path: '/opend-auth', heading: 'OpenD 認証操作', nav: 'OpenD 認証' },
] as const;

/** MSP の共通シェルが AST のナビのグループに付ける名前（MSP の `src/features/index.ts` の `unitNavGroups`）。 */
const UNIT_NAV_GROUP = '株式自動売買';
const BROKER_PROVIDER_INTERNAL_PAPER = 0;
const BROKER_PROVIDER_MOOMOO_REAL = 1;
const STOP_LOSS_METHOD_BROKER_STOP = 0;
const PAPER_BANNER = '内蔵 paper 稼働中の警告';

let writes: WriteRecord[] = [];
test.beforeEach(async ({ page }) => {
  writes = await installWriteGuard(page);
});
test.afterEach(() => {
  // FR-20, FR-09: 危険な要求は 1 件も送ろうとしていない。読み取りだけの層なので書き込みも 0 件。
  expect(dangerousOf(writes), '発注先の変更・OpenD 認証の送信').toEqual([]);
  expect(writes, '/bff/ への GET 以外の要求').toEqual([]);
});

/** 画面を開き、見出しが出て NotFound が出ないことと、`apis` の GET が 200 であることを見る。 */
async function openScreen(page: Page, path: string, heading: string, apis: string[]) {
  const settled = Promise.allSettled(
    apis.map((api) =>
      page.waitForResponse((r) => r.request().method() === 'GET' && new URL(r.url()).pathname === api),
    ),
  );
  await page.goto(path);
  await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toHaveCount(0);
  const results = await settled;
  return Object.fromEntries(
    apis.map((api, i) => {
      const r = results[i];
      return [api, r.status === 'fulfilled' ? r.value.status() : '受け取れなかった'];
    }),
  );
}
const allOk = (apis: string[]) => Object.fromEntries(apis.map((a) => [a, 200]));

/** 取引オーナーの現在の発注先と損切りの実行機構（GET だけ）。 */
async function currentSettings(page: Page): Promise<{ brokerProvider: number; stopLossMethod: number }> {
  const res = await page.request.get('/bff/risk-controls/settings');
  expect(res.status(), 'GET /bff/risk-controls/settings').toBe(200);
  return (await res.json()) as { brokerProvider: number; stopLossMethod: number };
}

// A-002（閲覧者〔e2e-user〕で 4 画面が NotFound・ナビに株式自動売買が無い）は MSP の live-ro（`e2e/live/shell.ro.spec.ts`）が持つ（IADR-0589 決定 5）。
// AST へ e2e-user の state を残させない（残すセッションを取引オーナーの 1 つに限り、cookie を手元に置く面を狭くする）。

test.describe('未認証', () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  // NFR-06: A-003。
  test('A-003: 未認証で /settings を開くと /login へ誘導される', { tag: ['@p0', '@live-ro'] }, async ({ page }) => {
    await page.goto('/settings');
    await expect(page).toHaveURL(/\/login(\?|$)/);
    await expect(page.getByRole('heading', { name: '設定', level: 1 })).toHaveCount(0);
  });
});

test.describe('取引オーナー（e2e-trading-owner）', () => {
  test.use({ storageState: stateFile(cfg.authDir, 'trading-owner') });

  // 共通シェル: A-004。
  test(
    'A-004: ナビに株式自動売買のグループと 4 画面が出て、パンくずが ホーム / 取引 / 設定 になる',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      const me = await page.request.get('/bff/auth/me');
      expect(((await me.json()) as { roles?: string[] }).roles ?? []).toContain(OWNER_ROLE);
      await page.goto('/settings');
      const nav = page.getByRole('navigation', { name: '主要ナビゲーション' });
      await expect(nav.getByRole('heading', { name: UNIT_NAV_GROUP })).toBeVisible();
      for (const s of SCREENS)
        await expect(nav.getByRole('link', { name: s.nav, exact: true }), s.nav).toHaveAttribute('href', s.path);
      await expect(page.getByRole('navigation', { name: 'パンくず' })).toContainText(/ホーム.*取引.*設定/);
    },
  );

  // FR-12: A-007。バナーの有無は、BFF の統制状態の発注先（paper か否か）と一致する。
  test(
    'A-007: 内蔵 paper 稼働中の警告バナーの有無が、統制状態の発注先と一致する（3 画面）',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }, testInfo) => {
      const status = await page.request.get('/bff/risk-controls/status');
      expect(status.status(), 'GET /bff/risk-controls/status').toBe(200);
      const { brokerProvider } = (await status.json()) as { brokerProvider: number };
      testInfo.annotations.push({ type: '発注先（status）', description: String(brokerProvider) });
      const expected = brokerProvider === BROKER_PROVIDER_INTERNAL_PAPER ? 1 : 0;
      for (const s of SCREENS.slice(0, 3)) {
        await page.goto(s.path);
        await expect(page.getByRole('heading', { name: s.heading, level: 1 })).toBeVisible();
        await expect(page.getByRole('alert', { name: PAPER_BANNER }), s.path).toHaveCount(expected);
      }
    },
  );

  // SC-01, FR-17: A-025。
  test('A-025: 設定に到達し、現在値と履歴の取得が 200 で、現在のバージョンが出る', { tag: ['@p0', '@live-ro'] }, async ({ page }) => {
    const apis = ['/bff/assumptions', '/bff/assumptions/history'];
    expect(await openScreen(page, '/settings', '設定', apis)).toEqual(allOk(apis));
    await expect(page.getByText(/現在のバージョン:\s*\d+/)).toBeVisible();
  });

  // SC-02, FR-09: A-042。
  test(
    'A-042: リスク設定に到達し、上限・発注先・監視銘柄・履歴の取得が 200 で、発注先の変更の欄が出る',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      const apis = [
        '/bff/risk-controls/settings',
        '/bff/risk-controls/settings/history',
        '/bff/monitor/watchlist',
        '/bff/risk-controls/status',
      ];
      expect(await openScreen(page, '/settings/risk', 'リスク設定', apis)).toEqual(allOk(apis));
      await expect(page.getByText('運用段階と発注先（参照）')).toBeVisible();
      await expect(page.getByRole('form', { name: '発注先の変更' })).toBeVisible();
    },
  );

  // SC-02, FR-20: A-043。REAL の確認モーダルを開き、チェックだけを入れて送信が無効のままであることを見て閉じる（PUT は送らない）。
  test(
    'A-043: REAL を選んで理由を入れ確認モーダルを開き、チェックだけでは実弾へ切り替えるが押せないまま閉じる（発注先の変更を送らない）',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      const current = await currentSettings(page);
      test.skip(current.brokerProvider === BROKER_PROVIDER_MOOMOO_REAL, '現在の発注先が REAL（A-043 の前提外）');
      test.skip(
        current.stopLossMethod !== STOP_LOSS_METHOD_BROKER_STOP,
        '損切りの実行機構が S0 以外（確認モーダルの手前の関門で止まる。L1 の sc02-risk-settings.spec.ts が固定）',
      );
      await page.goto('/settings/risk');
      const form = page.getByRole('form', { name: '発注先の変更' });
      await form.getByRole('radio', { name: /moomoo REAL/ }).check();
      // 理由は確認モーダルを開く条件である（RiskSettingsPage.tsx 982 行: 理由が空なら「実弾への切替を確認する」が無効）。入力するのは画面の中だけ。
      await form.getByLabel('変更理由').fill('E2E live-ro の確認（送信しない）');
      await form.getByRole('button', { name: '実弾への切替を確認する' }).click();
      const dialog = page.getByRole('dialog', { name: '実弾（moomoo REAL）への切替の確認' });
      await expect(dialog).toBeVisible();
      // 送信（実弾へ切り替える）の条件は「同意のチェック かつ 『REAL』の文字入力」（RiskSettingsPage.tsx 878 行の confirmationComplete・
      // 1202 行の disabled）。チェックだけを入れ、文字は入れないので、送信は無効のままである（PUT は起きない。起きても guard が abort して落とす）。
      await dialog.getByRole('checkbox', { name: '実資金で執行されることを理解しました' }).check();
      await expect(dialog.getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();
      await dialog.getByRole('button', { name: 'キャンセル' }).click();
      await expect(dialog).toHaveCount(0);
      // 発注先は変わっていない（GET で読み直す）。
      expect((await currentSettings(page)).brokerProvider).toBe(current.brokerProvider);
    },
  );

  // SC-03, FR-10: A-057。参照専用（入力の要素が 0）。
  test(
    'A-057: 統制状態に到達し、統制状態と段階の取得が 200 で、入力の要素が 1 つも無い',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/risk-controls/status', '/bff/risk-controls/stage-gate'];
      expect(await openScreen(page, '/controls', '統制状態', apis)).toEqual(allOk(apis));
      const main = page.getByRole('main');
      for (const role of ['textbox', 'combobox', 'checkbox', 'radio', 'spinbutton', 'slider'] as const)
        await expect(main.getByRole(role), role).toHaveCount(0);
    },
  );

  // SC-03, FR-12: A-058。paper 稼働中は維持率などが「取得できていません」になる。
  test(
    'A-058: paper 稼働中なら、維持率・空売りの現況に取得できていませんが出る',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }, testInfo) => {
      const { brokerProvider } = await currentSettings(page);
      testInfo.annotations.push({ type: '発注先（settings）', description: String(brokerProvider) });
      test.skip(
        brokerProvider !== BROKER_PROVIDER_INTERNAL_PAPER,
        'paper 以外の分岐（REAL 照会の有無で数値の出方が変わる）は live-ro では状態の attach だけにする',
      );
      await page.goto('/controls');
      await expect(page.getByRole('heading', { name: '統制状態', level: 1 })).toBeVisible();
      await expect(page.getByText(/取得できていません/)).not.toHaveCount(0);
    },
  );

  // SC-04, FR-09, FR-11: A-066。状態の読み取りだけで到達する（code / resend は送らない）。
  test(
    'A-066: OpenD 認証操作に到達し、状態の取得が 200 で、検証コードの送信・再送を送らない',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/opend-auth/state'];
      expect(await openScreen(page, '/opend-auth', 'OpenD 認証操作', apis)).toEqual(allOk(apis));
    },
  );
});
