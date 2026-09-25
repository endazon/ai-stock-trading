import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { renderWithProviders } from '@ai-stock-trading/testing/renderWithProviders';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// T-10-991〜T-10-994, SC-02, FR-10, FR-12, FR-11, UC-06, ADR-0040 決定1・決定3, #823, IADR-0342 決定2, IADR-0422 決定2:
// **損切りの実行機構を SC-02 だけで選べること**と、**実弾では S0 以外を選べない表示がサーバの拒否と整合すること**。
//
// 計画（ADR-0040 決定1）: 「本番（moomoo REAL）では S0 以外を選べない」。サーバは 2 方向で 400 を返す
// （発注先が REAL の間の S0 以外／S0 以外のまま REAL へ切替）。画面は同じ条件で送信する前に止める。
// **片方だけを固定すると、画面は選ばせるのにサーバが 400 を返す（またはその逆）へ黙って崩れる。**
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { RiskSettingsPage } from './RiskSettingsPage';
import {
  CONTRACT_RISK_SETTINGS,
  CONTRACT_RISK_STATUS,
  cloneContract,
} from '@ai-stock-trading/testing/riskContractFixtures';
import { CONTRACT_MONITOR_SETTINGS } from '@ai-stock-trading/testing/monitorContractFixtures';
import {
  BROKER_PROVIDER_INTERNAL_PAPER,
  BROKER_PROVIDER_MOOMOO_REAL,
  BROKER_PROVIDER_MOOMOO_SIMULATE,
  isStopLossMethodPermittedOn,
} from '@ai-stock-trading/lib/risk/contracts';

const STATUS = cloneContract(CONTRACT_RISK_STATUS);
const MONITOR = cloneContract(CONTRACT_MONITOR_SETTINGS);

const S0 = 0;
const S1 = 1;
const S2 = 2;
const S3 = 3;

interface Options {
  stopLossMethod?: number;
  brokerProvider?: number;
  put?: 'ok' | 'validation';
  history?: unknown[];
}

function mockBff(options: Options = {}) {
  const {
    stopLossMethod = S0,
    brokerProvider = BROKER_PROVIDER_MOOMOO_SIMULATE,
    put = 'ok',
    history = [],
  } = options;
  const settings = { ...cloneContract(CONTRACT_RISK_SETTINGS), stopLossMethod, brokerProvider };
  mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
    if (path === '/risk-controls/settings/history') return history;
    if (path === '/risk-controls/status') return { ...STATUS, stopLossMethod, brokerProvider };
    if (path === '/monitor/watchlist') return [];
    if (path === '/monitor/watchlist/history') return [];
    if (path === '/monitor/settings') return MONITOR;
    if (path === '/monitor/settings/history') return [];
    if (path === '/risk-controls/settings/stop-loss-method' && req?.method === 'PUT') {
      if (put === 'validation') {
        throw new ApiError('validation', '入力内容に誤りがあります。', 400, [
          '発注先が実弾（moomoo REAL）の間は、損切りの実行機構を S0（ブローカー側逆指値）以外にできません。',
        ]);
      }
      return settings;
    }
    return settings;
  });
}

async function methodForm() {
  return screen.findByRole('form', { name: '損切りの実行機構の変更' });
}

function stopLossPuts() {
  return mocks.apiFetch.mock.calls.filter(
    (call) => call[0] === '/risk-controls/settings/stop-loss-method' && call[1]?.method === 'PUT',
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-02 損切りの実行機構の選択（#823）', () => {
  // T-10-991: 画面だけで選べる。
  it('現在の手法と 4 つの手法を表示名と挙動の説明つきで出す', async () => {
    mockBff({ stopLossMethod: S2 });
    renderWithProviders(<RiskSettingsPage />);
    const form = await methodForm();

    expect(within(form).getByText(/現在の手法:/)).toHaveTextContent('S2 逆指値なしの建玉を許容');
    const radios = within(form).getAllByRole('radio');
    expect(radios).toHaveLength(4);
    expect(within(form).getByRole('radio', { name: 'S0 ブローカー側逆指値（既定）' })).not.toBeChecked();
    expect(within(form).getByRole('radio', { name: 'S2 逆指値なしの建玉を許容' })).toBeChecked();
    // 挙動の説明（S2 は誰も決済しない）が選択肢に結び付いている。
    expect(within(form).getByRole('radio', { name: 'S2 逆指値なしの建玉を許容' })).toHaveAccessibleDescription(
      /システムもブローカーも決済しません/,
    );
  });

  it('手法と理由を入れると PUT /risk-controls/settings/stop-loss-method に {method, reason} を送る', async () => {
    mockBff({ stopLossMethod: S0 });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await methodForm();

    await user.click(within(form).getByRole('radio', { name: 'S2 逆指値なしの建玉を許容' }));
    await user.type(within(form).getByLabelText('手法の変更理由'), 'SIMULATE で建玉を観測する');
    expect(within(form).getByLabelText('手法の変更理由')).toHaveValue('SIMULATE で建玉を観測する');

    await waitFor(
      () => expect(within(form).getByRole('button', { name: '損切りの実行機構を保存' })).toBeEnabled(),
      { timeout: 5000 },
    );
    await user.click(within(form).getByRole('button', { name: '損切りの実行機構を保存' }));

    await waitFor(() => expect(stopLossPuts()).toHaveLength(1));
    expect(stopLossPuts()[0][1]).toEqual({
      method: 'PUT',
      json: { method: S2, reason: 'SIMULATE で建玉を観測する' },
    });
    expect(await within(form).findByText('損切りの実行機構を保存しました。')).toBeInTheDocument();
  });

  // 🔴 否定形: 理由が空・変更なしでは保存できない（監査のため理由必須・05_screens 設定変更の一般則）。
  it('理由が空・変更なしのあいだは保存できない', async () => {
    mockBff({ stopLossMethod: S0 });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await methodForm();
    const saveButton = () => within(form).getByRole('button', { name: '損切りの実行機構を保存' });

    // 変更なし＋理由なし。
    expect(saveButton()).toBeDisabled();
    // 理由だけ（変更なし）。
    await user.type(within(form).getByLabelText('手法の変更理由'), '確認');
    expect(saveButton()).toBeDisabled();
    // 手法を変えたが理由が空白だけ。
    await user.clear(within(form).getByLabelText('手法の変更理由'));
    await user.type(within(form).getByLabelText('手法の変更理由'), '   ');
    await user.click(within(form).getByRole('radio', { name: 'S1 ソフトウェア逆指値' }));
    expect(saveButton()).toBeDisabled();
    expect(stopLossPuts()).toHaveLength(0);
  });

  it('サーバの 400 は details を出し、自動再試行しない', async () => {
    mockBff({ stopLossMethod: S0, put: 'validation' });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await methodForm();

    await user.click(within(form).getByRole('radio', { name: 'S3 他のブローカー側注文種別' }));
    await user.type(within(form).getByLabelText('手法の変更理由'), '代替注文の拒否理由を記録する');
    await waitFor(() =>
      expect(within(form).getByRole('button', { name: '損切りの実行機構を保存' })).toBeEnabled(),
    );
    await user.click(within(form).getByRole('button', { name: '損切りの実行機構を保存' }));

    const alert = await within(form).findByRole('alert');
    expect(alert).toHaveTextContent('入力内容に誤りがあります。');
    expect(alert).toHaveTextContent('S0（ブローカー側逆指値）以外にできません');
    expect(stopLossPuts()).toHaveLength(1);
  });

  // 🔴 T-10-992（否定形）: 実弾（moomoo REAL）の間は S1〜S3 を選べない。理由を出す。S0 は選べる。
  it('発注先が moomoo REAL の間は S1〜S3 を無効化し、理由を出す', async () => {
    mockBff({ stopLossMethod: S0, brokerProvider: BROKER_PROVIDER_MOOMOO_REAL });
    renderWithProviders(<RiskSettingsPage />);
    const form = await methodForm();

    expect(within(form).getByRole('radio', { name: 'S0 ブローカー側逆指値（既定）' })).toBeEnabled();
    expect(within(form).getByRole('radio', { name: 'S1 ソフトウェア逆指値' })).toBeDisabled();
    expect(within(form).getByRole('radio', { name: 'S2 逆指値なしの建玉を許容' })).toBeDisabled();
    expect(within(form).getByRole('radio', { name: 'S3 他のブローカー側注文種別' })).toBeDisabled();
    expect(
      within(form).getByText(/発注先が実弾（moomoo REAL）の間は、S0（ブローカー側逆指値）以外を選べません/),
    ).toBeInTheDocument();
  });

  it.each([
    ['内蔵 paper', BROKER_PROVIDER_INTERNAL_PAPER],
    ['moomoo SIMULATE', BROKER_PROVIDER_MOOMOO_SIMULATE],
  ])('発注先が %s の間は 4 つとも選べ、実弾の理由は出さない', async (_name, provider) => {
    mockBff({ stopLossMethod: S0, brokerProvider: provider });
    renderWithProviders(<RiskSettingsPage />);
    const form = await methodForm();

    for (const radio of within(form).getAllByRole('radio')) {
      expect(radio).toBeEnabled();
    }
    expect(within(form).queryByText(/以外を選べません/)).not.toBeInTheDocument();
  });

  // 🔴 T-10-992: 画面の判定式はサーバの `StopLossMethodChange.IsPermittedOn` と同じ（S0 か、発注先が実弾でない）。
  // 全組み合わせ（手法 4 × 発注先 3）を表で固定する——片方だけ変えると画面とサーバの拒否が食い違う。
  it.each([
    [S0, BROKER_PROVIDER_INTERNAL_PAPER, true],
    [S0, BROKER_PROVIDER_MOOMOO_REAL, true],
    [S0, BROKER_PROVIDER_MOOMOO_SIMULATE, true],
    [S1, BROKER_PROVIDER_INTERNAL_PAPER, true],
    [S1, BROKER_PROVIDER_MOOMOO_REAL, false],
    [S1, BROKER_PROVIDER_MOOMOO_SIMULATE, true],
    [S2, BROKER_PROVIDER_INTERNAL_PAPER, true],
    [S2, BROKER_PROVIDER_MOOMOO_REAL, false],
    [S2, BROKER_PROVIDER_MOOMOO_SIMULATE, true],
    [S3, BROKER_PROVIDER_INTERNAL_PAPER, true],
    [S3, BROKER_PROVIDER_MOOMOO_REAL, false],
    [S3, BROKER_PROVIDER_MOOMOO_SIMULATE, true],
  ])('手法 %i・発注先 %i を選べるか = %s（サーバの受理条件と同じ）', (method, provider, permitted) => {
    expect(isStopLossMethodPermittedOn(method, provider)).toBe(permitted);
  });

  it('発注先フォームの直後に置かれている', async () => {
    mockBff();
    renderWithProviders(<RiskSettingsPage />);
    await methodForm();

    const html = document.body.innerHTML;
    const providerIndex = html.indexOf('発注先（変更）');
    const methodIndex = html.indexOf('損切りの実行機構（変更）');
    expect(providerIndex).toBeGreaterThanOrEqual(0);
    expect(methodIndex).toBeGreaterThan(providerIndex);
  });
});

describe('SC-02 発注先フォーム: S0 以外のまま実弾へは切り替えられない（#823）', () => {
  // 🔴 T-10-993（否定形）: サーバの `StopLossMethodNotBrokerStop` と同じ条件で、送信する前に止めて対処を示す。
  it('手法が S0 以外なら、moomoo REAL を選ぶと理由つきの警告を出し切替の確認へ進めない', async () => {
    mockBff({ stopLossMethod: S2, brokerProvider: BROKER_PROVIDER_MOOMOO_SIMULATE });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '発注先の変更' });

    await user.click(within(form).getByRole('radio', { name: /moomoo REAL/ }));
    await user.type(within(form).getByLabelText('変更理由'), '実弾へ');

    const alerts = within(form).getAllByRole('alert');
    expect(
      alerts.some((a) =>
        /損切りの実行機構がS2 逆指値なしの建玉を許容のため、実弾（moomoo REAL）へ切り替えられません。先に損切りの実行機構を S0/.test(
          a.textContent ?? '',
        ),
      ),
    ).toBe(true);
    expect(within(form).getByRole('button', { name: '実弾への切替を確認する' })).toBeDisabled();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('手法が S0 なら従来どおり切替の確認へ進める（警告は出さない）', async () => {
    mockBff({ stopLossMethod: S0, brokerProvider: BROKER_PROVIDER_MOOMOO_SIMULATE });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '発注先の変更' });

    await user.click(within(form).getByRole('radio', { name: /moomoo REAL/ }));
    await user.type(within(form).getByLabelText('変更理由'), '実弾へ');

    expect(within(form).queryByText(/切り替えられません/)).not.toBeInTheDocument();
    expect(within(form).getByRole('button', { name: '実弾への切替を確認する' })).toBeEnabled();
  });

  it('手法が S0 以外でも、実弾以外への切替は妨げない', async () => {
    mockBff({ stopLossMethod: S1, brokerProvider: BROKER_PROVIDER_MOOMOO_SIMULATE });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '発注先の変更' });

    await user.click(within(form).getByRole('radio', { name: /内蔵 paper/ }));
    await user.type(within(form).getByLabelText('変更理由'), 'デバッグ');

    expect(within(form).queryByText(/切り替えられません/)).not.toBeInTheDocument();
    expect(within(form).getByRole('button', { name: '保存' })).toBeEnabled();
  });
});

describe('SC-02 変更履歴: 損切りの実行機構の変更（#823）', () => {
  // T-10-994: 種別 9（StopLossMethodChanged）を「不明(9)」と出さない。
  it('種別 9 を「損切りの実行機構」と表示する', async () => {
    mockBff({
      history: [
        {
          actor: 'owner',
          changeType: 9,
          reason: 'SIMULATE で建玉を観測する',
          changedAt: '2026-09-24T01:00:00Z',
          before: 'BrokerStopOrder',
          after: 'NoProtectiveStop',
        },
      ],
    });
    renderWithProviders(<RiskSettingsPage />);

    const table = await screen.findByRole('table', { name: '変更履歴' });
    expect(within(table).getByText('損切りの実行機構')).toBeInTheDocument();
    expect(within(table).queryByText('不明(9)')).not.toBeInTheDocument();
  });
});
