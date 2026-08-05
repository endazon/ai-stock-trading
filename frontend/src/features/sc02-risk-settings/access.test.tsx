import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { User } from 'oidc-client-ts';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState } from '@foundation/auth/AuthContext';

// SC-02, FR-13, UC-06, IADR-0009/0035/0084: リスク設定画面のアクセス制御（利用者 trading-owner 限定＋存在秘匿）。
// feature ルート要素（RequireRole でラップ済み）をロール別に描画し、権限外は NotFound で画面の存在を示さないことを検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { sc02RiskSettingsFeature } from './index';
import { CONTRACT_RISK_SETTINGS, cloneContract } from '../risk/contractFixtures';
import type { RiskManagementSettings } from '../risk/contracts';

// #389, IADR-0146: モックはバックエンドの実応答（契約フィクスチャ）から作る。
const SAMPLE_SETTINGS: RiskManagementSettings = {
  ...cloneContract(CONTRACT_RISK_SETTINGS),
  stage: { ...cloneContract(CONTRACT_RISK_SETTINGS.stage), stage: 1, mode: 0 },
};

function makeJwt(payload: unknown): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `h.${b64url(payload)}.sig`;
}

function renderRoute(roles: string[]) {
  const user = { access_token: makeJwt({ realm_access: { roles } }) } as unknown as User;
  const value: AuthState = {
    user,
    isAuthenticated: true,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
  };
  const element = sc02RiskSettingsFeature.routes[0].element;
  return render(
    <AuthContext.Provider value={value}>
      <MemoryRouter>{element}</MemoryRouter>
    </AuthContext.Provider>,
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/settings/history') return [];
    return SAMPLE_SETTINGS;
  });
});

describe('SC-02 access control (#106)', () => {
  it('grants access to trading-owner', async () => {
    renderRoute(['trading-owner']);
    expect(await screen.findByRole('heading', { name: 'リスク設定' })).toBeInTheDocument();
  });

  it('hides existence (NotFound) for a non-owner user', () => {
    renderRoute(['user']);
    expect(screen.queryByRole('heading', { name: 'リスク設定' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    // 権限外では設定 API を呼ばない（存在を推測させない）。
    expect(mocks.apiFetch).not.toHaveBeenCalled();
  });

  it('exposes a nav entry limited to trading-owner', () => {
    expect(sc02RiskSettingsFeature.nav?.requiresAnyRole).toEqual(['trading-owner']);
    expect(sc02RiskSettingsFeature.nav?.to).toBe('/settings/risk');
  });
});
