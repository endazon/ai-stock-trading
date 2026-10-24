// FR-09 / FR-20 / NFR-06 / IADR-0589（#1417 / E-43）: AST の live-ro の**書き込みの保険**。
//
// 🔴 live-ro は読み取りだけである。とくに次の 3 種は本番の取引と OpenD を動かすので、**画面の操作がどう転んでも外へ出さない**:
//   - `PUT /bff/risk-controls/settings/broker-provider`（発注先の切替。実弾〔moomoo REAL〕への切替を含む）
//   - `POST /bff/opend-auth/code` / `POST /bff/opend-auth/resend`（オーナーの端末へ SMS が飛ぶ・OpenD の状態が変わる）
// **context** の経路（`page.context().route`）で `/bff/` への **GET 以外**の要求をすべて記録して abort する（別タブ〔popup〕の要求も塞ぐ）。
// test の終わりに、危険な 3 種の記録が 0 件であること、
// 書き込みの記録が 0 件であることを見る（abort は保険であり、送ろうとしたこと自体を失敗にする）。
import type { Page, Request } from '@playwright/test';

/** 本番の取引・OpenD を動かす要求（method と BFF の pathname）。 */
export const DANGEROUS: readonly { method: string; path: string }[] = [
  { method: 'PUT', path: '/bff/risk-controls/settings/broker-provider' },
  { method: 'POST', path: '/bff/opend-auth/code' },
  { method: 'POST', path: '/bff/opend-auth/resend' },
];

const READ_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

export interface WriteRecord {
  method: string;
  path: string;
}

/** 記録の中から危険な要求だけを取り出す（純関数）。 */
export function dangerousOf(records: readonly WriteRecord[]): WriteRecord[] {
  return records.filter((r) => DANGEROUS.some((d) => d.method === r.method && d.path === r.path));
}

/** URL の pathname。解析できなければ null（呼び出し側は安全側＝abort に倒す）。 */
function pathOf(url: string): string | null {
  try {
    return new URL(url).pathname;
  } catch {
    return null;
  }
}

/**
 * 要求が abort の対象か（`/bff/` への GET・HEAD・OPTIONS 以外）。純関数。
 * 🔴 **fail-closed**: URL を解析できない要求は、読み取り（GET・HEAD・OPTIONS）以外なら abort の対象にする（通さない）。
 */
export function isGuardedWrite(method: string, url: string): boolean {
  if (READ_METHODS.has(method.toUpperCase())) return false;
  const path = pathOf(url);
  return path === null || path.startsWith('/bff/');
}

/**
 * `/bff/` への書き込みをすべて記録して abort する。記録を返す（本文は記録しない）。
 * 🔴 `page.route` ではなく `page.context().route` に張る —— 画面が開いた別タブ（popup）の要求も同じ context を通るので塞がる。
 * Playwright は test ごとに新しい context を作るので、記録が test をまたいで混ざることは無い。
 */
export async function installWriteGuard(page: Page): Promise<WriteRecord[]> {
  const records: WriteRecord[] = [];
  await page.context().route('**/bff/**', async (route) => {
    const req: Request = route.request();
    if (!isGuardedWrite(req.method(), req.url())) {
      await route.fallback();
      return;
    }
    records.push({ method: req.method(), path: pathOf(req.url()) ?? req.url() });
    await route.abort('blockedbyclient');
  });
  return records;
}
