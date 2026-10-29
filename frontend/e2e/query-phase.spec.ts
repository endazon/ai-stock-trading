import { test, expect } from '@playwright/test';
import type { BffResponse } from './fixtures';
import { ASSUMPTIONS, RISK_SETTINGS, RISK_STATUS, defaultBff, installBff, pathWithRoles } from './fixtures';

// #1513（#1410 の網羅表 A-009）, SC-01, SC-02, SC-03, 横断規約（UI/UX 改善 2026-09-12 第 4 弾）:
// 取得の待ちは `role=status`、取得の失敗は `role=alert` と「再試行」で描く（QueryPhase）。
// 失敗を 0 件・空と混同せず、再試行で 200 が返れば本体を描く。偽 BFF のフィクスチャだけで見る（live には触れない）。
//
// 一過性の 5xx は QueryClient が 1 度だけ自動で吸収する（foundation の `shouldRetryQuery`）。
// よって 500 を 2 回返した後に失敗の描画になり、「再試行」の 3 回目で 200 を返す。

interface Screen {
  name: string;
  path: string;
  /** 取得を止める BFF のキー。 */
  key: string;
  /** 200 のときの本文。 */
  body: unknown;
  /** 失敗の見出し（QueryPhase の errorTitle）。 */
  errorTitle: RegExp;
  /** 本体が描かれたことの印。 */
  ready: (page: import('@playwright/test').Page) => import('@playwright/test').Locator;
}

const SCREENS: Screen[] = [
  {
    name: 'SC-01',
    path: '/settings',
    key: 'GET /assumptions',
    body: ASSUMPTIONS,
    errorTitle: /設定情報の取得に失敗しました/,
    ready: (page) => page.getByLabel('譲渡益税率'),
  },
  {
    name: 'SC-02',
    path: '/settings/risk',
    key: 'GET /risk-controls/settings',
    body: RISK_SETTINGS,
    errorTitle: /リスク設定の取得に失敗しました/,
    ready: (page) => page.getByRole('form', { name: 'リスク上限の変更' }),
  },
  {
    name: 'SC-03',
    path: '/controls',
    key: 'GET /risk-controls/status',
    body: RISK_STATUS,
    errorTitle: /統制状態の取得に失敗/,
    ready: (page) => page.getByRole('table', { name: '上限使用率' }),
  },
];

test.describe('取得の待ち・失敗・再試行の描き分け（A-009・#1513）', () => {
  for (const screen of SCREENS) {
    test(`A-009 ${screen.name}: 待ちは role=status、失敗は role=alert と再試行、再試行の 200 で本体を描く`, async ({ page }) => {
      let calls = 0;
      let release!: () => void;
      const held = new Promise<void>((resolve) => {
        release = resolve;
      });
      await installBff(page, {
        ...defaultBff(),
        [screen.key]: async (): Promise<BffResponse> => {
          calls++;
          if (calls === 1) {
            // 1 回目は待たせて「待ち」の描画を見る。その後 500。
            await held;
            return { status: 500 };
          }
          if (calls === 2) return { status: 500 };
          return { status: 200, body: screen.body };
        },
      });
      await page.goto(pathWithRoles(screen.path, ['trading-owner']));

      // 待ち: role=status（読み込み中）。本体はまだ無い。
      await expect(page.getByRole('status').filter({ hasText: '読み込み中' })).toHaveCount(1);
      await expect(screen.ready(page)).toHaveCount(0);
      release();

      // 失敗: role=alert に見出しと「再試行」。0 件・空として描かない。
      const alert = page.getByRole('alert').filter({ hasText: screen.errorTitle });
      await expect(alert).toBeVisible();
      await expect(screen.ready(page)).toHaveCount(0);
      expect(calls).toBe(2);

      // 再試行で 200 → 本体を描き、失敗の描画は消える。
      await page.getByRole('button', { name: '再試行' }).first().click();
      await expect(screen.ready(page)).toBeVisible();
      await expect(alert).toHaveCount(0);
      expect(calls).toBe(3);
    });
  }
});
