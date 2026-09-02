import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProviders } from '@ai-stock-trading/testing/renderWithProviders';

// SC-03, FR-10, FR-20, UC-06, #340: **参照専用性の否定形。**
//
// 計画（05_screens SC-03）の原文: 「**表示のみで設定変更・統制操作は行わない**」
// 「**発注先の変更も本画面では行わない**（変更は SC-02）」。破壊的な統制操作（昇格承認・pause/resume・
// kill switch）は Discord Bot へ一元化されており（#165・IADR-0081）、Web には置かない。
//
// 「置いていない」ことは**書かないだけでは守れない**。誰かが 1 つ入力欄を足しても既存のテストは緑のままである。
// したがって**入力要素・保存系ボタンが 1 つも存在しないこと**を機械的に固定する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { ControlStatusPage } from './ControlStatusPage';
import {
  CONTRACT_RISK_STATUS,
  CONTRACT_SETTINGS_HISTORY,
  CONTRACT_SHORT_SELLING,
  CONTRACT_STAGE_GATE,
  cloneContract,
} from '@ai-stock-trading/testing/riskContractFixtures';

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return cloneContract(CONTRACT_STAGE_GATE);
    if (path === '/risk-controls/short-selling') return cloneContract(CONTRACT_SHORT_SELLING);
    if (path === '/risk-controls/settings/history') return cloneContract(CONTRACT_SETTINGS_HISTORY);
    return cloneContract(CONTRACT_RISK_STATUS);
  });
});

describe('SC-03 参照専用性（#340）', () => {
  it('入力要素（テキスト・数値・チェックボックス・ラジオ・選択）を 1 つも持たない', async () => {
    renderWithProviders(<ControlStatusPage />);
    // 全領域が描画されるまで待つ（描画前に数えると常に 0 で緑になる＝検査が死ぬ）。
    await screen.findByRole('table', { name: '上限使用率' });
    await screen.findByRole('table', { name: '取引統制（優先順位順）' });
    await screen.findByRole('heading', { name: '維持率' });

    expect(screen.queryAllByRole('textbox')).toHaveLength(0);
    expect(screen.queryAllByRole('spinbutton')).toHaveLength(0);
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
    expect(screen.queryAllByRole('radio')).toHaveLength(0);
    expect(screen.queryAllByRole('combobox')).toHaveLength(0);
    // <form> そのものを持たない（送信経路が存在しない）。
    expect(document.querySelectorAll('form')).toHaveLength(0);
  });

  it('変更・統制操作のボタンを 1 つも持たない', async () => {
    renderWithProviders(<ControlStatusPage />);
    await screen.findByRole('table', { name: '上限使用率' });

    // details/summary は開閉のみで副作用が無いため button ロールを持たない。
    // ここで数えるのは「押すと何かが起きる」ボタンであり、0 でなければならない。
    expect(screen.queryAllByRole('button')).toHaveLength(0);
  });

  it('参照専用であること・変更は SC-02 であることを画面に明記する', async () => {
    renderWithProviders(<ControlStatusPage />);

    expect(
      await screen.findByText(/発注先の変更は「リスク設定」画面（SC-02）で行います。本画面は参照専用です。/),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/統制の変更・段階の承認は\s*Discord からのみ行えます（本画面は参照専用）。/),
    ).toBeInTheDocument();
  });

  it('書き込み系の API を一切呼ばない（GET のみ）', async () => {
    renderWithProviders(<ControlStatusPage />);
    await screen.findByRole('table', { name: '上限使用率' });

    // apiFetch の第 2 引数（method 等）を渡している呼び出しが無い＝すべて既定の GET である。
    for (const call of mocks.apiFetch.mock.calls) {
      expect(call[1]).toBeUndefined();
    }
    expect(mocks.apiFetch.mock.calls.length).toBeGreaterThan(0);
  });
});
