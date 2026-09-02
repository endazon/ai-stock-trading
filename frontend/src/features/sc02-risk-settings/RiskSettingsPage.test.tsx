import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProviders } from '../../testing/renderWithProviders';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-02, FR-13, FR-19, FR-20, UC-06, IADR-0084: リスク設定画面（リスク上限の閲覧/変更）の振る舞い。
// データ取得・更新は apiFetch をモックし、実 BFF 疎通に依存しない。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { RiskSettingsPage } from './RiskSettingsPage';
import {
  CONTRACT_RISK_SETTINGS,
  CONTRACT_RISK_STATUS,
  CONTRACT_SETTINGS_HISTORY,
  cloneContract,
} from '@ai-stock-trading/testing/riskContractFixtures';
import { CONTRACT_MONITOR_SETTINGS } from '@ai-stock-trading/testing/monitorContractFixtures';
import type {
  RiskManagementSettings,
  RiskStatusView,
  SettingsChangeEntry,
} from '@ai-stock-trading/lib/risk/contracts';
import { METRIC_NOT_SUPPLIED_TEXT } from '@ai-stock-trading/lib/risk/contracts';

// #389, IADR-0146: モックは**バックエンドの実応答**（契約フィクスチャ）を土台に作り、テストが要る差分だけ
// 上書きする。インラインの literal で書くと「フロントが思っている形をフロント自身が検証する」構造へ戻り、
// バックエンドの改名（#329 / #333）を緑のまま通してしまう。
const SETTINGS: RiskManagementSettings = {
  ...cloneContract(CONTRACT_RISK_SETTINGS),
  guard: {
    ...cloneContract(CONTRACT_RISK_SETTINGS.guard),
    enabledProductTypes: [0, 1],
    bannedSymbols: [{ symbol: '9999', market: 0, reason: '監視対象外', registeredOn: '2026-07-10' }],
  },
  limits: {
    ...cloneContract(CONTRACT_RISK_SETTINGS.limits),
    maxOpenPositions: 5,
    losingStreakThreshold: 3,
  },
  // 段階は Stage 2（最小実弾）／既定発注先 moomoo REAL の組み合わせで表示を確認する。
  stage: { ...cloneContract(CONTRACT_RISK_SETTINGS.stage), stage: 2, mode: 1 },
};
const HISTORY: SettingsChangeEntry[] = [
  { ...cloneContract(CONTRACT_SETTINGS_HISTORY[1]), changeType: 1, reason: '上限見直し' },
  { ...cloneContract(CONTRACT_SETTINGS_HISTORY[1]), changeType: 0, reason: 'ガード初期化' },
];
// #362, IADR-0151 決定4: 実額の併記に使う equity（`RiskStatusView.capital`）。実応答の値をそのまま使う。
const STATUS: RiskStatusView = cloneContract(CONTRACT_RISK_STATUS);
const EQUITY = STATUS.capital;

function mockDefault() {
  mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
    if (path === '/risk-controls/settings/history') return HISTORY;
    if (path === '/risk-controls/settings/limits' && req?.method === 'PUT') return SETTINGS;
    if (path === '/risk-controls/status') return STATUS;
    // 監視銘柄セクション（#196・別サービス）は独立ロードするため、本ページのテストでは空応答で満たす。
    if (path === '/monitor/watchlist') return [];
    if (path === '/monitor/watchlist/history') return [];
    // #423: 市場監視パラメータ（変動閾値・クールダウン）も別サービス由来で独立ロードする。
    if (path === '/monitor/settings') return CONTRACT_MONITOR_SETTINGS;
    if (path === '/monitor/settings/history') return [];
    return SETTINGS;
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mockDefault();
});

// #362, IADR-0151 決定1: 発注額上限のラベル（**百分率**であることを単位で明示した文言）。
// テストから参照して表記の齟齬を 1 か所に集める。
const LIMIT_RATIO_LABEL = '1注文発注額上限（equity 比） %';
const POSITIONS_LABEL = '保有建玉数上限 件';

// リスク上限フォーム（#186）。ガード変更フォーム（#188）と同名の要素があるため絞り込みに使う。
function limitsForm(): HTMLElement {
  return screen.getByRole('form', { name: 'リスク上限の変更' });
}

describe('RiskSettingsPage (SC-02, FR-13)', () => {
  it('renders current limits into the form', async () => {
    renderWithProviders(<RiskSettingsPage />);
    expect(await screen.findByRole('heading', { name: 'リスク設定' })).toBeInTheDocument();
    // 読み込み完了（フォーム描画）を待ってから現在値を検証する（見出しは読み込み中も描画されるため待受にしない）。
    // FR-10, #329, #389, #362: 発注額の上限は **equity 比**（実応答の 0.25）であり、画面には
    // **百分率（25）**で出る（IADR-0151 決定1）。旧キー（金額）を読んでいたときはここが undefined になり、
    // フォームには "undefined" が入っていた。
    expect(await screen.findByLabelText(LIMIT_RATIO_LABEL)).toHaveValue(25);
    expect(screen.getByLabelText(POSITIONS_LABEL)).toHaveValue(5);
  });

  it('inputs limits as percents with the unit shown, not as raw ratios', async () => {
    // #362, IADR-0151 決定1: 比率（0.25）ではなく百分率（25）で入力させる。比率入力で `25` と打つと
    // equity の 25 倍（統制の消滅）になるが、百分率入力で `0.25` と打っても 0.25%（安全側）で済む。
    // **単位は必ず画面に出す**（比率・百分率・金額の取り違えを目視で検出できるようにする）。
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: 'リスク上限の変更' });
    expect(within(form).getByLabelText('1日発注額上限（equity 比） %/日')).toHaveValue(150);
    expect(within(form).getByLabelText('日次損失上限（equity 比） %')).toHaveValue(2);
    expect(within(form).getByLabelText('連敗時サイズ縮小係数 倍')).toHaveValue(0.5);
    // 計画 §5・ADR-0016 決定9 は「保有銘柄数上限」の語を禁じている（建玉数と銘柄数は一致しない）。
    expect(within(form).queryByLabelText(/保有銘柄数上限/)).not.toBeInTheDocument();
    // #362 で保存が復旧したため、「保存は拒否される」注記は消えている。
    expect(within(form).queryByText(/保存はサーバに拒否されます/)).not.toBeInTheDocument();
  });

  it('shows the resolved amount at the current equity alongside each equity-ratio input', async () => {
    // #362, IADR-0151 決定4: 割合だけでは実効額を判断できない。**保存前の入力値に対する実額**は
    // サーバの解決済み実額（現在の設定由来）では表せないため、画面が `capital × 入力比率` を計算する。
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: 'リスク上限の変更' });
    const expected = new Intl.NumberFormat('ja-JP', { maximumFractionDigits: 0 });
    // 既定 25%（＝実応答の 0.25）の実額。
    expect(within(form).getByText(new RegExp(expected.format(EQUITY * 0.25)))).toBeInTheDocument();
    // 入力を変えると併記も追随する（保存前の値に対する実額であることの確認）。
    const input = within(form).getByLabelText(LIMIT_RATIO_LABEL);
    await user.clear(input);
    await user.type(input, '40');
    expect(within(form).getByText(new RegExp(expected.format(EQUITY * 0.4)))).toBeInTheDocument();
  });

  it('declares the resolved amount as not supplied (never a dash) when equity is unavailable', async () => {
    // SC-02, #424, IADR-0162 決定3: **未供給を「—」で描かない**（05_screens「供給が無い値の表示規約」）。
    // 併記できないことを黙って隠さないのは従来どおりだが、**「—」は「対象なし」の記号**であり、
    // equity が取れていない（＝判断材料が無い）ことを同じ見た目にすると 3 状態の区別が消える。
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/risk-controls/status') throw new ApiError('server', 'boom', 500);
      if (path === '/risk-controls/settings/history') return HISTORY;
      if (path === '/monitor/watchlist') return [];
      if (path === '/monitor/watchlist/history') return [];
      return SETTINGS;
    });
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: 'リスク上限の変更' });
    // equity 比の 5 項目すべてが未供給として明示される（一部だけ数字が残る中途半端な状態にしない）。
    expect(
      within(form).getAllByText(new RegExp(`現在の equity での実額: ${METRIC_NOT_SUPPLIED_TEXT}`)),
    ).toHaveLength(5);
    // **否定形**: 「—」へ落とさない（未供給と対象なしを混ぜない）。
    expect(within(form).queryByText(/現在の equity での実額: —/)).not.toBeInTheDocument();
    // 0 へも落とさない（供給が無いことを「上限 0」に見せない）。
    expect(within(form).queryByText(/現在の equity での実額: \$0/)).not.toBeInTheDocument();
    expect(within(form).getByText(new RegExp(`現在の equity（${METRIC_NOT_SUPPLIED_TEXT}）`))).toBeInTheDocument();
    // 供給が無い旨は警告として出す（統制の判断材料が無い状態である）。
    expect(within(form).getByText(/判断材料が無い状態/)).toBeInTheDocument();
  });

  it('shows the stage orderable cap as a ratio of total capital', async () => {
    // FR-20, #333, #389, IADR-0136: capitalCapRatio（総資金比）。#389 まで画面に一切出ておらず、
    // キー名のずれが描画結果に現れなかった。
    renderWithProviders(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const term = screen.getByText('段階の発注可能額（総資金比）');
    expect(term.nextElementSibling).toHaveTextContent(String(SETTINGS.stage.capitalCapRatio));
  });

  it('shows stage as read-only (stage change is via gate approval, not this screen)', async () => {
    renderWithProviders(<RiskSettingsPage />);
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
    renderWithProviders(<RiskSettingsPage />);
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
    renderWithProviders(<RiskSettingsPage />);
    expect(await screen.findByText('変更履歴は利用できません。')).toBeInTheDocument();
  });

  it('requires a reason before saving (save disabled until reason entered)', async () => {
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
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
    renderWithProviders(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    await user.type(within(form).getByLabelText('変更理由'), '上限調整');
    const save = within(form).getByRole('button', { name: '保存' });
    expect(save).toBeEnabled();
    // 上限を空欄にすると、黙って 0 送信せず保存を無効化し警告する（安全既定）。
    await user.clear(within(form).getByLabelText(LIMIT_RATIO_LABEL));
    expect(save).toBeDisabled();
    expect(within(form).getByRole('alert')).toHaveTextContent(/入力してください/);
  });

  // ---- #362 の否定形（本 issue の中核） ----
  //
  // PUT のキーを `*Ratio` へ切り替えた瞬間、保存は成功するようになる。そのとき利用者が従来の感覚で
  // `35000` と入れられてしまうと、**equity の 35,000 倍**が上限として保存され統制が事実上無効になる。
  // 画面は百分率入力（IADR-0151 決定1）なので同じ危険は `3500000`（%）として現れる。
  // **統制を無効化する値では PUT が 1 度も飛ばない**ことを固定する。
  it.each([
    ['3500000', '1注文上限に equity の 35,000 倍（％表記）'],
    ['35000', '1注文上限に equity の 350 倍'],
    ['101', '1注文上限が equity 全額を超える'],
    ['0', '1注文上限が 0（発注できない）'],
  ])('never issues PUT when the order limit is out of range (%s)', async (value) => {
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    const input = within(form).getByLabelText(LIMIT_RATIO_LABEL);
    await user.clear(input);
    await user.type(input, value);
    await user.type(within(form).getByLabelText('変更理由'), '上限調整');

    const save = within(form).getByRole('button', { name: '保存' });
    expect(save).toBeDisabled();
    expect(within(form).getByRole('alert')).toHaveTextContent(/1注文発注額上限/);
    // ボタン無効化だけに頼らない（送信経路の二重防御）。クリックしても PUT は飛ばない。
    await user.click(save);
    expect(
      mocks.apiFetch.mock.calls.filter(
        ([p, r]) => p === '/risk-controls/settings/limits' && r?.method === 'PUT',
      ),
    ).toHaveLength(0);
  });

  it('rejects a losing-streak size factor of 1.0 (which would disable the control)', async () => {
    // IADR-0151 決定2: 1.0 は「縮小しない」＝連敗時縮小の無効化であり、設定値として認めない。
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    const input = within(form).getByLabelText('連敗時サイズ縮小係数 倍');
    await user.clear(input);
    await user.type(input, '1');
    await user.type(within(form).getByLabelText('変更理由'), '縮小をやめる');

    expect(within(form).getByRole('button', { name: '保存' })).toBeDisabled();
    expect(within(form).getByRole('alert')).toHaveTextContent(/連敗時サイズ縮小係数/);
  });

  it('rejects a fractional position count', async () => {
    // 3.5 件の建玉は存在しない（件数項目は整数のみ）。
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    const input = within(form).getByLabelText(POSITIONS_LABEL);
    await user.clear(input);
    await user.type(input, '3.5');
    await user.type(within(form).getByLabelText('変更理由'), '建玉枠調整');

    expect(within(form).getByRole('button', { name: '保存' })).toBeDisabled();
    expect(within(form).getByRole('alert')).toHaveTextContent(/整数/);
  });

  it('submits PUT to /settings/limits with the equity-ratio keys (percent converted to ratio)', async () => {
    // FR-10, SC-02, #362, #389, IADR-0151 決定3: **PUT は `*Ratio`（比率）で送る。**
    // #389 まで旧名（金額キー）で送って 400 に落ちていたのは、入力欄が金額のままだったためである。
    // 百分率入力と値域の関門（画面＋サーバ）が揃った本 issue で初めてこの形にしてよい。
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    await user.type(within(form).getByLabelText('変更理由'), '上限調整');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    expect(mocks.apiFetch).toHaveBeenCalledWith(
      '/risk-controls/settings/limits',
      expect.objectContaining({
        method: 'PUT',
        json: {
          limits: {
            // 画面は 25（%）だが、送るのは 0.25（比率）である。
            maxOrderAmountRatio: SETTINGS.limits.maxOrderAmountRatio,
            maxDailyOrderAmountRatio: SETTINGS.limits.maxDailyOrderAmountRatio,
            maxOpenPositions: SETTINGS.limits.maxOpenPositions,
            dailyLossLimitRatio: SETTINGS.limits.dailyLossLimitRatio,
            perTradeRiskRatio: SETTINGS.limits.perTradeRiskRatio,
            maxDrawdownRatio: SETTINGS.limits.maxDrawdownRatio,
            losingStreakThreshold: SETTINGS.limits.losingStreakThreshold,
            losingStreakSizeFactor: SETTINGS.limits.losingStreakSizeFactor,
          },
          reason: '上限調整',
        },
      }),
    );
  });

  it('converts an edited percent into the wire ratio without floating point drift', async () => {
    // #362, IADR-0151 決定1: 変換は 10 進文字列の小数点移動で行う。`33 / 100` の丸め誤差が
    // 統制値としてサーバへ届く経路を作らない。
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    const input = within(form).getByLabelText(LIMIT_RATIO_LABEL);
    await user.clear(input);
    await user.type(input, '33.3');
    await user.type(within(form).getByLabelText('変更理由'), '上限調整');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    const putCall = mocks.apiFetch.mock.calls.find(
      ([p, r]) => p === '/risk-controls/settings/limits' && r?.method === 'PUT',
    )!;
    expect(putCall[1].json.limits.maxOrderAmountRatio).toBe(0.333);
  });

  it('reflects an edited limit in the submitted payload', async () => {
    const user = userEvent.setup();
    renderWithProviders(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    const input = within(form).getByLabelText(POSITIONS_LABEL);
    await user.clear(input);
    await user.type(input, '8');
    await user.type(within(form).getByLabelText('変更理由'), '保有枠拡大');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    const putCall = mocks.apiFetch.mock.calls.find(
      ([p, r]) => p === '/risk-controls/settings/limits' && r?.method === 'PUT',
    )!;
    expect(putCall[1].json.limits.maxOpenPositions).toBe(8);
  });

  it('shows the server 400 details when the server rejects out-of-range limits', async () => {
    // 画面の値域とサーバの値域が食い違ったときも、**サーバが実効**である（IADR-0151 決定2）。
    // その 400 の details をそのまま利用者へ提示する（何が範囲外だったかを読ませる）。
    const user = userEvent.setup();
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/risk-controls/settings/history') return HISTORY;
      if (path === '/risk-controls/status') return STATUS;
      if (path === '/risk-controls/settings/limits' && req?.method === 'PUT')
        throw new ApiError('validation', '検証', 400, [
          '1注文あたりの発注金額上限（equity 比）は 0 より大きく 1（100%）以下でなければなりません。',
        ]);
      if (path === '/monitor/watchlist') return [];
      if (path === '/monitor/watchlist/history') return [];
      return SETTINGS;
    });
    renderWithProviders(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const form = limitsForm();
    await user.type(within(form).getByLabelText('変更理由'), '上限調整');
    await user.click(within(form).getByRole('button', { name: '保存' }));

    expect(await within(form).findByRole('alert')).toHaveTextContent(/1注文あたりの発注金額上限/);
  });

  it('shows a conflict message on 409 without destructive retry', async () => {
    const user = userEvent.setup();
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/risk-controls/settings/history') return HISTORY;
      if (path === '/risk-controls/settings/limits' && req?.method === 'PUT')
        throw new ApiError('conflict', '競合', 409);
      return SETTINGS;
    });
    renderWithProviders(<RiskSettingsPage />);
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
    renderWithProviders(<RiskSettingsPage />);
    expect(await screen.findByText('リスク設定は利用できません。')).toBeInTheDocument();
  });
});
