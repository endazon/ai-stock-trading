import { defineConfig, devices, type PlaywrightTestConfig } from '@playwright/test';
import { parseLiveEnv, type Layer } from './e2e/live/support/env';

// NFR / MSP/IADR-0569 決定 3・4 / MSP/IADR-0624 / IADR-0588（#1416 / E-33）: **AST の live 層の config**。
// L1（`playwright.config.ts`。ハーネス＋ page.route のモック）とは別のファイルにする —— L1 の webServer と baseURL が live に掛からない。
//
// - AST は**自分ではログインしない**。MSP の live の setup が書いた storageState を `E2E_AUTH_DIR` から読む（`e2e/live/support/env.ts`）。
//   流す順は: ① MSP の live の setup を `E2E_KEEP_SESSIONS=trading-owner` で流す（取引オーナーの state だけが残る）→ ② 本 config →
//   ③ 本 config の globalTeardown が取引オーナーを失効させ state を消す（作業仕様書 20261025_1416 と IADR-0588）。
// - 🔴 `E2E_LIVE` が未設定・空なら全 project を 0 件にする（`--pass-with-no-tests` で exit 0）。`1` だけが live。
// - 前提（取引オーナーの state・`/bff/auth/me` 200 で trading-owner）は globalSetup の門が確かめ、満たさなければ exit 2。
// - 🔴 本番の OpenD・証券口座には触らない。live-ro は読み取りだけ（`PUT broker-provider` と `POST /opend-auth/*` は送らない。E-43 が abort で固定する）。

const cfg = parseLiveEnv(process.env);

if (!cfg.live) {
  process.stderr.write(
    '[e2e-live] E2E_LIVE=1 が無いので 0 件で終わる（稼働の環境へ当たる実行は明示の opt-in でだけ流す）\n',
  );
}

type Project = NonNullable<PlaywrightTestConfig['projects']>[number];

/** 層の project（MSP/IADR-0569 決定 3 の表と同じ名前・接尾辞）。 */
const LAYER_PROJECTS: Record<Layer, Project> = {
  ro: { name: 'live-ro', testMatch: /\.ro\.spec\.ts$/, workers: 2, retries: 1 },
  trading: {
    name: 'trading',
    testMatch: /\.trading\.spec\.ts$/,
    workers: 1,
    retries: 0,
    use: { trace: 'on' },
  },
};

export default defineConfig({
  testDir: './e2e/live',
  timeout: 60_000,
  globalTimeout: 40 * 60_000,
  forbidOnly: !!process.env.CI,
  // NFR / IADR-0565 決定 2・3 / IADR-0599（#1418 / E-60）: `trading` 層を選んだときだけ、readiness の後に trading の門とスナップショット
  // （snapshot.ts）を、取引オーナーの失効（teardown.ts）の前に突合と復元（restore.ts）を置く。配列は書いた順に走る（teardown も同じ順）。
  // trading の門は、閉場（次の開場まで 60 分以上）と in-flight の注文が無いこと（IADR-0600）を確かめ、満たさなければ exit 2。
  globalSetup: cfg.live
    ? cfg.layers.includes('trading')
      ? ['./e2e/live/support/readiness.ts', './e2e/live/support/snapshot.ts']
      : './e2e/live/support/readiness.ts'
    : undefined,
  globalTeardown: cfg.live
    ? cfg.layers.includes('trading')
      ? ['./e2e/live/support/restore.ts', './e2e/live/support/teardown.ts']
      : './e2e/live/support/teardown.ts'
    : undefined,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    ...devices['Desktop Chrome'],
    baseURL: cfg.edgeUrl,
    ignoreHTTPSErrors: cfg.insecureTls,
    trace: { mode: 'retain-on-failure', snapshots: true, sources: false },
    screenshot: 'only-on-failure',
    video: 'off',
    locale: 'ja-JP',
  },
  projects: cfg.live ? cfg.layers.map((layer) => LAYER_PROJECTS[layer]) : [],
});
