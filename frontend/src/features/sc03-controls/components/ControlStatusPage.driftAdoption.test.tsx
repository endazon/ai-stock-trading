import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProviders } from '@ai-stock-trading/testing/renderWithProviders';

// SC-03, FR-11, FR-06, UC-06, ADR-0041 決定1, #870, IADR-0360 決定5:
// 統制状態参照画面の**当日のシステム外売買の取り込み件数**。
//
// 計画（05_screens SC-03）:「当期のシステム外売買の取り込み件数。**参照のみであり、本画面から取り込みは行わない**」。
// 🔴 件数を出す目的は「基準資金・当日損益が実際の口座とずれていることを画面から知る手段」を与えることである。
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

// #389, IADR-0146: モックはバックエンドの実応答（契約フィクスチャ）を土台に作る。
function status(driftAdoptionCountToday: number): RiskStatusView {
  return { ...cloneContract(CONTRACT_RISK_STATUS), stage: 1, driftAdoptionCountToday };
}

function mockApi(driftAdoptionCountToday: number) {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return cloneContract(CONTRACT_STAGE_GATE);
    if (path === '/risk-controls/settings/history') return [];
    if (path === '/risk-controls/short-selling') return cloneContract(CONTRACT_SHORT_SELLING);
    return status(driftAdoptionCountToday);
  });
}

const WARNING = /実現損益は不明です/;

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-03 当日のシステム外売買の取り込み件数（#870）', () => {
  it('当日の取り込み件数を当日損益の隣に表示する', async () => {
    mockApi(2);
    renderWithProviders(<ControlStatusPage />);

    const label = await screen.findByText('システム外売買の取り込み');
    expect(label).toBeInTheDocument();
    // 値のセル（テキストノードは件数と単位の 2 つ・子要素なし）。
    expect(
      screen.getByText((_, el) => el?.textContent === '2件' && el.children.length === 0),
    ).toBeInTheDocument();
  });

  it('取り込みがある日は、当日損益が実際の口座とずれ得ることを文言で出す', async () => {
    mockApi(1);
    renderWithProviders(<ControlStatusPage />);

    await screen.findByText('システム外売買の取り込み');
    expect(screen.getByText(WARNING)).toBeInTheDocument();
  });

  // 🔴 0 件は「取り込みが無かった」という**事実**であり、行ごと消さない（消すと未供給と区別できない）。
  it('取り込みが 0 件でも行は残り、警告の文言は出さない', async () => {
    mockApi(0);
    renderWithProviders(<ControlStatusPage />);

    expect(await screen.findByText('システム外売買の取り込み')).toBeInTheDocument();
    expect(screen.queryByText(WARNING)).not.toBeInTheDocument();
  });

  // 05_screens:「参照のみであり、本画面から取り込みは行わない」。
  it('本画面から取り込みを行う操作は無い', async () => {
    mockApi(3);
    renderWithProviders(<ControlStatusPage />);

    await screen.findByText('システム外売買の取り込み');
    expect(screen.queryByRole('button', { name: /取り込/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('form', { name: /取り込/ })).not.toBeInTheDocument();
  });
});
