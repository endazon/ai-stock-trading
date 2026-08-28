import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import type { RenderResult } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-02, FR-13, FR-19, UC-06, IADR-0086: 取引ガードの変更 UI の振る舞い。
// 上限フォーム（#186）と同じ apiFetch モック方針で、実 BFF 疎通に依存しない。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { RiskSettingsPage } from './RiskSettingsPage';
import { CONTRACT_RISK_SETTINGS, cloneContract } from '../risk/contractFixtures';
import type { RiskManagementSettings } from '../risk/contracts';

// #389, IADR-0146: モックはバックエンドの実応答（契約フィクスチャ）を土台にする（インライン literal で
// 自作した形を自分で検証しない）。ガードの差分だけを上書きする。
const SETTINGS: RiskManagementSettings = {
  ...cloneContract(CONTRACT_RISK_SETTINGS),
  guard: {
    ...cloneContract(CONTRACT_RISK_SETTINGS.guard),
    enabledProductTypes: [0], // 現物のみ（信用は無効）
    bannedSymbols: [{ symbol: '9999', market: 0, reason: '監視対象外', registeredOn: '2026-07-10' }],
  },
  limits: { ...cloneContract(CONTRACT_RISK_SETTINGS.limits), maxOpenPositions: 5, losingStreakThreshold: 3 },
  stage: { ...cloneContract(CONTRACT_RISK_SETTINGS.stage), stage: 2, mode: 1 },
};

function mockDefault() {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/settings/history') return [];
    // 監視銘柄セクション（#196・別サービス）は独立ロードするため、ガードのテストでは空応答で満たす。
    if (path === '/monitor/watchlist') return [];
    if (path === '/monitor/watchlist/history') return [];
    return SETTINGS;
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mockDefault();
});

// 取引ガードの変更フォームを取得する（上限フォームと区別するためアクセシブル名で絞る）。
function guardForm(view: RenderResult): HTMLElement {
  return within(view.container).getByRole('form', { name: '取引ガードの変更' });
}

async function renderReady(): Promise<RenderResult> {
  const view = render(<RiskSettingsPage />);
  await screen.findByRole('heading', { name: 'リスク設定' });
  return view;
}

function lastGuardPut() {
  return mocks.apiFetch.mock.calls
    .filter(([p, r]) => p === '/risk-controls/settings/guard' && r?.method === 'PUT')
    .at(-1);
}

describe('RiskSettingsPage guard editing (SC-02, FR-13, FR-19, IADR-0086)', () => {
  it('renders current guard into the editable form', async () => {
    const view = await renderReady();
    const form = guardForm(view);
    // 現物のみ有効（既定）。信用買い・空売りは無効（現在値の反映。FR-19・ADR-0016 決定1）。
    expect(within(form).getByRole('checkbox', { name: '現物' })).toBeChecked();
    expect(within(form).getByRole('checkbox', { name: '信用買い' })).not.toBeChecked();
    expect(within(form).getByRole('checkbox', { name: '空売り' })).not.toBeChecked();
    // 市場は日本・米国とも有効。
    expect(within(form).getByRole('checkbox', { name: '日本' })).toBeChecked();
    expect(within(form).getByRole('checkbox', { name: '米国' })).toBeChecked();
    // トグルは両方有効。
    expect(within(form).getByRole('checkbox', { name: '同日再エントリー禁止' })).toBeChecked();
    expect(within(form).getByRole('checkbox', { name: '相場操縦パターン禁止' })).toBeChecked();
    // 既存の禁止銘柄が編集表に出る。
    expect(within(form).getByText('9999')).toBeInTheDocument();
  });

  it('requires a reason before the guard can be saved', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = guardForm(view);
    const save = within(form).getByRole('button', { name: '保存' });
    expect(save).toBeDisabled();
    // 厳格化（米国を無効にする＝取引可能範囲を狭める）は危険確認不要だが理由は必須。
    await user.click(within(form).getByRole('checkbox', { name: '米国' }));
    expect(save).toBeDisabled();
    await user.type(within(form).getByLabelText('変更理由'), '市場を絞る');
    expect(save).toBeEnabled();
  });

  it('submits PUT to /settings/guard with the edited guard and reason', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = guardForm(view);
    // 厳格化のみ（米国を無効化）＝危険確認は不要。
    await user.click(within(form).getByRole('checkbox', { name: '米国' }));
    await user.type(within(form).getByLabelText('変更理由'), '米国を一時停止');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    const call = lastGuardPut();
    expect(call).toBeDefined();
    expect(call![1].method).toBe('PUT');
    expect(call![1].json.reason).toBe('米国を一時停止');
    expect(call![1].json.enabledMarkets).toEqual([0]); // 米国(1) を除いた
    expect(call![1].json.enabledProductTypes).toEqual([0]);
    expect(call![1].json.preventSameDayReentry).toBe(true);
    expect(call![1].json.prohibitManipulativeOrderPatterns).toBe(true);
  });

  it('blocks a dangerous loosening (toggle off) until the confirmation is checked', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = guardForm(view);
    await user.type(within(form).getByLabelText('変更理由'), '検証のため一時緩和');
    // 相場操縦パターン禁止を OFF にする＝危険な緩和。
    await user.click(within(form).getByRole('checkbox', { name: '相場操縦パターン禁止' }));
    const save = within(form).getByRole('button', { name: '保存' });
    // 理由はあるが確認未チェックのため保存不可。
    expect(save).toBeDisabled();
    expect(within(form).getByRole('alert')).toHaveTextContent(/相場操縦/);
    // 確認をチェックすると保存可能。
    await user.click(within(form).getByRole('checkbox', { name: /確認/ }));
    expect(save).toBeEnabled();
    await user.click(save);
    expect(lastGuardPut()![1].json.prohibitManipulativeOrderPatterns).toBe(false);
  });

  it('re-requires confirmation when a further dangerous change is made after confirming', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = guardForm(view);
    await user.type(within(form).getByLabelText('変更理由'), '段階的に緩和');
    // 危険 1: 相場操縦パターン禁止を OFF → 確認 ON で保存可能。
    await user.click(within(form).getByRole('checkbox', { name: '相場操縦パターン禁止' }));
    await user.click(within(form).getByRole('checkbox', { name: /確認/ }));
    const save = within(form).getByRole('button', { name: '保存' });
    expect(save).toBeEnabled();
    // 危険 2 を追加（同日再エントリー禁止も OFF）→ 確認が外れ、再確認が必要（fail-safe）。
    await user.click(within(form).getByRole('checkbox', { name: '同日再エントリー禁止' }));
    expect(within(form).getByRole('checkbox', { name: /確認/ })).not.toBeChecked();
    expect(save).toBeDisabled();
  });

  // FR-19 / ADR-0016 決定1・#332: 商品種別は 3 値。現物以外（信用買い・空売り）の有効化は危険な緩和。
  it('treats enabling margin-long as a dangerous change requiring confirmation', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = guardForm(view);
    await user.type(within(form).getByLabelText('変更理由'), '信用買いを試す');
    await user.click(within(form).getByRole('checkbox', { name: '信用買い' }));
    const save = within(form).getByRole('button', { name: '保存' });
    expect(save).toBeDisabled();
    expect(within(form).getByRole('alert')).toHaveTextContent(/信用買い/);
  });

  // 空売りは**損失に上限が無い**（ADR-0016）。信用買いと同様に確認を求める（既定は無効）。
  it('treats enabling short-selling as a dangerous change requiring confirmation', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = guardForm(view);
    await user.type(within(form).getByLabelText('変更理由'), '空売りを試す');
    await user.click(within(form).getByRole('checkbox', { name: '空売り' }));
    const save = within(form).getByRole('button', { name: '保存' });
    expect(save).toBeDisabled();
    expect(within(form).getByRole('alert')).toHaveTextContent(/空売り/);
    // 確認をチェックすれば保存でき、送信内容に空売り(2)が含まれる。
    await user.click(within(form).getByRole('checkbox', { name: /確認/ }));
    await user.click(save);
    expect(lastGuardPut()![1].json.enabledProductTypes).toContain(2);
  });

  it('adds and removes banned symbols and reflects them in the payload', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = guardForm(view);
    // 追加。
    await user.type(within(form).getByLabelText('禁止銘柄コード'), '7203');
    await user.type(within(form).getByLabelText('禁止理由'), '流動性');
    await user.click(within(form).getByRole('button', { name: '禁止銘柄を追加' }));
    // 追加のみなら危険ではない → 理由入力で保存可能。
    await user.type(within(form).getByLabelText('変更理由'), '禁止追加');
    await user.click(within(form).getByRole('button', { name: '保存' }));
    const symbols = lastGuardPut()![1].json.bannedSymbols.map((b: { symbol: string }) => b.symbol);
    expect(symbols).toContain('7203');
    expect(symbols).toContain('9999');
  });

  it('treats removing a banned symbol as a dangerous change requiring confirmation', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = guardForm(view);
    await user.type(within(form).getByLabelText('変更理由'), '禁止解除');
    const bannedTable = within(form).getByRole('table', { name: '禁止銘柄（編集）' });
    const row = within(bannedTable).getByText('9999').closest('tr')!;
    await user.click(within(row).getByRole('button', { name: '削除' }));
    const save = within(form).getByRole('button', { name: '保存' });
    expect(save).toBeDisabled();
    expect(within(form).getByRole('alert')).toHaveTextContent(/禁止銘柄|9999/);
    await user.click(within(form).getByRole('checkbox', { name: /確認/ }));
    expect(save).toBeEnabled();
  });

  it('requires a ban reason before a banned symbol can be added (FR-19)', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const form = guardForm(view);
    const addBtn = within(form).getByRole('button', { name: '禁止銘柄を追加' });
    await user.type(within(form).getByLabelText('禁止銘柄コード'), '7203');
    // 理由未入力では追加できない（禁止根拠の記録を担保）。
    expect(addBtn).toBeDisabled();
    await user.type(within(form).getByLabelText('禁止理由'), '流動性');
    expect(addBtn).toBeEnabled();
  });

  it('preserves in-progress guard edits when the adjacent limits form is saved', async () => {
    const user = userEvent.setup();
    const view = await renderReady();
    const gForm = guardForm(view);
    // ガードを編集（米国を無効化＋理由）。
    await user.click(within(gForm).getByRole('checkbox', { name: '米国' }));
    await user.type(within(gForm).getByLabelText('変更理由'), '銘柄整理');
    // 隣接するリスク上限フォームを保存する（current が再生成されるが guard の内容は不変）。
    const lForm = within(view.container).getByRole('form', { name: 'リスク上限の変更' });
    await user.type(within(lForm).getByLabelText('変更理由'), '上限調整');
    await user.click(within(lForm).getByRole('button', { name: '保存' }));
    await within(lForm).findByText('保存しました。');
    // ガード側の未保存編集が黙って初期化されない（fail-safe）。
    expect(within(guardForm(view)).getByRole('checkbox', { name: '米国' })).not.toBeChecked();
    expect(within(guardForm(view)).getByLabelText('変更理由')).toHaveValue('銘柄整理');
  });

  // #539: mount 時にも走っていた prop 追随を描画中の state 調整へ揃えた。**`guardSignature` が
  // 実際に変わったとき（自分の保存成功後の再取得）の初期化は従来どおり効く**ことを固定する
  // （置換前は `useEffect([guardSignature])` が同じ役割を担っていた）。
  it('保存成功後の再取得でガード内容が実際に変わると、理由・確認・下書きが初期化される', async () => {
    let saved = false;
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/risk-controls/settings/history') return [];
      if (path === '/monitor/watchlist' || path === '/monitor/watchlist/history') return [];
      if (path === '/risk-controls/settings/guard' && req?.method === 'PUT') {
        saved = true;
        return { settings: SETTINGS };
      }
      // 保存後の再取得は、実際に内容が変わった値を返す（米国(1) が除かれている）。
      if (saved) return { ...SETTINGS, guard: { ...SETTINGS.guard, enabledMarkets: [0] } };
      return SETTINGS;
    });
    const user = userEvent.setup();
    const view = await renderReady();
    const form = guardForm(view);
    await user.type(within(form).getByLabelText('変更理由'), '保存確認用');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    await within(form).findByText('保存しました。');
    // guardSignature が実際に変わったので、理由・下書きが初期化され、新しいガード内容が反映される。
    expect(within(guardForm(view)).getByLabelText('変更理由')).toHaveValue('');
    expect(within(guardForm(view)).getByRole('checkbox', { name: '米国' })).not.toBeChecked();
  });

  it('shows a conflict message on 409 without destructive retry', async () => {
    const user = userEvent.setup();
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/risk-controls/settings/history') return [];
      if (path === '/risk-controls/settings/guard' && req?.method === 'PUT')
        throw new ApiError('conflict', '競合', 409);
      return SETTINGS;
    });
    const view = await renderReady();
    const form = guardForm(view);
    await user.click(within(form).getByRole('checkbox', { name: '米国' }));
    await user.type(within(form).getByLabelText('変更理由'), '市場変更');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    expect(await within(form).findByText(/競合/)).toBeInTheDocument();
    const puts = mocks.apiFetch.mock.calls.filter(
      ([p, r]) => p === '/risk-controls/settings/guard' && r?.method === 'PUT',
    );
    expect(puts).toHaveLength(1); // 自動再試行しない
  });

  it('shows a validation message with detail on 400', async () => {
    const user = userEvent.setup();
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/risk-controls/settings/history') return [];
      if (path === '/risk-controls/settings/guard' && req?.method === 'PUT')
        throw new ApiError('validation', '入力不正', 400, ['理由は必須です']);
      return SETTINGS;
    });
    const view = await renderReady();
    const form = guardForm(view);
    await user.click(within(form).getByRole('checkbox', { name: '米国' }));
    await user.type(within(form).getByLabelText('変更理由'), '市場変更');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    expect(await within(form).findByText(/入力内容に誤りがあります/)).toHaveTextContent(/理由は必須です/);
  });
});
