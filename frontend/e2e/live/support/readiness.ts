// NFR / MSP/IADR-0569 決定 4 / IADR-0588（#1416 / E-33）: AST の live 層の**前提（門）**。live のときだけ全 project より先に 1 回走る。
//
// 確かめること（読み取りだけ。ログインはしない）:
//   1. MSP の setup が書いた取引オーナーの storageState（`<E2E_AUTH_DIR>/trading-owner.json`）が在る
//   2. その cookie で BFF の `/bff/auth/me` が 200 を返し、ロールに `trading-owner` を持つ（失効済み・別の役割の state を掴まない）
// 🔴 満たさなければ **exit 2（前提未整備）**。skip の緑とも、製品の不具合の赤とも区別する（MSP の readiness と同じ扱い）。
// 🔴 exit 2 で抜けると globalTeardown が走らないので、state が在れば先に失効と削除を済ませる（`gate`）。
import { existsSync } from 'node:fs';
import { chromium, request } from '@playwright/test';
import { OWNER_ROLE, parseLiveEnv, stateFile, type LiveEnv } from './env';
import { revokeOwnerSession } from './teardown';

export const NOT_READY_EXIT_CODE = 2;

/** `/bff/auth/me` を引く関数（state のファイル → 状態とロール）。試験で差し替える。 */
export type MeProbe = (stateFilePath: string) => Promise<{ status: number | string; roles: string[] }>;

/** 門の判定。不成立の理由を並べて返す（値は持たない・出さない）。 */
export async function readinessProblems(
  cfg: LiveEnv,
  probe: MeProbe,
  exists: (path: string) => boolean = existsSync,
): Promise<string[]> {
  const owner = stateFile(cfg.authDir, 'trading-owner');
  if (!exists(owner)) {
    return [
      `取引オーナーの storageState が無い: ${owner}（MSP の live の setup を先に流す。MSP の teardown が state を消す設定のままでないか）`,
    ];
  }
  const me = await probe(owner);
  if (me.status !== 200) {
    return [`取引オーナーの cookie で /bff/auth/me が 200 でない: ${me.status}（セッションが失効している）`];
  }
  if (!me.roles.includes(OWNER_ROLE)) {
    return [`取引オーナーの state のロールに ${OWNER_ROLE} が無い（別の役割の state を掴んでいる）`];
  }
  return [];
}

/**
 * 門が不成立なら、取引オーナーの state が在るときに限り**失効と削除を先に済ませてから**不成立を返す。門で exit 2 に抜けると
 * globalTeardown は走らない（Playwright は globalSetup が落ちた実行の teardown を呼ばない）ので、残した cookie をそのままにしない。
 */
export async function gate(
  cfg: LiveEnv,
  probe: MeProbe,
  revoke: () => Promise<unknown>,
  exists: (path: string) => boolean = existsSync,
): Promise<string[]> {
  const problems = await readinessProblems(cfg, probe, exists);
  if (problems.length && exists(stateFile(cfg.authDir, 'trading-owner'))) await revoke();
  return problems;
}

export default async function globalSetup(): Promise<void> {
  const cfg = parseLiveEnv(process.env);
  if (!cfg.live) return;
  if (cfg.insecureTls) {
    process.stdout.write('::warning::[e2e-live] E2E_INSECURE_TLS=1: 証明書を検証せずに流す\n');
  }
  const probe: MeProbe = async (file) => {
    const ctx = await request.newContext({
      storageState: file,
      baseURL: cfg.edgeUrl,
      ignoreHTTPSErrors: cfg.insecureTls,
    });
    try {
      const res = await ctx.get('/bff/auth/me', { failOnStatusCode: false, timeout: 15_000 });
      const roles =
        res.status() === 200 ? (((await res.json()) as { roles?: string[] }).roles ?? []) : [];
      return { status: res.status(), roles };
    } catch (e) {
      return { status: `届かない（${e instanceof Error ? e.message.split('\n')[0] : String(e)}）`, roles: [] };
    } finally {
      await ctx.dispose();
    }
  };
  const problems = await gate(cfg, probe, async () => {
    const browser = await chromium.launch();
    try {
      const result = await revokeOwnerSession(cfg, browser);
      if (result.failure)
        process.stderr.write(
          `[e2e-live] 門で落ちる前に取引オーナーを失効させられなかった（state は消した。セッションは寿命で切れるまで生きている）: ${result.failure}\n`,
        );
    } finally {
      await browser.close();
    }
  });
  if (problems.length) {
    process.stderr.write(
      `[e2e-live] 前提未整備（exit ${NOT_READY_EXIT_CODE}）。test は 1 件も流していない:\n  - ${problems.join('\n  - ')}\n`,
    );
    process.exit(NOT_READY_EXIT_CODE);
  }
  process.stdout.write(`[e2e-live] 前提を確かめた: ${cfg.edgeUrl}（取引オーナーの state・/bff/auth/me 200）\n`);
}
