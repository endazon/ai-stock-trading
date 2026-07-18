import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { ApiError } from '@foundation/api/ApiError';

// SC-03, FR-10, FR-20, UC-06, IADR-0084: 承認・統制状態参照画面（参照専用）の振る舞い。
// データ取得は apiFetch をモックし、実 BFF 疎通に依存しない。破壊的操作の UI を持たないことも検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { ControlStatusPage } from './ControlStatusPage';

const STATUS = {
  killSwitchEngaged: true,
  dailyLossLockoutActive: false,
  lockoutReleaseOn: null,
  tradingPaused: false,
  activeControl: 1, // KillSwitch
  newEntriesBlocked: true,
  stage: 2,
  dailyRealizedPnl: -1500,
  unrealizedPnl: 500,
  dailyPnl: -1000,
  capital: 1000000,
  dailyOrderedAmount: 150000,
  maxDailyOrderAmount: 300000,
  drawdownRatio: 0.03, // 30.0%（他の使用率 50.0%/40.0% と区別）
  maxDrawdownRatio: 0.1,
  openPositionCount: 2,
  maxOpenPositions: 5,
};
const STAGE_GATE = {
  currentStage: 2,
  currentSettings: { stage: 2, mode: 1, capitalCap: 1000000 },
  history: [
    {
      sequence: 1,
      fromStage: 0,
      toStage: 1,
      kind: 0, // Promotion
      approvedBy: 'owner',
      occurredAtUtc: '2026-07-10T00:00:00Z',
      reason: 'バックテスト合格',
    },
    {
      sequence: 2,
      fromStage: 1,
      toStage: 2,
      kind: 0,
      approvedBy: 'owner',
      occurredAtUtc: '2026-07-15T00:00:00Z',
      reason: 'ペーパー実績良好',
    },
  ],
  promotion: { targetStage: 3, eligible: false, unmetCriteria: [3, 4] },
  withdrawal: { triggered: false, reason: null, haltNewEntries: false, proposedStage: null },
};

function mockDefault() {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return STAGE_GATE;
    return STATUS;
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mockDefault();
});

describe('ControlStatusPage (SC-03, FR-10/FR-20)', () => {
  it('renders control status with the active control mapped to a label', async () => {
    render(<ControlStatusPage />);
    expect(await screen.findByRole('heading', { name: '統制状態' })).toBeInTheDocument();
    expect(screen.getByText('緊急停止（kill switch）')).toBeInTheDocument();
    expect(screen.getByText('作動中')).toBeInTheDocument();
    // 新規建てが停止中である旨を表示する。
    expect(screen.getByText('停止中')).toBeInTheDocument();
  });

  it('shows usage ratios computed from current/limit', async () => {
    render(<ControlStatusPage />);
    const table = await screen.findByRole('table', { name: '上限使用率' });
    // 1日発注 150000/300000 = 50.0%
    expect(within(table).getByText('50.0%')).toBeInTheDocument();
  });

  it('renders stage-gate promotion assessment with unmet criteria labels', async () => {
    render(<ControlStatusPage />);
    await screen.findByRole('heading', { name: '統制状態' });
    expect(await screen.findByText(/未充足基準/)).toHaveTextContent('スリッページ/費用が想定超過');
    expect(screen.getByText(/未充足基準/)).toHaveTextContent('日次損失上限の運用違反');
  });

  it('lists transition history newest first with from→to stage labels', async () => {
    render(<ControlStatusPage />);
    const table = await screen.findByRole('table', { name: '段階遷移履歴' });
    const rows = within(table).getAllByRole('row');
    // 先頭データ行が新しい順（sequence 2）。
    expect(within(rows[1]).getByText('ペーパー実績良好')).toBeInTheDocument();
    expect(within(rows[1]).getByText('Stage 1（ペーパー） → Stage 2（少額実弾）')).toBeInTheDocument();
  });

  it('has no destructive control buttons (read-only; #165 Bot owns those)', async () => {
    render(<ControlStatusPage />);
    await screen.findByRole('heading', { name: '統制状態' });
    // 参照専用: いかなるボタンも持たない（pause/resume/kill switch/承認は Bot 側）。
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('degrades the stage-gate area independently when it fails but status succeeds', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/risk-controls/stage-gate') throw new ApiError('server', 'boom', 500);
      return STATUS;
    });
    render(<ControlStatusPage />);
    // 統制状態は表示され、段階ゲート領域のみ縮退する。
    expect(await screen.findByText('緊急停止（kill switch）')).toBeInTheDocument();
    expect(screen.getByText('段階ゲートは利用できません。')).toBeInTheDocument();
  });

  it('degrades to a not-available message when status 404s (BFF unwired)', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/risk-controls/status') throw new ApiError('notFound', '未登録', 404);
      throw new ApiError('notFound', '未登録', 404);
    });
    render(<ControlStatusPage />);
    expect(await screen.findByText('統制状態は利用できません。')).toBeInTheDocument();
  });

  it('renders unknown enum values and a zero limit safely (fail-safe UI)', async () => {
    // 未知の activeControl(9) はラベル写像テーブルに無く「不明(9)」へ、上限 0 の使用率は「—」へ安全側に倒す。
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/risk-controls/stage-gate') return STAGE_GATE;
      return { ...STATUS, activeControl: 9, maxOpenPositions: 0, openPositionCount: 0 };
    });
    render(<ControlStatusPage />);
    await screen.findByRole('heading', { name: '統制状態' });
    expect(screen.getByText('不明(9)')).toBeInTheDocument();
    const table = screen.getByRole('table', { name: '上限使用率' });
    // 保有銘柄数の上限 0 → 使用率は「—」（0 除算を安全側に倒す）。
    const positionRow = within(table).getByText('保有銘柄数').closest('tr')!;
    expect(within(positionRow).getByText('—')).toBeInTheDocument();
  });

  it('shows a triggered withdrawal assessment as an alert with reason label', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/risk-controls/stage-gate') {
        return {
          ...STAGE_GATE,
          withdrawal: { triggered: true, reason: 0, haltNewEntries: true, proposedStage: 1 },
        };
      }
      return STATUS;
    });
    render(<ControlStatusPage />);
    const alert = await screen.findByText(/撤退基準に到達/);
    expect(alert).toHaveTextContent('実DD がバックテスト最大DD × 倍率に到達');
  });
});
