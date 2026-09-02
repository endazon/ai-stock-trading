import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-03, FR-10, FR-20, UC-06, IADR-0009/0035/0084/0282: 統制状態参照画面のアクセス制御
// （利用者 trading-owner 限定＋存在秘匿）。ルート factory を実際に描画する（理由は SC-01 の同名テスト冒頭）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { createSc03ControlsRoute, sc03ControlsNav } from './index';

// 本 feature のルートだけを載せる（理由は SC-01 の同名テスト）。
const createRoutes = (shell: Parameters<typeof createSc03ControlsRoute>[0]) =>
  [createSc03ControlsRoute(shell)] as const;
import {
  CONTRACT_RISK_STATUS,
  CONTRACT_SHORT_SELLING,
  CONTRACT_STAGE_GATE,
  cloneContract,
} from '@ai-stock-trading/testing/riskContractFixtures';
import type { RiskStatusView, StageGateStatus } from '@ai-stock-trading/lib/risk/contracts';

// #389, IADR-0146: モックはバックエンドの実応答（契約フィクスチャ）から作る。
const STATUS: RiskStatusView = { ...cloneContract(CONTRACT_RISK_STATUS), stage: 1 };
const STAGE_GATE: StageGateStatus = {
  ...cloneContract(CONTRACT_STAGE_GATE),
  currentStage: 1,
  currentSettings: { ...cloneContract(CONTRACT_STAGE_GATE.currentSettings), stage: 1, mode: 0 },
  history: [],
  promotion: { targetStage: 2, eligible: false, unmetCriteria: [0] },
};

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return STAGE_GATE;
    if (path === '/risk-controls/short-selling') return cloneContract(CONTRACT_SHORT_SELLING);
    return STATUS;
  });
});

describe('SC-03 access control (#106, #414)', () => {
  it('grants access to trading-owner', async () => {
    await renderUnitRoute(createRoutes, {
      initialEntry: sc03ControlsNav.to,
      roles: ['trading-owner'],
    });
    expect(await screen.findByRole('heading', { name: '統制状態' })).toBeInTheDocument();
  });

  it('hides existence (NotFound) for a non-owner user', async () => {
    await renderUnitRoute(createRoutes, {
      initialEntry: sc03ControlsNav.to,
      roles: ['user'],
    });
    expect(screen.queryByRole('heading', { name: '統制状態' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    // 権限外では統制 API を呼ばない（存在を推測させない）。
    expect(mocks.apiFetch).not.toHaveBeenCalled();
  });

  it('exposes a nav entry limited to trading-owner', () => {
    expect(sc03ControlsNav.requiresAnyRole).toEqual(['trading-owner']);
    expect(sc03ControlsNav.to).toBe('/controls');
  });
});
