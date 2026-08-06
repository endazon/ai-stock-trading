import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';

// SC-03, FR-10, UC-06, ADR-0016（決定3・決定7・決定9・決定15）, #340, IADR-0154:
// 「維持率・空売りの現況」（画面最上位）の表示。
//
// **本テストの主眼は「未供給を正常値に見せないこと」である。**
// 計画（05_screens SC-03）は維持率を「本画面の最上位に置く。マージンコールは口座を失う唯一の経路である」と
// 定めた。供給が無いのに 0 や「—」だけで描くと、画面は正常に働いている統制として見える
// （#403 の `ControlViolationCount` 既定 0 が「違反なし」に見えた fail-open と同型）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { ControlStatusPage } from './ControlStatusPage';
import {
  CONTRACT_RISK_STATUS,
  CONTRACT_SHORT_SELLING,
  CONTRACT_STAGE_GATE,
  cloneContract,
} from '../risk/contractFixtures';
import type { ShortSellingStatusView } from '../risk/contracts';
import {
  METRIC_AVAILABLE,
  METRIC_NOT_APPLICABLE,
  METRIC_NOT_SUPPLIED,
  METRIC_NOT_SUPPLIED_TEXT,
} from '../risk/contracts';

// #389, IADR-0146: モックは**バックエンドの実応答**（契約フィクスチャ）を土台にし、各テストが要る差分だけを
// 上書きする。インライン literal で全体を自作すると、フロントが「こう返ってくるはずだ」と思っている形を
// フロント自身が検証する構造へ戻る。
function mockBff(shortSelling: ShortSellingStatusView | 'fail'): void {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return cloneContract(CONTRACT_STAGE_GATE);
    if (path === '/risk-controls/short-selling') {
      if (shortSelling === 'fail') throw new Error('boom');
      return shortSelling;
    }
    return cloneContract(CONTRACT_RISK_STATUS);
  });
}

const BASE = (): ShortSellingStatusView => cloneContract(CONTRACT_SHORT_SELLING);

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-03 維持率・空売りの現況（#340・ADR-0016 決定15）', () => {
  // ---- 維持率（ADR-0016 決定7・画面最上位） ----

  it('維持率が未供給のとき「取得できていません」と明示し、統制が働いていない旨を警告する', async () => {
    // 実応答（既定構成）そのものが未供給である。**これが現在の事実**であり、テストはそれを固定する。
    mockBff(BASE());
    render(<ControlStatusPage />);

    expect(await screen.findByRole('heading', { name: '維持率' })).toBeInTheDocument();
    // 値の位置に「取得できていません」が出る（0.0% でも「—」でもない）。
    expect(screen.getAllByText(METRIC_NOT_SUPPLIED_TEXT).length).toBeGreaterThan(0);
    // 「この統制は現在まったく働いていません」を警告として出す。
    expect(
      screen.getByText(/この統制は現在まったく働いていません/),
    ).toBeInTheDocument();
  });

  it('維持率が未供給のとき 0% や — を維持率の値として表示しない（fail-open を作らない）', async () => {
    mockBff(BASE());
    render(<ControlStatusPage />);
    await screen.findByRole('heading', { name: '維持率' });

    // 「現在の維持率: 0.0%」のような**正常に見える表示**が無いことを否定形で固定する。
    expect(screen.queryByText(/現在の維持率: 0\.0%/)).not.toBeInTheDocument();
    expect(screen.queryByText(/現在の維持率: —/)).not.toBeInTheDocument();
  });

  it('維持率が供給されているとき、値・適用閾値・回復目標（閾値 + 5pt）を表示する', async () => {
    mockBff({
      ...BASE(),
      maintenanceMarginAvailability: METRIC_AVAILABLE,
      maintenanceMarginRatio: 0.38,
      appliedMaintenanceMarginThreshold: 0.4,
      appliedMaintenanceRecoveryTarget: 0.45,
    });
    render(<ControlStatusPage />);

    expect(await screen.findByText('38.0%')).toBeInTheDocument();
    // 適用閾値（40.0%）と設定上の閾値（40.0%）は別の行として並ぶ（この応答では一致する）。
    expect(screen.getAllByText('40.0%')).toHaveLength(2);
    expect(screen.getByText('45.0%')).toBeInTheDocument();
    // 供給があるときは「働いていません」の警告を出さない（警告が常時出ると誰も読まなくなる）。
    expect(screen.queryByText(/この統制は現在まったく働いていません/)).not.toBeInTheDocument();
  });

  it('回復目標のオフセット（+5 ポイント）を応答から取り、画面に直書きしない', async () => {
    // 計画の改訂に画面が追随しないことを防ぐ（Stage1GateCriteria と同じ方針）。
    mockBff({ ...BASE(), maintenanceRecoveryTargetOffset: 0.07 });
    render(<ControlStatusPage />);

    expect(await screen.findByText(/回復目標（閾値 \+ 7\.0%）/)).toBeInTheDocument();
  });

  // ---- 空売り比率（ADR-0016 決定9） ----

  it('空売り比率は建玉が無ければ「該当なし」、評価額が揃わなければ「取得できていません」を出し分ける', async () => {
    mockBff({
      ...BASE(),
      positions: [],
      shortExposureAvailability: METRIC_NOT_APPLICABLE,
      shortExposureRatio: null,
    });
    const { unmount } = render(<ControlStatusPage />);
    expect(await screen.findByText(/該当なし（対象の建玉がありません）/)).toBeInTheDocument();
    unmount();

    mockBff({ ...BASE(), shortExposureAvailability: METRIC_NOT_SUPPLIED, shortExposureRatio: null });
    render(<ControlStatusPage />);
    expect(
      await screen.findByText(/空売り比率の分母（建玉総額）は/),
    ).toBeInTheDocument();
  });

  it('空売り比率と上限を応答から表示する', async () => {
    mockBff({
      ...BASE(),
      shortExposureAvailability: METRIC_AVAILABLE,
      shortExposureRatio: 0.42,
      shortExposureRatioCap: 0.5,
    });
    render(<ControlStatusPage />);

    expect(await screen.findByText(/現在の空売り比率:/)).toHaveTextContent('42.0%');
    expect(screen.getByText(/現在の空売り比率:/)).toHaveTextContent('50.0%');
  });

  // ---- 保有ポジション（ADR-0016 決定15） ----

  it('保有ポジションに建玉の方向（ロング / ショート）を表示する', async () => {
    const base = BASE();
    mockBff({
      ...base,
      positions: [
        { ...base.positions[0], symbol: 'SHRT', side: 1 },
        { ...base.positions[0], symbol: 'LONG', side: 0 },
      ],
    });
    render(<ControlStatusPage />);

    const table = await screen.findByRole('table', { name: '保有ポジション（方向・借株料）' });
    const rows = within(table).getAllByRole('row');
    expect(within(rows[1]).getByText('ショート')).toBeInTheDocument();
    expect(within(rows[2]).getByText('ロング')).toBeInTheDocument();
  });

  it('借株料の累計は未供給として明示され、0 を表示しない', async () => {
    mockBff(BASE());
    render(<ControlStatusPage />);

    expect(await screen.findByText(/借株料の累計:/)).toHaveTextContent(METRIC_NOT_SUPPLIED_TEXT);
    // 「0 ではなく不明」であることを利用者に伝える（費用が発生していないと読ませない）。
    expect(screen.getByText(/0 ではなく「不明」です/)).toBeInTheDocument();
    expect(screen.queryByText(/借株料の累計: \$0/)).not.toBeInTheDocument();
  });

  // ---- 維持率割れによる自動縮小（05_screens 2026-08-02 追加） ----

  it('維持率割れ自動縮小は 3 統制と別枠で「動かす」統制として描かれる', async () => {
    mockBff(BASE());
    render(<ControlStatusPage />);

    // 3 統制の表とは別の region に置く（計画: 3 統制と同じ枠に並べると誤読されるため視覚的に区別する）。
    const region = await screen.findByRole('region', {
      name: '維持率割れによる自動縮小（動かす統制）',
    });
    expect(within(region).getByText(/すでに建てた玉を決済します/)).toBeInTheDocument();
    expect(within(region).getByText(/利用者の承認を待たず、AI を介さずに動きます/)).toBeInTheDocument();
    // 縮小対象は**必要証拠金の降順**（含み損の大きい順ではない）。
    expect(within(region).getByText(/必要証拠金が最も大きい建玉から/)).toBeInTheDocument();
    expect(within(region).getByText(/含み損の大きい順ではありません/)).toBeInTheDocument();

    // 3 統制の表には含まれない（別枠であることの否定形）。
    const controls = await screen.findByRole('table', { name: '取引統制（優先順位順）' });
    expect(within(controls).queryByText(/自動縮小/)).not.toBeInTheDocument();
  });

  it('発動履歴が未供給のとき「発動なし」と表示しない', async () => {
    mockBff(BASE());
    render(<ControlStatusPage />);

    const region = await screen.findByRole('region', {
      name: '維持率割れによる自動縮小（動かす統制）',
    });
    expect(within(region).getByText(/記録する経路がない/)).toBeInTheDocument();
    // **「発動履歴はありません」＝発動なし**という表示にしない（統制が働いていると読める）。
    expect(within(region).queryByText('発動履歴はありません。')).not.toBeInTheDocument();
  });

  it('発動履歴が供給されていれば発動日時・前後の維持率・決済した建玉を表示する', async () => {
    mockBff({
      ...BASE(),
      reductionHistoryAvailability: METRIC_AVAILABLE,
      reductionHistory: [
        {
          executedAt: '2026-08-05T13:00:00.0000000+00:00',
          ratioBefore: 0.39,
          threshold: 0.4,
          recoveryTarget: 0.45,
          ratioAfter: 0.46,
          legs: [
            { symbol: 'AAPL', market: 1, positionSide: 1, quantity: 20, requiredMarginUsd: 3000 },
          ],
        },
      ],
    });
    render(<ControlStatusPage />);

    const table = await screen.findByRole('table', { name: '維持率割れ自動縮小の発動履歴' });
    expect(within(table).getByText('39.0%')).toBeInTheDocument();
    expect(within(table).getByText('46.0%')).toBeInTheDocument();
    expect(within(table).getByText(/AAPL（ショート 20 株・必要証拠金 \$3,000）/)).toBeInTheDocument();
  });

  // ---- 取得失敗（領域の独立した縮退） ----

  it('空売り現況の取得失敗は当該領域のみ縮退し「問題なし」に見せない', async () => {
    mockBff('fail');
    render(<ControlStatusPage />);

    expect(
      await screen.findByText(/値が無いのではなく、確認できていません/),
    ).toBeInTheDocument();
    // 統制状態（別領域）は独立して表示される。
    expect(await screen.findByRole('table', { name: '上限使用率' })).toBeInTheDocument();
  });
});
