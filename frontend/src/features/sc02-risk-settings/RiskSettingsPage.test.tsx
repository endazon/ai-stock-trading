import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-02, FR-13, FR-19, FR-20, UC-06, IADR-0084: リスク設定画面（リスク上限の閲覧/変更）の振る舞い。
// データ取得・更新は apiFetch をモックし、実 BFF 疎通に依存しない。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { RiskSettingsPage } from './RiskSettingsPage';

const SETTINGS = {
  guard: {
    enabledProductTypes: [0, 1],
    enabledMarkets: [0, 1],
    bannedSymbols: [
      { symbol: '9999', market: 0, reason: '監視対象外', registeredOn: '2026-07-10' },
    ],
    preventSameDayReentry: true,
    prohibitManipulativeOrderPatterns: true,
  },
  limits: {
    maxOrderAmount: 100000,
    maxDailyOrderAmount: 300000,
    maxOpenPositions: 5,
    dailyLossLimitRatio: 0.02,
    perTradeRiskRatio: 0.01,
    maxDrawdownRatio: 0.1,
    losingStreakThreshold: 3,
    losingStreakSizeFactor: 0.5,
  },
  stage: { stage: 2, mode: 1, capitalCap: 1000000 },
};
const HISTORY = [
  { actor: 'owner', changeType: 1, reason: '上限見直し', changedAt: '2026-07-17T00:00:00Z' },
  { actor: 'owner', changeType: 0, reason: 'ガード初期化', changedAt: '2026-07-01T00:00:00Z' },
];

function mockDefault() {
  mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
    if (path === '/risk-controls/settings/history') return HISTORY;
    if (path === '/risk-controls/settings/limits' && req?.method === 'PUT') return SETTINGS;
    // 監視銘柄セクション（#196・別サービス）は独立ロードするため、本ページのテストでは空応答で満たす。
    if (path === '/monitor/watchlist') return [];
    if (path === '/monitor/watchlist/history') return [];
    return SETTINGS;
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mockDefault();
});

// リスク上限フォーム（#186）。ガード変更フォーム（#188）と同名の要素があるため絞り込みに使う。
function limitsForm(): HTMLElement {
  return screen.getByRole('form', { name: 'リスク上限の変更' });
}

describe('RiskSettingsPage (SC-02, FR-13)', () => {
  it('renders current limits into the form', async () => {
    render(<RiskSettingsPage />);
    expect(await screen.findByRole('heading', { name: 'リスク設定' })).toBeInTheDocument();
    // 読み込み完了（フォーム描画）を待ってから現在値を検証する（見出しは読み込み中も描画されるため待受にしない）。
    expect(await screen.findByLabelText('1注文金額上限')).toHaveValue(100000);
    expect(screen.getByLabelText('保有銘柄数上限')).toHaveValue(5);
  });

  it('shows stage as read-only (stage change is via gate approval, not this screen)', async () => {
    render(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    // 段階（数値 enum）がラベルへ写像される（参照表示。直接変更 UI は無い＝#20/#165 段階ゲート承認へ一元化）。
    // #333/#334: 段階の呼称は計画（06_daytrading-review §4 表）に従う。
    expect(screen.getByText('Stage 2（最小実弾）')).toBeInTheDocument();
    // 段階の編集 UI は存在しない（段階変更フォームは開かない）。
    expect(screen.queryByRole('form', { name: '運用段階の変更' })).not.toBeInTheDocument();
    // ガードは編集可能（詳細は RiskSettingsPage.guard.test.tsx）。禁止銘柄は編集表として現在値を表示する。
    const banned = screen.getByRole('table', { name: '禁止銘柄（編集）' });
    expect(within(banned).getByText('9999')).toBeInTheDocument();
  });

  it('lists change history (newest first) mapping changeType to a label', async () => {
    render(<RiskSettingsPage />);
    const table = await screen.findByRole('table', { name: '変更履歴' });
    const rows = within(table).getAllByRole('row');
    expect(within(rows[1]).getByText('上限')).toBeInTheDocument();
    expect(within(rows[1]).getByText('上限見直し')).toBeInTheDocument();
    expect(within(rows[2]).getByText('ガード')).toBeInTheDocument();
  });

  it('degrades the history area when history fetch fails', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/risk-controls/settings/history') throw new ApiError('server', 'boom', 500);
      return SETTINGS;
    });
    render(<RiskSettingsPage />);
    expect(await screen.findByText('変更履歴は利用できません。')).toBeInTheDocument();
  });

  it('requires a reason before saving (save disabled until reason entered)', async () => {
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    // ガード変更フォーム（#188）も同名の「保存」「変更理由」を持つため、上限フォームに絞る。
    const form = limitsForm();
    const save = within(form).getByRole('button', { name: '保存' });
    expect(save).toBeDisabled();
    await user.type(within(form).getByLabelText('変更理由'), '上限調整');
    expect(save).toBeEnabled();
  });

  it('disables save and warns when a numeric field is empty or non-numeric', async () => {
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    await user.type(within(form).getByLabelText('変更理由'), '上限調整');
    const save = within(form).getByRole('button', { name: '保存' });
    expect(save).toBeEnabled();
    // 上限を空欄にすると、黙って 0 送信せず保存を無効化し警告する（安全既定）。
    await user.clear(within(form).getByLabelText('1注文金額上限'));
    expect(save).toBeDisabled();
    expect(within(form).getByRole('alert')).toHaveTextContent(/未入力|数値/);
  });

  it('submits PUT to /settings/limits with limits and reason', async () => {
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    await user.type(within(form).getByLabelText('変更理由'), '上限調整');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    expect(mocks.apiFetch).toHaveBeenCalledWith(
      '/risk-controls/settings/limits',
      expect.objectContaining({
        method: 'PUT',
        json: { limits: SETTINGS.limits, reason: '上限調整' },
      }),
    );
  });

  it('reflects an edited limit in the submitted payload', async () => {
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    const input = within(form).getByLabelText('保有銘柄数上限');
    await user.clear(input);
    await user.type(input, '8');
    await user.type(within(form).getByLabelText('変更理由'), '保有枠拡大');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    const putCall = mocks.apiFetch.mock.calls.find(
      ([p, r]) => p === '/risk-controls/settings/limits' && r?.method === 'PUT',
    )!;
    expect(putCall[1].json.limits.maxOpenPositions).toBe(8);
  });

  it('shows a conflict message on 409 without destructive retry', async () => {
    const user = userEvent.setup();
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/risk-controls/settings/history') return HISTORY;
      if (path === '/risk-controls/settings/limits' && req?.method === 'PUT')
        throw new ApiError('conflict', '競合', 409);
      return SETTINGS;
    });
    render(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    await user.type(within(form).getByLabelText('変更理由'), '上限調整');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    expect(await within(form).findByRole('alert')).toHaveTextContent(/競合/);
    const putCalls = mocks.apiFetch.mock.calls.filter(
      ([p, r]) => p === '/risk-controls/settings/limits' && r?.method === 'PUT',
    );
    expect(putCalls).toHaveLength(1);
  });

  it('degrades to a not-available message when settings fetch 404s (BFF unwired)', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/risk-controls/settings') throw new ApiError('notFound', '未登録', 404);
      return [];
    });
    render(<RiskSettingsPage />);
    expect(await screen.findByText('リスク設定は利用できません。')).toBeInTheDocument();
  });
});
