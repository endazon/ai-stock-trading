import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProviders } from '@ai-stock-trading/testing/renderWithProviders';

// 🔴 SC-01, FR-17, 05_trading-assumptions §6, IADR-0605, #1514（#1410 の網羅表 A-023・A-024）:
// 月次費用上限は「総額 ＝ LLM＋インフラ＋データ」で、各欄は 0 以上。画面は同じ規則で保存を無効にし、理由を alert に出す
// （正は後段の 400。画面だけだと API の直叩きで抜ける）。金額の単位「円」を明示する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { SettingsPage } from './SettingsPage';

const SAMPLE = {
  assumptions: {
    capitalGainsTaxRate: 0.20315,
    japanCommission: { rate: 0.00055, minimum: 0, cap: 1100 },
    unitedStatesCommission: { rate: 0.00495, minimum: 0, cap: 22 },
    fxSpreadRatio: 0.0025,
    minimumExpectedProfitMultiple: 1.5,
    // 計画 §6 の既定値（20,000 ＝ 15,000＋5,000＋0）。
    costLimits: { total: 20000, llm: 15000, infrastructure: 5000, data: 0 },
    unitedStatesSellRegulatoryFees: { secFeePerMillion: 20.6, tafPerShare: 0.000166, tafCapPerTrade: 8.3 },
  },
  version: 3,
  isResolved: true,
};

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) => (path === '/assumptions/history' ? [] : SAMPLE));
});

async function ready() {
  const user = userEvent.setup();
  renderWithProviders(<SettingsPage />);
  await screen.findByRole('form', { name: '全体前提条件の変更' });
  await user.type(screen.getByLabelText('変更理由'), '費用上限の見直し');
  return { user, save: screen.getByRole('button', { name: '保存' }) };
}

describe('SettingsPage 月次費用上限の整合と通貨（#1514）', () => {
  it('既定値（20,000 ＝ 15,000＋5,000＋0）は保存できる', async () => {
    const { save } = await ready();
    expect(save).toBeEnabled();
    expect(screen.queryByText(/一致しません|0 以上/)).not.toBeInTheDocument();
  });

  it('内訳の合計が総額と一致しなければ保存を無効にし、合計と総額を alert に出す', async () => {
    const { user, save } = await ready();
    await user.clear(screen.getByLabelText('月次費用上限 総額'));
    await user.type(screen.getByLabelText('月次費用上限 総額'), '25000');

    expect(save).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent(
      '月次費用上限の内訳の合計（LLM＋インフラ＋データ ＝ 20000 円）が総額（25000 円）と一致しません。',
    );

    // 内訳を直して一致させれば保存できる（データ 5,000 を足す）。
    await user.clear(screen.getByLabelText('月次費用上限 データ'));
    await user.type(screen.getByLabelText('月次費用上限 データ'), '5000');
    expect(save).toBeEnabled();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('負の欄があれば保存を無効にし、負の欄の名前を alert に出す（0 は許す）', async () => {
    const { user, save } = await ready();
    await user.clear(screen.getByLabelText('月次費用上限 インフラ'));
    await user.type(screen.getByLabelText('月次費用上限 インフラ'), '-5000');
    await user.clear(screen.getByLabelText('月次費用上限 LLM'));
    await user.type(screen.getByLabelText('月次費用上限 LLM'), '25000');

    expect(save).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent('月次費用上限は 0 以上で指定してください（負の値: インフラ）。');
  });

  it('矛盾したまま送信しても PUT を送らない', async () => {
    const { user } = await ready();
    await user.clear(screen.getByLabelText('月次費用上限 LLM'));
    await user.type(screen.getByLabelText('月次費用上限 LLM'), '1{Enter}');

    expect(mocks.apiFetch).not.toHaveBeenCalledWith('/assumptions', expect.objectContaining({ method: 'PUT' }));
  });

  it('月次費用上限の 4 欄に単位「円」を明示する（アクセシブル名は変えず、説明として結び付ける）', async () => {
    await ready();
    for (const label of ['月次費用上限 総額', '月次費用上限 LLM', '月次費用上限 インフラ', '月次費用上限 データ']) {
      expect(screen.getByLabelText(label)).toHaveAccessibleDescription('円');
    }
    // 税率など金額でない欄には付けない。
    expect(screen.getByLabelText('譲渡益税率')).not.toHaveAccessibleDescription('円');
  });
});
