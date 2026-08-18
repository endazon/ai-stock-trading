import { defineConfig, devices } from '@playwright/test';

// SC-01/02/03, IADR-0087: フロント3画面の Playwright E2E 設定（test-only）。
// - webServer は E2E ハーネス専用の vite（Docker/実 API 不要）。SPA なので deep link は index.html へフォールバックする。
// - BFF 応答は各テストの page.route でモックする（実 BFF/Keycloak/クラスタ疎通は #82 系／MSP#284 の live 検証へ分離）。
// - ブラウザは chromium のみ（UI フローの決定性・CI 速度を優先。クロスブラウザ検証は本 issue の目的外）。
const PORT = 4173;

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.spec.ts',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: 'on-first-retry',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: `npm run e2e:serve -- --port ${PORT} --strictPort`,
    url: `http://localhost:${PORT}`,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
