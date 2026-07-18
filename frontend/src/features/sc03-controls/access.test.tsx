import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { User } from 'oidc-client-ts';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState } from '@foundation/auth/AuthContext';

// SC-03, FR-10, FR-20, UC-06, IADR-0009/0035/0084: 統制状態参照画面のアクセス制御（利用者 trading-owner 限定＋存在秘匿）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { sc03ControlsFeature } from './index';

const STATUS = {
  killSwitchEngaged: false,
  dailyLossLockoutActive: false,
  lockoutReleaseOn: null,
  tradingPaused: false,
  activeControl: 0,
  newEntriesBlocked: false,
  stage: 1,
  dailyRealizedPnl: 0,
  unrealizedPnl: 0,
  dailyPnl: 0,
  capital: 1000000,
  dailyOrderedAmount: 0,
  maxDailyOrderAmount: 300000,
  drawdownRatio: 0,
  maxDrawdownRatio: 0.1,
  openPositionCount: 0,
  maxOpenPositions: 5,
};
const STAGE_GATE = {
  currentStage: 1,
  currentSettings: { stage: 1, mode: 0, capitalCap: 1000000 },
  history: [],
  promotion: { targetStage: 2, eligible: false, unmetCriteria: [0] },
  withdrawal: { triggered: false, reason: null, haltNewEntries: false, proposedStage: null },
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
  const element = sc03ControlsFeature.routes[0].element;
  return render(
    <AuthContext.Provider value={value}>
      <MemoryRouter>{element}</MemoryRouter>
    </AuthContext.Provider>,
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return STAGE_GATE;
    return STATUS;
  });
});

describe('SC-03 access control (#106)', () => {
  it('grants access to trading-owner', async () => {
    renderRoute(['trading-owner']);
    expect(await screen.findByRole('heading', { name: '統制状態' })).toBeInTheDocument();
  });

  it('hides existence (NotFound) for a non-owner user', () => {
    renderRoute(['user']);
    expect(screen.queryByRole('heading', { name: '統制状態' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    // 権限外では統制 API を呼ばない（存在を推測させない）。
    expect(mocks.apiFetch).not.toHaveBeenCalled();
  });

  it('exposes a nav entry limited to trading-owner', () => {
    expect(sc03ControlsFeature.nav?.requiresAnyRole).toEqual(['trading-owner']);
    expect(sc03ControlsFeature.nav?.to).toBe('/controls');
  });
});
