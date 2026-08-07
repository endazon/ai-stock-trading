import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-01, FR-17, UC-06, IADR-0080: 設定画面（全体前提条件の閲覧/変更）の振る舞い。
// データ取得・更新は apiFetch をモックし、実 BFF 疎通に依存しない。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { SettingsPage } from './SettingsPage';
import {
  CONTRACT_MONITOR_SETTINGS,
  CONTRACT_MONITOR_SETTINGS_HISTORY,
} from '../monitor/contractFixtures';
import { METRIC_NOT_SUPPLIED_TEXT } from '../risk/contracts';

const SAMPLE = {
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
const HISTORY = [
  { actor: 'owner', reason: '税率見直し', changedAt: '2026-07-17T00:00:00Z', version: 3 },
  { actor: 'owner', reason: '初期値', changedAt: '2026-07-01T00:00:00Z', version: 1 },
];

function mockDefault() {
  mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
    if (path === '/assumptions/history') return HISTORY;
    // SC-01 §2, #340, IADR-0146: 収集パラメータのモックは**バックエンドの実応答**（契約フィクスチャ）を使う。
    if (path === '/monitor/settings/history') return CONTRACT_MONITOR_SETTINGS_HISTORY;
    if (path === '/monitor/settings') return CONTRACT_MONITOR_SETTINGS;
    if (path === '/assumptions' && req?.method === 'PUT') return SAMPLE;
    return SAMPLE;
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mockDefault();
});

describe('SettingsPage (SC-01, FR-17)', () => {
  it('renders current assumptions and version', async () => {
    render(<SettingsPage />);
    expect(await screen.findByRole('heading', { name: '設定' })).toBeInTheDocument();
    // 読み込み完了（フォーム描画）を待ってから現在値を検証する（見出しは読み込み中も描画されるため待受にしない）。
    expect(await screen.findByText(/現在のバージョン:\s*3/)).toBeInTheDocument();
    // 譲渡益税率が現在値として入力に反映される。
    expect(screen.getByLabelText('譲渡益税率')).toHaveValue(0.20315);
  });

  it('lists change history (newest first) and degrades when history fails', async () => {
    render(<SettingsPage />);
    const table = await screen.findByRole('table', { name: '変更履歴' });
    const rows = within(table).getAllByRole('row');
    // 先頭はヘッダ行、次にデータ行（新しい順）。
    expect(within(rows[1]).getByText('税率見直し')).toBeInTheDocument();
    expect(within(rows[2]).getByText('初期値')).toBeInTheDocument();
  });

  it('degrades the history area when history fetch fails', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/assumptions/history') throw new ApiError('server', 'boom', 500);
      return SAMPLE;
    });
    render(<SettingsPage />);
    expect(await screen.findByText('変更履歴は利用できません。')).toBeInTheDocument();
  });

  it('requires a reason before saving (save disabled until reason entered)', async () => {
    const user = userEvent.setup();
    render(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });
    const save = screen.getByRole('button', { name: '保存' });
    expect(save).toBeDisabled();
    await user.type(screen.getByLabelText('変更理由'), '税率調整');
    expect(save).toBeEnabled();
  });

  it('disables save and warns when a numeric field is empty or non-numeric', async () => {
    const user = userEvent.setup();
    render(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });
    await user.type(screen.getByLabelText('変更理由'), '税率調整');
    const save = screen.getByRole('button', { name: '保存' });
    expect(save).toBeEnabled();
    // 財務パラメータを空欄にすると、黙って 0 送信せず保存を無効化し警告する（安全既定）。
    await user.clear(screen.getByLabelText('譲渡益税率'));
    expect(save).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent(/未入力|数値/);
  });

  it('submits PUT with assumptions, expectedVersion and reason', async () => {
    const user = userEvent.setup();
    render(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });
    await user.type(screen.getByLabelText('変更理由'), '税率調整');
    await user.click(screen.getByRole('button', { name: '保存' }));

    expect(mocks.apiFetch).toHaveBeenCalledWith(
      '/assumptions',
      expect.objectContaining({
        method: 'PUT',
        json: { assumptions: SAMPLE.assumptions, expectedVersion: 3, reason: '税率調整' },
      }),
    );
  });

  it('reflects an edited field in the submitted payload', async () => {
    const user = userEvent.setup();
    render(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });
    const taxInput = screen.getByLabelText('譲渡益税率');
    await user.clear(taxInput);
    await user.type(taxInput, '0.25');
    await user.type(screen.getByLabelText('変更理由'), '税率引き上げ');
    await user.click(screen.getByRole('button', { name: '保存' }));

    const [, req] = mocks.apiFetch.mock.calls.find(([p, r]) => p === '/assumptions' && r?.method === 'PUT')!;
    expect(req.json.assumptions.capitalGainsTaxRate).toBe(0.25);
    expect(req.json.expectedVersion).toBe(3);
  });

  it('shows a conflict message on 409 without destructive retry', async () => {
    const user = userEvent.setup();
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/assumptions/history') return HISTORY;
      if (path === '/monitor/settings/history') return CONTRACT_MONITOR_SETTINGS_HISTORY;
      if (path === '/monitor/settings') return CONTRACT_MONITOR_SETTINGS;
      if (path === '/assumptions' && req?.method === 'PUT') throw new ApiError('conflict', '競合', 409);
      return SAMPLE;
    });
    render(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });
    await user.type(screen.getByLabelText('変更理由'), '税率調整');
    await user.click(screen.getByRole('button', { name: '保存' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/競合/);
    // PUT は 1 回のみ（破壊的な自動再試行をしない）。
    const putCalls = mocks.apiFetch.mock.calls.filter(([p, r]) => p === '/assumptions' && r?.method === 'PUT');
    expect(putCalls).toHaveLength(1);
  });

  it('shows a validation message on 400', async () => {
    const user = userEvent.setup();
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/assumptions/history') return HISTORY;
      if (path === '/monitor/settings/history') return CONTRACT_MONITOR_SETTINGS_HISTORY;
      if (path === '/monitor/settings') return CONTRACT_MONITOR_SETTINGS;
      if (path === '/assumptions' && req?.method === 'PUT')
        throw new ApiError('validation', '入力エラー', 400, ['理由は必須です']);
      return SAMPLE;
    });
    render(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });
    await user.type(screen.getByLabelText('変更理由'), '不正値');
    await user.click(screen.getByRole('button', { name: '保存' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/入力/);
  });

  // ---- SC-01 §1, #424, IADR-0162 決定4: 供給が無い値の表示規約（全画面共通） ----

  it('declares the assumptions as not supplied when the server says they are unresolved', async () => {
    // **供給可否はサーバが宣言する。** `isResolved`（＝`Version > 0`）は ConfigurationService 由来の値を
    // 一度でも解決できたかをサーバが宣言したものであり、画面は値の中身から推測しない。
    // 未解決のとき表示しているのは**組み込みの既定値であって権威値ではない**。
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/assumptions/history') return HISTORY;
      if (path === '/monitor/settings/history') return CONTRACT_MONITOR_SETTINGS_HISTORY;
      if (path === '/monitor/settings') return CONTRACT_MONITOR_SETTINGS;
      return { ...SAMPLE, version: 0, isResolved: false };
    });
    render(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });

    const notice = screen.getByText(/全体前提条件を/);
    expect(notice).toHaveTextContent(METRIC_NOT_SUPPLIED_TEXT);
    // **否定形**: 「—」や「0」で「値が無い」ように弱めない。表示中の値が既定値であることを明示する。
    expect(notice).not.toHaveTextContent('全体前提条件を—');
    expect(notice).toHaveTextContent(/組み込みの既定値であり、実際に適用されている値ではありません/);
  });

  it('does not warn about supply when the server declares the assumptions resolved', async () => {
    // 逆方向の否定形。供給があるのに未供給の警告を出すと、警告が常時出て誰も読まなくなる。
    render(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });

    expect(screen.queryByText(/全体前提条件を/)).not.toBeInTheDocument();
  });
});
