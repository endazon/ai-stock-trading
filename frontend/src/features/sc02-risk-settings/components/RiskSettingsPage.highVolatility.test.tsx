import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { renderWithProviders } from '@ai-stock-trading/testing/renderWithProviders';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { serverBadRequest } from '@ai-stock-trading/testing/serverErrors';

// T-10-2593〜T-10-2601, SC-02, FR-10, FR-11, UC-06, ADR-0066 決定1・決定2, ADR-0063 決定1・決定2, #1291, IADR-0530:
// **高ボラティリティ銘柄の明示指定と区分の上限を SC-02 から表示・設定できること**（ADR-0066 決定2 の受け入れ基準）。
//
//   - 手続きは §5 の他の統制値と同じ: 理由必須・値域（1% 以上 25% 以下）の外は送らない・サーバの拒否は表示する（自動再試行しない）。
//   - 経路は既存の `PUT /risk-controls/settings/high-volatility` を BFF 経由で使う（本文は比率と一覧の全置換）。
//   - 🔴 **銘柄コードの誤記を防ぐ**: 主の経路は監視銘柄からの選択。手入力は書式で拒み、監視銘柄に無い・市場が違う・照合できない
//     ときは警告し、**確認の印が無ければ追加させない**。保存済みの指定にも照合の印を出す。
//   - 統制を緩める変更（上限を上げる・指定を外す）は確認を求める。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { RiskSettingsPage } from './RiskSettingsPage';
import { CONTRACT_RISK_SETTINGS, CONTRACT_RISK_STATUS, cloneContract } from '@ai-stock-trading/testing/riskContractFixtures';
import { CONTRACT_MONITOR_SETTINGS } from '@ai-stock-trading/testing/monitorContractFixtures';
import type { HighVolatilitySettings } from '@ai-stock-trading/lib/risk/contracts';

const STATUS = cloneContract(CONTRACT_RISK_STATUS);
const MONITOR = cloneContract(CONTRACT_MONITOR_SETTINGS);
const PATH = '/risk-controls/settings/high-volatility';

// 監視銘柄: TSLA（米国）・NVDA（米国）・7203（日本）。
const WATCHLIST = [
  { symbol: 'TSLA', market: 1 },
  { symbol: 'NVDA', market: 1 },
  { symbol: '7203', market: 0 },
];

function mockBff(
  options: {
    highVolatility?: HighVolatilitySettings;
    watchlist?: 'ok' | 'error' | 'pending';
    put?: 'ok' | 'validation';
  } = {},
) {
  const { highVolatility, watchlist = 'ok', put = 'ok' } = options;
  const settings = cloneContract(CONTRACT_RISK_SETTINGS);
  if (highVolatility !== undefined) settings.highVolatility = highVolatility;
  mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
    if (path === '/risk-controls/settings/history') return [];
    if (path === '/risk-controls/status') return STATUS;
    if (path === '/monitor/watchlist') {
      if (watchlist === 'error') throw new ApiError('server', '監視銘柄を取得できません。', 502, []);
      if (watchlist === 'pending') return new Promise(() => {}); // 取得中のまま返らない
      return WATCHLIST;
    }
    if (path === '/monitor/watchlist/history') return [];
    if (path === '/monitor/settings') return MONITOR;
    if (path === '/monitor/settings/history') return [];
    if (path === PATH && req?.method === 'PUT') {
      if (put === 'validation') {
        throw serverBadRequest({
          error: '高ボラティリティ銘柄の設定が受理できません。',
          details: [
            '高ボラティリティ銘柄の 1 注文あたりの発注金額上限（equity 比）は 0.01（新規建ての最小の名目額）以上 0.25（区分外の上限）以下でなければなりません（指定値: 0.3）。',
          ],
        });
      }
      return settings;
    }
    return settings;
  });
}

async function hvForm() {
  return screen.findByRole('form', { name: '高ボラティリティ銘柄の設定の変更' });
}

function putCalls() {
  return mocks.apiFetch.mock.calls.filter((call) => call[0] === PATH && call[1]?.method === 'PUT');
}

async function waitEnabled(form: HTMLElement) {
  await waitFor(
    () => expect(within(form).getByRole('button', { name: '高ボラティリティ銘柄の設定を保存' })).toBeEnabled(),
    { timeout: 5000 },
  );
}

const ratioLabel = '区分の 1 注文あたりの発注金額上限 %';
const reasonLabel = '高ボラティリティ銘柄の変更理由';

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-02 高ボラティリティ銘柄の明示指定と区分の上限（#1291・ADR-0066 決定2）', () => {
  // T-10-2593: 現在値（区分の上限・明示指定）を表示し、保存済みの指定を監視銘柄と照合した印を出す。
  it('T-10-2593 現在の上限と明示指定を表示し、監視銘柄に無い指定に印を出す', async () => {
    mockBff({
      highVolatility: {
        maxOrderAmountRatio: 0.05,
        designatedSymbols: [
          { symbol: 'TSLA', market: 1 },
          { symbol: 'TSL', market: 1 }, // API で入った誤記の例
        ],
      },
    });
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();

    expect(within(form).getByLabelText(ratioLabel)).toHaveValue(5);
    const table = within(form).getByRole('table', { name: '明示指定の銘柄の一覧' });
    const rows = within(table).getAllByRole('row');
    await waitFor(() => expect(rows[1]).toHaveTextContent('監視銘柄にあります'));
    expect(rows[1]).toHaveTextContent('TSLA');
    expect(rows[2]).toHaveTextContent('TSL');
    expect(rows[2]).toHaveTextContent('監視銘柄にありません');
  });

  // T-10-2594（否定形）: 理由が無い・何も変えていないときは保存できない。
  it('T-10-2594 理由が無いと保存できず、変更が無ければ理由があっても保存できない', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();
    const save = within(form).getByRole('button', { name: '高ボラティリティ銘柄の設定を保存' });

    expect(save).toBeDisabled();
    await user.type(within(form).getByLabelText(reasonLabel), '理由だけ');
    expect(save).toBeDisabled();
    expect(putCalls()).toHaveLength(0);
  });

  // T-10-2595: 監視銘柄から選んで追加し、理由を入れると、比率と一覧の全置換を PUT する（銘柄と市場は選んだ組のまま）。
  it('T-10-2595 監視銘柄から選んで追加し、全置換の本文を PUT する', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();

    const pick = within(form).getByLabelText('監視銘柄');
    await waitFor(() => expect(within(pick).getByRole('option', { name: 'TSLA（米国）' })).toBeInTheDocument());
    await user.selectOptions(pick, 'TSLA|1');
    await user.click(within(form).getByRole('button', { name: '選んだ監視銘柄を追加' }));
    // 追加済みの銘柄は選択肢から消える（同じ指定を 2 度入れない）。
    expect(within(pick).queryByRole('option', { name: 'TSLA（米国）' })).toBeNull();
    await user.type(within(form).getByLabelText(reasonLabel), '値動きの大きい銘柄を区分に入れる');

    await waitEnabled(form);
    await user.click(within(form).getByRole('button', { name: '高ボラティリティ銘柄の設定を保存' }));

    expect(putCalls()).toHaveLength(1);
    expect(mocks.apiFetch).toHaveBeenCalledWith(PATH, {
      method: 'PUT',
      json: {
        maxOrderAmountRatio: 0.05,
        designatedSymbols: [{ symbol: 'TSLA', market: 1 }],
        reason: '値動きの大きい銘柄を区分に入れる',
      },
    });
  });

  // 🔴 T-10-2596（否定形）: 手入力で監視銘柄に無い銘柄は警告し、確認の印が無ければ追加できない。小文字は大文字へ揃える。
  it('T-10-2596 監視銘柄に無い手入力は警告し、確認するまで追加させない', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();
    await within(form).findByRole('option', { name: 'TSLA（米国）' });

    await user.selectOptions(within(form).getByLabelText('市場（手入力）'), '1');
    await user.type(within(form).getByLabelText('銘柄コード（手入力）'), 'tsl');
    const add = within(form).getByRole('button', { name: '手入力の銘柄を追加' });

    expect(within(form).getByText(/TSL（米国）: 監視銘柄にありません/)).toBeInTheDocument();
    expect(add).toBeDisabled();

    await user.click(within(form).getByRole('checkbox', { name: /綴りと市場を確かめた/ }));
    expect(add).toBeEnabled();
    await user.click(add);

    const table = within(form).getByRole('table', { name: '明示指定の銘柄の一覧' });
    expect(within(table).getByText('TSL')).toBeInTheDocument();
  });

  // 🔴 T-10-2597（否定形）: 同じ銘柄コードが別の市場でだけ監視銘柄にあれば「市場が違う」と警告する。監視銘柄にある組は確認なしで足せる。
  it('T-10-2597 市場の取り違えを警告し、監視銘柄にある組は確認なしで追加できる', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();
    await within(form).findByRole('option', { name: 'TSLA（米国）' });

    await user.selectOptions(within(form).getByLabelText('市場（手入力）'), '0');
    await user.type(within(form).getByLabelText('銘柄コード（手入力）'), 'NVDA');
    expect(within(form).getByText(/NVDA（日本）: 監視銘柄では市場が違います/)).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: '手入力の銘柄を追加' })).toBeDisabled();

    await user.selectOptions(within(form).getByLabelText('市場（手入力）'), '1');
    expect(within(form).queryByText(/監視銘柄では市場が違います/)).toBeNull();
    expect(within(form).getByRole('button', { name: '手入力の銘柄を追加' })).toBeEnabled();
  });

  // 🔴 T-10-2598（否定形）: 書式の誤り（空白・記号）と重複は追加できない。
  it.each(['TS LA', 'TSLA!', '$TSLA'])('T-10-2598 書式の誤り（%s）は追加できない', async (value) => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();

    await user.type(within(form).getByLabelText('銘柄コード（手入力）'), value);
    expect(within(form).getByText(/英数字（と「.」「-」）の 12 文字以内/)).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: '手入力の銘柄を追加' })).toBeDisabled();
  });

  it('T-10-2598 既に指定した銘柄と市場は重ねて追加できない', async () => {
    mockBff({ highVolatility: { maxOrderAmountRatio: 0.05, designatedSymbols: [{ symbol: 'TSLA', market: 1 }] } });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();

    await user.selectOptions(within(form).getByLabelText('市場（手入力）'), '1');
    await user.type(within(form).getByLabelText('銘柄コード（手入力）'), 'tsla');
    expect(within(form).getByText('この銘柄と市場は既に明示指定されています。')).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: '手入力の銘柄を追加' })).toBeDisabled();
  });

  // T-10-2599（境界値）: 区分の上限は 1% 以上 25% 以下（ADR-0066 決定1。下端は既定値の 1% 固定）。外は送らない。
  it.each([
    ['1', true],
    ['25', true],
    ['3.5', true],
    ['0.9', false],
    ['25.1', false],
    ['0', false],
  ])('T-10-2599 区分の上限 %s%% の保存可否は %s', async (value, ok) => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();

    await user.clear(within(form).getByLabelText(ratioLabel));
    await user.type(within(form).getByLabelText(ratioLabel), value);
    await user.type(within(form).getByLabelText(reasonLabel), '境界の検証');
    // 上げる変更は確認を求める（T-10-2600）。ここでは値域だけを見るため、出ていれば確認する。
    const confirm = within(form).queryByRole('checkbox', { name: '上記の緩める変更を確認しました' });
    if (confirm !== null) await user.click(confirm);

    const save = within(form).getByRole('button', { name: '高ボラティリティ銘柄の設定を保存' });
    if (ok) {
      expect(save).toBeEnabled();
    } else {
      expect(save).toBeDisabled();
      expect(within(form).getByText(/1% 以上 25% 以下 の範囲で入力してください/)).toBeInTheDocument();
    }
  });

  // 🔴 T-10-2600: 統制を緩める変更（上限を上げる・指定を外す）は確認を求める。締める変更（上限を下げる）は求めない。
  it('T-10-2600 上限を上げる・指定を外すときは確認を求め、下げるときは求めない', async () => {
    mockBff({ highVolatility: { maxOrderAmountRatio: 0.05, designatedSymbols: [{ symbol: 'TSLA', market: 1 }] } });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();
    const save = within(form).getByRole('button', { name: '高ボラティリティ銘柄の設定を保存' });
    await user.type(within(form).getByLabelText(reasonLabel), '見直し');

    // 下げる: 確認なしで保存できる。
    await user.clear(within(form).getByLabelText(ratioLabel));
    await user.type(within(form).getByLabelText(ratioLabel), '3');
    expect(within(form).queryByText(/統制を緩めます/)).toBeNull();
    expect(save).toBeEnabled();

    // 上げる: 確認するまで保存できない。
    await user.clear(within(form).getByLabelText(ratioLabel));
    await user.type(within(form).getByLabelText(ratioLabel), '10');
    expect(within(form).getByText(/区分の上限を 5% から 10% へ引き上げ/)).toBeInTheDocument();
    expect(save).toBeDisabled();
    await user.click(within(form).getByRole('checkbox', { name: '上記の緩める変更を確認しました' }));
    expect(save).toBeEnabled();

    // さらに指定を外す: 緩める変更が増えたので確認は外れる。
    await user.click(within(form).getByRole('button', { name: 'TSLA（米国）の明示指定を外す' }));
    expect(within(form).getByText(/明示指定を外す（TSLA（米国））/)).toBeInTheDocument();
    expect(save).toBeDisabled();
  });

  // T-10-2601（否定形）: サーバの拒否（400）は詳細つきで表示し、自動再試行しない。
  it('T-10-2601 サーバの 400 は詳細を表示し、自動再試行しない', async () => {
    mockBff({ put: 'validation' });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();

    await user.clear(within(form).getByLabelText(ratioLabel));
    await user.type(within(form).getByLabelText(ratioLabel), '4');
    await user.type(within(form).getByLabelText(reasonLabel), '検証');
    await waitEnabled(form);
    await user.click(within(form).getByRole('button', { name: '高ボラティリティ銘柄の設定を保存' }));

    expect(await within(form).findByText(/入力内容に誤りがあります。/)).toHaveTextContent('0.25（区分外の上限）');
    expect(putCalls()).toHaveLength(1);
  });

  // 🔴 T-10-2602（否定形）: 監視銘柄を取得できないときは照合できないことを明示し、手入力の追加には確認を求める（黙って通さない）。
  it('T-10-2602 監視銘柄を取得できなければ照合できないと示し、手入力に確認を求める', async () => {
    mockBff({ watchlist: 'error' });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();

    expect(await within(form).findByText(/監視銘柄を取得できないため/)).toBeInTheDocument();
    await user.type(within(form).getByLabelText('銘柄コード（手入力）'), 'TSLA');
    expect(within(form).getByText(/TSLA（日本）: 監視銘柄と照合できません/)).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: '手入力の銘柄を追加' })).toBeDisabled();
  });

  // 🔴 T-10-2604（否定形）: 監視銘柄の取得中は、保存済みの行に照合の結果（警告）を出さず「照合中」と出す。
  // 手入力は照合の結果が出るまで追加できず、警告も出さない（取得前に「照合できません」を点滅させない・確認なしで通る窓を作らない）。
  it('T-10-2604 監視銘柄の取得中は「照合中」と出し、警告を点滅させず手入力も足させない', async () => {
    mockBff({
      watchlist: 'pending',
      highVolatility: { maxOrderAmountRatio: 0.05, designatedSymbols: [{ symbol: 'TSL', market: 1 }] },
    });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();

    const table = within(form).getByRole('table', { name: '明示指定の銘柄の一覧' });
    expect(within(table).getByText('照合中')).toBeInTheDocument();
    expect(within(table).queryByText('監視銘柄にありません')).toBeNull();
    expect(within(table).queryByText('監視銘柄と照合できません')).toBeNull();
    expect(within(form).queryByText(/監視銘柄を取得できないため/)).toBeNull();

    await user.type(within(form).getByLabelText('銘柄コード（手入力）'), 'TSLA');
    expect(within(form).queryByText(/監視銘柄と照合できません/)).toBeNull();
    expect(within(form).getByRole('button', { name: '手入力の銘柄を追加' })).toBeDisabled();
  });

  // 🔴 T-10-2605（否定形）: 手入力の確認の印は「確かめた銘柄・市場」に紐づく。印を付けた後に銘柄コードや市場を書き換えたら
  // 印は外れ、書き換えた先の誤記を確認なしで足せない。
  it('T-10-2605 確認の印を付けた後に銘柄や市場を書き換えると印が外れて追加できない', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await hvForm();
    await within(form).findByRole('option', { name: 'TSLA（米国）' });

    await user.selectOptions(within(form).getByLabelText('市場（手入力）'), '1');
    await user.type(within(form).getByLabelText('銘柄コード（手入力）'), 'TSL');
    await user.click(within(form).getByRole('checkbox', { name: /綴りと市場を確かめた/ }));
    const add = within(form).getByRole('button', { name: '手入力の銘柄を追加' });
    expect(add).toBeEnabled();

    // 銘柄コードを書き換える → 印が外れる。
    await user.type(within(form).getByLabelText('銘柄コード（手入力）'), 'X');
    expect(within(form).getByRole('checkbox', { name: /綴りと市場を確かめた/ })).not.toBeChecked();
    expect(add).toBeDisabled();

    // 印を付け直してから市場を書き換える → 印が外れる。
    await user.click(within(form).getByRole('checkbox', { name: /綴りと市場を確かめた/ }));
    expect(add).toBeEnabled();
    await user.selectOptions(within(form).getByLabelText('市場（手入力）'), '0');
    expect(within(form).getByRole('checkbox', { name: /綴りと市場を確かめた/ })).not.toBeChecked();
    expect(add).toBeDisabled();
  });
});
