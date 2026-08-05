import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-02, FR-13, FR-19, FR-20, UC-06, IADR-0084: リスク設定画面（リスク上限の閲覧/変更）の振る舞い。
// データ取得・更新は apiFetch をモックし、実 BFF 疎通に依存しない。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { RiskSettingsPage } from './RiskSettingsPage';
import {
  CONTRACT_RISK_SETTINGS,
  CONTRACT_SETTINGS_HISTORY,
  cloneContract,
} from '../risk/contractFixtures';
import type { RiskManagementSettings, SettingsChangeEntry } from '../risk/contracts';

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

// #389: 発注額上限のラベル（equity 比であることを明示した文言）。テストから参照して表記の齟齬を 1 か所に集める。
const LIMIT_RATIO_LABEL = '1注文発注額上限（equity 比・0.25＝25%）';

// リスク上限フォーム（#186）。ガード変更フォーム（#188）と同名の要素があるため絞り込みに使う。
function limitsForm(): HTMLElement {
  return screen.getByRole('form', { name: 'リスク上限の変更' });
}

describe('RiskSettingsPage (SC-02, FR-13)', () => {
  it('renders current limits into the form', async () => {
    render(<RiskSettingsPage />);
    expect(await screen.findByRole('heading', { name: 'リスク設定' })).toBeInTheDocument();
    // 読み込み完了（フォーム描画）を待ってから現在値を検証する（見出しは読み込み中も描画されるため待受にしない）。
    // FR-10, #329, #389: 発注額の上限は **equity 比**（実応答の 0.25）である。旧キー（金額）を読んでいたときは
    // ここが undefined になり、フォームには "undefined" が入っていた。
    expect(await screen.findByLabelText(LIMIT_RATIO_LABEL)).toHaveValue(
      CONTRACT_RISK_SETTINGS.limits.maxOrderAmountRatio,
    );
    expect(screen.getByLabelText('保有銘柄数上限')).toHaveValue(5);
  });

  it('labels the amount limits as equity ratios and warns that saving is currently rejected', async () => {
    // #389: 比率を「金額上限」と表示すると桁が 6 桁ずれて読める。単位をラベルに明示する。
    // #362: 保存は 400 で拒否される（安全側として意図的）。沈黙の失敗にしない。
    render(<RiskSettingsPage />);
    const form = await screen.findByRole('form', { name: 'リスク上限の変更' });
    expect(within(form).getByLabelText(LIMIT_RATIO_LABEL)).toBeInTheDocument();
    expect(within(form).getByText(/保存はサーバに拒否されます/)).toBeInTheDocument();
  });

  it('shows the stage orderable cap as a ratio of total capital', async () => {
    // FR-20, #333, #389, IADR-0136: capitalCapRatio（総資金比）。#389 まで画面に一切出ておらず、
    // キー名のずれが描画結果に現れなかった。
    render(<RiskSettingsPage />);
    await screen.findByRole('form', { name: 'リスク上限の変更' });
    const term = screen.getByText('段階の発注可能額（総資金比）');
    expect(term.nextElementSibling).toHaveTextContent(String(SETTINGS.stage.capitalCapRatio));
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
    await user.clear(within(form).getByLabelText(LIMIT_RATIO_LABEL));
    expect(save).toBeDisabled();
    expect(within(form).getByRole('alert')).toHaveTextContent(/未入力|数値/);
  });

  it('submits PUT to /settings/limits keeping the legacy amount keys (rejected by the server by design)', async () => {
    // FR-10, SC-02, #362, #389: **本文は旧名（金額キー）のまま送る。サーバは 400 で拒否する。**
    // キー名だけ `*Ratio` へ直すと、金額入力のまま比率として保存されて統制が事実上無効化される
    // （利用者が `35000` と入れれば equity の 35,000 倍）。拒否される方が安全側であり、
    // 入力 UI の作り直し（#362）と同時にしか変えてはならない。この期待値は**その意図の錠前**である。
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
        json: {
          limits: {
            maxOrderAmount: SETTINGS.limits.maxOrderAmountRatio,
            maxDailyOrderAmount: SETTINGS.limits.maxDailyOrderAmountRatio,
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
