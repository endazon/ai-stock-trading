import { test, expect } from '@playwright/test';
import {
  defaultBff,
  installBff,
  pathWithRoles,
  ASSUMPTIONS,
  ASSUMPTIONS_400,
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
        // #1465, #1470: ConfigurationService の 400 の実応答（契約フィクスチャ。`{ error }`）。
        body: ASSUMPTIONS_400,
      },
    });
    await page.goto(pathWithRoles('/settings', ['trading-owner']));
    await expect(page.getByRole('heading', { name: '設定' })).toBeVisible();

    await page.getByLabel('変更理由', { exact: true }).fill('不正値');
    await page.getByRole('button', { name: '保存', exact: true }).click();

    await expect(page.getByRole('alert').filter({ hasText: '入力' })).toContainText(ASSUMPTIONS_400.error);
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

// #1513（#1410 の網羅表 A-023 の型の部分）, SC-01, FR-17: 前提条件の欄が空欄・数値でなければ保存できない。
// 黙って 0 を送らない（安全既定）。範囲の検証は後段の 400（A-021）、内訳の合計と総額の整合は #1514 で扱う。
test.describe('SC-01 前提条件の型の検証（#1513）', () => {
  // 欄は `type=number` であり、ブラウザは数値でない文字を受け付けない（実ブラウザで打てる「型の誤り」は空欄だけ）。
  // 数値でない値の判定（invalidFieldLabels）は単体テストが持つ。
  for (const label of ['月次費用上限 総額', '月次費用上限 LLM', '譲渡益税率'] as const) {
    test(`A-023 「${label}」が空欄なら保存できず、欄の名前を alert に出して PUT を送らない`, async ({ page }) => {
      let putCount = 0;
      await installBff(page, {
        ...defaultBff(),
        'PUT /assumptions': () => {
          putCount++;
          return { status: 200, body: ASSUMPTIONS };
        },
      });
      await page.goto(pathWithRoles('/settings', ['trading-owner']));
      await expect(page.getByRole('heading', { name: '設定' })).toBeVisible();

      await page.getByLabel('変更理由', { exact: true }).fill('型の検証');
      const field = page.getByLabel(label, { exact: true });
      const original = await field.inputValue();
      await field.fill('');

      await expect(page.getByRole('alert').filter({ hasText: '未入力または数値でない項目があります' })).toContainText(label);
      const save = page.getByRole('button', { name: '保存', exact: true });
      await expect(save).toBeDisabled();
      // フォームの送信（Enter）でも送らない。
      await field.press('Enter');
      await expect(page.getByText('保存しました。')).toHaveCount(0);
      expect(putCount).toBe(0);

      // 元の数値へ直せば保存できる（検証が欄の値に追随している）。#1514 で費用上限は合計 ＝ 総額も要るため、元の値へ戻す。
      await field.fill(original);
      await expect(save).toBeEnabled();
    });
  }
});

// #1514（#1410 の網羅表 A-023 の合計の整合・A-024）, SC-01, FR-17, 05_trading-assumptions §6, IADR-0605:
// 月次費用上限は「総額 ＝ LLM＋インフラ＋データ」・各欄 0 以上。画面は同じ規則で保存を無効にし（正は後段の 400）、
// 金額の単位「円」を明示する（費用上限は円建て。判定の基準通貨 USD はリスク統制の側だけ）。
test.describe('SC-01 月次費用上限の整合と通貨（#1514）', () => {
  test('A-023 内訳の合計が総額と一致しなければ保存できず、理由を alert に出して PUT を送らない', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /assumptions': () => {
        putCount++;
        return { status: 200, body: ASSUMPTIONS };
      },
    });
    await page.goto(pathWithRoles('/settings', ['trading-owner']));
    await expect(page.getByRole('heading', { name: '設定' })).toBeVisible();

    await page.getByLabel('変更理由', { exact: true }).fill('費用上限の見直し');
    // フィクスチャは 50,000 ＝ 30,000＋15,000＋5,000。LLM だけを 1 円増やす。
    await page.getByLabel('月次費用上限 LLM', { exact: true }).fill('30001');

    await expect(page.getByRole('alert').filter({ hasText: '一致しません' })).toContainText(
      '内訳の合計（LLM＋インフラ＋データ ＝ 50001 円）が総額（50000 円）と一致しません。',
    );
    const save = page.getByRole('button', { name: '保存', exact: true });
    await expect(save).toBeDisabled();
    await page.getByLabel('月次費用上限 LLM', { exact: true }).press('Enter');
    expect(putCount).toBe(0);

    // 総額を合わせれば保存できる。
    await page.getByLabel('月次費用上限 総額', { exact: true }).fill('50001');
    await expect(save).toBeEnabled();
    await save.click();
    await expect(page.getByText('保存しました。')).toBeVisible();
    expect(putCount).toBe(1);
  });

  test('A-024 月次費用上限の 4 欄に単位「円」を明示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings', ['trading-owner']));
    await expect(page.getByRole('heading', { name: '設定' })).toBeVisible();

    for (const label of ['月次費用上限 総額', '月次費用上限 LLM', '月次費用上限 インフラ', '月次費用上限 データ']) {
      await expect(page.getByLabel(label, { exact: true })).toHaveAccessibleDescription('円');
    }
    await expect(page.getByText('月次費用上限（円／月。総額 ＝ LLM＋インフラ＋データ）')).toBeVisible();
  });
});
