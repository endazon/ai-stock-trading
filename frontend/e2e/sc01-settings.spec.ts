import { test, expect } from '@playwright/test';
import {
  defaultBff,
  installBff,
  pathWithRoles,
  ASSUMPTIONS,
  ASSUMPTIONS_HISTORY,
} from './fixtures';

// SC-01, FR-17, UC-06, IADR-0087: 設定画面（全体前提条件の閲覧/変更）の実ブラウザ E2E。
// BFF 応答はモック（page.route）。実 API・実クラスタ疎通に依存しない（live 検証は #82 系／MSP#284）。

test.describe('SC-01 設定（#187）', () => {
  test('trading-owner に現在値とバージョンを表示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings', ['trading-owner']));

    await expect(page.getByRole('heading', { name: '設定' })).toBeVisible();
    await expect(page.getByText(/現在のバージョン:\s*3/)).toBeVisible();
    await expect(page.getByLabel('譲渡益税率')).toHaveValue(String(ASSUMPTIONS.assumptions.capitalGainsTaxRate));
  });

  test('権限外は NotFound（存在秘匿）で BFF を呼ばない', async ({ page }) => {
    let bffCalled = false;
    page.on('request', (req) => {
      if (req.url().includes('/bff/')) bffCalled = true;
    });
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings', ['user']));

    await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
    await expect(page.getByRole('heading', { name: '設定' })).toHaveCount(0);
    expect(bffCalled).toBe(false);
  });

  test('変更履歴を新しい順に表示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings', ['trading-owner']));

    // #423 で §2 が廃止され同名衝突は解消したが、`exact: true` は**外さない**。
    // 本画面へ新しい節（履歴を伴うもの）が足されたときに、部分一致だと 2 要素へ解決して
    // strict mode 違反になる。**§1 の要素だけを指していることを名前で保証する。**
    const table = page.getByRole('table', { name: '変更履歴', exact: true });
    await expect(table).toBeVisible();
    const rows = table.getByRole('row');
    // 先頭はヘッダ行、次にデータ行（新しい順）。
    await expect(rows.nth(1)).toContainText(ASSUMPTIONS_HISTORY[0].reason);
    await expect(rows.nth(2)).toContainText(ASSUMPTIONS_HISTORY[1].reason);
  });

  test('保存（PUT 200）で成功通知を表示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings', ['trading-owner']));
    await expect(page.getByRole('heading', { name: '設定' })).toBeVisible();

    await page.getByLabel('変更理由', { exact: true }).fill('税率調整');
    await page.getByRole('button', { name: '保存', exact: true }).click();

    await expect(page.getByText('保存しました。')).toBeVisible();
  });

  test('競合（PUT 409）でメッセージを表示し破壊的な再試行をしない', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /assumptions': () => {
        putCount += 1;
        return { status: 409, body: { title: '競合', detail: '競合が発生しました。' } };
      },
    });
    await page.goto(pathWithRoles('/settings', ['trading-owner']));
    await expect(page.getByRole('heading', { name: '設定' })).toBeVisible();

    await page.getByLabel('変更理由', { exact: true }).fill('税率調整');
    await page.getByRole('button', { name: '保存', exact: true }).click();

    await expect(page.getByRole('alert').filter({ hasText: '競合' })).toBeVisible();
    expect(putCount).toBe(1); // 自動再試行しない
  });

  test('検証エラー（PUT 400）でメッセージを表示する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'PUT /assumptions': {
        status: 400,
        body: { errors: { assumptions: ['理由は必須です'] } },
      },
    });
    await page.goto(pathWithRoles('/settings', ['trading-owner']));
    await expect(page.getByRole('heading', { name: '設定' })).toBeVisible();

    await page.getByLabel('変更理由', { exact: true }).fill('不正値');
    await page.getByRole('button', { name: '保存', exact: true }).click();

    await expect(page.getByRole('alert').filter({ hasText: '入力' })).toBeVisible();
  });

  test('履歴取得の失敗（500）は履歴領域のみ縮退する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'GET /assumptions/history': { status: 500 },
    });
    await page.goto(pathWithRoles('/settings', ['trading-owner']));

    // 本体（設定）は表示され、履歴領域のみ縮退表示になる。
    await expect(page.getByRole('heading', { name: '設定' })).toBeVisible();
    await expect(page.getByText('変更履歴は利用できません。')).toBeVisible();
  });
});

// SC-01, FR-13, UC-06, #423, IADR-0164 決定1: **§2「収集パラメータ」の廃止**（2026-08-07 の利用者裁定）。
//
//   収集間隔 … **画面から変更しない。起動時構成とする**（質問票 第 13 回 Q11・案 A）
//   変動閾値 … **SC-02 へ移す**（同 Q12・案 B。権威は MarketMonitorService であり監視銘柄と同じ由来である）
//
// **本節はすべて否定形である。**「実装していない」と「実装してはならない」は別であり、
// 前者のままにすると次に画面を触る者が「未実装の項目」として素直に実装してしまう。
test.describe('SC-01 §2 の廃止（#423）', () => {
  test('収集パラメータの節・入力欄・保存操作が存在しない', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings', ['trading-owner']));
    await expect(page.getByRole('heading', { name: '設定' })).toBeVisible();

    await expect(page.getByText('収集パラメータ')).toHaveCount(0);
    await expect(page.getByLabel(/変動閾値/)).toHaveCount(0);
    await expect(page.getByLabel(/クールダウン/)).toHaveCount(0);
    await expect(page.getByLabel(/収集間隔/)).toHaveCount(0);
    await expect(page.getByRole('button', { name: /変動閾値/ })).toHaveCount(0);
  });

  test('MarketMonitorService（/bff/monitor/*）を 1 度も呼ばない', async ({ page }) => {
    const monitorCalls: string[] = [];
    page.on('request', (req) => {
      if (req.url().includes('/bff/monitor')) monitorCalls.push(req.url());
    });
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings', ['trading-owner']));
    await expect(page.getByRole('form', { name: '全体前提条件の変更' })).toBeVisible();

    expect(monitorCalls).toEqual([]);
  });

  test('収集間隔は起動時構成である旨と SC-02 への導線を明示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings', ['trading-owner']));

    await expect(page.getByText(/本画面からも API からも変更しません/)).toBeVisible();
    await expect(page.getByText(/市場監視パラメータ（変動閾値・クールダウン）/)).toBeVisible();
  });
});
