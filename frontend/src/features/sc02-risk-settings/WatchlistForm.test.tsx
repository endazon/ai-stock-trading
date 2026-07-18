import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import type { RenderResult } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-02, FR-13, FR-03, FR-11, UC-06, IADR-0088, IADR-0090: 監視銘柄（watchlist）変更 UI の振る舞い。
// リスク上限/ガード（#186/#188）と同じ apiFetch モック方針で、実 BFF 疎通に依存しない。別サービス（MarketMonitor）を
// 独立ロードするコンポーネントのため、ページ全体ではなく WatchlistForm 単体を描画して検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { WatchlistForm } from './WatchlistForm';

const WATCHLIST = [
  { symbol: '7203', market: 0 }, // 日本
  { symbol: 'AAPL', market: 1 }, // 米国
];
const HISTORY = [
  { actor: 'owner', changeType: 0, reason: '監視追加', changedAt: '2026-07-17T00:00:00Z' },
  { actor: 'owner', changeType: 1, reason: '監視解除', changedAt: '2026-07-16T00:00:00Z' },
];

function mockDefault() {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/monitor/watchlist/history') return HISTORY;
    if (path === '/monitor/watchlist') return WATCHLIST;
    return [];
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mockDefault();
});

async function renderReady(): Promise<RenderResult> {
  const view = render(<WatchlistForm />);
  // 一覧（表）の描画完了を待つ。
  await screen.findByRole('table', { name: '監視銘柄' });
  return view;
}

function addForm(view: RenderResult): HTMLElement {
  return within(view.container).getByRole('form', { name: '監視銘柄の追加' });
}

function watchlistTable(view: RenderResult): HTMLElement {
  return within(view.container).getByRole('table', { name: '監視銘柄' });
}

function lastCall(path: string, method: string) {
  return mocks.apiFetch.mock.calls.filter(([p, r]) => p === path && r?.method === method).at(-1);
}

describe('WatchlistForm (SC-02, FR-13, FR-03, IADR-0090)', () => {
  it('lists monitored symbols mapping numeric market to a label', async () => {
    const view = await renderReady();
    const table = watchlistTable(view);
    const jp = within(table).getByText('7203').closest('tr')!;
    expect(within(jp).getByText('日本')).toBeInTheDocument();
    const us = within(table).getByText('AAPL').closest('tr')!;
    expect(within(us).getByText('米国')).toBeInTheDocument();
  });

  it('maps history changeType (add/remove) to labels (FR-11)', async () => {
    const view = await renderReady();
    const table = within(view.container).getByRole('table', { name: '監視銘柄の変更履歴' });
    const rows = within(table).getAllByRole('row');
    expect(within(rows[1]).getByText('追加')).toBeInTheDocument();
    expect(within(rows[1]).getByText('監視追加')).toBeInTheDocument();
    expect(within(rows[2]).getByText('削除')).toBeInTheDocument();
  });

  it('requires a reason before a symbol can be added', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = addForm(view);
    const add = within(form).getByRole('button', { name: '監視銘柄を追加' });
    expect(add).toBeDisabled();
    await user.type(within(form).getByLabelText('監視銘柄コード'), '6758');
    // 理由未入力では追加できない（理由必須の担保）。
    expect(add).toBeDisabled();
    await user.type(within(form).getByLabelText('追加理由'), '半導体監視');
    expect(add).toBeEnabled();
  });

  it('POSTs { symbol, market, reason } with the selected market on add', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = addForm(view);
    await user.type(within(form).getByLabelText('監視銘柄コード'), '6758');
    await user.selectOptions(within(form).getByLabelText('監視銘柄の市場'), '1'); // 米国
    await user.type(within(form).getByLabelText('追加理由'), '半導体監視');
    await user.click(within(form).getByRole('button', { name: '監視銘柄を追加' }));

    const call = lastCall('/monitor/watchlist', 'POST');
    expect(call).toBeDefined();
    expect(call![1].json).toEqual({ symbol: '6758', market: 1, reason: '半導体監視' });
    // 成功後に一覧・履歴を再取得する（破壊的操作なし）。
    expect(await within(form).findByText('追加しました。')).toBeInTheDocument();
  });

  it('does not delete on the row button alone — requires explicit confirmation (fail-safe)', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const row = within(watchlistTable(view)).getByText('AAPL').closest('tr')!;
    await user.click(within(row).getByRole('button', { name: '削除' }));
    // 行の「削除」だけでは DELETE を投げない（明示確認パネルを開くだけ）。
    expect(lastCall('/monitor/watchlist', 'DELETE')).toBeUndefined();
    const confirm = within(view.container).getByRole('group', { name: '監視銘柄の削除確認' });
    // 削除理由が入るまで確定できない。
    const commit = within(confirm).getByRole('button', { name: '監視から削除' });
    expect(commit).toBeDisabled();
    await user.type(within(confirm).getByLabelText('削除理由'), '監視終了');
    expect(commit).toBeEnabled();
  });

  it('DELETEs { symbol, market, reason } after confirmation', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const row = within(watchlistTable(view)).getByText('AAPL').closest('tr')!;
    await user.click(within(row).getByRole('button', { name: '削除' }));
    const confirm = within(view.container).getByRole('group', { name: '監視銘柄の削除確認' });
    await user.type(within(confirm).getByLabelText('削除理由'), '監視終了');
    await user.click(within(confirm).getByRole('button', { name: '監視から削除' }));

    const call = lastCall('/monitor/watchlist', 'DELETE');
    expect(call).toBeDefined();
    expect(call![1].json).toEqual({ symbol: 'AAPL', market: 1, reason: '監視終了' });
  });

  it('cancels the delete confirmation without calling the API', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const row = within(watchlistTable(view)).getByText('AAPL').closest('tr')!;
    await user.click(within(row).getByRole('button', { name: '削除' }));
    const confirm = within(view.container).getByRole('group', { name: '監視銘柄の削除確認' });
    await user.click(within(confirm).getByRole('button', { name: 'キャンセル' }));
    expect(within(view.container).queryByRole('group', { name: '監視銘柄の削除確認' })).not.toBeInTheDocument();
    expect(lastCall('/monitor/watchlist', 'DELETE')).toBeUndefined();
  });

  it('shows a conflict message on 409 without destructive retry (add)', async () => {
    const user = userEvent.setup();
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/monitor/watchlist/history') return HISTORY;
      if (path === '/monitor/watchlist' && req?.method === 'POST') throw new ApiError('conflict', '競合', 409);
      if (path === '/monitor/watchlist') return WATCHLIST;
      return [];
    });
    const view = await renderReady();
    const form = addForm(view);
    await user.type(within(form).getByLabelText('監視銘柄コード'), '6758');
    await user.type(within(form).getByLabelText('追加理由'), '半導体監視');
    await user.click(within(form).getByRole('button', { name: '監視銘柄を追加' }));

    expect(await within(form).findByText(/競合/)).toBeInTheDocument();
    const posts = mocks.apiFetch.mock.calls.filter(([p, r]) => p === '/monitor/watchlist' && r?.method === 'POST');
    expect(posts).toHaveLength(1); // 自動再試行しない
  });

  it('shows a validation message with detail on 400 (delete)', async () => {
    const user = userEvent.setup();
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/monitor/watchlist/history') return HISTORY;
      if (path === '/monitor/watchlist' && req?.method === 'DELETE')
        throw new ApiError('validation', '入力不正', 400, ['理由は必須です']);
      if (path === '/monitor/watchlist') return WATCHLIST;
      return [];
    });
    const view = await renderReady();
    const row = within(watchlistTable(view)).getByText('AAPL').closest('tr')!;
    await user.click(within(row).getByRole('button', { name: '削除' }));
    const confirm = within(view.container).getByRole('group', { name: '監視銘柄の削除確認' });
    await user.type(within(confirm).getByLabelText('削除理由'), '監視終了');
    await user.click(within(confirm).getByRole('button', { name: '監視から削除' }));

    expect(await within(confirm).findByText(/入力内容に誤りがあります/)).toHaveTextContent(/理由は必須です/);
  });

  it('degrades to a not-available message when the watchlist fetch 404s (BFF unwired)', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/monitor/watchlist') throw new ApiError('notFound', '未登録', 404);
      return [];
    });
    render(<WatchlistForm />);
    expect(await screen.findByText('監視銘柄設定は利用できません。')).toBeInTheDocument();
  });

  it('degrades the history area when history fetch fails', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/monitor/watchlist/history') throw new ApiError('server', 'boom', 500);
      if (path === '/monitor/watchlist') return WATCHLIST;
      return [];
    });
    render(<WatchlistForm />);
    expect(await screen.findByText('変更履歴は利用できません。')).toBeInTheDocument();
  });

  it('shows an empty message when there are no monitored symbols', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/monitor/watchlist/history') return [];
      if (path === '/monitor/watchlist') return [];
      return [];
    });
    render(<WatchlistForm />);
    expect(await screen.findByText('監視銘柄はありません。')).toBeInTheDocument();
  });
});
