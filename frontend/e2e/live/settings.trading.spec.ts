import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { parseLiveEnv, stateFile } from './support/env';
import { DANGEROUS, dangerousOf, type WriteRecord } from './support/guard';
import { restoreCore, type TradingSnapshot } from './support/snapshot';

// NFR / SC-01 / SC-02 / FR-03 / FR-10 / FR-13 / FR-17 / FR-20 / IADR-0565 決定 1・3 / IADR-0599 / IADR-0601（#1419 / E-61）:
// live の取引影響層（`trading` project・L3-T）の **SC-01 / SC-02 の変更 → 確認 → 復元**。網羅表（#1410）の A-026・A-044〜A-048。
//
// 前提（globalSetup が確かめる。満たさなければ exit 2 で、本 spec は 1 本も流れない）:
//   閉場で次の開場まで 60 分以上・in-flight の注文が無い・事前スナップショットを取った（`support/snapshot.ts`）。
// 🔴 送らないもの（IADR-0565 決定 1）: `PUT …/broker-provider`（どの値へも）・`POST /bff/opend-auth/{code,resend}`。
//    context の経路でこの 3 種だけを abort し、test の後に**送ろうとした記録が 0 件**であることを見る（ほかの書き込みは通す）。
// 🔴 各 test の後に、事前スナップショットと突合して戻す（`restoreCore.reconcile`。理由 `[e2e <runId> restore]`）。差分が残れば赤。
//    globalTeardown も同じ突合をもう一度行う（二重の保険）。
// 🔴 履歴は消せない。履歴を見る test は「自分の行（理由に `[e2e <runId>]`）が増えた」ことだけを見て、絶対件数を見ない。

const cfg = parseLiveEnv(process.env);
const RUN_ID = process.env.E2E_RUN_ID ?? '';
const TAG = `[e2e ${RUN_ID}]`;


let writes: WriteRecord[] = [];

/** 危険な 3 種だけを abort し、`/bff/` への書き込みをすべて記録する（別タブも同じ context を通る）。 */
async function installTradingGuard(page: Page): Promise<WriteRecord[]> {
  const records: WriteRecord[] = [];
  await page.context().route('**/bff/**', async (route) => {
    const req = route.request();
    const method = req.method().toUpperCase();
    if (method === 'GET' || method === 'HEAD' || method === 'OPTIONS') {
      await route.fallback();
      return;
    }
    let path: string;
    try {
      path = new URL(req.url()).pathname;
    } catch {
      records.push({ method, path: req.url() });
      await route.abort('blockedbyclient'); // fail-closed: 解析できない書き込みは通さない
      return;
    }
    records.push({ method, path });
    // 危険な 3 種の定義は live-ro の保険（support/guard.ts）と同じものを使う（綴りを 2 か所に置かない）。
    if (dangerousOf([{ method, path }]).length > 0 || DANGEROUS.some((d) => d.path === path)) {
      await route.abort('blockedbyclient');
      return;
    }
    await route.fallback();
  });
  return records;
}

async function getJson<T>(page: Page, path: string): Promise<T> {
  const res = await page.request.get(path);
  expect(res.status(), `GET ${path}`).toBe(200);
  return (await res.json()) as T;
}

/** 履歴のうち、この実行の理由（`[e2e <runId>]`）を持つ行の数。 */
async function ownRows(page: Page, historyPath: string): Promise<number> {
  const rows = await getJson<{ reason?: string }[]>(page, historyPath);
  expect(Array.isArray(rows), `${historyPath} は配列`).toBe(true);
  return rows.filter((r) => (r.reason ?? '').includes(TAG)).length;
}

function snapshotOfRun(): TradingSnapshot {
  const file = process.env.E2E_TRADING_SNAPSHOT;
  expect(file, 'trading の globalSetup がスナップショットの場所を渡す').toBeTruthy();
  return JSON.parse(readFileSync(file!, 'utf8')) as TradingSnapshot;
}

test.describe('取引オーナー（e2e-trading-owner）・閉場後', () => {
  test.use({ storageState: stateFile(cfg.authDir, 'trading-owner') });

  test.beforeAll(() => {
    // runId が無いと、理由で自分の行を見分けられない（globalSetup が設定する）。
    expect(RUN_ID, 'E2E_RUN_ID（trading の globalSetup が設定する）').not.toBe('');
  });

  test.beforeEach(async ({ page }) => {
    writes = await installTradingGuard(page);
  });

  test.afterEach(async ({ page }) => {
    // FR-20, FR-09: 発注先の変更・OpenD 認証は送ろうともしていない。
    expect(dangerousOf(writes), '発注先の変更・OpenD 認証の送信').toEqual([]);
    // 事前スナップショットへ戻す（理由 `[e2e <runId> restore]`）。差分が残れば赤。
    const result = await restoreCore.reconcile(restoreCore.wrapPlaywrightRequest(page.request), snapshotOfRun());
    expect(result.errors, '復元の失敗').toEqual([]);
    expect(result.remaining.map((d) => d.key), '復元の後に残った差分').toEqual([]);
  });

  // SC-01, FR-17: A-026。同値の保存で版が +1 になり、履歴に自分の行が 1 行増える（値は変わらない＝復元は要らない）。
  test('A-026: 全体前提条件を現在値と同じ値で保存すると、版が +1・履歴に自分の行が 1 行増え、値は変わらない', { tag: ['@p1', '@trading'] }, async ({ page }) => {
    const before = await getJson<{ assumptions: unknown; version: number }>(page, '/bff/assumptions');
    const ownBefore = await ownRows(page, '/bff/assumptions/history');
    await page.goto('/settings');
    await expect(page.getByRole('heading', { name: '設定', level: 1 })).toBeVisible();
    await expect(page.getByText(new RegExp(`現在のバージョン:\\s*${before.version}`))).toBeVisible();
    await page.getByLabel('変更理由', { exact: true }).fill(`${TAG} A-026 同値の保存`);
    await page.getByRole('button', { name: '保存', exact: true }).click();
    await expect(page.getByText('保存しました。')).toBeVisible();
    const after = await getJson<{ assumptions: unknown; version: number }>(page, '/bff/assumptions');
    expect(after.version).toBe(before.version + 1);
    expect(after.assumptions).toEqual(before.assumptions);
    expect(await ownRows(page, '/bff/assumptions/history')).toBe(ownBefore + 1);
  });

  // SC-02, FR-10: A-044。保有建玉数上限を 1 件だけ変える（下げられるなら下げる＝統制を緩めない向き）→ 画面と履歴で確かめる → afterEach で戻す。
  test('A-044: リスク上限の 1 項目（保有建玉数上限）を 1 単位変えて保存し、画面と履歴で確かめる（後で戻す）', { tag: ['@p1', '@trading'] }, async ({ page }) => {
    const settings = await getJson<{ limits: { maxOpenPositions: number } }>(page, '/bff/risk-controls/settings');
    const current = settings.limits.maxOpenPositions;
    const next = current > 1 ? current - 1 : current + 1; // 値域 1〜20
    const ownBefore = await ownRows(page, '/bff/risk-controls/settings/history');
    await page.goto('/settings/risk');
    const form = page.getByRole('form', { name: 'リスク上限の変更' });
    const field = form.getByLabel('保有建玉数上限 件');
    await expect(field).toHaveValue(String(current));
    await field.fill(String(next));
    await form.getByLabel('変更理由').fill(`${TAG} A-044 上限を 1 単位変える`);
    await form.getByRole('button', { name: '保存' }).click();
    await expect(form.getByText('保存しました。')).toBeVisible();
    const after = await getJson<{ limits: { maxOpenPositions: number } }>(page, '/bff/risk-controls/settings');
    expect(after.limits.maxOpenPositions).toBe(next);
    expect(await ownRows(page, '/bff/risk-controls/settings/history')).toBe(ownBefore + 1);
    await page.reload();
    await expect(page.getByRole('form', { name: 'リスク上限の変更' }).getByLabel('保有建玉数上限 件')).toHaveValue(String(next));
  });

  // SC-02, FR-13: A-045。値を変えても、理由が空のままなら保存ボタンが無効（送らない）。理由を入れると有効になる（保存はしない）。
  // 🔴 値を変えてから見る —— 未変更のままだと、無効の理由が「理由が空」か「未変更」かを区別できない。
  test('A-045: リスク上限を変えても変更理由が空のままなら保存ボタンが無効で、理由を入れると有効になる（PUT は送らない）', { tag: ['@p1', '@trading'] }, async ({ page }) => {
    const settings = await getJson<{ limits: { maxOpenPositions: number } }>(page, '/bff/risk-controls/settings');
    const current = settings.limits.maxOpenPositions;
    const next = current > 1 ? current - 1 : current + 1; // 値域 1〜20
    await page.goto('/settings/risk');
    const form = page.getByRole('form', { name: 'リスク上限の変更' });
    const field = form.getByLabel('保有建玉数上限 件');
    await expect(field).toHaveValue(String(current));
    await field.fill(String(next));
    await form.getByLabel('変更理由').fill('');
    const save = form.getByRole('button', { name: '保存' });
    await expect(save, '値を変えても理由が空なら無効').toBeDisabled();
    await form.getByLabel('変更理由').fill(`${TAG} A-045 理由を入れると有効`);
    await expect(save, '理由を入れると有効').toBeEnabled();
    expect(writes.filter((w) => w.path === '/bff/risk-controls/settings/limits'), 'limits への PUT').toEqual([]);
  });

  // A-046: 409 の競合（別 context で版を進めた後に保存 → alert「競合」・自動再試行なし → 再読込）。
  // 🔴 版を要求に載せて楽観排他を掛けるのは全体前提条件（SC-01 の `expectedVersion`）だけである。SC-02 のリスク上限の PUT は版を持たず、
  //    409 は DB の同時書き込みの競合でしか出ない（live で決定的に起こせない。L1 が 409 の描画を固定している）。行の画面 ID は SC-02 だが、
  //    同じ「別 context で版を進めた後の保存」を SC-01 で検証する（作業仕様書・IADR-0601 決定 2）。
  test('A-046: 別の context で版を進めた後に保存すると「競合」を出し、自動で再試行しない（SC-01 の版で検証）', { tag: ['@p2', '@trading'] }, async ({ page, browser }) => {
    const before = await getJson<{ assumptions: unknown; version: number }>(page, '/bff/assumptions');
    await page.goto('/settings');
    await expect(page.getByText(new RegExp(`現在のバージョン:\\s*${before.version}`))).toBeVisible();
    // 別の context（同じ取引オーナーの cookie）の**画面**から、同じ値を保存して版を進める。
    // 🔴 page.request の書き込みは使わない（guard は browser context の経路だけを塞ぐ。live-opt-in.spec.ts が検査する）。
    const other = await browser.newContext({
      storageState: stateFile(cfg.authDir, 'trading-owner'),
      baseURL: cfg.edgeUrl,
      ignoreHTTPSErrors: cfg.insecureTls,
      locale: 'ja-JP',
    });
    try {
      const otherPage = await other.newPage();
      const otherWrites = await installTradingGuard(otherPage);
      await otherPage.goto('/settings');
      await expect(otherPage.getByText(new RegExp(`現在のバージョン:\\s*${before.version}`))).toBeVisible();
      await otherPage.getByLabel('変更理由', { exact: true }).fill(`${TAG} A-046 別 context で版を進める`);
      await otherPage.getByRole('button', { name: '保存', exact: true }).click();
      await expect(otherPage.getByText('保存しました。')).toBeVisible();
      expect(dangerousOf(otherWrites), '別 context の危険な送信').toEqual([]);
    } finally {
      await other.close();
    }
    const putsBefore = writes.filter((w) => w.path === '/bff/assumptions').length;
    await page.getByLabel('変更理由', { exact: true }).fill(`${TAG} A-046 古い版で保存`);
    await page.getByRole('button', { name: '保存', exact: true }).click();
    await expect(page.getByRole('alert').filter({ hasText: '競合' })).toBeVisible();
    // 自動再試行なし: 画面からの PUT は 1 回だけ。
    await page.waitForTimeout(1_000);
    expect(writes.filter((w) => w.path === '/bff/assumptions').length - putsBefore, '画面からの PUT の回数').toBe(1);
    await page.reload();
    await expect(page.getByText(new RegExp(`現在のバージョン:\\s*${before.version + 1}`))).toBeVisible();
  });

  // SC-02, FR-03, FR-13: A-047。試験銘柄を足し、一覧と履歴で確かめ、直ちに削除する。既存の銘柄には触れない。
  test('A-047: 監視銘柄に試験銘柄を足して一覧と履歴で確かめ、直ちに削除する（既存の銘柄は触らない）', { tag: ['@p2', '@trading'] }, async ({ page }) => {
    const symbol = `E2E${RUN_ID.replace(/\D/g, '').slice(-6)}`;
    const existing = await getJson<{ symbol: string; market: number }[]>(page, '/bff/monitor/watchlist');
    expect(existing.some((s) => s.symbol === symbol), '試験銘柄は既存の一覧に無い').toBe(false);
    const ownBefore = await ownRows(page, '/bff/monitor/watchlist/history');
    await page.goto('/settings/risk');
    const add = page.getByRole('form', { name: '監視銘柄の追加' });
    await add.getByLabel('監視銘柄コード').fill(symbol);
    await add.getByLabel('監視銘柄の市場').selectOption('1'); // 米国
    await add.getByLabel('追加理由').fill(`${TAG} A-047 試験銘柄の追加`);
    await add.getByRole('button', { name: '監視銘柄を追加' }).click();
    const table = page.getByRole('table', { name: '監視銘柄' });
    await expect(table.getByRole('row', { name: new RegExp(symbol) })).toBeVisible();
    await table.getByRole('row', { name: new RegExp(symbol) }).getByRole('button', { name: '削除' }).click();
    const confirm = page.getByRole('group', { name: '監視銘柄の削除確認' });
    await confirm.getByLabel('削除理由').fill(`${TAG} A-047 試験銘柄の削除`);
    await confirm.getByRole('button', { name: '監視から削除' }).click();
    await expect(table.getByRole('row', { name: new RegExp(symbol) })).toHaveCount(0);
    const after = await getJson<{ symbol: string; market: number }[]>(page, '/bff/monitor/watchlist');
    expect(after.map((s) => `${s.market}:${s.symbol}`).sort()).toEqual(existing.map((s) => `${s.market}:${s.symbol}`).sort());
    expect(await ownRows(page, '/bff/monitor/watchlist/history')).toBe(ownBefore + 2);
    // 削除の要求は自分が足した銘柄の 1 件だけ（既存の銘柄を消そうとしていない）。
    expect(writes.filter((w) => w.method === 'DELETE' && w.path === '/bff/monitor/watchlist')).toHaveLength(1);
  });

  // SC-02: A-048。変動閾値・クールダウン・Stage 1 最小件数を同値で保存し、履歴に自分の行が増える（復元は要らない）。
  // 🔴 閉場前の統制とテーマは、保存すると「構成の値」から「画面で保存した値」へ由来が移る（同値でも戻せない副作用）。
  //    既に「画面で保存した値」が効いているときだけ同値で保存する（構成の値のときは保存しない。IADR-0601 決定 3）。
  test('A-048: 変動閾値・クールダウン・Stage 1 最小件数（と、保存値が効いているときの閉場前の統制・テーマ）を同値で保存し、履歴に行が増える', { tag: ['@p2', '@trading'] }, async ({ page }, testInfo) => {
    const monitorOwnBefore = await ownRows(page, '/bff/monitor/settings/history');
    const riskOwnBefore = await ownRows(page, '/bff/risk-controls/settings/history');
    const preClose = await getJson<{ cutoffSource: number; flattenSource: number }>(page, '/bff/risk-controls/settings/pre-close');
    const theme = await getJson<{ source?: number }>(page, '/bff/risk-controls/settings/theme-concentration');
    await page.goto('/settings/risk');

    const threshold = page.getByRole('form', { name: '変動閾値の変更' });
    await threshold.getByLabel('変動閾値の変更理由').fill(`${TAG} A-048 同値`);
    await threshold.getByRole('button', { name: '変動閾値を保存' }).click();
    const cooldown = page.getByRole('form', { name: 'クールダウンの変更' });
    await cooldown.getByLabel('クールダウンの変更理由').fill(`${TAG} A-048 同値`);
    await cooldown.getByRole('button', { name: 'クールダウンを保存' }).click();
    const count = page.getByRole('form', { name: 'Stage 1 の最小取引件数の変更' });
    await count.getByLabel('最小取引件数の変更理由').fill(`${TAG} A-048 同値`);
    await count.getByRole('button', { name: '最小取引件数を保存' }).click();
    await expect.poll(() => ownRows(page, '/bff/monitor/settings/history')).toBe(monitorOwnBefore + 2);
    let riskWrites = 1;

    const SAVED = 1; // ControlValueSource: 1=画面で保存した値が効いている
    if (preClose.cutoffSource === SAVED && preClose.flattenSource === SAVED) {
      const form = page.getByRole('form', { name: '閉場前の統制の変更' });
      await form.getByLabel('閉場前の統制の変更理由').fill(`${TAG} A-048 同値`);
      await form.getByRole('button', { name: '閉場前の統制を保存' }).click();
      await expect(form.getByText('閉場前の統制を保存しました。')).toBeVisible();
      riskWrites += 1;
    } else {
      testInfo.annotations.push({ type: '閉場前の統制', description: '構成の値が効いているので保存しない（由来が移る副作用を避ける）' });
    }
    if (theme.source === SAVED) {
      const form = page.getByRole('form', { name: 'テーマの定義の変更' });
      await form.getByLabel('テーマの定義の変更理由').fill(`${TAG} A-048 同値`);
      await form.getByRole('button', { name: 'テーマの定義を保存' }).click();
      riskWrites += 1;
    } else {
      testInfo.annotations.push({ type: 'テーマの定義', description: '構成の値が効いているので保存しない（由来が移る副作用を避ける）' });
    }
    await expect.poll(() => ownRows(page, '/bff/risk-controls/settings/history')).toBe(riskOwnBefore + riskWrites);
  });
});
