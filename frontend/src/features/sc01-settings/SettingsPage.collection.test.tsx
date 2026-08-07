import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-01 §2, FR-13, FR-03, UC-06, #340, IADR-0155: 収集パラメータ（変動閾値・収集間隔）の閲覧・変更。
//
// 計画（05_screens SC-01 §2）は変動閾値と収集間隔の閲覧・変更を求める。実装の現況は
//   変動閾値 … 供給がある（MarketMonitorService `/monitor/settings`）
//   収集間隔 … **供給が無い**（起動時構成のみ。読み書きする経路が存在しない）
// であり、**後者は入力欄を作らず「変更できない」ことを明示する**。動かない入力欄は
// 「変更できたつもり」を生み、統制設定では「設定したはずの値と実際の値が違う」ことが最も危険である。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { SettingsPage } from './SettingsPage';
import {
  CONTRACT_MONITOR_SETTINGS,
  CONTRACT_MONITOR_SETTINGS_HISTORY,
} from '../monitor/contractFixtures';
import { METRIC_NOT_SUPPLIED_TEXT } from '../risk/contracts';

const ASSUMPTIONS = {
  assumptions: {
    capitalGainsTaxRate: 0.20315,
    japanCommission: { rate: 0.00055, minimum: 0, cap: 1100 },
    unitedStatesCommission: { rate: 0.00495, minimum: 0, cap: 22 },
    fxSpreadRatio: 0.0025,
    minimumExpectedProfitMultiple: 1.5,
    costLimits: { total: 50000, llm: 30000, infrastructure: 15000, data: 5000 },
  },
  version: 3,
  isResolved: true,
};

// #389, IADR-0146: 収集パラメータのモックは**バックエンドの実応答**（契約フィクスチャ）を土台にする。
type MonitorOverrides = {
  settings?: unknown | 'fail';
  history?: unknown | 'fail';
  put?: 'ok' | ApiError;
};

function mockBff(overrides: MonitorOverrides = {}): void {
  mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
    if (path === '/assumptions/history') return [];
    if (path === '/monitor/settings' && req?.method === 'PUT') return CONTRACT_MONITOR_SETTINGS;
    if (path === '/monitor/settings/movement-threshold') {
      if (overrides.put instanceof ApiError) throw overrides.put;
      return CONTRACT_MONITOR_SETTINGS;
    }
    if (path === '/monitor/settings/history') {
      if (overrides.history === 'fail') throw new ApiError('server', 'boom', 500);
      return overrides.history ?? CONTRACT_MONITOR_SETTINGS_HISTORY;
    }
    if (path === '/monitor/settings') {
      if (overrides.settings === 'fail') throw new ApiError('server', 'boom', 500);
      return overrides.settings ?? CONTRACT_MONITOR_SETTINGS;
    }
    return ASSUMPTIONS;
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mockBff();
});

describe('SC-01 §2 収集パラメータ（#340）', () => {
  it('変動閾値を現在値（百分率）で表示し、入力欄に反映する', async () => {
    render(<SettingsPage />);

    // 実応答は比率 0.03。画面は百分率で見せる（IADR-0151 決定1: 画面＝百分率／ワイヤ＝比率）。
    expect(await screen.findByLabelText('変動閾値 %')).toHaveValue(3);
    expect(screen.getByText(/現在の変動閾値:/)).toHaveTextContent('3%');
  });

  it('収集間隔は入力欄を持たず、変更できないことを明示する', async () => {
    render(<SettingsPage />);
    await screen.findByLabelText('変動閾値 %');

    // **否定形**: 収集間隔の入力欄は存在しない（動かない入力欄を置かない）。
    expect(screen.queryByLabelText(/収集間隔/)).not.toBeInTheDocument();
    // SC-01 §2, #424, IADR-0162: 供給が無い値は**規約の文言**で明示する（05_screens 共通規約）。
    const note = screen.getByText(/現在の収集間隔:/);
    expect(note).toHaveTextContent(METRIC_NOT_SUPPLIED_TEXT);
    // **否定形**: 0 や「—」で「値がある」ように見せない。
    expect(note).not.toHaveTextContent('現在の収集間隔: 0');
    expect(note).not.toHaveTextContent('現在の収集間隔: —');
    expect(screen.getByText(/本画面から閲覧・変更できません。/)).toBeInTheDocument();
    expect(screen.getByText(/値を読み書きする経路がありません/)).toBeInTheDocument();
  });

  it('変更理由が空欄では保存できない（監査のため理由必須）', async () => {
    render(<SettingsPage />);
    await screen.findByLabelText('変動閾値 %');

    expect(screen.getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();
  });

  it('理由を入れると保存でき、比率（百分率ではない）と理由を PUT する', async () => {
    const user = userEvent.setup();
    render(<SettingsPage />);
    const input = await screen.findByLabelText('変動閾値 %');

    await user.clear(input);
    await user.type(input, '5');
    await user.type(screen.getByLabelText('収集パラメータの変更理由'), 'ボラティリティ上昇');
    await user.click(screen.getByRole('button', { name: '変動閾値を保存' }));

    const put = mocks.apiFetch.mock.calls.find(
      ([p, r]) => p === '/monitor/settings/movement-threshold' && r?.method === 'PUT',
    );
    expect(put).toBeDefined();
    // **ワイヤは比率**である（5% → 0.05）。百分率のまま送ると閾値が 100 倍になる。
    expect(put![1].json).toEqual({ movementThresholdRatio: 0.05, reason: 'ボラティリティ上昇' });
  });

  it('値域外（0 以下・上限超）は保存できず、理由を表示する', async () => {
    const user = userEvent.setup();
    render(<SettingsPage />);
    const input = await screen.findByLabelText('変動閾値 %');
    await user.type(screen.getByLabelText('収集パラメータの変更理由'), '検証');

    await user.clear(input);
    await user.type(input, '0');
    expect(screen.getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();
    expect(screen.getByText(/0 超 50 以下（%） の範囲で入力してください/)).toBeInTheDocument();

    await user.clear(input);
    await user.type(input, '51');
    expect(screen.getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();

    // 範囲内へ戻せば保存できる（値域が広すぎず狭すぎないことの確認）。
    await user.clear(input);
    await user.type(input, '50');
    expect(screen.getByRole('button', { name: '変動閾値を保存' })).toBeEnabled();
  });

  it('空欄は保存できない（黙って 0 を送らない）', async () => {
    const user = userEvent.setup();
    render(<SettingsPage />);
    const input = await screen.findByLabelText('変動閾値 %');
    await user.type(screen.getByLabelText('収集パラメータの変更理由'), '検証');

    await user.clear(input);

    expect(screen.getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();
    // **0 は「すべての変動で発火する」危険側の設定**である。空欄を 0 として扱わない。
    expect(screen.getByText('変動閾値を入力してください。')).toBeInTheDocument();
  });

  it('競合（409）は自動再試行せず再読込を促す', async () => {
    const user = userEvent.setup();
    mockBff({ put: new ApiError('conflict', '競合', 409) });
    render(<SettingsPage />);
    const input = await screen.findByLabelText('変動閾値 %');

    await user.clear(input);
    await user.type(input, '5');
    await user.type(screen.getByLabelText('収集パラメータの変更理由'), '競合検証');
    await user.click(screen.getByRole('button', { name: '変動閾値を保存' }));

    expect(await screen.findByText(/競合が発生しました。最新を取得して再試行してください。/)).toBeInTheDocument();
    const putCalls = mocks.apiFetch.mock.calls.filter(
      ([p, r]) => p === '/monitor/settings/movement-threshold' && r?.method === 'PUT',
    );
    expect(putCalls).toHaveLength(1);
  });

  it('収集パラメータの変更履歴を種別・前後値つきで表示する', async () => {
    render(<SettingsPage />);

    const table = await screen.findByRole('table', { name: '収集パラメータの変更履歴' });
    const rows = within(table).getAllByRole('row');
    // 実応答の履歴は「変動閾値の変更（changeType=2）」と「監視銘柄の追加（0）」の 2 件。
    // 本節は収集パラメータだけを出すため 1 件になる（監視銘柄は SC-02 の関心）。
    expect(rows).toHaveLength(2);
    expect(within(rows[1]).getByText('変動閾値')).toBeInTheDocument();
    expect(within(rows[1]).getByText('0.03')).toBeInTheDocument();
    expect(within(rows[1]).getByText('0.04')).toBeInTheDocument();
  });

  it('収集パラメータの取得失敗は当該領域のみ縮退し、§1 の前提条件は表示され続ける', async () => {
    mockBff({ settings: 'fail', history: 'fail' });
    render(<SettingsPage />);

    // #424: 取得失敗も**供給が無い**状態の 1 つであり、規約の文言で明示する。
    const notice = await screen.findByText(/値が無いのではなく、確認できていません/);
    expect(notice).toHaveTextContent(METRIC_NOT_SUPPLIED_TEXT);
    // **否定形**: 「収集パラメータはありません」のように「値が無い」とは書かない。
    expect(notice).not.toHaveTextContent('—');
    // §1（ConfigurationService 由来）は独立して表示される。
    expect(screen.getByRole('form', { name: '全体前提条件の変更' })).toBeInTheDocument();
  });
});
