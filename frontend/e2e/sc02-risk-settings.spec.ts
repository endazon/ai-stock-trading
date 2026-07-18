import { test, expect } from '@playwright/test';
import { defaultBff, installBff, pathWithRoles, RISK_SETTINGS } from './fixtures';

// SC-02, FR-13, FR-19, FR-20, UC-06, IADR-0087: リスク設定画面（リスク上限の閲覧/変更）の実ブラウザ E2E。
// BFF 応答はモック（page.route）。実 API・実クラスタ疎通に依存しない（live 検証は #82 系／MSP #284）。

test.describe('SC-02 リスク設定（#187）', () => {
  test('trading-owner にリスク上限とガード/段階（参照）を表示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    await expect(page.getByRole('heading', { name: 'リスク設定' })).toBeVisible();
    await expect(page.getByLabel('1注文金額上限')).toHaveValue(String(RISK_SETTINGS.limits.maxOrderAmount));
    // ガード・段階は参照表示（見出し存在で確認）。
    await expect(page.getByText('取引ガード（参照）')).toBeVisible();
    await expect(page.getByText('運用段階（参照）')).toBeVisible();
  });

  test('権限外は NotFound（存在秘匿）で BFF を呼ばない', async ({ page }) => {
    let bffCalled = false;
    page.on('request', (req) => {
      if (req.url().includes('/bff/')) bffCalled = true;
    });
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['user']));

    await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'リスク設定' })).toHaveCount(0);
    expect(bffCalled).toBe(false);
  });

  test('保存（PUT /risk-controls/settings/limits 200）で成功通知を表示する', async ({ page }) => {
    let putPath: string | null = null;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/limits': (route) => {
        putPath = new URL(route.request().url()).pathname;
        return { status: 200, body: RISK_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    await expect(page.getByRole('heading', { name: 'リスク設定' })).toBeVisible();

    await page.getByLabel('変更理由').fill('上限調整');
    await page.getByRole('button', { name: '保存' }).click();

    await expect(page.getByText('保存しました。')).toBeVisible();
    // 画面は /bff/risk-controls/settings/limits を叩く（BFF が Risk へプロキシすべき契約の追認）。
    expect(putPath).toBe('/bff/risk-controls/settings/limits');
  });

  test('競合（PUT 409）でメッセージを表示し破壊的な再試行をしない', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/limits': () => {
        putCount += 1;
        return { status: 409, body: { title: '競合', detail: '競合が発生しました。' } };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    await expect(page.getByRole('heading', { name: 'リスク設定' })).toBeVisible();

    await page.getByLabel('変更理由').fill('上限調整');
    await page.getByRole('button', { name: '保存' }).click();

    await expect(page.getByRole('alert').filter({ hasText: '競合' })).toBeVisible();
    expect(putCount).toBe(1);
  });

  test('取得失敗（500）は取得失敗メッセージへ縮退する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'GET /risk-controls/settings': { status: 500 },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    await expect(page.getByRole('alert').filter({ hasText: 'リスク設定の取得に失敗' })).toBeVisible();
  });
});
