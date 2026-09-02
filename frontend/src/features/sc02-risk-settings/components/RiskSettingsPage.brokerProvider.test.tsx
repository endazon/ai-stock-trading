import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProviders } from '@ai-stock-trading/testing/renderWithProviders';
import userEvent from '@testing-library/user-event';

// SC-02, FR-20, FR-12, FR-13, UC-06, INDEX 決定 46, #334, IADR-0140 / IADR-0141:
// 発注先（Broker Provider）の表示・変更と、**実弾切替の警告モーダル**の振る舞い。
//
// 計画（FR-20 (1) / 05_screens SC-02）は 4 点の提示と明示的な確認操作を義務づけ、
// **「既定の『OK』ボタン 1 押しで切り替えられてはならない」**と明記する。
// 本テスト群の主眼は**否定形**である——確認が揃わない限り PUT が飛ばないことを固定する
// （「OK」1 押し通過への退行を検知する）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { RiskSettingsPage } from './RiskSettingsPage';
import {
  BROKER_PROVIDER_INTERNAL_PAPER,
  BROKER_PROVIDER_MOOMOO_REAL,
  BROKER_PROVIDER_MOOMOO_SIMULATE,
  formatAmount,
} from '@ai-stock-trading/lib/risk/contracts';
import type { RiskManagementSettings, RiskStatusView } from '@ai-stock-trading/lib/risk/contracts';
import {
  CONTRACT_RISK_SETTINGS,
  CONTRACT_RISK_STATUS,
  cloneContract,
} from '@ai-stock-trading/testing/riskContractFixtures';
import {
  PAPER_BANNER_DEBUG_MESSAGE,
  PAPER_BANNER_EXCLUSION_MESSAGE,
} from '@ai-stock-trading/lib/paperMode';

// #389, IADR-0146: モックはバックエンドの実応答（契約フィクスチャ）を土台にする。
// 実弾切替モーダル③は equity と統制値の**実額**を提示するため、フィクスチャの数値をそのまま使う。
const BASE_SETTINGS = cloneContract(CONTRACT_RISK_SETTINGS);

// Stage 1（SIMULATE）で稼働中・発注先は moomoo SIMULATE、という既定の状況。
function settings(
  brokerProvider: number,
  stageMode = BROKER_PROVIDER_MOOMOO_SIMULATE,
  stage = 1,
): RiskManagementSettings {
  return {
    ...cloneContract(BASE_SETTINGS),
    stage: { ...cloneContract(BASE_SETTINGS.stage), stage, mode: stageMode },
    brokerProvider,
  };
}

const STATUS: RiskStatusView = {
  ...cloneContract(CONTRACT_RISK_STATUS),
  stage: 1,
  brokerProvider: BROKER_PROVIDER_MOOMOO_SIMULATE,
};

interface Call {
  path: string;
  method?: string;
  json?: unknown;
}

let calls: Call[] = [];

function mockApi(
  current: number,
  options?: { statusFails?: boolean; stageMode?: number; stage?: number },
) {
  calls = [];
  const stageMode = options?.stageMode ?? BROKER_PROVIDER_MOOMOO_SIMULATE;
  const stage = options?.stage ?? 1;
  mocks.apiFetch.mockImplementation(
    async (path: string, req?: { method?: string; json?: unknown }) => {
      calls.push({ path, method: req?.method, json: req?.json });
      if (path === '/risk-controls/settings/history') return [];
      if (path === '/monitor/watchlist' || path === '/monitor/watchlist/history') return [];
      if (path === '/risk-controls/status') {
        if (options?.statusFails) throw new Error('status unavailable');
        return STATUS;
      }
      if (path === '/risk-controls/settings/broker-provider') {
        return { settings: settings(current, stageMode, stage) };
      }
      return settings(current, stageMode, stage);
    },
  );
}

function providerPuts(): Call[] {
  return calls.filter((c) => c.path === '/risk-controls/settings/broker-provider');
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-02 発注先（Broker Provider）の表示（#334）', () => {
  it('運用段階と発注先を別々の行として表示する（1 行に混ぜない）', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    renderWithProviders(<RiskSettingsPage />);

    await screen.findByRole('form', { name: '発注先の変更' });
    // 05_screens 共通規約: 「運用段階と発注先は独立した 2 軸（1 行に混ぜて表示しない）」。
    expect(screen.getByText('運用段階')).toBeInTheDocument();
    expect(screen.getAllByText('発注先').length).toBeGreaterThan(0);
    expect(screen.getByText('Stage 1（SIMULATE）')).toBeInTheDocument();
  });

  it('発注先の選択肢は計画の 3 値だけを出す', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    renderWithProviders(<RiskSettingsPage />);

    const form = await screen.findByRole('form', { name: '発注先の変更' });
    expect(within(form).getAllByRole('radio')).toHaveLength(3);
  });
});

describe('SC-02 内蔵 paper 稼働中の警告バナー（FR-12・#334）', () => {
  it('内蔵 paper 稼働中は必須 2 文言のバナーを表示する', async () => {
    mockApi(BROKER_PROVIDER_INTERNAL_PAPER);
    renderWithProviders(<RiskSettingsPage />);

    await screen.findByRole('form', { name: '発注先の変更' });
    const banner = screen.getByRole('alert', { name: '内蔵 paper 稼働中の警告' });
    expect(within(banner).getByText(PAPER_BANNER_DEBUG_MESSAGE)).toBeInTheDocument();
    expect(within(banner).getByText(PAPER_BANNER_EXCLUSION_MESSAGE)).toBeInTheDocument();
  });

  // 否定形: paper でないときに出してはならない（実弾稼働をデバッグ稼働と誤認させる）。
  it('SIMULATE 稼働中はバナーを表示しない', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    renderWithProviders(<RiskSettingsPage />);

    await screen.findByRole('form', { name: '発注先の変更' });
    expect(screen.queryByRole('alert', { name: '内蔵 paper 稼働中の警告' })).not.toBeInTheDocument();
  });
});

describe('SC-02 発注先の変更（FR-13・#334）', () => {
  it('理由が空では保存できない（ボタンが無効）', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '発注先の変更' });

    await userEvent.click(within(form).getByRole('radio', { name: /内蔵 paper/ }));

    expect(within(form).getByRole('button', { name: '保存' })).toBeDisabled();
    expect(providerPuts()).toHaveLength(0);
  });

  it('実弾以外への切替は理由だけで PUT できる', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '発注先の変更' });

    await userEvent.click(within(form).getByRole('radio', { name: /内蔵 paper/ }));
    await userEvent.type(within(form).getByLabelText('変更理由'), 'デバッグのため');
    await userEvent.click(within(form).getByRole('button', { name: '保存' }));

    const puts = providerPuts().filter((c) => c.method === 'PUT');
    expect(puts).toHaveLength(1);
    expect(puts[0].json).toMatchObject({
      provider: BROKER_PROVIDER_INTERNAL_PAPER,
      reason: 'デバッグのため',
    });
  });

  // #539: mount 時にも走っていた prop 追随を描画中の state 調整へ揃えた。**`current` が実際に
  // 変わったとき（自分の保存成功後の再取得）の初期化は従来どおり効く**ことを固定する
  // （置換前は `useEffect([current])` が同じ役割を担っていた）。
  it('保存成功後の再取得で発注先が実際に変わると、選択・理由が新しい current で初期化される', async () => {
    let liveCurrent = BROKER_PROVIDER_MOOMOO_SIMULATE;
    calls = [];
    mocks.apiFetch.mockImplementation(
      async (path: string, req?: { method?: string; json?: unknown }) => {
        calls.push({ path, method: req?.method, json: req?.json });
        if (path === '/risk-controls/settings/history') return [];
        if (path === '/monitor/watchlist' || path === '/monitor/watchlist/history') return [];
        if (path === '/risk-controls/status') return STATUS;
        if (path === '/risk-controls/settings/broker-provider' && req?.method === 'PUT') {
          liveCurrent = (req.json as { provider: number }).provider;
          return { settings: settings(liveCurrent) };
        }
        return settings(liveCurrent);
      },
    );
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '発注先の変更' });

    await userEvent.click(within(form).getByRole('radio', { name: /内蔵 paper/ }));
    await userEvent.type(within(form).getByLabelText('変更理由'), 'デバッグのため');
    await userEvent.click(within(form).getByRole('button', { name: '保存' }));

    await within(form).findByText('保存しました。');
    // current が実際に変わったので、選択は新しい current に追随し、理由は初期化される。
    expect(within(form).getByRole('radio', { name: /内蔵 paper/ })).toBeChecked();
    expect(within(form).getByLabelText('変更理由')).toHaveValue('');
    expect(within(form).getByText('発注先は変更されていません。')).toBeInTheDocument();
  });
});

describe('SC-02 実弾（moomoo REAL）への切替（FR-20 (1)・IADR-0141）', () => {
  async function openModal() {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '発注先の変更' });

    await userEvent.click(within(form).getByRole('radio', { name: /moomoo REAL/ }));
    await userEvent.type(within(form).getByLabelText('変更理由'), '実弾へ移行する');
    await userEvent.click(within(form).getByRole('button', { name: '実弾への切替を確認する' }));

    return screen.getByRole('dialog', { name: '実弾（moomoo REAL）への切替の確認' });
  }

  // 計画が定める 4 点がすべて提示されること。
  it('警告モーダルは計画の 4 点を提示する', async () => {
    const dialog = await openModal();

    // ① 実資金で執行される旨
    expect(within(dialog).getByText('これ以降の注文は実際の資金で執行されます。')).toBeInTheDocument();
    // ② 切替先と現在の Stage の組み合わせ（Stage 1 のままなら段階ゲートを飛ばしている旨）
    expect(within(dialog).getByText(/段階ゲート.*を飛ばしています/)).toBeInTheDocument();
    expect(within(dialog).getByText('Stage 1（SIMULATE）')).toBeInTheDocument();
    // ③ 現在の equity と、それに対する統制値の実額
    // #389: 期待値は契約フィクスチャ（実応答）由来にする。ここは equity から解決済みの**実額**であり、
    // 設定側の比率（maxOrderAmountRatio）とは別物である。
    const table = within(dialog).getByRole('table', { name: '現在の equity と統制値' });
    // SC-02, FR-10, #362, #364, IADR-0151 決定4 / IADR-0152 決定6: 実弾切替は**取り消しが困難な操作**であり、
    // その直前に提示する 4 点は画面の他の実額と同じ表記規約（桁区切り ＋ 基準通貨 USD の記号）に従う。
    // 生の数値のままだと桁を読み違える余地が残り、「単位を取り違えさせない」という決定4 の目的に反する。
    expect(within(table).getByText(formatAmount(STATUS.capital))).toBeInTheDocument();
    expect(within(table).getByText(formatAmount(STATUS.maxOrderAmount))).toBeInTheDocument();
    expect(within(table).getByText(formatAmount(STATUS.maxDailyOrderAmount))).toBeInTheDocument();
    // ④ 明示的な確認操作（チェックボックス＋文字入力）
    expect(within(dialog).getByRole('checkbox')).toBeInTheDocument();
    expect(within(dialog).getByLabelText(/「REAL」と入力/)).toBeInTheDocument();
  });

  // **本 issue の中核（否定形）**: 「OK」1 押しでは通過できない。
  it('確認操作なしでは切替ボタンが無効で PUT が飛ばない', async () => {
    const dialog = await openModal();

    expect(within(dialog).getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();
    expect(providerPuts().filter((c) => c.method === 'PUT')).toHaveLength(0);
  });

  it('チェックボックスだけでは切替ボタンが無効のままである', async () => {
    const dialog = await openModal();

    await userEvent.click(within(dialog).getByRole('checkbox'));

    expect(within(dialog).getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();
    expect(providerPuts().filter((c) => c.method === 'PUT')).toHaveLength(0);
  });

  it('文字入力だけでは切替ボタンが無効のままである', async () => {
    const dialog = await openModal();

    await userEvent.type(within(dialog).getByLabelText(/「REAL」と入力/), 'REAL');

    expect(within(dialog).getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();
    expect(providerPuts().filter((c) => c.method === 'PUT')).toHaveLength(0);
  });

  // 境界値: 照合は大文字小文字を区別する（IADR-0141 決定2）。
  it('小文字の real では切替ボタンが有効にならない', async () => {
    const dialog = await openModal();

    await userEvent.click(within(dialog).getByRole('checkbox'));
    await userEvent.type(within(dialog).getByLabelText(/「REAL」と入力/), 'real');

    expect(within(dialog).getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();
  });

  // FR-20 (2), #422: 照合規則は計画が 2026-08-07 に確定させた
  //（前後空白のみを除いた完全一致・大文字小文字を区別する）。**緩める変更を止めるのが本テストの目的である。**
  it.each(['Real', 'REAl', 'rEaL', 'ＲＥＡＬ', 'ＲeＡl', 'R E A L', 'REALLY', 'REA'])(
    '確認文字列が「%s」では切替ボタンが有効にならない',
    async (phrase) => {
      const dialog = await openModal();

      await userEvent.click(within(dialog).getByRole('checkbox'));
      await userEvent.type(within(dialog).getByLabelText(/「REAL」と入力/), phrase);

      expect(within(dialog).getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();
      expect(providerPuts().filter((c) => c.method === 'PUT')).toHaveLength(0);
    },
  );

  // 境界値の裏面: **前後空白のみ**は除く（貼り付けで混じる空白は利用者の意図と無関係）。
  it.each([' REAL', 'REAL ', '  REAL  '])(
    '前後空白を除いて REAL と一致する「%s」では切替できる',
    async (phrase) => {
      const dialog = await openModal();

      await userEvent.click(within(dialog).getByRole('checkbox'));
      await userEvent.type(within(dialog).getByLabelText(/「REAL」と入力/), phrase);

      expect(within(dialog).getByRole('button', { name: '実弾へ切り替える' })).toBeEnabled();
    },
  );

  it('同意と REAL の入力が揃うと切替でき、確認内容とともに PUT する', async () => {
    const dialog = await openModal();

    await userEvent.click(within(dialog).getByRole('checkbox'));
    await userEvent.type(within(dialog).getByLabelText(/「REAL」と入力/), 'REAL');
    const confirm = within(dialog).getByRole('button', { name: '実弾へ切り替える' });
    expect(confirm).toBeEnabled();
    await userEvent.click(confirm);

    const puts = providerPuts().filter((c) => c.method === 'PUT');
    expect(puts).toHaveLength(1);
    expect(puts[0].json).toMatchObject({
      provider: BROKER_PROVIDER_MOOMOO_REAL,
      reason: '実弾へ移行する',
      acknowledgedLiveTrading: true,
      acknowledgement: 'REAL',
    });
  });

  // 否定形: ③ を提示できない状態（equity が取れない）では切り替えさせない。
  it('equity と統制値を取得できない場合は切替ボタンが無効である', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE, { statusFails: true });
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '発注先の変更' });

    await userEvent.click(within(form).getByRole('radio', { name: /moomoo REAL/ }));
    await userEvent.type(within(form).getByLabelText('変更理由'), '実弾へ移行する');
    await userEvent.click(within(form).getByRole('button', { name: '実弾への切替を確認する' }));

    const dialog = screen.getByRole('dialog', { name: '実弾（moomoo REAL）への切替の確認' });
    await userEvent.click(within(dialog).getByRole('checkbox'));
    await userEvent.type(within(dialog).getByLabelText(/「REAL」と入力/), 'REAL');

    expect(within(dialog).getByText(/equity と統制値を取得できないため/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: '実弾へ切り替える' })).toBeDisabled();
    expect(providerPuts().filter((c) => c.method === 'PUT')).toHaveLength(0);
  });

  // **FR-20 (1) の中核（#422）**: 「同意したのに 1 件も発注されない」理由を画面に出す。
  // 計画は「この旨を SC-02 の警告モーダルにも含める」と名指ししており、**文言を消せばここが赤くなる**。
  it('警告モーダルに「段階が実弾を既定とするまで発注は行われない」旨を含む', async () => {
    const dialog = await openModal();

    expect(
      within(dialog).getByText(/段階が実弾を既定とするまで発注は行われません/),
    ).toBeInTheDocument();
    // 「保存できる」ことと「発注できる」ことが別だと読めること（計画が名指しした誤読の否定）。
    expect(within(dialog).getByText(/保存できますが、実弾の注文は段階ゲートが拒否します/)).toBeInTheDocument();
  });

  // 一覧側（モーダルを開く前）にも同じ旨を出す。モーダルへ進まない利用者にも届かせるため。
  it('フォームの段階ゲート警告にも発注が行われない旨を含む', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '発注先の変更' });

    await userEvent.click(within(form).getByRole('radio', { name: /moomoo REAL/ }));

    expect(within(form).getByText(/段階が実弾を既定とするまで発注は行われません/)).toBeInTheDocument();
  });

  // **否定形**: 段階が既に実弾（Stage 2）なら注文は実際に発注される。同じ文言を出すと**嘘になる**
  // （狼少年にもなる）。条件が `skipsStageGate` と一致していることを固定する。
  it('段階が既に実弾なら発注が行われない旨は表示しない', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE, {
      stageMode: BROKER_PROVIDER_MOOMOO_REAL,
      stage: 2,
    });
    renderWithProviders(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: '発注先の変更' });

    await userEvent.click(within(form).getByRole('radio', { name: /moomoo REAL/ }));
    await userEvent.type(within(form).getByLabelText('変更理由'), '実弾へ移行する');
    await userEvent.click(within(form).getByRole('button', { name: '実弾への切替を確認する' }));

    const dialog = screen.getByRole('dialog', { name: '実弾（moomoo REAL）への切替の確認' });
    expect(
      within(dialog).queryByText(/段階が実弾を既定とするまで発注は行われません/),
    ).not.toBeInTheDocument();
    expect(within(dialog).queryByText(/段階ゲート.*を飛ばしています/)).not.toBeInTheDocument();
  });

  it('キャンセルすると確認状態が破棄され PUT も飛ばない', async () => {
    const dialog = await openModal();

    await userEvent.click(within(dialog).getByRole('checkbox'));
    await userEvent.type(within(dialog).getByLabelText(/「REAL」と入力/), 'REAL');
    await userEvent.click(within(dialog).getByRole('button', { name: 'キャンセル' }));

    expect(
      screen.queryByRole('dialog', { name: '実弾（moomoo REAL）への切替の確認' }),
    ).not.toBeInTheDocument();
    expect(providerPuts().filter((c) => c.method === 'PUT')).toHaveLength(0);
  });
});
