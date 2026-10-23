import AxeBuilder from '@axe-core/playwright';
import { test, expect } from '@playwright/test';
import type { Page, TestInfo } from '@playwright/test';
import { defaultBff, installBff, pathWithRoles, RISK_SETTINGS, WATCHLIST } from './fixtures';
import type { BffConfig } from './fixtures';

// NFR-19（WCAG 2.2 レベル AA）, SC-01〜SC-04, FR-20, FR-03, IADR-0087, #1414（網羅表 A-010・A-011）:
// アクセシビリティの機械検査（axe）とキーボード経路の実ブラウザ E2E（L1）。
//
// ■ タグは計画 NFR-19 の 5 つ ＋ `best-practice`（planning#814 の裁定。基盤 NFR-30 と同じ値。基盤の E-10 と揃える）。
//   NFR-19 は「機械検査（axe のタグ `wcag2a`・`wcag2aa`・`wcag21a`・`wcag21aa`・`wcag22aa`）が検出する違反を
//   0 件に保つ」と定める。`best-practice` は適合タグではないが、ランドマークの規則（`region`・`landmark-*`）は
//   このタグにしか属さないので外さない（基盤 IADR-0442 決定 3 と同じ扱い）。
//   **機械検査の 0 件は適合の証明ではない**（同 NFR）。手動の確認の代わりにしない。
//
// ■ 既知の違反は「指紋」（`<規則 ID> <対象セレクタ>`）で受け入れ、**一致を求める**（部分集合ではない）。
//   増えれば落ち、直して減っても台帳から消すまで落ちる（受け入れが恒久化しない）。1 件ずつ理由を書く。
//   `toMatchSnapshot` は `--update-snapshots` で理由なしに受け入れを増やせるため採らない（基盤 IADR-0614 決定 2）。
//
// ■ 🔴 **L1 の射程の限界**（報告済み。テストで埋めたことにしない）:
//   - ハーネスは `@platform/ui` を**スタブ**（`test/ui-stub`）で描き、トークンの CSS も載せない。したがって
//     **色のコントラスト（WCAG 1.4.3）はブラウザ既定の配色でしか測れず、テーマ（light / dark）も無い**。
//     本ユニットの画面の実配色は、基盤の SPA へ合成したときにしか測れない。
//   - Dialog のスタブは**フォーカストラップを写さない**（`test/ui-stub/Dialog.tsx`）。トラップは実物
//     （Base UI）の性質であり、基盤の axe・キーボードの E2E が測る。本 spec が確かめるのは、本ユニットの
//     コードが決めること（初期フォーカスを取消側へ置く・Esc で閉じて送らない・キーボードだけで完結する）である。

/** 計画 NFR-19 の機械検査のタグ（＋ランドマークを見る `best-practice`）。 */
const NFR19_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice'] as const;

interface KnownAxeViolation {
  /** `<規則 ID> <対象セレクタ>`。 */
  fingerprint: string;
  /** 受け入れる理由と、直す issue。 */
  reason: string;
}

/**
 * 既知の違反の台帳（キーは面の名前）。**現在 0 件**。
 * 初回の走査で出た違反は、画面とハーネスの側で直した（作業仕様書 `20261024_1414_e2e-axe-keyboard` §初回の走査）。
 */
const KNOWN_AXE_VIOLATIONS: Readonly<Record<string, readonly KnownAxeViolation[]>> = {};

async function expectAxeClean(page: Page, surface: string, testInfo: TestInfo): Promise<void> {
  // 遷移・アニメーションの途中の色を読まない（基盤 #1438 の実測と同じ予防）。
  await page.addStyleTag({
    content: '*, *::before, *::after { transition: none !important; animation: none !important; }',
  });
  const results = await new AxeBuilder({ page }).withTags([...NFR19_TAGS]).analyze();
  await testInfo.attach(`axe ${surface}`, {
    body: JSON.stringify(results.violations, null, 2),
    contentType: 'application/json',
  });

  // 件数ではなく指紋の配列を突き合わせる（落ちたときに、どの規則がどの要素で落ちたかが残る）。
  const found = results.violations.flatMap((v) => v.nodes.map((n) => `${v.id} ${n.target.join(' ')}`)).sort();
  const known = (KNOWN_AXE_VIOLATIONS[surface] ?? []).map((k) => k.fingerprint).sort();
  expect(found, surface).toEqual(known);

  // ★ 陽性対照: NFR-19 のタグで走ったこと（タグが黙って落ちると、違反 0 件のまま緑になる）。
  expect(results.toolOptions.runOnly, `${surface} の走査タグ`).toEqual({ type: 'tag', values: [...NFR19_TAGS] });
  // ★ 陽性対照: axe が実際に走ったこと（注入に失敗しても violations は空配列になる）。
  expect(results.passes.length, `${surface} で axe の合格判定が 0 件`).toBeGreaterThan(0);
}

async function openAsOwner(page: Page, path: string, heading: string, config: BffConfig = defaultBff()) {
  await installBff(page, config);
  await page.goto(pathWithRoles(path, ['trading-owner']));
  await expect(page.getByRole('heading', { name: heading, exact: true })).toBeVisible();
  // 領域ごとに独立して読み込む画面であり、読み込み中の表示（role=status）を測っても意味がない。
  await page.waitForLoadState('networkidle');
}

function providerForm(page: Page) {
  return page.getByRole('form', { name: '発注先の変更' });
}

function liveDialog(page: Page) {
  return page.getByRole('dialog', { name: '実弾（moomoo REAL）への切替の確認' });
}

function watchlistRowDelete(page: Page, symbol: string) {
  return page.getByRole('table', { name: '監視銘柄' }).getByRole('row').filter({ hasText: symbol }).getByRole('button', { name: '削除' });
}

function deleteConfirm(page: Page) {
  return page.getByRole('group', { name: '監視銘柄の削除確認' });
}

// ---- A-010（P1）: axe 違反 0 を SC-01〜04 と、操作の後に現れる UI で ----

test.describe('axe（WCAG 2.2 A/AA ＋ best-practice）（#1414）', () => {
  const SCREENS = [
    { surface: 'SC-01 設定', path: '/settings', heading: '設定' },
    { surface: 'SC-02 リスク設定', path: '/settings/risk', heading: 'リスク設定' },
    { surface: 'SC-03 統制状態参照', path: '/controls', heading: '統制状態' },
    { surface: 'SC-04 OpenD 認証操作', path: '/opend-auth', heading: 'OpenD 認証操作' },
  ] as const;

  for (const s of SCREENS) {
    test(`${s.surface} に違反が無い`, async ({ page }, testInfo) => {
      await openAsOwner(page, s.path, s.heading);
      await expectAxeClean(page, s.surface, testInfo);
    });
  }

  test('SC-02 実弾（moomoo REAL）切替の警告モーダルを開いた状態に違反が無い', async ({ page }, testInfo) => {
    await openAsOwner(page, '/settings/risk', 'リスク設定');
    const form = providerForm(page);
    await form.getByRole('radio', { name: /moomoo REAL/ }).check();
    await form.getByLabel('変更理由').fill('実弾へ移行する');
    await form.getByRole('button', { name: '実弾への切替を確認する' }).click();
    await expect(liveDialog(page)).toBeVisible();

    await expectAxeClean(page, 'SC-02 REAL 警告モーダル', testInfo);
  });

  test('SC-02 監視銘柄の削除確認を開いた状態に違反が無い', async ({ page }, testInfo) => {
    await openAsOwner(page, '/settings/risk', 'リスク設定');
    await watchlistRowDelete(page, 'AAPL').click();
    await expect(deleteConfirm(page)).toBeVisible();

    await expectAxeClean(page, 'SC-02 監視銘柄の削除確認', testInfo);
  });

  test('SC-02 取引ガードの危険な緩和の確認を開いた状態に違反が無い', async ({ page }, testInfo) => {
    await openAsOwner(page, '/settings/risk', 'リスク設定');
    await page.getByRole('form', { name: '取引ガードの変更' }).getByRole('checkbox', { name: '同日再エントリー禁止' }).uncheck();
    await expect(page.getByText('上記の危険な変更を確認しました')).toBeVisible();

    await expectAxeClean(page, 'SC-02 取引ガードの危険な緩和の確認', testInfo);
  });
});

// ---- A-011（P2）: キーボードだけで完結する経路 ----

test.describe('キーボード経路（#1414）', () => {
  test('REAL 警告モーダル: 取消側へ初期フォーカスし、Esc で閉じて送らず、キーボードだけで切替を確定できる', async ({ page }) => {
    const bodies: unknown[] = [];
    await openAsOwner(page, '/settings/risk', 'リスク設定', {
      ...defaultBff(),
      'PUT /risk-controls/settings/broker-provider': (route) => {
        bodies.push(route.request().postDataJSON());
        return { status: 200, body: { settings: RISK_SETTINGS } };
      },
    });
    const form = providerForm(page);

    // 発注先の選択 → 理由 → 確認ボタンまでをキーボードで辿る。
    await form.getByRole('radio', { name: /moomoo REAL/ }).focus();
    await page.keyboard.press('Space');
    await expect(form.getByRole('radio', { name: /moomoo REAL/ })).toBeChecked();
    await page.keyboard.press('Tab');
    await expect(form.getByLabel('変更理由')).toBeFocused();
    await page.keyboard.type('実弾へ移行する');
    await page.keyboard.press('Enter');

    // 🔴 破壊的な確定ではなく取消側に初期フォーカスがある（Enter の連打で通過させない）。
    const dialog = liveDialog(page);
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'キャンセル' })).toBeFocused();

    // Esc で閉じ、何も送らない。
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    expect(bodies).toHaveLength(0);

    // もう一度開き、同意（Space）と「REAL」の入力をキーボードだけで行って確定する。
    await form.getByRole('button', { name: '実弾への切替を確認する' }).focus();
    await page.keyboard.press('Enter');
    await expect(dialog.getByRole('button', { name: 'キャンセル' })).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(dialog.getByLabel(/「REAL」と入力/)).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(dialog.getByRole('checkbox')).toBeFocused();
    await page.keyboard.press('Space');
    await page.keyboard.press('Tab');
    await page.keyboard.type('REAL');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await expect(dialog.getByRole('button', { name: '実弾へ切り替える' })).toBeFocused();
    await page.keyboard.press('Enter');

    await expect(form.getByText('保存しました。')).toBeVisible();
    expect(bodies).toEqual([
      { provider: 1, reason: '実弾へ移行する', acknowledgedLiveTrading: true, acknowledgement: 'REAL' },
    ]);
  });

  test('監視銘柄の削除確認: 開くと削除理由へ、取り消すと押した行の「削除」へフォーカスが移る', async ({ page }) => {
    const bodies: unknown[] = [];
    let watchlist: { symbol: string; market: number }[] = [...WATCHLIST];
    await openAsOwner(page, '/settings/risk', 'リスク設定', {
      ...defaultBff(),
      'GET /monitor/watchlist': () => ({ status: 200, body: watchlist }),
      'DELETE /monitor/watchlist': (route) => {
        bodies.push(route.request().postDataJSON());
        watchlist = watchlist.filter((w) => w.symbol !== 'AAPL');
        return { status: 200, body: watchlist };
      },
    });

    // 🔴 行の「削除」は確認中に無効化されるため、以前は押した直後にフォーカスが <body> へ落ちていた。
    await watchlistRowDelete(page, 'AAPL').focus();
    await page.keyboard.press('Enter');
    await expect(deleteConfirm(page)).toBeVisible();
    await expect(deleteConfirm(page).getByLabel('削除理由')).toBeFocused();

    // 理由が空の間は「監視から削除」が無効で、Tab は取消へ進む。取り消すと押した行へ戻る。
    await page.keyboard.press('Tab');
    await expect(deleteConfirm(page).getByRole('button', { name: 'キャンセル' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(deleteConfirm(page)).toHaveCount(0);
    await expect(watchlistRowDelete(page, 'AAPL')).toBeFocused();
    expect(bodies).toHaveLength(0);

    // キーボードだけで削除を確定する。行が消えたら、残る先頭の行の「削除」へフォーカスが移る。
    await page.keyboard.press('Enter');
    await expect(deleteConfirm(page).getByLabel('削除理由')).toBeFocused();
    await page.keyboard.type('監視を終える');
    await page.keyboard.press('Tab');
    await expect(deleteConfirm(page).getByRole('button', { name: '監視から削除' })).toBeFocused();
    await page.keyboard.press('Enter');

    await expect(deleteConfirm(page)).toHaveCount(0);
    expect(bodies).toEqual([{ symbol: 'AAPL', market: 1, reason: '監視を終える' }]);
    await expect(watchlistRowDelete(page, '7203')).toBeFocused();
  });

  // #1467 の AI レビュー 🟡: 削除の成功後に一覧の再取得が遅れても、フォーカスを <body> へ落とさない
  // （キーボードの利用者の位置を失わない）。再取得を 1.5 秒遅らせ、その間と後の両方を見る。
  test('監視銘柄の削除確認: 削除後の再取得が遅れても、フォーカスは <body> へ落ちず、残る行へ移る', async ({ page }) => {
    let watchlist: { symbol: string; market: number }[] = [...WATCHLIST];
    let deleted = false;
    await openAsOwner(page, '/settings/risk', 'リスク設定', {
      ...defaultBff(),
      'GET /monitor/watchlist': async () => {
        if (deleted) await new Promise((resolve) => setTimeout(resolve, 1_500));
        return { status: 200, body: watchlist };
      },
      'DELETE /monitor/watchlist': () => {
        deleted = true;
        watchlist = watchlist.filter((w) => w.symbol !== 'AAPL');
        return { status: 200, body: watchlist };
      },
    });

    await watchlistRowDelete(page, 'AAPL').focus();
    await page.keyboard.press('Enter');
    await page.keyboard.type('監視を終える');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Enter');

    // 送信中（DELETE の応答の後、再取得の完了まで 1.5 秒）は、押した「監視から削除」にフォーカスが残る。
    // 以前は押したボタンが送信中に無効化され、この間フォーカスが <body> にあった（実測）。
    const commit = deleteConfirm(page).getByRole('button', { name: '監視から削除' });
    await expect(commit).toBeFocused();
    await expect(commit).toHaveAttribute('aria-disabled', 'true');
    // 終わった後は、削除していない行の「削除」へ移る（削除した行を探さない）。
    await expect(watchlistRowDelete(page, '7203')).toBeFocused();
    await expect(page.getByRole('table', { name: '監視銘柄' }).getByText('AAPL')).toHaveCount(0);
    await expect(watchlistRowDelete(page, '7203')).toBeFocused();
  });

  // 同じ 🟡 の残る半分: 削除の成功後も、削除した行が DOM に残っていることがある（再取得が古い一覧を返す・
  // 一覧の反映が遅れる）。そこへフォーカスを当てると、行が消えた時点で <body> へ落ちる。削除した行は探さない。
  test('監視銘柄の削除確認: 削除した行が一覧に残っていても、そこではなく残る行へフォーカスを移す', async ({ page }) => {
    await openAsOwner(page, '/settings/risk', 'リスク設定');
    // defaultBff() の再取得は元の 2 行（AAPL を含む）を返し続ける。

    await watchlistRowDelete(page, 'AAPL').focus();
    await page.keyboard.press('Enter');
    await page.keyboard.type('監視を終える');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Enter');

    await expect(deleteConfirm(page)).toHaveCount(0);
    await expect(watchlistRowDelete(page, '7203')).toBeFocused();
    await expect(watchlistRowDelete(page, 'AAPL')).not.toBeFocused();
  });
});

