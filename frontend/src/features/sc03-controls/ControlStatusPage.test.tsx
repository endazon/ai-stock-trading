import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProviders } from '../../testing/renderWithProviders';
import { ApiError } from '@foundation/api/ApiError';

// SC-03, FR-10, FR-20, UC-06, IADR-0084: 承認・統制状態参照画面（参照専用）の振る舞い。
// データ取得は apiFetch をモックし、実 BFF 疎通に依存しない。破壊的操作の UI を持たないことも検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { ControlStatusPage } from './ControlStatusPage';
import {
  CONTRACT_RISK_STATUS,
  CONTRACT_SHORT_SELLING,
  CONTRACT_STAGE_GATE,
  cloneContract,
} from '@ai-stock-trading/testing/riskContractFixtures';
import type { RiskStatusView, StageGateStatus, StageTransition } from '@ai-stock-trading/lib/risk/contracts';

// #389, IADR-0146: モックは**バックエンドの実応答**（契約フィクスチャ）を土台に作り、この画面の検証に必要な
// 差分（統制成立・損益・使用率・遷移履歴）だけを上書きする。インライン literal で全体を自作すると、
// バックエンドの改名（#329 / #333）を緑のまま通す構造へ戻る。
const STATUS: RiskStatusView = {
  ...cloneContract(CONTRACT_RISK_STATUS),
  killSwitchEngaged: true,
  activeControl: 1, // KillSwitch
  newEntriesBlocked: true,
  stage: 2,
  dailyRealizedPnl: -1500,
  unrealizedPnl: 500,
  dailyPnl: -1000,
  capital: 1000000,
  dailyOrderedAmount: 150000,
  maxOrderAmount: 250000,
  maxDailyOrderAmount: 300000,
  drawdownRatio: 0.03, // 30.0%（他の使用率 50.0%/40.0% と区別）
  openPositionCount: 2,
  maxOpenPositions: 5,
};
// 遷移履歴の要素は実応答の 1 件を土台に複製する（形はバックエンド由来・値だけをテスト用に置く）。
const TRANSITION: StageTransition = cloneContract(CONTRACT_STAGE_GATE.history[0]);
const STAGE_GATE: StageGateStatus = {
  ...cloneContract(CONTRACT_STAGE_GATE),
  currentStage: 2,
  currentSettings: { ...cloneContract(CONTRACT_STAGE_GATE.currentSettings), stage: 2, mode: 1 },
  history: [
    { ...TRANSITION, sequence: 1, fromStage: 0, toStage: 1, kind: 0, approvedBy: 'owner', reason: 'バックテスト合格' },
    { ...TRANSITION, sequence: 2, fromStage: 1, toStage: 2, kind: 0, approvedBy: 'owner', reason: 'ペーパー実績良好' },
  ],
  promotion: { targetStage: 3, eligible: false, unmetCriteria: [3, 4] },
};

// FR-10, SC-03, #340, IADR-0154: 空売りの現況も**実応答**（契約フィクスチャ）を土台にする。
// 既定では維持率・借株料の累計・自動縮小の発動履歴がいずれも未供給である（それが現在の事実）。
const SHORT_SELLING = cloneContract(CONTRACT_SHORT_SELLING);

function mockDefault() {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return STAGE_GATE;
    if (path === '/risk-controls/short-selling') return SHORT_SELLING;
    return STATUS;
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mockDefault();
});

describe('ControlStatusPage (SC-03, FR-10/FR-20)', () => {
  it('renders control status with the active control mapped to a label', async () => {
    renderWithProviders(<ControlStatusPage />);
    expect(await screen.findByRole('heading', { name: '統制状態' })).toBeInTheDocument();
    // 読み込み完了（統制状態の描画）を待ってから内容を検証する（見出しは読み込み中も描画されるため待受にしない）。
    const table = await screen.findByRole('table', { name: '取引統制（優先順位順）' });
    expect(within(table).getByText('緊急停止（kill switch）')).toBeInTheDocument();
    expect(within(table).getByText('作動中')).toBeInTheDocument();
    // 新規建てが停止中である旨を表示する。
    expect(screen.getByText('停止中')).toBeInTheDocument();
  });

  // FR-10, ADR-0009, SC-03, #340: 3 統制を**優先順位順**（kill switch ＞ 日次損失ロックアウト ＞ 一時停止）で
  // 表示し、**優先統制を明示**する。順序を画面に書かないと「同時に成立したときどれが効くのか」が読めない。
  it('3 統制を優先順位順に表示し優先統制を明示する', async () => {
    renderWithProviders(<ControlStatusPage />);
    const table = await screen.findByRole('table', { name: '取引統制（優先順位順）' });
    const rows = within(table).getAllByRole('row');

    // 見出し行を除いた 3 行が重い順に並ぶ。
    expect(within(rows[1]).getByText('緊急停止（kill switch）')).toBeInTheDocument();
    expect(within(rows[2]).getByText('日次損失ロックアウト')).toBeInTheDocument();
    expect(within(rows[3]).getByText('一時停止')).toBeInTheDocument();

    // STATUS は activeControl=1（kill switch）。優先統制の印は 1 行だけに付く。
    expect(within(rows[1]).getByText('← 現在の優先統制')).toBeInTheDocument();
    expect(within(table).getAllByText('← 現在の優先統制')).toHaveLength(1);

    // ADR-0009: 日次損失ロックアウトはシステム自動発動であり利用者は解除できない。
    expect(within(rows[2]).getByText('システム自動')).toBeInTheDocument();
    expect(within(rows[2]).getByText(/利用者は解除できない/)).toBeInTheDocument();
  });

  it('shows usage ratios computed from current/limit', async () => {
    renderWithProviders(<ControlStatusPage />);
    const table = await screen.findByRole('table', { name: '上限使用率' });
    // 1日発注 150000/300000 = 50.0%
    expect(within(table).getByText('50.0%')).toBeInTheDocument();
  });

  it('renders stage-gate promotion assessment with unmet criteria labels', async () => {
    renderWithProviders(<ControlStatusPage />);
    await screen.findByRole('heading', { name: '統制状態' });
    expect(await screen.findByText(/未充足基準/)).toHaveTextContent('スリッページ/費用が想定超過');
    expect(screen.getByText(/未充足基準/)).toHaveTextContent('日次損失上限の運用違反');
  });

  it('lists transition history newest first with from→to stage labels', async () => {
    renderWithProviders(<ControlStatusPage />);
    const table = await screen.findByRole('table', { name: '段階遷移履歴' });
    const rows = within(table).getAllByRole('row');
    // 先頭データ行が新しい順（sequence 2）。
    expect(within(rows[1]).getByText('ペーパー実績良好')).toBeInTheDocument();
    expect(within(rows[1]).getByText('Stage 1（SIMULATE） → Stage 2（最小実弾）')).toBeInTheDocument();
  });

  it('has no destructive control buttons (read-only; #165 Bot owns those)', async () => {
    renderWithProviders(<ControlStatusPage />);
    await screen.findByRole('heading', { name: '統制状態' });
    // 参照専用: いかなるボタンも持たない（pause/resume/kill switch/承認は Bot 側）。
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('degrades the stage-gate area independently when it fails but status succeeds', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/risk-controls/stage-gate') throw new ApiError('server', 'boom', 500);
      if (path === '/risk-controls/short-selling') return SHORT_SELLING;
      return STATUS;
    });
    renderWithProviders(<ControlStatusPage />);
    // 統制状態は表示され、段階ゲート領域のみ縮退する。
    const table = await screen.findByRole('table', { name: '取引統制（優先順位順）' });
    expect(within(table).getByText('緊急停止（kill switch）')).toBeInTheDocument();
    expect(screen.getByText('段階ゲートは利用できません。')).toBeInTheDocument();
  });

  it('degrades to a not-available message when status 404s (BFF unwired)', async () => {
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/risk-controls/status') throw new ApiError('notFound', '未登録', 404);
      throw new ApiError('notFound', '未登録', 404);
    });
    renderWithProviders(<ControlStatusPage />);
    expect(await screen.findByText('統制状態は利用できません。')).toBeInTheDocument();
  });

  it('renders unknown enum values and a zero limit safely (fail-safe UI)', async () => {
    // 未知の activeControl(9) はラベル写像テーブルに無く「不明(9)」へ、上限 0 の使用率は「—」へ安全側に倒す。
    mocks.apiFetch.mockImplementation(async (path: string) => {
      if (path === '/risk-controls/stage-gate') return STAGE_GATE;
      if (path === '/risk-controls/short-selling') return SHORT_SELLING;
      return { ...STATUS, activeControl: 9, maxOpenPositions: 0, openPositionCount: 0 };
    });
    renderWithProviders(<ControlStatusPage />);
    // 読み込み完了（統制状態の描画）を待つ。未知 enum は安全側フォールバック表示になる。
    expect(await screen.findByText('不明(9)')).toBeInTheDocument();
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
      if (path === '/risk-controls/short-selling') return SHORT_SELLING;
      return STATUS;
    });
    renderWithProviders(<ControlStatusPage />);
    const alert = await screen.findByText(/撤退基準に到達/);
    expect(alert).toHaveTextContent('実DD がバックテスト最大DD × 倍率に到達');
  });
});
