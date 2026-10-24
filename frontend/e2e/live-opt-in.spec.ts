import { spawnSync } from 'node:child_process';
import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import { OPTION_NAMES, parseLiveEnv, stateFile, type LiveEnv } from './live/support/env';
import { gate, readinessProblems } from './live/support/readiness';
import { dangerousOf, installWriteGuard, isGuardedWrite } from './live/support/guard';

// NFR / IADR-0588（#1416 / E-33）: AST の live 層の config（`playwright.live.config.ts`）の **opt-in・読み込み・state の読み先・門**を L1 で固定する。
// 稼働の環境には 1 度も触れない（ブラウザも開かない。`--list` は globalSetup を走らせない）。live の実走は MSP の setup の後に流す（作業仕様書）。

const FRONTEND = fileURLToPath(new URL('..', import.meta.url));

/** live の config を子プロセスで起動する。E2E_* は親から引き継がない（与えたものだけが値を持つ）。 */
function runLive(env: Record<string, string>, extra: string[] = []) {
  const base = Object.fromEntries(
    Object.entries(process.env).filter(([k]) => !k.startsWith('E2E_')),
  ) as Record<string, string>;
  const blank = Object.fromEntries(OPTION_NAMES.map((n) => [n, '']));
  const out = mkdtempSync(join(tmpdir(), 'ast-e2e-live-out-'));
  try {
    const r = spawnSync(
      process.execPath,
      [
        fileURLToPath(import.meta.resolve('@playwright/test/cli')),
        'test',
        '--config',
        'playwright.live.config.ts',
        '--pass-with-no-tests',
        '--reporter',
        'list',
        '--output',
        out,
        ...extra,
      ],
      { cwd: FRONTEND, env: { ...base, ...blank, ...env }, encoding: 'utf8', timeout: 120_000 },
    );
    return { status: r.status, out: `${r.stdout}\n${r.stderr}` };
  } finally {
    rmSync(out, { recursive: true, force: true });
  }
}

const live = (authDir: string): LiveEnv => ({
  live: true,
  layers: ['ro'],
  edgeUrl: 'https://localhost',
  insecureTls: false,
  authDir,
});

test('E2E_LIVE が未設定なら 0 件で exit 0（稼働の環境へ当たらない）', () => {
  const r = runLive({});
  expect(r.status, r.out).toBe(0);
  expect(r.out).toContain('E2E_LIVE=1 が無いので 0 件で終わる');
});

test('E2E_LIVE=1 なら live-ro の spec が読み込める（--list。門は走らない）', () => {
  const r = runLive({ E2E_LIVE: '1', E2E_AUTH_DIR: '/tmp/ast-e2e-auth' }, ['--list']);
  expect(r.status, r.out).toBe(0);
  expect(r.out).toMatch(/\[live-ro\] › owner-reach\.ro\.spec\.ts:\d+:\d+ › /);
});

test('E2E_LIVE・E2E_LAYER の値域外と、E2E_LIVE=1 で E2E_AUTH_DIR が無いのは、読み込みで失敗する（0 件の緑にしない）', () => {
  expect(() => parseLiveEnv({ E2E_LIVE: 'true' })).toThrow(/E2E_LIVE の値域外/);
  expect(() => parseLiveEnv({ E2E_LAYER: 'ro' })).toThrow(/E2E_LIVE=1 が無い/);
  expect(() => parseLiveEnv({ E2E_LIVE: '1', E2E_AUTH_DIR: '/x', E2E_LAYER: 'rw' })).toThrow(
    /E2E_LAYER の値域外/,
  );
  expect(() => parseLiveEnv({ E2E_LIVE: '1' })).toThrow(/E2E_AUTH_DIR が無い/);
  expect(parseLiveEnv({}).live).toBe(false);
  // config の読み込み（子プロセス）でも落ちる。
  const r = runLive({ E2E_LIVE: '1' }, ['--list']);
  expect(r.status, r.out).not.toBe(0);
  expect(r.out).toContain('E2E_AUTH_DIR が無い');
});

test('取引オーナーの state は MSP の setup と同じ名前 <E2E_AUTH_DIR>/trading-owner.json から読む', () => {
  // MSP の `e2e/live/support/env.ts` の `stateFile(authDir, role)` は `${authDir}/${role}.json`。
  expect(stateFile('/a/b', 'trading-owner')).toBe('/a/b/trading-owner.json');
  expect(stateFile('/a/b/', 'trading-owner')).toBe('/a/b/trading-owner.json');
  expect(stateFile(parseLiveEnv({ E2E_LIVE: '1', E2E_AUTH_DIR: '/msp/.auth' }).authDir, 'user')).toBe(
    '/msp/.auth/user.json',
  );
});

test('門: state が無い・cookie が失効している・trading-owner を持たないなら不成立、揃えば成立（値は読まない）', async () => {
  const dir = '/msp/.auth';
  const owner = `${dir}/trading-owner.json`;
  const seen: string[] = [];
  const probe = (status: number, roles: string[]) => async (file: string) => {
    seen.push(file);
    return { status, roles };
  };
  // state が無い（MSP の teardown が消した、または setup を流していない）。
  expect(await readinessProblems(live(dir), probe(200, ['trading-owner']), () => false)).toEqual([
    expect.stringContaining('storageState が無い'),
  ]);
  // 失効している。
  expect(await readinessProblems(live(dir), probe(401, []), () => true)).toEqual([
    expect.stringContaining('/bff/auth/me が 200 でない: 401'),
  ]);
  // 別の役割の state。
  expect(await readinessProblems(live(dir), probe(200, ['platform-operator']), () => true)).toEqual([
    expect.stringContaining('trading-owner が無い'),
  ]);
  // 揃っている。
  expect(await readinessProblems(live(dir), probe(200, ['trading-owner']), () => true)).toEqual([]);
  // 門は MSP の名前のファイルを読みに行く。
  expect(new Set(seen)).toEqual(new Set([owner]));
});

test('門で落ちるときも、取引オーナーの state が在れば失効と削除を 1 回だけ先に済ませる（exit 2 で teardown が走らないため）', async () => {
  const dir = '/msp/.auth';
  let revoked = 0;
  const revoke = async () => {
    revoked += 1;
  };
  // state は在るが失効している → 不成立。失効の関数を 1 回呼ぶ。
  const failed = await gate(live(dir), async () => ({ status: 401, roles: [] }), revoke, () => true);
  expect(failed).toHaveLength(1);
  expect(revoked).toBe(1);
  // state が無い → 不成立だが、失効させるものが無いので呼ばない。
  await gate(live(dir), async () => ({ status: 200, roles: ['trading-owner'] }), revoke, () => false);
  expect(revoked).toBe(1);
  // 成立 → 呼ばない（teardown が実行の終わりに失効させる）。
  expect(await gate(live(dir), async () => ({ status: 200, roles: ['trading-owner'] }), revoke, () => true)).toEqual([]);
  expect(revoked).toBe(1);
});

test('書き込みの保険: /bff/ への GET 以外だけを abort の対象にし、危険な 3 種（発注先の変更・OpenD 認証の送信と再送）を見分ける（#1417）', () => {
  expect(isGuardedWrite('PUT', 'https://localhost/bff/risk-controls/settings/broker-provider')).toBe(true);
  expect(isGuardedWrite('POST', 'https://localhost/bff/opend-auth/code')).toBe(true);
  expect(isGuardedWrite('GET', 'https://localhost/bff/risk-controls/settings')).toBe(false);
  expect(isGuardedWrite('POST', 'https://keycloak.localhost/realms/platform/login-actions/authenticate')).toBe(false);
  // fail-closed: URL を解析できない書き込みは abort の対象（通さない）。読み取りは通す。
  expect(isGuardedWrite('PUT', 'not a url')).toBe(true);
  expect(isGuardedWrite('POST', '')).toBe(true);
  expect(isGuardedWrite('GET', 'not a url')).toBe(false);
  expect(
    dangerousOf([
      { method: 'PUT', path: '/bff/risk-controls/settings/broker-provider' },
      { method: 'POST', path: '/bff/opend-auth/resend' },
      { method: 'PUT', path: '/bff/risk-controls/settings/limits' },
      { method: 'GET', path: '/bff/risk-controls/settings/broker-provider' },
    ]),
  ).toEqual([
    { method: 'PUT', path: '/bff/risk-controls/settings/broker-provider' },
    { method: 'POST', path: '/bff/opend-auth/resend' },
  ]);
});

test('書き込みの保険: 画面の中から送られた PUT broker-provider と POST opend-auth/code は abort され、記録に残る（GET は通す）', async ({
  page,
}) => {
  const records = await installWriteGuard(page);
  await page.goto('/');
  const outcomes = await page.evaluate(async () => {
    const tryFetch = async (method: string, path: string) => {
      try {
        await fetch(path, { method });
        return 'sent';
      } catch {
        return 'aborted';
      }
    };
    return {
      broker: await tryFetch('PUT', '/bff/risk-controls/settings/broker-provider'),
      code: await tryFetch('POST', '/bff/opend-auth/code'),
    };
  });
  expect(outcomes).toEqual({ broker: 'aborted', code: 'aborted' });
  expect(dangerousOf(records)).toEqual([
    { method: 'PUT', path: '/bff/risk-controls/settings/broker-provider' },
    { method: 'POST', path: '/bff/opend-auth/code' },
  ]);
});

test('書き込みの保険: 画面が開いた別タブ（popup）から送られた PUT broker-provider も abort され、記録に残る（guard は context に張る）', async ({
  page,
}) => {
  const records = await installWriteGuard(page);
  await page.goto('/');
  const [popup] = await Promise.all([
    page.waitForEvent('popup'),
    page.evaluate(() => {
      window.open('/', '_blank');
    }),
  ]);
  await popup.waitForLoadState();
  const outcome = await popup.evaluate(async () => {
    try {
      await fetch('/bff/risk-controls/settings/broker-provider', { method: 'PUT' });
      return 'sent';
    } catch {
      return 'aborted';
    }
  });
  expect(outcome).toBe('aborted');
  expect(dangerousOf(records)).toEqual([{ method: 'PUT', path: '/bff/risk-controls/settings/broker-provider' }]);
});

test('live の spec は page.request / request の書き込み系（post・put・patch・delete・fetch）を使わない（guard は page の経路だけを塞ぐ。IADR-0589）', () => {
  const dir = fileURLToPath(new URL('./live/', import.meta.url));
  const specs = readdirSync(dir).filter((f) => f.endsWith('.spec.ts'));
  expect(specs.length, 'live の spec').toBeGreaterThan(0);
  const offenders = specs.flatMap((f) =>
    readFileSync(join(dir, f), 'utf8')
      .split('\n')
      .map((line, i) => ({ f, i: i + 1, line }))
      .filter(({ line }) => /\brequest\s*\.\s*(post|put|patch|delete|fetch)\s*\(/.test(line))
      .map(({ f, i }) => `${f}:${i}`),
  );
  expect(offenders).toEqual([]);
});

