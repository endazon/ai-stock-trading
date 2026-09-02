import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProviders } from '../../testing/renderWithProviders';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-01, FR-17, UC-06, IADR-0080: 設定画面（全体前提条件の閲覧/変更）の振る舞い。
// データ取得・更新は apiFetch をモックし、実 BFF 疎通に依存しない。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { SettingsPage } from './SettingsPage';
// #424, IADR-0162: §1 の「供給が無い値」の表示規約で使う文言。**monitor の契約フィクスチャは
// 意図的に import しない** —— 本画面は `/monitor/*` を 1 度も呼ばないことを下の否定形テストで
// 固定しており（#423）、テスト側にだけ monitor の口を残すと「呼ばない」の根拠が薄まる。
import { METRIC_NOT_SUPPLIED_TEXT } from '@ai-stock-trading/lib/risk/contracts';

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
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByRole('heading', { name: '設定' })).toBeInTheDocument();
    // 読み込み完了（フォーム描画）を待ってから現在値を検証する（見出しは読み込み中も描画されるため待受にしない）。
    expect(await screen.findByText(/現在のバージョン:\s*3/)).toBeInTheDocument();
    // 譲渡益税率が現在値として入力に反映される。
    expect(screen.getByLabelText('譲渡益税率')).toHaveValue(0.20315);
  });

  it('lists change history (newest first) and degrades when history fails', async () => {
    renderWithProviders(<SettingsPage />);
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
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByText('変更履歴は利用できません。')).toBeInTheDocument();
  });

  it('requires a reason before saving (save disabled until reason entered)', async () => {
    const user = userEvent.setup();
    renderWithProviders(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });
    const save = screen.getByRole('button', { name: '保存' });
    expect(save).toBeDisabled();
    await user.type(screen.getByLabelText('変更理由'), '税率調整');
    expect(save).toBeEnabled();
  });

  it('disables save and warns when a numeric field is empty or non-numeric', async () => {
    const user = userEvent.setup();
    renderWithProviders(<SettingsPage />);
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
    renderWithProviders(<SettingsPage />);
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
    renderWithProviders(<SettingsPage />);
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
      if (path === '/assumptions' && req?.method === 'PUT') throw new ApiError('conflict', '競合', 409);
      return SAMPLE;
    });
    renderWithProviders(<SettingsPage />);
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
      if (path === '/assumptions' && req?.method === 'PUT')
        throw new ApiError('validation', '入力エラー', 400, ['理由は必須です']);
      return SAMPLE;
    });
    renderWithProviders(<SettingsPage />);
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
      return { ...SAMPLE, version: 0, isResolved: false };
    });
    renderWithProviders(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });

    const notice = screen.getByText(/全体前提条件を/);
    expect(notice).toHaveTextContent(METRIC_NOT_SUPPLIED_TEXT);
    // **否定形**: 「—」や「0」で「値が無い」ように弱めない。表示中の値が既定値であることを明示する。
    expect(notice).not.toHaveTextContent('全体前提条件を—');
    expect(notice).toHaveTextContent(/組み込みの既定値であり、実際に適用されている値ではありません/);
  });

  it('does not warn about supply when the server declares the assumptions resolved', async () => {
    // 逆方向の否定形。供給があるのに未供給の警告を出すと、警告が常時出て誰も読まなくなる。
    renderWithProviders(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });

    expect(screen.queryByText(/全体前提条件を/)).not.toBeInTheDocument();
  });
});

// SC-01, FR-13, #423, IADR-0164 決定1: **§2「収集パラメータ」の廃止**（2026-08-07 の利用者裁定）。
//
//   収集間隔 … **画面から変更しない。起動時構成とする**（質問票 第 13 回 Q11・案 A）
//   変動閾値 … **SC-02 へ移す**（同 Q12・案 B。権威は MarketMonitorService であり監視銘柄と同じ由来である）
//
// **本節はすべて否定形である。** 「実装していない」と「実装してはならない」は別であり、
// 前者のままにすると次に画面を触る者が「未実装の項目」として素直に実装してしまう。
// 裁定に反する実装が「機能追加」の顔をして入り込む経路を、ここで赤くする。
describe('SC-01 §2 の廃止（#423）', () => {
  it('収集パラメータの節そのものが存在しない', async () => {
    renderWithProviders(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });

    expect(screen.queryByText(/収集パラメータ/)).not.toBeInTheDocument();
    expect(screen.queryByRole('form', { name: /収集パラメータ/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('form', { name: /変動閾値/ })).not.toBeInTheDocument();
  });

  it('変動閾値の入力欄・保存操作が存在しない（SC-02 へ移管済み）', async () => {
    renderWithProviders(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });

    expect(screen.queryByLabelText(/変動閾値/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /変動閾値/ })).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/クールダウン/)).not.toBeInTheDocument();
  });

  it('収集間隔の入力欄が存在しない', async () => {
    renderWithProviders(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });

    expect(screen.queryByLabelText(/収集間隔/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/ポーリング/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /収集間隔/ })).not.toBeInTheDocument();
  });

  // **MarketMonitorService を一切呼ばない。** 呼んでいれば、節を隠しただけで実体が残っている。
  it('MarketMonitorService（/monitor/*）を 1 度も呼ばない', async () => {
    renderWithProviders(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });

    const monitorCalls = mocks.apiFetch.mock.calls.filter(
      ([path]) => typeof path === 'string' && path.startsWith('/monitor'),
    );
    expect(monitorCalls).toHaveLength(0);
  });

  // 「変更しないことが裁定である」ことを画面に書く。入力欄を消すだけでは
  // 「未実装なので実装しよう」と読まれる余地が残る。
  it('収集間隔は起動時構成である旨を画面に明記する', async () => {
    renderWithProviders(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });

    const note = screen.getByText(/収集間隔/);
    expect(note).toHaveTextContent('本画面からも API からも変更しません');
    expect(note).toHaveTextContent('起動時の構成値');
  });

  // 移管先（SC-02）への導線を示す。「無くなった」だけでは利用者が探せない。
  it('市場監視パラメータが SC-02 にある旨を案内する', async () => {
    renderWithProviders(<SettingsPage />);
    await screen.findByRole('form', { name: '全体前提条件の変更' });

    expect(screen.getByText(/市場監視パラメータ（変動閾値・クールダウン）/)).toBeInTheDocument();
  });
});
