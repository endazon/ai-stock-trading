// NFR / ADR-0038 / MSP の IADR-0624 / IADR-0588（#1416 / E-33）: AST の live 層の**後始末**。live の実行の終わりに（成功でも失敗でも）1 回走る。
//
// 🔴 MSP の live の setup を `E2E_KEEP_SESSIONS=trading-owner` で流すと、MSP の teardown は取引オーナーのセッションだけを残す（他の 5 役割は
// 必ず失効させる）。残したセッションの後始末は AST が引き受ける: 取引オーナーのセッションを BFF のログアウトで失効させ、state のファイルを消す。
// 失効を確かめられなければ、役割名だけを出して exit 1（MSP の teardown と同じ形）。値は出さない。
//
// ログアウトの経路（MSP の `AuthBffEndpoints.cs`）: GET `/bff/auth/logout?sid=<sid>`。sid は `/bff/auth/me` の `logoutUrl` が配る。
// 失効したことは、**同じ state の cookie で `/bff/auth/me` が 401** になることで確かめる。
import { existsSync, rmSync } from 'node:fs';
import { chromium, request, type Browser } from '@playwright/test';
import { parseLiveEnv, stateFile, type LiveEnv } from './env';

export interface RevokeResult {
  revoked: boolean;
  /** state が無かった（前提未整備で流れなかった・既に消えていた）。 */
  missing: boolean;
  /** 失効を確かめられなかった理由（値は持たない）。 */
  failure: string | null;
}

/** 取引オーナーのセッションを失効させ、state のファイルを消す。 */
export async function revokeOwnerSession(cfg: LiveEnv, browser: Browser): Promise<RevokeResult> {
  const file = stateFile(cfg.authDir, 'trading-owner');
  if (!existsSync(file)) return { revoked: false, missing: true, failure: null };
  try {
    const context = await browser.newContext({
      storageState: file,
      baseURL: cfg.edgeUrl,
      ignoreHTTPSErrors: cfg.insecureTls,
    });
    try {
      const me = await context.request.get('/bff/auth/me', { failOnStatusCode: false });
      if (me.status() === 200) {
        const logoutUrl = ((await me.json()) as { logoutUrl?: string | null }).logoutUrl;
        if (!logoutUrl) throw new Error('/bff/auth/me が logoutUrl を返さない');
        const page = await context.newPage();
        const landed = await page.goto(logoutUrl, { timeout: 30_000 });
        if (!landed || landed.status() >= 400) {
          throw new Error(`ログアウトの応答が ${landed ? landed.status() : '無い'}`);
        }
        await page.waitForURL(
          (url) => url.origin === cfg.edgeUrl && !url.pathname.startsWith('/bff/'),
          { timeout: 30_000 },
        );
      } else if (me.status() !== 401) {
        throw new Error(`/bff/auth/me（ログアウトの前）が ${me.status()}`);
      }
    } finally {
      await context.close();
    }
    const probe = await request.newContext({
      storageState: file,
      baseURL: cfg.edgeUrl,
      ignoreHTTPSErrors: cfg.insecureTls,
    });
    try {
      const after = await probe.get('/bff/auth/me', { failOnStatusCode: false });
      if (after.status() !== 401)
        throw new Error(`ログアウトの後も同じ cookie で /bff/auth/me が ${after.status()}`);
    } finally {
      await probe.dispose();
    }
    return { revoked: true, missing: false, failure: null };
  } catch (e) {
    return {
      revoked: false,
      missing: false,
      failure: e instanceof Error ? e.message.split('\n')[0] : String(e),
    };
  } finally {
    rmSync(file, { force: true });
  }
}

export default async function globalTeardown(): Promise<void> {
  const cfg = parseLiveEnv(process.env);
  if (!cfg.live) return;
  const browser = await chromium.launch();
  let result: RevokeResult;
  try {
    result = await revokeOwnerSession(cfg, browser);
  } finally {
    await browser.close();
  }
  if (result.failure) {
    process.stderr.write(
      `[e2e-live] セッションを失効させられなかった役割（trace の cookie がセッションの寿命まで有効のまま）:\n  - e2e-trading-owner: ${result.failure}\n`,
    );
    process.exit(1);
  }
  process.stdout.write(
    result.revoked
      ? '[e2e-live] セッションを失効させた: trading-owner\n'
      : '[e2e-live] state が無かった: trading-owner（失効させるものが無い）\n',
  );
}
