import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';

// SC-03, FR-20, FR-12, UC-06, INDEX 決定 46, #334, IADR-0140 / IADR-0142:
// 統制状態参照画面における発注先の**参照表示**・`paper` 警告バナー／ラベル・変更履歴・
// Stage 1 進捗（除外営業日数の併記）。
//
// 計画（05_screens SC-03）は本画面を**参照専用**と定める——発注先の変更は SC-02 だけが持つ。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { ControlStatusPage } from './ControlStatusPage';
import {
  BROKER_PROVIDER_INTERNAL_PAPER,
  BROKER_PROVIDER_MOOMOO_SIMULATE,
  CHANGE_TYPE_BROKER_PROVIDER,
} from '../risk/contracts';
import {
  PAPER_BANNER_DEBUG_MESSAGE,
  PAPER_BANNER_EXCLUSION_MESSAGE,
  PAPER_REFERENCE_LABEL,
} from '../shared/paperMode';

function status(brokerProvider: number) {
  return {
    killSwitchEngaged: false,
    dailyLossLockoutActive: false,
    lockoutReleaseOn: null,
    tradingPaused: false,
    activeControl: 0,
    newEntriesBlocked: false,
    stage: 1,
    brokerProvider,
    dailyRealizedPnl: 0,
    unrealizedPnl: 0,
    dailyPnl: 0,
    capital: 3000,
    dailyOrderedAmount: 0,
    maxOrderAmount: 750,
    maxDailyOrderAmount: 4500,
    drawdownRatio: 0,
    maxDrawdownRatio: 0.1,
    openPositionCount: 0,
    maxOpenPositions: 3,
  };
}

const STAGE_GATE = {
  currentStage: 1,
  currentSettings: { stage: 1, mode: BROKER_PROVIDER_MOOMOO_SIMULATE, capitalCap: 1 },
  history: [],
  promotion: { targetStage: 2, eligible: false, unmetCriteria: [] },
  withdrawal: { triggered: false, reason: null, haltNewEntries: false, proposedStage: null },
  stage1Progress: { qualifiedTradingDays: 42, tradeCount: 70, excludedInternalPaperDays: 3 },
  stage1Criteria: { targetTradingDays: 60, minimumTradeCount: 100, maximumTradingDays: 120 },
};

const PROVIDER_HISTORY = [
  {
    actor: 'owner',
    changeType: CHANGE_TYPE_BROKER_PROVIDER,
    reason: 'デバッグのため内蔵擬似約定へ落とす',
    changedAt: '2026-08-04T00:00:00Z',
    before: 'MoomooSimulate',
    after: 'InternalPaper',
  },
  // 発注先以外の履歴（上限変更）は本表に出さない。
  { actor: 'owner', changeType: 1, reason: '上限見直し', changedAt: '2026-08-03T00:00:00Z' },
];

function mockApi(brokerProvider: number, gate: unknown = STAGE_GATE) {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return gate;
    if (path === '/risk-controls/settings/history') return PROVIDER_HISTORY;
    return status(brokerProvider);
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-03 発注先の参照表示（#334）', () => {
  it('運用段階と発注先を別々の行として表示する（1 行に混ぜない）', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    await screen.findByText('発注先');
    expect(screen.getByText('運用段階')).toBeInTheDocument();
    expect(screen.getAllByText('Stage 1（SIMULATE）').length).toBeGreaterThan(0);
    expect(screen.getAllByText(/moomoo SIMULATE/).length).toBeGreaterThan(0);
  });

  // 05_screens:「本画面は参照専用のため表示のみとし、変更は SC-02 で行う」。
  it('発注先の変更 UI を持たない（参照専用）', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    await screen.findByText('発注先');
    expect(screen.queryByRole('form', { name: '発注先の変更' })).not.toBeInTheDocument();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
  });
});

describe('SC-03 内蔵 paper 稼働中の表示（FR-12・#334）', () => {
  it('必須 2 文言のバナーと統制状態カードの paper ラベルを表示する', async () => {
    mockApi(BROKER_PROVIDER_INTERNAL_PAPER);
    render(<ControlStatusPage />);

    const banner = await screen.findByRole('alert', { name: '内蔵 paper 稼働中の警告' });
    expect(within(banner).getByText(PAPER_BANNER_DEBUG_MESSAGE)).toBeInTheDocument();
    expect(within(banner).getByText(PAPER_BANNER_EXCLUSION_MESSAGE)).toBeInTheDocument();
    // 05_screens: 統制状態のカード類にも `paper` である旨のラベルを付す。
    expect(screen.getAllByText(new RegExp(PAPER_REFERENCE_LABEL)).length).toBeGreaterThan(0);
  });

  // 否定形: paper でないときにバナー・ラベルを出してはならない。
  it('SIMULATE 稼働中はバナーも paper ラベルも出さない', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    await screen.findByText('発注先');
    expect(screen.queryByRole('alert', { name: '内蔵 paper 稼働中の警告' })).not.toBeInTheDocument();
    expect(screen.queryByText(new RegExp(PAPER_REFERENCE_LABEL))).not.toBeInTheDocument();
  });
});

describe('SC-03 発注先の変更履歴（FR-20 (2)・#334）', () => {
  it('日時・変更前後・理由を表示し、発注先以外の履歴は混ぜない', async () => {
    mockApi(BROKER_PROVIDER_INTERNAL_PAPER);
    render(<ControlStatusPage />);

    const table = await screen.findByRole('table', { name: '発注先の変更履歴' });
    const rows = within(table).getAllByRole('row');
    // ヘッダ + 発注先の 1 件（上限変更は含まれない）。
    expect(rows).toHaveLength(2);
    expect(within(rows[1]).getByText('MoomooSimulate')).toBeInTheDocument();
    expect(within(rows[1]).getByText('InternalPaper')).toBeInTheDocument();
    expect(within(rows[1]).getByText('デバッグのため内蔵擬似約定へ落とす')).toBeInTheDocument();
    expect(within(table).queryByText('上限見直し')).not.toBeInTheDocument();
  });
});

describe('SC-03 Stage 1 進捗と除外営業日数（FR-20・IADR-0142）', () => {
  it('経過営業日数に paper 稼働による除外日数を併記する', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    expect(await screen.findByText(/経過 42 \/ 60 営業日/)).toHaveTextContent(
      'paper 稼働により 3 日を除外',
    );
    expect(screen.getByText(/取引 70 \/ 100 件/)).toBeInTheDocument();
  });

  it('SIMULATE の約定のみを集計している旨の注記を置く', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    expect(await screen.findByText(/moomoo SIMULATE.*の約定のみを集計/)).toBeInTheDocument();
  });

  // 除外が 0 日なら括弧書きを出さない（常に「除外あり」と読ませない）。
  it('除外日数が 0 なら併記しない', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE, {
      ...STAGE_GATE,
      stage1Progress: { qualifiedTradingDays: 42, tradeCount: 70, excludedInternalPaperDays: 0 },
    });
    render(<ControlStatusPage />);

    const line = await screen.findByText(/経過 42 \/ 60 営業日/);
    expect(line).not.toHaveTextContent('除外');
  });

  // 縮退: 進捗を含まない応答（BFF 未追随・旧版サーバ）でも画面を壊さない。
  it('進捗を含まない応答では進捗領域のみ縮退する', async () => {
    const withoutProgress = { ...STAGE_GATE } as Record<string, unknown>;
    delete withoutProgress.stage1Progress;
    delete withoutProgress.stage1Criteria;
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE, withoutProgress);
    render(<ControlStatusPage />);

    expect(await screen.findByText('Stage 1 の進捗は利用できません。')).toBeInTheDocument();
    expect(screen.getByText('発注先')).toBeInTheDocument();
  });
});
