// FR-09 / FR-20 / NFR-06 / IADR-0589（#1417 / E-43）: AST の live-ro の**書き込みの保険**。
//
// 🔴 live-ro は読み取りだけである。とくに次の 2 つは本番の取引と OpenD を動かすので、**画面の操作がどう転んでも外へ出さない**:
//   - `PUT /bff/risk-controls/settings/broker-provider`（発注先の切替。実弾〔moomoo REAL〕への切替を含む）
//   - `POST /bff/opend-auth/code` / `POST /bff/opend-auth/resend`（オーナーの端末へ SMS が飛ぶ・OpenD の状態が変わる）
// `page.route` で `/bff/` への **GET 以外**の要求をすべて記録して abort する。test の終わりに、危険な 2 種の記録が 0 件であること、
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

/** 要求が abort の対象か（`/bff/` への GET・HEAD・OPTIONS 以外）。純関数。 */
export function isGuardedWrite(method: string, url: string): boolean {
  let path: string;
  try {
    path = new URL(url).pathname;
  } catch {
    return false;
  }
  return path.startsWith('/bff/') && !READ_METHODS.has(method.toUpperCase());
}

/** `/bff/` への書き込みをすべて記録して abort する。記録を返す（本文は記録しない）。 */
export async function installWriteGuard(page: Page): Promise<WriteRecord[]> {
  const records: WriteRecord[] = [];
  await page.route('**/bff/**', async (route) => {
    const req: Request = route.request();
    if (!isGuardedWrite(req.method(), req.url())) {
      await route.fallback();
      return;
    }
    records.push({ method: req.method(), path: new URL(req.url()).pathname });
    await route.abort('blockedbyclient');
  });
  return records;
}
