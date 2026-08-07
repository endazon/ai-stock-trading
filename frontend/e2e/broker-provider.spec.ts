import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';
import {
  defaultBff,
  installBff,
  pathWithRoles,
  RISK_SETTINGS,
  RISK_SETTINGS_PAPER,
  RISK_STATUS_PAPER,
} from './fixtures';

// SC-01/02/03, FR-20, FR-12, FR-13, UC-06, INDEX 決定 46, #334, IADR-0141:
// 発注先（Broker Provider）の 2 軸分離まわりの実ブラウザ E2E。BFF 応答はモック（page.route）。
//
// **本 spec の中核は「実弾切替が『OK』1 押しで通れないこと」の退行検知である**（FR-20 (1)）。
// 画面の実装が「保存ボタンの disabled を外す」1 行で退行しても、ここで落ちる。

function providerForm(page: Page) {
  return page.getByRole('form', { name: '発注先の変更' });
}

function liveDialog(page: Page) {
  return page.getByRole('dialog', { name: '実弾（moomoo REAL）への切替の確認' });
}

test.describe('SC-02 発注先の変更（#334）', () => {
  test('運用段階と発注先を別の行として表示し、3 値から選べる', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    await expect(page.getByText('運用段階と発注先（参照）')).toBeVisible();
    await expect(providerForm(page).getByRole('radio')).toHaveCount(3);
  });

  test('理由が空では発注先を保存できない', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/broker-provider': () => {
        putCount += 1;
        return { status: 200, body: { settings: RISK_SETTINGS } };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    const form = providerForm(page);
    await form.getByRole('radio', { name: /内蔵 paper/ }).check();

    await expect(form.getByRole('button', { name: '保存' })).toBeDisabled();
    expect(putCount).toBe(0);
  });

  test('実弾以外への切替は理由だけで保存できる', async ({ page }) => {
    let body: unknown = null;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/broker-provider': (route) => {
        body = route.request().postDataJSON();
        return { status: 200, body: { settings: RISK_SETTINGS } };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    const form = providerForm(page);
    await form.getByRole('radio', { name: /内蔵 paper/ }).check();
    await form.getByLabel('変更理由').fill('デバッグのため');
    await form.getByRole('button', { name: '保存' }).click();

    await expect(form.getByText('保存しました。')).toBeVisible();
    expect(body).toMatchObject({ provider: 0, reason: 'デバッグのため' });
  });
});

test.describe('SC-02 実弾（moomoo REAL）切替の警告モーダル（#334）', () => {
  async function openModal(page: Page) {
    const form = providerForm(page);
    await form.getByRole('radio', { name: /moomoo REAL/ }).check();
    await form.getByLabel('変更理由').fill('実弾へ移行する');
    await form.getByRole('button', { name: '実弾への切替を確認する' }).click();
    return liveDialog(page);
  }

  test('モーダルは計画の 4 点を提示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const dialog = await openModal(page);

    // ① 実資金で執行される旨
    await expect(dialog.getByText('これ以降の注文は実際の資金で執行されます。')).toBeVisible();
    // ② 切替先と現在の Stage の組み合わせ（Stage 1 のまま実弾＝段階ゲートを飛ばしている旨）
    await expect(dialog.getByText(/段階ゲート.*を飛ばしています/)).toBeVisible();
    // FR-20 (1), #422: **同意しても 1 件も発注されない**旨。計画が「警告モーダルにも含める」と名指しした。
    // これが無いと「同意したのに発注されない＝壊れている」と読まれる。
    await expect(dialog.getByText(/段階が実弾を既定とするまで発注は行われません/)).toBeVisible();
    // ③ 現在の equity と統制値の実額
    await expect(dialog.getByRole('table', { name: '現在の equity と統制値' })).toBeVisible();
    // ④ 明示的な確認操作（チェックボックス＋文字入力）
    await expect(dialog.getByRole('checkbox')).toBeVisible();
    await expect(dialog.getByLabel(/「REAL」と入力/)).toBeVisible();
  });

  // **退行防止の中核**: 「OK」1 押しで通過できないこと。
  test('確認操作なしでは切替ボタンが無効で PUT が飛ばない', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/broker-provider': () => {
        putCount += 1;
        return { status: 200, body: { settings: RISK_SETTINGS } };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const dialog = await openModal(page);

    await expect(dialog.getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();

    // 片方だけでも通らない（同意のみ）。
    await dialog.getByRole('checkbox').check();
    await expect(dialog.getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();

    // 片方だけでも通らない（文字入力のみ）。
    await dialog.getByRole('checkbox').uncheck();
    await dialog.getByLabel(/「REAL」と入力/).fill('REAL');
    await expect(dialog.getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();

    // 大文字小文字を区別する（IADR-0141 決定2）。
    await dialog.getByRole('checkbox').check();
    await dialog.getByLabel(/「REAL」と入力/).fill('real');
    await expect(dialog.getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();

    expect(putCount).toBe(0);
  });

  test('同意と REAL の入力が揃うと切替でき、確認内容とともに PUT する', async ({ page }) => {
    let body: unknown = null;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/broker-provider': (route) => {
        body = route.request().postDataJSON();
        return { status: 200, body: { settings: RISK_SETTINGS } };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const dialog = await openModal(page);

    await dialog.getByRole('checkbox').check();
    await dialog.getByLabel(/「REAL」と入力/).fill('REAL');
    await dialog.getByRole('button', { name: '実弾へ切り替える' }).click();

    await expect(providerForm(page).getByText('保存しました。')).toBeVisible();
    expect(body).toMatchObject({
      provider: 1,
      reason: '実弾へ移行する',
      acknowledgedLiveTrading: true,
      acknowledgement: 'REAL',
    });
  });

  // ③ を提示できない状態では切り替えさせない（提示できない項目がある確認は同意にならない）。
  test('equity と統制値を取得できない場合は切替できない', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'GET /risk-controls/status': { status: 500 },
      'PUT /risk-controls/settings/broker-provider': () => {
        putCount += 1;
        return { status: 200, body: { settings: RISK_SETTINGS } };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const dialog = await openModal(page);

    await dialog.getByRole('checkbox').check();
    await dialog.getByLabel(/「REAL」と入力/).fill('REAL');

    await expect(dialog.getByText(/equity と統制値を取得できないため/)).toBeVisible();
    await expect(dialog.getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();
    expect(putCount).toBe(0);
  });
});

// FR-12, 05_screens 共通規約: 内蔵 paper 稼働中は SC-01 / SC-02 / SC-03 の**すべて**で上部にバナーを出す。
// モックアップは SC-02 にしかバナーを描いていない——見た目だけを頼りにすると SC-01・SC-03 を落とす。
test.describe('内蔵 paper 稼働中の警告バナー（FR-12・#334）', () => {
  const PAPER_ROUTES: readonly { name: string; path: string }[] = [
    { name: 'SC-01 設定', path: '/settings' },
    { name: 'SC-02 リスク設定', path: '/settings/risk' },
    { name: 'SC-03 統制状態', path: '/controls' },
  ];

  for (const route of PAPER_ROUTES) {
    test(`${route.name} は paper 稼働中に必須 2 文言のバナーを表示する`, async ({ page }) => {
      await installBff(page, {
        ...defaultBff(),
        'GET /risk-controls/settings': { status: 200, body: RISK_SETTINGS_PAPER },
        'GET /risk-controls/status': { status: 200, body: RISK_STATUS_PAPER },
      });
      await page.goto(pathWithRoles(route.path, ['trading-owner']));

      const banner = page.getByRole('alert', { name: '内蔵 paper 稼働中の警告' });
      await expect(banner).toBeVisible();
      await expect(banner).toContainText('デバッグモードです。外部へ発注していません');
      await expect(banner).toContainText('この期間は Stage 1 の実績に算入されません');
    });

    // 否定形: paper でないときに出してはならない。
    test(`${route.name} は SIMULATE 稼働中にバナーを表示しない`, async ({ page }) => {
      await installBff(page, defaultBff());
      await page.goto(pathWithRoles(route.path, ['trading-owner']));

      await expect(page.getByRole('heading').first()).toBeVisible();
      await expect(page.getByRole('alert', { name: '内蔵 paper 稼働中の警告' })).toHaveCount(0);
    });
  }
});

// FR-20 (2), SC-03: 発注先の変更履歴と Stage 1 進捗（除外営業日数の併記）。SC-03 は参照専用である。
test.describe('SC-03 発注先の参照と変更履歴（#334）', () => {
  test('発注先を表示し、変更 UI は持たない', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/controls', ['trading-owner']));

    await expect(page.getByText('発注先').first()).toBeVisible();
    await expect(page.getByRole('form', { name: '発注先の変更' })).toHaveCount(0);
  });

  test('Stage 1 の進捗に paper 稼働による除外日数を併記する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/controls', ['trading-owner']));

    await expect(page.getByText(/経過 42 \/ 60 営業日/)).toContainText(
      'paper 稼働により 3 日を除外',
    );
  });

  test('発注先の変更履歴の取得失敗はその領域のみ縮退する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'GET /risk-controls/settings/history': { status: 500 },
    });
    await page.goto(pathWithRoles('/controls', ['trading-owner']));

    await expect(page.getByText('発注先の変更履歴は利用できません。')).toBeVisible();
    await expect(page.getByRole('heading', { name: '統制状態' })).toBeVisible();
  });
});
