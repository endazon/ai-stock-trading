import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { User } from 'oidc-client-ts';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState } from '@foundation/auth/AuthContext';

// SC-01, FR-17, UC-06, IADR-0009/0035/0080: 設定画面のアクセス制御（利用者 trading-owner 限定＋存在秘匿）。
// feature ルート要素（RequireRole でラップ済み）をロール別に描画し、権限外は NotFound で画面の存在を示さないことを検証する。
// 許可時のデータ取得はモックする（実 BFF 疎通に依存しない）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { sc01SettingsFeature } from './index';

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

function makeJwt(payload: unknown): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `h.${b64url(payload)}.sig`;
}

function renderSettingsRoute(roles: string[]) {
  const user = { access_token: makeJwt({ realm_access: { roles } }) } as unknown as User;
  const value: AuthState = {
    user,
    isAuthenticated: true,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
  };
  const element = sc01SettingsFeature.routes[0].element;
  return render(
    <AuthContext.Provider value={value}>
      <MemoryRouter>{element}</MemoryRouter>
    </AuthContext.Provider>,
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/assumptions/history') return [];
    return EMPTY_ASSUMPTIONS;
  });
});

describe('SC-01 access control (#106)', () => {
  it('grants access to trading-owner', async () => {
    renderSettingsRoute(['trading-owner']);
    expect(await screen.findByRole('heading', { name: '設定' })).toBeInTheDocument();
  });

  it('hides existence (NotFound) for a non-owner user', () => {
    renderSettingsRoute(['user']);
    expect(screen.queryByRole('heading', { name: '設定' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    // 権限外では設定 API を呼ばない（存在を推測させない）。
    expect(mocks.apiFetch).not.toHaveBeenCalled();
  });

  it('exposes a nav entry limited to trading-owner', () => {
    expect(sc01SettingsFeature.nav?.requiresAnyRole).toEqual(['trading-owner']);
  });
});
