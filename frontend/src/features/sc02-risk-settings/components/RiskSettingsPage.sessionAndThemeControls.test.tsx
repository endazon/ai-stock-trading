import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { renderWithProviders } from '@ai-stock-trading/testing/renderWithProviders';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// T-10-2956〜T-10-2962, SC-02, FR-10, FR-19, FR-11, UC-06, ADR-0070 決定 5, ADR-0071 決定 2・5, #1407, IADR-0564 決定 3・6:
// **閉場前の統制（締め切り N・手仕舞いの有効と M）とテーマの定義を SC-02 から表示・設定できること。**
//
//   - 画面は効いている値を表示し、どちらが効いているか（構成／画面で保存した値）と、保存値と構成の値の食い違いを示す。
//   - 手仕舞いは画面で無効でも方針の行で有効になり得ることを示す。
//   - 手続きは §5 の他の統制値と同じ: 理由必須・値域の外は送らない・サーバの拒否は表示する。統制を緩める変更は確認を求める。
//   - テーマの銘柄コードの誤記を防ぐ（監視銘柄からの選択・手入力は書式と照合と確認の印）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { RiskSettingsPage } from './RiskSettingsPage';
import {
  CONTRACT_PRE_CLOSE_CONTROLS,
  CONTRACT_RISK_SETTINGS,
  CONTRACT_RISK_STATUS,
  CONTRACT_THEME_CONCENTRATION,
  cloneContract,
} from '@ai-stock-trading/testing/riskContractFixtures';
import { CONTRACT_MONITOR_SETTINGS } from '@ai-stock-trading/testing/monitorContractFixtures';
import type { PreCloseControlsView, ThemeConcentrationControlView } from '@ai-stock-trading/lib/risk/contracts';

const PRE_CLOSE = '/risk-controls/settings/pre-close';
const THEMES = '/risk-controls/settings/theme-concentration';

// 監視銘柄: COIN・MARA・NVDA（米国）・7203（日本）。
const WATCHLIST = [
  { symbol: 'COIN', market: 1 },
  { symbol: 'MARA', market: 1 },
  { symbol: 'NVDA', market: 1 },
  { symbol: '7203', market: 0 },
];

// 構成の値が効いている（保存なし・食い違いなし）状態。契約フィクスチャ（保存後の形）から組み立てる。
function configuredPreClose(): PreCloseControlsView {
  const view = cloneContract(CONTRACT_PRE_CLOSE_CONTROLS);
  return {
    ...view,
    effectiveCutoff: cloneContract(view.configuredCutoff),
    savedCutoff: null,
    cutoffSource: 0,
    cutoffDrift: false,
    effectiveFlatten: cloneContract(view.configuredFlatten),
    savedFlatten: null,
    flattenSource: 0,
    flattenDrift: false,
  };
}

function mockBff(
  options: {
    preClose?: PreCloseControlsView | 'error';
    themes?: ThemeConcentrationControlView;
    put?: 'ok' | 'validation';
  } = {},
) {
  const { preClose = configuredPreClose(), themes = cloneContract(CONTRACT_THEME_CONCENTRATION), put = 'ok' } = options;
  mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
    if (path === '/risk-controls/settings/history') return [];
    if (path === '/risk-controls/status') return cloneContract(CONTRACT_RISK_STATUS);
    if (path === '/monitor/watchlist') return WATCHLIST;
    if (path === '/monitor/watchlist/history') return [];
    if (path === '/monitor/settings') return cloneContract(CONTRACT_MONITOR_SETTINGS);
    if (path === '/monitor/settings/history') return [];
    if ((path === PRE_CLOSE || path === THEMES) && req?.method === 'PUT') {
      if (put === 'validation') {
        throw new ApiError('validation', '入力内容に誤りがあります。', 400, ['米国 の N は 1〜390 分で指定してください（指定値 400）。']);
      }
      return path === PRE_CLOSE ? preClose : themes;
    }
    if (path === PRE_CLOSE) {
      if (preClose === 'error') throw new ApiError('server', '取得できません。', 502, []);
      return preClose;
    }
    if (path === THEMES) return themes;
    return cloneContract(CONTRACT_RISK_SETTINGS);
  });
}

function putCalls(path: string) {
  return mocks.apiFetch.mock.calls.filter((call) => call[0] === path && call[1]?.method === 'PUT');
}

async function preCloseForm() {
  return screen.findByRole('form', { name: '閉場前の統制の変更' });
}

async function themeForm() {
  return screen.findByRole('form', { name: 'テーマの定義の変更' });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-02 閉場前の統制（#1407・ADR-0070 決定 5）', () => {
  // T-10-2956: 効いている値を表示し、出どころ（構成）と、手仕舞いは方針の行でも有効になり得ることを示す。
  it('T-10-2956 構成の値が効いているときは値と出どころを示し、食い違いは示さない', async () => {
    mockBff();
    renderWithProviders(<RiskSettingsPage />);
    const form = await preCloseForm();

    expect(within(form).getByLabelText('米国の締め切り N（分）')).toHaveValue(60);
    expect(within(form).getByLabelText('日本の締め切り N（分）')).toHaveValue(60);
    expect(within(form).getByLabelText('米国の手仕舞いの M（分）')).toHaveValue(15);
    expect(within(form).getByRole('checkbox', { name: '閉場前の手仕舞いを有効にする（設定）' })).not.toBeChecked();
    expect(screen.getAllByText('構成の値が効いています')).toHaveLength(2);
    expect(screen.queryByText(/構成の値と食い違っています/)).toBeNull();
    expect(screen.getByText(/方針に「閉場前の手仕舞い: 有効」の行があれば手仕舞いは有効になります/)).toBeInTheDocument();
  });

  // T-10-2957: 保存値が構成と違えば、保存値が効いていることと構成の値を並べて示す（契約フィクスチャ＝米国 N 90・手仕舞い有効を保存）。
  it('T-10-2957 保存値が構成と食い違うときは食い違いと構成の値を示す', async () => {
    mockBff({ preClose: cloneContract(CONTRACT_PRE_CLOSE_CONTROLS) });
    renderWithProviders(<RiskSettingsPage />);
    const form = await preCloseForm();

    expect(within(form).getByLabelText('米国の締め切り N（分）')).toHaveValue(90);
    expect(within(form).getByRole('checkbox', { name: '閉場前の手仕舞いを有効にする（設定）' })).toBeChecked();
    expect(screen.getAllByText('画面で保存した値が効いています')).toHaveLength(2);
    expect(screen.getByText(/締め切りの保存値が構成の値と食い違っています.*構成の値: 日本 60 分 \/ 米国 60 分/)).toBeInTheDocument();
    expect(screen.getByText(/手仕舞いの保存値が構成の値と食い違っています.*構成の値: 無効/)).toBeInTheDocument();
  });

  // T-10-2958: 理由を入れて値を変えると、市場ごとの N・M と有効を PUT する。理由が無い・値域の外（N 391）は送らない。
  it('T-10-2958 値域の中の変更を理由つきで PUT し、値域の外と理由なしは送らない', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await preCloseForm();
    const save = within(form).getByRole('button', { name: '閉場前の統制を保存' });

    const usCutoff = within(form).getByLabelText('米国の締め切り N（分）');
    await user.clear(usCutoff);
    await user.type(usCutoff, '391');
    await user.type(within(form).getByLabelText('閉場前の統制の変更理由'), '締め切りを早める');
    expect(within(form).getByRole('alert')).toHaveTextContent('米国の締め切り N は 1〜390 分で入力してください。');
    expect(save).toBeDisabled();

    await user.clear(usCutoff);
    await user.type(usCutoff, '90');
    await user.click(within(form).getByRole('checkbox', { name: '閉場前の手仕舞いを有効にする（設定）' }));
    await waitFor(() => expect(save).toBeEnabled());
    await user.click(save);

    await waitFor(() => expect(putCalls(PRE_CLOSE)).toHaveLength(1));
    expect(putCalls(PRE_CLOSE)[0]![1]).toEqual({
      method: 'PUT',
      json: {
        cutoffMinutes: [
          { market: 0, minutes: 60 },
          { market: 1, minutes: 90 },
        ],
        flattenEnabled: true,
        flattenMinutes: [
          { market: 0, minutes: 15 },
          { market: 1, minutes: 15 },
        ],
        reason: '締め切りを早める',
      },
    });
  });

  // T-10-2959: 緩める変更（N を縮める・手仕舞いを無効にする）は列挙して確認を求める。サーバの 400 は詳細を表示し、自動再試行しない。
  it('T-10-2959 緩める変更は確認を求め、サーバの 400 は詳細を表示する', async () => {
    mockBff({ preClose: cloneContract(CONTRACT_PRE_CLOSE_CONTROLS), put: 'validation' });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await preCloseForm();
    const save = within(form).getByRole('button', { name: '閉場前の統制を保存' });

    const usCutoff = within(form).getByLabelText('米国の締め切り N（分）');
    await user.clear(usCutoff);
    await user.type(usCutoff, '30');
    await user.click(within(form).getByRole('checkbox', { name: '閉場前の手仕舞いを有効にする（設定）' }));
    await user.type(within(form).getByLabelText('閉場前の統制の変更理由'), '緩める');

    expect(within(form).getByText(/米国の締め切り N を 90 分から 30 分へ縮める、閉場前の手仕舞いを無効にする/)).toBeInTheDocument();
    expect(save).toBeDisabled();
    await user.click(within(form).getByRole('checkbox', { name: '上記の緩める変更を確認しました' }));
    await waitFor(() => expect(save).toBeEnabled());
    await user.click(save);

    expect(await within(form).findByText(/入力内容に誤りがあります。（米国 の N は 1〜390 分/)).toBeInTheDocument();
    expect(putCalls(PRE_CLOSE)).toHaveLength(1);
  });

  // T-10-2960（否定形）: 取得できなければ既定値で入力欄を出さない（効いている値が分からないまま保存させない）。
  it('T-10-2960 取得できなければ入力欄を出さずに取得できないと示す', async () => {
    mockBff({ preClose: 'error' });
    renderWithProviders(<RiskSettingsPage />);

    expect(await screen.findByText(/閉場前の統制を取得できません。効いている値が分からないため、変更できません。/)).toBeInTheDocument();
    expect(screen.queryByRole('form', { name: '閉場前の統制の変更' })).toBeNull();
  });
});

describe('SC-02 テーマごとの同時保有の上限（#1407・ADR-0071 決定 5）', () => {
  // T-10-2961: 効いている定義を表示し、構成の定義との食い違いを示す。監視銘柄から選んで足し、全置換で PUT する。
  it('T-10-2961 定義と食い違いを示し、監視銘柄から足して全置換で PUT する', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await themeForm();

    expect(within(form).getByLabelText('テーマの名前')).toHaveValue('暗号資産');
    expect(screen.getByText('画面で保存した定義が効いています')).toBeInTheDocument();
    expect(screen.getByText(/構成の定義と食い違っています.*AI・半導体（米国・上限 1）: NVDA・SMCI/)).toBeInTheDocument();
    const table = within(form).getByRole('table', { name: '暗号資産の銘柄の一覧' });
    await waitFor(() => expect(within(table).getAllByText('監視銘柄にあります')).toHaveLength(2)); // COIN・MARA
    expect(within(table).getAllByText('監視銘柄にありません')).toHaveLength(2); // MSTR・HOOD

    const pick = within(form).getByLabelText('監視銘柄から追加');
    // テーマの市場（米国）の監視銘柄だけを出し、既にある銘柄は出さない。
    expect(within(pick).queryByRole('option', { name: '7203' })).toBeNull();
    expect(within(pick).queryByRole('option', { name: 'COIN' })).toBeNull();
    await user.selectOptions(pick, 'NVDA');
    await user.click(within(form).getByRole('button', { name: '選んだ監視銘柄を追加' }));
    await user.type(within(form).getByLabelText('テーマの定義の変更理由'), 'NVDA も同じ値動き');
    const save = within(form).getByRole('button', { name: 'テーマの定義を保存' });
    await waitFor(() => expect(save).toBeEnabled());
    await user.click(save);

    await waitFor(() => expect(putCalls(THEMES)).toHaveLength(1));
    expect(putCalls(THEMES)[0]![1]).toEqual({
      method: 'PUT',
      json: {
        themes: [{ name: '暗号資産', market: 1, symbols: ['COIN', 'MARA', 'MSTR', 'HOOD', 'NVDA'], maxPositions: 1 }],
        reason: 'NVDA も同じ値動き',
      },
    });
  });

  // T-10-2962: 手入力は書式で拒み、監視銘柄に無ければ確認の印が付くまで追加できない。上限を上げる・銘柄を外すは確認を求める。上限 0 は送らない。
  it('T-10-2962 誤記を防ぎ、緩める変更は確認を求め、上限 0 は送らない', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await themeForm();
    const manual = within(form).getByLabelText('銘柄コード（手入力）');
    const add = within(form).getByRole('button', { name: '手入力の銘柄を追加' });

    await user.type(manual, 'RI OT');
    expect(within(form).getByText(/銘柄コードは英数字/)).toBeInTheDocument();
    expect(add).toBeDisabled();
    await user.clear(manual);
    await user.type(manual, 'riot');
    await waitFor(() => expect(within(form).getByText(/RIOT（米国）: 監視銘柄にありません/)).toBeInTheDocument());
    expect(add).toBeDisabled();
    await user.click(within(form).getByRole('checkbox', { name: /綴りと市場を確かめた/ }));
    expect(add).toBeEnabled();
    await user.click(add);
    expect(within(form).getByRole('table', { name: '暗号資産の銘柄の一覧' })).toHaveTextContent('RIOT');

    const max = within(form).getByLabelText('同時保有の上限（銘柄数）');
    await user.clear(max);
    await user.type(max, '0');
    await user.type(within(form).getByLabelText('テーマの定義の変更理由'), '変更');
    expect(within(form).getByText(/テーマ「暗号資産」の上限は 1 以上の整数で入力してください。/)).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: 'テーマの定義を保存' })).toBeDisabled();

    await user.clear(max);
    await user.type(max, '2');
    await user.click(within(form).getByRole('button', { name: 'MSTRをテーマから外す' }));
    expect(within(form).getByText(/テーマ「暗号資産」から銘柄を外す（MSTR）、テーマ「暗号資産」の上限を 1 から 2 へ上げる/)).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: 'テーマの定義を保存' })).toBeDisabled();
    await user.click(within(form).getByRole('checkbox', { name: '上記の緩める変更を確認しました' }));
    await waitFor(() => expect(within(form).getByRole('button', { name: 'テーマの定義を保存' })).toBeEnabled());
    expect(putCalls(THEMES)).toHaveLength(0);
  });
});
