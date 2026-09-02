import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-01, FR-17, UC-06, IADR-0009/0035/0080/0282: 設定画面のアクセス制御（利用者 trading-owner 限定＋存在秘匿）。
//
// **ルート factory を実際に描画する。** `renderUnitRoute` は実アプリと同じ id（`_shell`）を持つ
// 共通シェルの下にユニットのルートを載せるため、「ルート木に載っていない画面」「パスの取り違え」も
// ここで落ちる（従前は feature オブジェクトから element を取り出して描いており、ルートに載って
// いるかどうかは何も検証していなかった）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { createSc01SettingsRoute, sc01SettingsNav } from './index';

// **本 feature のルートだけ**を載せる。ユニットの合成面（`../index`）を引くと、feature の
// テストが他の feature の存在に依存する（ADR-0066 決定 1 の向きにも反する。ESLint が error にする）。
// 合成面そのものの不変条件は `src/features/index.test.tsx` が持つ。
const createRoutes = (shell: Parameters<typeof createSc01SettingsRoute>[0]) =>
  [createSc01SettingsRoute(shell)] as const;

const EMPTY_ASSUMPTIONS = {
  assumptions: {
    capitalGainsTaxRate: 0.20315,
    japanCommission: { rate: 0, minimum: 0, cap: 0 },
    unitedStatesCommission: { rate: 0, minimum: 0, cap: 0 },
    fxSpreadRatio: 0,
    minimumExpectedProfitMultiple: 1.5,
    costLimits: { total: 0, llm: 0, infrastructure: 0, data: 0 },
  },
  version: 1,
  isResolved: true,
};

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/assumptions/history') return [];
    return EMPTY_ASSUMPTIONS;
  });
});

describe('SC-01 access control (#106, #414)', () => {
  it('grants access to trading-owner', async () => {
    await renderUnitRoute(createRoutes, {
      initialEntry: sc01SettingsNav.to,
      roles: ['trading-owner'],
    });
    expect(await screen.findByRole('heading', { name: '設定' })).toBeInTheDocument();
  });

  it('hides existence (NotFound) for a non-owner user', async () => {
    await renderUnitRoute(createRoutes, {
      initialEntry: sc01SettingsNav.to,
      roles: ['user'],
    });
    expect(screen.queryByRole('heading', { name: '設定' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    // 権限外では設定 API を呼ばない（存在を推測させない）。
    expect(mocks.apiFetch).not.toHaveBeenCalled();
  });

  it('exposes a nav entry limited to trading-owner', () => {
    expect(sc01SettingsNav.requiresAnyRole).toEqual(['trading-owner']);
    expect(sc01SettingsNav.to).toBe('/settings');
  });
});
