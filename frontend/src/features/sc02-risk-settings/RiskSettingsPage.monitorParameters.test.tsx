import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProviders } from '../../testing/renderWithProviders';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-02, FR-03, FR-13, FR-11, UC-06, #423, IADR-0164 決定2:
// **市場監視パラメータ（変動閾値・クールダウン）が SC-02 へ移ったことの退行防止。**
//
// 2026-08-07 の利用者裁定（質問票 第 13 回 Q11・Q12）:
//   - SC-01 §2「収集パラメータ」を廃止する
//   - **変動閾値・クールダウンは SC-02 へ移す**（権威は MarketMonitorService）
//   - **収集間隔は画面から変更しない。起動時構成とする**
//
// **本テスト群の主眼は 3 つである。**
//   1. 変動閾値・クールダウンが SC-02 で変更できること（移管が成立していること）
//   2. **収集間隔の入力欄が SC-02 にも作られていないこと**（否定形・移管先で復活させない）
//   3. 値域と理由必須が画面で即時提示されること（実効はサーバ側）
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { RiskSettingsPage } from './RiskSettingsPage';
import { CONTRACT_RISK_SETTINGS, CONTRACT_RISK_STATUS, cloneContract } from '../risk/contractFixtures';
import {
  CONTRACT_MONITOR_SETTINGS,
  CONTRACT_MONITOR_SETTINGS_HISTORY,
} from '../monitor/contractFixtures';

const SETTINGS = cloneContract(CONTRACT_RISK_SETTINGS);
const STATUS = cloneContract(CONTRACT_RISK_STATUS);
const MONITOR = cloneContract(CONTRACT_MONITOR_SETTINGS);

type MonitorOverrides = {
  settings?: 'ok' | 'fail';
  history?: 'ok' | 'fail';
  put?: 'ok' | 'conflict';
};

function mockBff(overrides: MonitorOverrides = {}) {
  const { settings = 'ok', history = 'ok', put = 'ok' } = overrides;
  mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
    if (path === '/risk-controls/settings/history') return [];
    if (path === '/risk-controls/status') return STATUS;
    if (path === '/monitor/watchlist') return [];
    if (path === '/monitor/watchlist/history') return [];
    if (path === '/monitor/settings') {
      if (settings === 'fail') throw new ApiError('server', 'boom', 500);
      return MONITOR;
    }
    if (path === '/monitor/settings/history') {
      if (history === 'fail') throw new ApiError('server', 'boom', 500);
      return CONTRACT_MONITOR_SETTINGS_HISTORY;
    }
    if (path.startsWith('/monitor/settings/') && req?.method === 'PUT') {
      if (put === 'conflict') throw new ApiError('conflict', '競合が発生しました。', 409);
      return MONITOR;
    }
    return SETTINGS;
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-02 市場監視パラメータ（#423・SC-01 §2 からの移管）', () => {
  it('変動閾値とクールダウンを SC-02 で閲覧できる', async () => {
    mockBff();
    renderWithProviders(<RiskSettingsPage />);

    // 現在値（比率 0.03 → 3%／TimeSpan "00:15:00" → 0.25 時間）が表示される。
    expect(await screen.findByLabelText('変動閾値 %')).toHaveValue(3);
    expect(screen.getByLabelText('クールダウン 時間')).toHaveValue(0.25);
  });

  // ------------------------------------------------------------------
  // **否定形（裁定の中核）**: 収集間隔は移管先にも作らない
  // ------------------------------------------------------------------

  // 裁定（Q11・案 A）は「収集間隔は画面から変更しない。起動時構成とする」である。
  // SC-01 §2 を消したうえで SC-02 に作り直したら、裁定に反したことになる。
  it('収集間隔の入力欄は SC-02 に存在しない', async () => {
    mockBff();
    renderWithProviders(<RiskSettingsPage />);
    await screen.findByLabelText('変動閾値 %');

    expect(screen.queryByLabelText(/収集間隔/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/ポーリング/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/巡回間隔/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /収集間隔/ })).not.toBeInTheDocument();
  });

  // ------------------------------------------------------------------
  // 変動閾値: 理由必須・値域・ワイヤの単位（比率）
  // ------------------------------------------------------------------

  it('変動閾値は理由が無いと保存できない', async () => {
    mockBff();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '変動閾値の変更' });

    expect(within(form).getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();
  });

  it('変動閾値は百分率で入力し比率で送る', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '変動閾値の変更' });

    await user.clear(within(form).getByLabelText('変動閾値 %'));
    await user.type(within(form).getByLabelText('変動閾値 %'), '5');
    await user.type(within(form).getByLabelText('変動閾値の変更理由'), 'ボラティリティ上昇');
    await user.click(within(form).getByRole('button', { name: '変動閾値を保存' }));

    // **ワイヤは比率**（5% → 0.05）。百分率のまま送ると閾値が 100 倍になる。
    expect(mocks.apiFetch).toHaveBeenCalledWith('/monitor/settings/movement-threshold', {
      method: 'PUT',
      json: { movementThresholdRatio: 0.05, reason: 'ボラティリティ上昇' },
    });
    expect(await within(form).findByText('変動閾値を保存しました。')).toBeInTheDocument();
  });

  // **0 を許してはならない。** 0 ではあらゆる変動が閾値超過となり LLM 費用が暴走する。
  it.each(['0', '-1', '51'])('値域外の変動閾値（%s）は保存できずサーバへ送らない', async (value) => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '変動閾値の変更' });

    await user.type(within(form).getByLabelText('変動閾値の変更理由'), '検証');
    await user.clear(within(form).getByLabelText('変動閾値 %'));
    await user.type(within(form).getByLabelText('変動閾値 %'), value);

    expect(within(form).getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();
    expect(mocks.apiFetch).not.toHaveBeenCalledWith(
      '/monitor/settings/movement-threshold',
      expect.anything(),
    );
  });

  it('境界値（0.01% と 50%）は保存できる', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '変動閾値の変更' });

    await user.type(within(form).getByLabelText('変動閾値の変更理由'), '境界の検証');
    await user.clear(within(form).getByLabelText('変動閾値 %'));
    await user.type(within(form).getByLabelText('変動閾値 %'), '50');

    expect(within(form).getByRole('button', { name: '変動閾値を保存' })).toBeEnabled();
  });

  // ------------------------------------------------------------------
  // クールダウン: 理由必須・値域・ワイヤの単位（TimeSpan 文字列）
  // ------------------------------------------------------------------

  it('クールダウンは理由が無いと保存できない', async () => {
    mockBff();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: 'クールダウンの変更' });

    expect(within(form).getByRole('button', { name: 'クールダウンを保存' })).toBeDisabled();
  });

  it('クールダウンは時間で入力し TimeSpan 文字列で送る', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: 'クールダウンの変更' });

    await user.clear(within(form).getByLabelText('クールダウン 時間'));
    await user.type(within(form).getByLabelText('クールダウン 時間'), '2');
    await user.type(within(form).getByLabelText('クールダウンの変更理由'), '再トリガー抑制を延ばす');
    await user.click(within(form).getByRole('button', { name: 'クールダウンを保存' }));

    // **ワイヤは TimeSpan 文字列**。数値のまま送るとサーバが解釈できない。
    expect(mocks.apiFetch).toHaveBeenCalledWith('/monitor/settings/cooldown', {
      method: 'PUT',
      json: { cooldown: '02:00:00', reason: '再トリガー抑制を延ばす' },
    });
  });

  // 0（抑制なし）は正当な設定である。変動閾値の 0 とは非対称であることを固定する。
  it('クールダウンの 0（抑制なし）は保存できる', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: 'クールダウンの変更' });

    await user.clear(within(form).getByLabelText('クールダウン 時間'));
    await user.type(within(form).getByLabelText('クールダウン 時間'), '0');
    await user.type(within(form).getByLabelText('クールダウンの変更理由'), '抑制なしで検証する');

    expect(within(form).getByRole('button', { name: 'クールダウンを保存' })).toBeEnabled();
  });

  it.each(['-1', '25', '48'])(
    '値域外のクールダウン（%s）は保存できずサーバへ送らない', async (value) => {
      mockBff();
      const user = userEvent.setup();
      renderWithProviders(<RiskSettingsPage />);
      const form = await screen.findByRole('form', { name: 'クールダウンの変更' });

      await user.type(within(form).getByLabelText('クールダウンの変更理由'), '検証');
      await user.clear(within(form).getByLabelText('クールダウン 時間'));
      await user.type(within(form).getByLabelText('クールダウン 時間'), value);

      expect(within(form).getByRole('button', { name: 'クールダウンを保存' })).toBeDisabled();
      expect(mocks.apiFetch).not.toHaveBeenCalledWith('/monitor/settings/cooldown', expect.anything());
    });

  it('クールダウンの 24 時間ちょうどは保存できる（上限は含む）', async () => {
    mockBff();
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: 'クールダウンの変更' });

    await user.type(within(form).getByLabelText('クールダウンの変更理由'), '境界の検証');
    await user.clear(within(form).getByLabelText('クールダウン 時間'));
    await user.type(within(form).getByLabelText('クールダウン 時間'), '24');

    expect(within(form).getByRole('button', { name: 'クールダウンを保存' })).toBeEnabled();
  });

  // ------------------------------------------------------------------
  // 履歴と縮退
  // ------------------------------------------------------------------

  it('市場監視パラメータの変更履歴を表示する（監視銘柄の履歴とは別の表）', async () => {
    mockBff();
    renderWithProviders(<RiskSettingsPage />);

    const table = await screen.findByRole('table', { name: '市場監視パラメータの変更履歴' });
    // 変動閾値・クールダウンの変更だけを出す（監視銘柄の追加・削除は隣の節の関心）。
    expect(within(table).queryByText('追加')).not.toBeInTheDocument();
  });

  it('楽観排他の競合（409）は自動再試行せず再読込を促す', async () => {
    mockBff({ put: 'conflict' });
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '変動閾値の変更' });

    await user.type(within(form).getByLabelText('変動閾値の変更理由'), '競合検証');
    await user.click(within(form).getByRole('button', { name: '変動閾値を保存' }));

    expect(await within(form).findByText(/競合が発生しました/)).toBeInTheDocument();
    const putCalls = mocks.apiFetch.mock.calls.filter(
      ([path]) => path === '/monitor/settings/movement-threshold',
    );
    expect(putCalls).toHaveLength(1); // 自動再試行しない
  });

  // 別サービス（MarketMonitorService）の障害をリスク設定の障害にしない（fail-safe な疎結合）。
  it('市場監視パラメータの取得失敗は当該領域のみ縮退する', async () => {
    mockBff({ settings: 'fail', history: 'fail' });
    renderWithProviders(<RiskSettingsPage />);

    // #424, IADR-0162: 取得失敗も**供給が無い**状態の 1 つであり、規約の文言で示す。
    // 文言は `<strong>` を挟んで複数のテキストノードへ分かれるため、要素の textContent で検証する。
    // **リテラルで固定する**（定数を参照すると定数を書き換えたときにテストが追随し退行を検知できない）。
    // `role="alert"` のうち本節のものを取る。素の文字列一致では `確認中…`（`role="status"`）を、
    // 素の `findByRole('alert')` では `paper` 稼働中の警告バナーを拾ってしまう。
    const alerts = await screen.findAllByRole('alert');
    const unavailable = alerts.find((el) => el.textContent?.startsWith('市場監視パラメータを'));
    expect(unavailable).toBeDefined();
    expect(unavailable).toHaveTextContent('取得できていません（供給元がありません）');
    expect(unavailable).toHaveTextContent('値が無いのではなく、確認できていません。');
    // **否定形**: 「—」や「0」で「値が無い」ように弱めない。
    expect(unavailable).not.toHaveTextContent('市場監視パラメータを—');
    // リスク上限（RiskManagementService 由来）は独立して表示される。
    expect(screen.getByRole('form', { name: 'リスク上限の変更' })).toBeInTheDocument();
  });
});
