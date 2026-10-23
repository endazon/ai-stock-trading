import { test, expect } from '@playwright/test';
import type { Page, Request } from '@playwright/test';
import {
  defaultBff,
  installBff,
  pathWithRoles,
  OPEND_AUTH_STATE_WAITING,
  RISK_STATUS_PAPER,
} from './fixtures';
import type { BffConfig } from './fixtures';

// SC-04, FR-09, FR-11, FR-12, UC-06（代替フロー「ゲートウェイの有人認証」）, NFR-05, NFR-06, ADR-0024,
// IADR-0321, IADR-0322, IADR-0087, #1413（網羅表 A-001〔SC-04 分〕・A-006・A-060〜A-065）:
// OpenD 認証操作画面の実ブラウザ E2E（L1）。BFF 応答はモック（page.route）であり、状態の本文は
// BFF の実応答（契約フィクスチャ `opend-auth.state.json`）を土台にする。
//
// 🔴 本画面で高くつく取り違えは 2 つある。
//   (1) 「いま入力を待っていない（ログイン済み・正常）」と「状態を取得できていない（ゲートウェイ不達）」を混ぜる
//       ——壊れたゲートウェイが正常に見える。
//   (2) 検証コードの値を画面・履歴に残す（NFR-05 の適用範囲を広げた扱い）。

const ROUTE = '/opend-auth';
const CODE = '483921';

// 入力を待っていない（ログイン済み）。BFF は promptAvailability = 2（NotApplicable＝対象なし）を宣言する。
const IDLE = { ...OPEND_AUTH_STATE_WAITING, connection: 'idle', prompt: null, promptAvailability: 2 };
// ゲートウェイへ到達できない。BFF は 200 のまま promptAvailability = 1（NotSupplied＝供給が無い）を宣言する。
const UNAVAILABLE = { ...OPEND_AUTH_STATE_WAITING, connection: 'unavailable', prompt: null, promptAvailability: 1 };

function codeForm(page: Page) {
  return page.getByRole('form', { name: '検証コードの送信' });
}

function codeInput(page: Page) {
  return codeForm(page).getByLabel(/SMS 検証コード|検証コード/);
}

async function open(page: Page, config: BffConfig = defaultBff()) {
  await installBff(page, config);
  await page.goto(pathWithRoles(ROUTE, ['trading-owner']));
  await expect(page.getByRole('heading', { name: 'OpenD 認証操作' })).toBeVisible();
}

// POST の本文と回数を記録する（「自動で再試行しない」「本文はコードだけ」を数えて確かめる）。
function recorder() {
  const bodies: unknown[] = [];
  const handler = (status: number, body?: unknown) => (route: { request(): Request }) => {
    bodies.push(route.request().postDataJSON());
    return { status, body };
  };
  return { bodies, handler };
}

test.describe('SC-04 OpenD 認証操作（#1413）', () => {
  // A-060（P1）
  test('trading-owner にゲートウェイの状態と送信履歴（供給が無い）を表示する', async ({ page }) => {
    await open(page);

    await expect(page.getByText('検証コード待ち', { exact: true })).toBeVisible();
    await expect(page.getByText('（API は未稼働 — 発注・照会は届きません）')).toBeVisible();
    await expect(page.getByText('SMS 検証コード', { exact: true })).toBeVisible();
    await expect(page.getByText('永続化済', { exact: true })).toBeVisible();
    await expect(page.getByText('安定（固定 NAT）', { exact: true })).toBeVisible();
    // 現在の発注先は参照のみ（変更は SC-02）。既定は moomoo SIMULATE。
    await expect(page.getByText(/moomoo SIMULATE（デモ環境）（参照のみ — 変更は「リスク設定」画面）/)).toBeVisible();

    // 送信履歴は照会 API が無い。「履歴はありません」と描かず、供給が無いと述べる（FR-11・計画「表示項目の供給元」）。
    await expect(page.getByText(/送信履歴を取得できていません（供給元がありません）/)).toBeVisible();
    await expect(page.getByText(/送信履歴はありません/)).toHaveCount(0);
  });

  // A-001（SC-04 分）・A-060 の否定形（P0）: 存在秘匿。
  test('trading-owner を持たない利用者は NotFound で、/bff/ を 1 件も呼ばない', async ({ page }) => {
    const bffRequests: string[] = [];
    page.on('request', (req) => {
      if (req.url().includes('/bff/')) bffRequests.push(req.url());
    });
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles(ROUTE, ['user']));

    await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'OpenD 認証操作' })).toHaveCount(0);
    await expect(page.getByRole('textbox')).toHaveCount(0);
    expect(bffRequests).toEqual([]);
  });

  // A-006（P1）: FR-12 のバナーは SC-01〜04 のすべてに出す（2026-09-09 に SC-04 も対象へ追加）。
  test('内蔵 paper 稼働中は警告バナーを表示する', async ({ page }) => {
    await open(page, { ...defaultBff(), 'GET /risk-controls/status': { status: 200, body: RISK_STATUS_PAPER } });

    const banner = page.getByRole('alert', { name: '内蔵 paper 稼働中の警告' });
    await expect(banner).toContainText('デバッグモードです。外部へ発注していません');
    await expect(banner).toContainText('この期間は Stage 1 の実績に算入されません');
  });

  test('SIMULATE 稼働中は警告バナーを表示しない', async ({ page }) => {
    await open(page);
    await expect(page.getByText('検証コード待ち', { exact: true })).toBeVisible();
    await expect(page.getByRole('alert', { name: '内蔵 paper 稼働中の警告' })).toHaveCount(0);
  });

  // A-061（P1）: 待機中でなければ入力と送信を無効にする（計画「入力 / バリデーション」）。
  test('入力を待っていない（ログイン済み）ときは入力欄と送信が無効で、理由を述べる', async ({ page }) => {
    const code = recorder();
    await open(page, {
      ...defaultBff(),
      'GET /opend-auth/state': { status: 200, body: IDLE },
      'POST /opend-auth/code': code.handler(204),
    });

    await expect(page.getByText('ログイン済み — API 稼働中', { exact: true })).toBeVisible();
    await expect(page.getByText('いま OpenD は入力を待っていません', { exact: true })).toBeVisible();
    await expect(codeInput(page)).toBeDisabled();
    await expect(codeForm(page).getByRole('button', { name: '送信' })).toBeDisabled();
    await expect(
      page.getByText('いま OpenD は入力を待っていないため、入力・送信はできません（ログイン済みです）。'),
    ).toBeVisible();
    expect(code.bodies).toHaveLength(0);
  });

  test('SMS 待ちでは 6 桁の数字が揃うまで送信できない', async ({ page }) => {
    const code = recorder();
    await open(page, { ...defaultBff(), 'POST /opend-auth/code': code.handler(204) });

    const input = codeInput(page);
    const submit = codeForm(page).getByRole('button', { name: '送信' });
    await expect(input).toBeEnabled();
    await expect(submit).toBeDisabled();
    for (const invalid of ['12345', '1234567', 'abcdef']) {
      await input.fill(invalid);
      await expect(submit).toBeDisabled();
      await input.press('Enter');
    }
    expect(code.bodies).toHaveLength(0);

    await input.fill(CODE);
    await expect(submit).toBeEnabled();
  });

  // A-062（P1）: 送れるのは 3 種だけで、自由入力のコンソールが無い（許可リストはサーバ側で強制する）。
  test('入力欄は 1 つ、操作は送信と SMS の再送だけで、送る本文はコードだけである', async ({ page }) => {
    const code = recorder();
    const resend = recorder();
    await open(page, {
      ...defaultBff(),
      'POST /opend-auth/code': code.handler(204),
      'POST /opend-auth/resend': resend.handler(204),
    });

    const main = page.locator('#root');
    await expect(main.getByRole('textbox')).toHaveCount(1);
    await expect(main.locator('textarea, select, input:not([name="code"])')).toHaveCount(0);
    await expect(main.getByRole('button')).toHaveText(['送信', 'SMS を再送']);
    // 画面はコマンド名を選ばせない（コマンドはサーバが待機中のプロンプトから決める）。
    await expect(main.getByRole('combobox')).toHaveCount(0);
    await expect(main.getByRole('radio')).toHaveCount(0);

    // 許可リストの提示: 3 コマンドが「許可」、残りの 2 行（6 コマンド）が「送れない」。
    const allowList = page.getByRole('table', { name: 'この画面から送れる操作' });
    await expect(allowList.getByText('許可', { exact: true })).toHaveCount(3);
    await expect(allowList.getByText('送れない', { exact: true })).toHaveCount(2);

    await codeInput(page).fill(CODE);
    await codeForm(page).getByRole('button', { name: '送信' }).click();
    await expect(page.getByText('検証コードを送信しました。')).toBeVisible();
    await codeForm(page).getByRole('button', { name: 'SMS を再送' }).click();
    await expect(page.getByText('SMS の再送を要求しました。')).toBeVisible();

    // 本文はコードだけ（コマンド種別の欄を持たない）。再送は本文を持たない。
    expect(code.bodies).toEqual([{ code: CODE }]);
    expect(resend.bodies).toEqual([null]);
  });

  // A-063（P1）: 「待機していない」と「状態を取得できない」を描き分ける。
  test('状態の取得失敗（500）は「確認できていない」と述べ、ログイン済みに見せない', async ({ page }) => {
    await open(page, { ...defaultBff(), 'GET /opend-auth/state': { status: 500 } });

    await expect(page.getByText('「いま入力を待っていない」のではなく、確認できていません。')).toBeVisible();
    await expect(page.getByText('ログイン済み — API 稼働中')).toHaveCount(0);
    await expect(page.getByText(/ログイン済みです/)).toHaveCount(0);
    await expect(codeInput(page)).toBeDisabled();
    await expect(codeForm(page).getByRole('button', { name: 'SMS を再送' })).toBeDisabled();
    await expect(
      page.getByText('ゲートウェイの状態を取得できていません（供給元がありません）。入力・送信はできません。'),
    ).toBeVisible();
  });

  test('ゲートウェイ不達の宣言（unavailable）は「供給が無い」と描き、ログイン済みに見せない', async ({ page }) => {
    await open(page, { ...defaultBff(), 'GET /opend-auth/state': { status: 200, body: UNAVAILABLE } });

    await expect(page.getByText('OpenD の常駐コンテナへ到達できず、待機中のプロンプトを読めていません。', { exact: false })).toBeVisible();
    await expect(page.getByText('ログイン済み — API 稼働中')).toHaveCount(0);
    await expect(page.getByText('いま OpenD は入力を待っていません', { exact: true })).toHaveCount(0);
    await expect(codeInput(page)).toBeDisabled();
    await expect(
      page.getByText('ゲートウェイの状態を取得できていません（供給元がありません）。入力・送信はできません。'),
    ).toBeVisible();
  });

  // A-064（P2）: レート制限。**BFF の境界では 409 `rate_limited`**（上流の 429 を BFF が写す。#1456）。
  test('再送のレート制限は残り秒数つきの alert を出し、自動で再試行しない', async ({ page }) => {
    const resend = recorder();
    await open(page, {
      ...defaultBff(),
      'POST /opend-auth/resend': resend.handler(409, { reason: 'rate_limited', retryAfterSeconds: 42 }),
    });

    const button = codeForm(page).getByRole('button', { name: 'SMS を再送' });
    await button.click();

    await expect(codeForm(page).getByRole('alert')).toHaveText(
      '再送の間隔制限に掛かりました。あと 42 秒待ってから再送してください。',
    );
    await expect(button).toBeEnabled();
    expect(resend.bodies).toHaveLength(1);
  });

  test('送信のレート制限と「待機していない」の 409 を描き分け、いずれも再試行しない', async ({ page }) => {
    let calls = 0;
    await open(page, {
      ...defaultBff(),
      'POST /opend-auth/code': () => {
        calls += 1;
        return calls === 1
          ? { status: 409, body: { reason: 'rate_limited', retryAfterSeconds: 7 } }
          : { status: 409, body: { reason: 'not_waiting' } };
      },
    });

    await codeInput(page).fill(CODE);
    await codeForm(page).getByRole('button', { name: '送信' }).click();
    await expect(codeForm(page).getByRole('alert')).toHaveText('送信の上限に達しました。あと 7 秒待ってから送信してください。');

    await codeInput(page).fill(CODE);
    await codeForm(page).getByRole('button', { name: '送信' }).click();
    await expect(codeForm(page).getByRole('alert')).toHaveText('いま OpenD は入力を待っていないため、送信できませんでした。');
    expect(calls).toBe(2);
  });

  // A-065（P2）: 検証コードの値はどこにも残さない（NFR-05 の適用範囲を広げた扱い）。
  test('送信に成功すると入力欄が空になり、画面のどこにもコード値が出ない', async ({ page }) => {
    await open(page);

    await codeInput(page).fill(CODE);
    await codeForm(page).getByRole('button', { name: '送信' }).click();

    await expect(page.getByText('検証コードを送信しました。')).toBeVisible();
    await expect(codeInput(page)).toHaveValue('');
    await expect(page.locator('body')).not.toContainText(CODE);
  });

  test('送信が拒まれても入力値を反射せず、入力欄も空になる', async ({ page }) => {
    await open(page, {
      ...defaultBff(),
      'POST /opend-auth/code': { status: 400, body: { title: '受け付けられません', detail: 'invalid_code' } },
    });

    await codeInput(page).fill(CODE);
    await codeForm(page).getByRole('button', { name: '送信' }).click();

    await expect(codeForm(page).getByRole('alert')).toHaveText(
      '検証コードが受け付けられませんでした。もう一度入力してください。',
    );
    await expect(codeInput(page)).toHaveValue('');
    await expect(page.locator('body')).not.toContainText(CODE);
  });
});
