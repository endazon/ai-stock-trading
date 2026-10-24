import { expect, test } from '@playwright/test';
import { OWNER_ROLE, parseLiveEnv, stateFile } from './support/env';

// SC-01, ADR-0038, IADR-0588（#1416 / E-33）: AST の live-ro の**到達の土台**。MSP の live の setup が作った取引オーナーの storageState
// （`<E2E_AUTH_DIR>/trading-owner.json`）で、MSP の SPA 上の AST の画面（`/trading/settings`）に到達する。
// 🔴 読み取りだけ（画面を開く・画面が送る GET）。設定を変えない。発注先・OpenD 認証の要求は送らない（E-43 が abort で固定する）。

const cfg = parseLiveEnv(process.env);

test.describe('取引オーナー（e2e-trading-owner）の到達', () => {
  test.use({ storageState: stateFile(cfg.authDir, 'trading-owner') });

  // SC-01, FR-17: MSP の setup が作った state で、AST の設定画面の本体が出る。
  test(
    'MSP の setup が作った取引オーナーの state で /trading/settings を開くと、設定の見出しが出て、身元に trading-owner が載る',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      const me = await page.request.get('/bff/auth/me');
      expect(me.status(), '/bff/auth/me').toBe(200);
      expect(((await me.json()) as { roles?: string[] }).roles ?? []).toContain(OWNER_ROLE);

      const assumptions = page.waitForResponse(
        (r) => r.request().method() === 'GET' && new URL(r.url()).pathname === '/bff/assumptions',
      );
      await page.goto('/trading/settings');
      await expect(page.getByRole('heading', { name: '設定', level: 1 })).toBeVisible();
      await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toHaveCount(0);
      expect((await assumptions).status(), 'GET /bff/assumptions').toBe(200);
    },
  );
});
