import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProviders } from '@ai-stock-trading/testing/renderWithProviders';

// SC-03, FR-10, #1423 M-3, ADR-0073 決定3, IADR-0581:
// 基準資金（equity）の口座照会が直前の米国東部暦日に成功していないため新規建てを止めていることを、統制状態参照画面に出す。
// 計画（05_screens SC-03）:「基準資金（equity）の供給元と、その照会が成立しているか（止まっている理由を本画面から読めるようにする）」。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { ControlStatusPage } from './ControlStatusPage';
import type { RiskStatusView } from '@ai-stock-trading/lib/risk/contracts';
import {
  CONTRACT_RISK_STATUS,
  CONTRACT_SHORT_SELLING,
  CONTRACT_STAGE_GATE,
  cloneContract,
} from '@ai-stock-trading/testing/riskContractFixtures';

function status(missing: boolean): RiskStatusView {
  return {
    ...cloneContract(CONTRACT_RISK_STATUS),
    stage: 1,
    capital: missing ? null : 3000,
    capitalBaselineObservationMissing: missing,
    capitalBaselineLatestTradingDay: missing ? '2026-08-28' : null,
  };
}

function mockApi(missing: boolean) {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return cloneContract(CONTRACT_STAGE_GATE);
    if (path === '/risk-controls/settings/history') return [];
    if (path === '/risk-controls/short-selling') return cloneContract(CONTRACT_SHORT_SELLING);
    return status(missing);
  });
}

const STOPPED = /新規建てを停止中: 直前の米国東部暦日に口座照会（基準資金）が成功していません/;

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-03 基準資金の観測の欠けによる新規建ての停止（#1423 M-3）', () => {
  // T-10-3047
  it('欠けているときは停止の理由・最後の観測日・止めていないもの・自動解除を文で出す', async () => {
    mockApi(true);
    renderWithProviders(<ControlStatusPage />);

    const note = await screen.findByText(STOPPED);
    expect(note.textContent).toContain('2026-08-28');
    expect(note.textContent).toContain('手仕舞い・損切りは止めていません');
    expect(note.textContent).toContain('自動で解除されます');
  });

  // T-10-3047（否定形）
  it('照会できている平常時は出さない', async () => {
    mockApi(false);
    renderWithProviders(<ControlStatusPage />);

    await screen.findByText(/資金:/);
    expect(screen.queryByText(STOPPED)).toBeNull();
  });
});
