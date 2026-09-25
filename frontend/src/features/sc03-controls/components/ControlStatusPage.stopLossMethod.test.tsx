import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProviders } from '@ai-stock-trading/testing/renderWithProviders';

// T-10-995, SC-03, FR-10, FR-12, UC-06, ADR-0040 決定1, #823, IADR-0422 決定4:
// 統制状態参照画面に**選択中の損切りの実行機構**を出す（参照のみ）。
//
// 計画（ADR-0040 決定1）: 「どの手法を選んでいるかは、監査ログ・SC-03・日報に出す。選択式にした以上、
// 『いまどれで走っているか』が読めなければ観測結果を解釈できない」。本画面は参照専用であり、変更は SC-02。
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
function mockApi(stopLossMethod: number) {
  const status: RiskStatusView = {
    ...cloneContract(CONTRACT_RISK_STATUS),
    brokerProvider: 2,
    stopLossMethod,
  };
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return cloneContract(CONTRACT_STAGE_GATE);
    if (path === '/risk-controls/settings/history') return [];
    if (path === '/risk-controls/short-selling') return cloneContract(CONTRACT_SHORT_SELLING);
    return status;
  });
}

const SIMULATE_ONLY_NOTE = /S0 以外の手法は moomoo SIMULATE の新規建てにだけ効きます/;

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-03 選択中の損切りの実行機構（#823）', () => {
  it.each([
    [0, 'S0 ブローカー側逆指値（既定）'],
    [1, 'S1 ソフトウェア逆指値'],
    [2, 'S2 逆指値なしの建玉を許容'],
    [3, 'S3 他のブローカー側注文種別'],
  ])('手法 %i を表示名「%s」で出す', async (method, label) => {
    mockApi(method);
    renderWithProviders(<ControlStatusPage />);

    expect(await screen.findByText('損切りの実行機構')).toBeInTheDocument();
    expect(screen.getByText(label)).toBeInTheDocument();
  });

  it('S0 以外なら moomoo SIMULATE 限定である旨を注記する', async () => {
    mockApi(2);
    renderWithProviders(<ControlStatusPage />);

    await screen.findByText('損切りの実行機構');
    expect(screen.getByText(SIMULATE_ONLY_NOTE)).toBeInTheDocument();
  });

  it('S0 なら SIMULATE 限定の注記を出さない', async () => {
    mockApi(0);
    renderWithProviders(<ControlStatusPage />);

    await screen.findByText('損切りの実行機構');
    expect(screen.queryByText(SIMULATE_ONLY_NOTE)).not.toBeInTheDocument();
  });

  // 🔴 未知の値は画面を壊さず「不明(N)」と出す（S0 と読ませない）。
  it('未知の値は「不明(N)」と出す', async () => {
    mockApi(9);
    renderWithProviders(<ControlStatusPage />);

    await screen.findByText('損切りの実行機構');
    expect(screen.getByText('不明(9)')).toBeInTheDocument();
  });

  // 本画面は参照専用（変更は SC-02）。手法を変える操作は無い。
  it('本画面から手法を変更する操作は無い', async () => {
    mockApi(2);
    renderWithProviders(<ControlStatusPage />);

    await screen.findByText('損切りの実行機構');
    expect(screen.queryAllByRole('radio')).toHaveLength(0);
    expect(screen.queryByRole('form', { name: /損切り/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /損切り/ })).not.toBeInTheDocument();
  });
});
