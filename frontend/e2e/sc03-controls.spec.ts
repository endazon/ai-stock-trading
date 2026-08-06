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

// SC-03, FR-10, UC-06, ADR-0016 決定15, #340, IADR-0154: 空売り関連の表示と**参照専用性**。
//
// 計画は SC-03 を「表示のみで設定変更・統制操作は行わない」と定める。「置いていない」ことは
// 書かないだけでは守れないため、入力要素・保存系ボタンが 1 つも無いことを実ブラウザでも固定する。
test.describe('SC-03 空売りの現況と参照専用性（#340）', () => {
  test('維持率を画面最上位に置き、未供給を「取得できていません」と明示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/controls', ['trading-owner']));

    await expect(page.getByRole('heading', { name: '維持率' })).toBeVisible();
    await expect(page.getByText('取得できていません（供給元がありません）').first()).toBeVisible();
    await expect(page.getByText(/この統制は現在まったく働いていません/)).toBeVisible();
    // **否定形**: 未供給なのに正常値に見える表示（0.0%）を出さない。
    await expect(page.getByText('現在の維持率: 0.0%')).toHaveCount(0);
  });

  test('借株料の累計と自動縮小の発動履歴を「0 件」に見せない', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/controls', ['trading-owner']));

    await expect(page.getByText(/0 ではなく「不明」です/)).toBeVisible();
    const reduction = page.getByRole('region', { name: '維持率割れによる自動縮小（動かす統制）' });
    await expect(reduction.getByText(/記録する経路がない/)).toBeVisible();
    await expect(reduction.getByText('発動履歴はありません。')).toHaveCount(0);
  });

  test('維持率割れ自動縮小は 3 統制と別枠で「動かす」統制として描かれる', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/controls', ['trading-owner']));

    const reduction = page.getByRole('region', { name: '維持率割れによる自動縮小（動かす統制）' });
    await expect(reduction.getByText(/すでに建てた玉を決済します/)).toBeVisible();
    await expect(reduction.getByText(/必要証拠金が最も大きい建玉から/)).toBeVisible();
    // 3 統制の表には含めない（別枠であることの否定形）。
    const controls = page.getByRole('table', { name: '取引統制（優先順位順）' });
    await expect(controls.getByText(/自動縮小/)).toHaveCount(0);
  });

  test('3 統制を優先順位順に表示し優先統制を明示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/controls', ['trading-owner']));

    const rows = page.getByRole('table', { name: '取引統制（優先順位順）' }).getByRole('row');
    await expect(rows.nth(1)).toContainText('緊急停止（kill switch）');
    await expect(rows.nth(2)).toContainText('日次損失ロックアウト');
    await expect(rows.nth(3)).toContainText('一時停止');
  });

  test('変更操作が 1 つも存在しない（参照専用）', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/controls', ['trading-owner']));
    // 全領域の描画を待つ（描画前に数えると常に 0 で緑になる＝検査が死ぬ）。
    await expect(page.getByRole('table', { name: '上限使用率' })).toBeVisible();
    await expect(page.getByRole('heading', { name: '維持率' })).toBeVisible();

    await expect(page.locator('input')).toHaveCount(0);
    await expect(page.locator('select')).toHaveCount(0);
    await expect(page.locator('textarea')).toHaveCount(0);
    await expect(page.locator('form')).toHaveCount(0);
    await expect(page.getByRole('button')).toHaveCount(0);
  });
});
