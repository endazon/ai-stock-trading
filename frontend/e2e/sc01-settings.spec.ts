import { test, expect } from '@playwright/test';
import {
  defaultBff,
  installBff,
  pathWithRoles,
  ASSUMPTIONS,
  ASSUMPTIONS_HISTORY,
  MONITOR_SETTINGS,
} from './fixtures';

// SC-01, FR-17, UC-06, IADR-0087: 設定画面（全体前提条件の閲覧/変更）の実ブラウザ E2E。
// BFF 応答はモック（page.route）。実 API・実クラスタ疎通に依存しない（live 検証は #82 系／MSP #284）。

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

    // #340: `exact: true` を外さないこと。§2（収集パラメータ）が「収集パラメータの変更履歴」
    // 「収集パラメータの変更理由」を同一画面へ足したため、部分一致だと 2 要素へ解決して
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

// SC-01 §2, FR-13, FR-03, UC-06, #340, IADR-0155: 収集パラメータ（変動閾値・収集間隔）。
//
// 計画は変動閾値と収集間隔の閲覧・変更を求めるが、**収集間隔には供給が無い**（起動時構成のみ）。
// 動かない入力欄を置くと「変更できたつもり」を生むため、入力欄を作らず変更できない事実を明示する。
test.describe('SC-01 §2 収集パラメータ（#340）', () => {
  test('変動閾値を百分率で表示し、理由必須で保存できる', async ({ page }) => {
    let putBody: unknown = null;
    await installBff(page, {
      ...defaultBff(),
      'PUT /monitor/settings/movement-threshold': (route) => {
        putBody = route.request().postDataJSON();
        return { status: 200, body: MONITOR_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings', ['trading-owner']));

    const threshold = page.getByLabel('変動閾値 %');
    await expect(threshold).toHaveValue('3');

    // 理由が空欄のうちは保存できない（監査のため理由必須）。
    await expect(page.getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();

    await threshold.fill('5');
    await page.getByLabel('収集パラメータの変更理由').fill('ボラティリティ上昇');
    await page.getByRole('button', { name: '変動閾値を保存' }).click();

    await expect(page.getByText('保存しました。')).toBeVisible();
    // **ワイヤは比率**（5% → 0.05）。百分率のまま送ると閾値が 100 倍になる。
    expect(putBody).toEqual({ movementThresholdRatio: 0.05, reason: 'ボラティリティ上昇' });
  });

  test('収集間隔の入力欄が存在せず、変更できないことを明示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings', ['trading-owner']));

    await expect(page.getByText('収集間隔は本画面から閲覧・変更できません。')).toBeVisible();
    // **否定形**: 収集間隔の入力欄は 1 つも無い。
    await expect(page.getByLabel(/収集間隔/)).toHaveCount(0);
  });

  test('値域外の変動閾値は保存できない（サーバへ送らない）', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /monitor/settings/movement-threshold': () => {
        putCount += 1;
        return { status: 200, body: MONITOR_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings', ['trading-owner']));

    await page.getByLabel('収集パラメータの変更理由').fill('検証');
    await page.getByLabel('変動閾値 %').fill('51');

    await expect(page.getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();
    expect(putCount).toBe(0);
  });

  test('楽観排他の競合（409）は自動再試行せず再読込を促す', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /monitor/settings/movement-threshold': () => {
        putCount += 1;
        return { status: 409, body: { title: '競合', detail: '設定が他の更新と競合しました。' } };
      },
    });
    await page.goto(pathWithRoles('/settings', ['trading-owner']));

    await page.getByLabel('変動閾値 %').fill('5');
    await page.getByLabel('収集パラメータの変更理由').fill('競合検証');
    await page.getByRole('button', { name: '変動閾値を保存' }).click();

    await expect(
      page.getByText('競合が発生しました。最新を取得して再試行してください。'),
    ).toBeVisible();
    expect(putCount).toBe(1); // 自動再試行しない
  });

  test('収集パラメータの取得失敗（500）は当該領域のみ縮退する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'GET /monitor/settings': { status: 500 },
    });
    await page.goto(pathWithRoles('/settings', ['trading-owner']));

    await expect(page.getByText('収集パラメータを取得できませんでした。')).toBeVisible();
    // §1（ConfigurationService 由来）は独立して表示される。
    await expect(page.getByText(/現在のバージョン:\s*3/)).toBeVisible();
  });
});
