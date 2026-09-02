import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-02, FR-13, UC-06, IADR-0009/0035/0084/0282: リスク設定画面のアクセス制御（利用者 trading-owner 限定＋存在秘匿）。
// ルート factory を実際に描画する（理由は SC-01 の同名テスト冒頭）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { createSc02RiskSettingsRoute, sc02RiskSettingsNav } from './index';

// 本 feature のルートだけを載せる（理由は SC-01 の同名テスト）。
const createRoutes = (shell: Parameters<typeof createSc02RiskSettingsRoute>[0]) =>
  [createSc02RiskSettingsRoute(shell)] as const;
import { CONTRACT_RISK_SETTINGS, cloneContract } from '@ai-stock-trading/testing/riskContractFixtures';
import type { RiskManagementSettings } from '@ai-stock-trading/lib/risk/contracts';

// #389, IADR-0146: モックはバックエンドの実応答（契約フィクスチャ）から作る。
const SAMPLE_SETTINGS: RiskManagementSettings = {
  ...cloneContract(CONTRACT_RISK_SETTINGS),
  stage: { ...cloneContract(CONTRACT_RISK_SETTINGS.stage), stage: 1, mode: 0 },
};

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/settings/history') return [];
    return SAMPLE_SETTINGS;
  });
});

describe('SC-02 access control (#106, #414)', () => {
  it('grants access to trading-owner', async () => {
    await renderUnitRoute(createRoutes, {
      initialEntry: sc02RiskSettingsNav.to,
      roles: ['trading-owner'],
    });
    expect(await screen.findByRole('heading', { name: 'リスク設定' })).toBeInTheDocument();
  });

  it('hides existence (NotFound) for a non-owner user', async () => {
    await renderUnitRoute(createRoutes, {
      initialEntry: sc02RiskSettingsNav.to,
      roles: ['user'],
    });
    expect(screen.queryByRole('heading', { name: 'リスク設定' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    // 権限外では設定 API を呼ばない（存在を推測させない）。
    expect(mocks.apiFetch).not.toHaveBeenCalled();
  });

  it('exposes a nav entry limited to trading-owner', () => {
    expect(sc02RiskSettingsNav.requiresAnyRole).toEqual(['trading-owner']);
    expect(sc02RiskSettingsNav.to).toBe('/settings/risk');
  });
});
