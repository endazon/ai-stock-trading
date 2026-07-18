import { test, expect } from '@playwright/test';
import { defaultBff, installBff, pathWithRoles } from './fixtures';

// SC-03, FR-10, FR-20, UC-06, IADR-0087: 統制状態参照画面（参照専用）の実ブラウザ E2E。
// BFF 応答はモック（page.route）。実 API・実クラスタ疎通に依存しない（live 検証は #82 系／MSP #284）。
// 破壊的操作（pause/resume・kill switch・段階承認）は本画面に無い（Bot 側と役割分担）ため、参照表示と縮退を検証する。

test.describe('SC-03 統制状態参照（#187）', () => {
  test('trading-owner に統制状態と段階ゲートを表示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/controls', ['trading-owner']));

    await expect(page.getByRole('heading', { name: '統制状態' })).toBeVisible();
    // 上限使用率テーブル（統制状態領域）と昇格評価（段階ゲート領域）の双方が描画される。
    await expect(page.getByRole('table', { name: '上限使用率' })).toBeVisible();
    await expect(page.getByRole('heading', { name: '昇格評価' })).toBeVisible();
  });

  test('権限外は NotFound（存在秘匿）で BFF を呼ばない', async ({ page }) => {
    let bffCalled = false;
    page.on('request', (req) => {
      if (req.url().includes('/bff/')) bffCalled = true;
    });
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/controls', ['user']));

    await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
    await expect(page.getByRole('heading', { name: '統制状態' })).toHaveCount(0);
    expect(bffCalled).toBe(false);
  });

  test('段階ゲート取得の失敗（500）は段階ゲート領域のみ縮退する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'GET /risk-controls/stage-gate': { status: 500 },
    });
    await page.goto(pathWithRoles('/controls', ['trading-owner']));

    // 統制状態（上限使用率）は表示され、段階ゲートのみ縮退する（一方の失敗が他方を巻き込まない）。
    await expect(page.getByRole('table', { name: '上限使用率' })).toBeVisible();
    await expect(page.getByText('段階ゲートは利用できません。')).toBeVisible();
    await expect(page.getByRole('heading', { name: '昇格評価' })).toHaveCount(0);
  });

  test('統制状態取得の失敗（500）は統制状態領域のみ縮退する（段階ゲートは表示）', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'GET /risk-controls/status': { status: 500 },
    });
    await page.goto(pathWithRoles('/controls', ['trading-owner']));

    await expect(page.getByRole('alert').filter({ hasText: '統制状態の取得に失敗' })).toBeVisible();
    // 段階ゲート領域は独立して表示される。
    await expect(page.getByRole('heading', { name: '昇格評価' })).toBeVisible();
  });
});
